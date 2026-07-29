// contrib/aspire-dev/ArgoCd.Aspire.AppHost/AppHost.cs
//
// Aspire-managed Argo CD development loop.
//
// Architecture: the Kind cluster holds STATE ONLY — CRDs, RBAC, ConfigMaps, and Secrets (see
// ArgoCdManifestSet). It never runs a released Argo CD Deployment,
// StatefulSet, or Service. Instead, every Argo CD component that the repository's own Procfile
// would launch runs here as a native Aspire executable resource: the exact same `go run
// ./cmd ...` command, environment variables (including ARGOCD_FAKE_IN_CLUSTER=true), and
// ports as Procfile, so `aspire start` (or Visual Studio F5) replaces `make start` / `goreman start`
// / `tilt up` for day-to-day development without ever requiring `make`, a POSIX shell, a Linux
// cross-compiled binary, `docker build`, or `kind load docker-image`.
//
// This complements, and does NOT replace, Argo CD's official Tilt-based inner-loop workflow (see
// ./Tiltfile and docs/developer-guide/running-locally.md). Tilt remains the recommended workflow
// for iterating simultaneously on Argo CD *and* the manifests/CRDs it manages, or for scenarios
// needing in-cluster component images. This AppHost targets host-process-first, low-overhead,
// selective-restart iteration on Argo CD's own Go/TypeScript source.
//
// Run with:            cd contrib/aspire-dev && aspire run
// Or in Visual Studio:  F5 with ArgoCd.Aspire.AppHost as the startup project.
//
// See README.md for the full workflow, resource graph, and troubleshooting.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Go;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ArgoCd.Aspire.AppHost;

// Fail fast with actionable remediation text before creating any resources: Docker, kind,
// kubectl, and Go are hard prerequisites; Node/corepack are checked (pnpm soft-checked) since
// the UI resource below needs them.
if (!IsTruthy(ArgoCdPrerequisites.SkipEnvironmentVariable))
{
    ArgoCdPrerequisites.ValidateOrThrow();
}

var builder = DistributedApplication.CreateBuilder(args);

var repoRoot = ArgoCdRepository.Root;

// Opt-in feature flags. Both default OFF so the one-command happy path never requires Dex or the
// CMP sidecar — CMP additionally requires a real Unix domain socket and is therefore unsupported
// on Windows (see ArgoCdComponents.AddArgoCdCmpServer, which throws PlatformNotSupportedException
// there with an actionable message).
var enableDex = IsTruthy("ARGOCD_ASPIRE_ENABLE_DEX");
var enableCmp = IsTruthy("ARGOCD_ASPIRE_ENABLE_CMP");

// Pre-create the cross-platform local-data directories the Go components and dev-mounter read
// from / write to — equivalents of the Procfile's implicit `/tmp/argocd-local/...` creation.
ArgoCdPaths.EnsureDirectoryExists(ArgoCdPaths.TlsDataPath);
ArgoCdPaths.EnsureDirectoryExists(ArgoCdPaths.SshDataPath);
ArgoCdPaths.EnsureDirectoryExists(ArgoCdPaths.GpgKeysPath);
ArgoCdPaths.EnsureDirectoryExists(ArgoCdPaths.GpgSourcePath);

// ---------------------------------------------------------------------------------------------
// Kind cluster — state only. No Argo CD workload manifests are ever applied here; the generated
// state manifest contains only CRDs/RBAC/ConfigMaps/Secrets from ArgoCdManifestSet. The Kind
// integration creates the cluster, waits for readiness, and applies that manifest via WithManifest.
//
// CommunityToolkit.Aspire.Hosting.Kind uses the resource name as the default Kind cluster name,
// so this is derived per checkout by ArgoCdClusterName. Two worktrees/clones on the same machine
// never collide on one shared cluster/kubeconfig; set ARGOCD_ASPIRE_CLUSTER_NAME to override it.
// ---------------------------------------------------------------------------------------------
var kindClusterName = ArgoCdClusterName.Resolve(repoRoot);
var stateManifestPath = ArgoCdManifestSet.RenderStateOnlyManifest(
    Path.Combine(AppContext.BaseDirectory, "generated", "argocd-state.yaml"),
    enableDex);

var cluster = builder
    .AddKindCluster(kindClusterName)
    .WithClusterLifetime(ClusterLifetime.Persistent)
    .WithManifest(stateManifestPath);

cluster
    .WithRepoServerOverrideCommand()
    .WithDeleteClusterCommand()
    .WithAdminCredentialCommand();

// ---------------------------------------------------------------------------------------------
// Redis — Aspire-managed. The Go components receive Aspire's dynamically allocated plain TCP
// endpoint through REDIS_SERVER instead of a hard-coded --redis flag, because Aspire exposes the
// non-TLS Redis endpoint on a free host port even when a preferred host port is requested.
//
// SECURITY BOUNDARY: builder.AddRedis(...) generates and enforces a random password by default.
// The real Argo CD components authenticate to Redis via the REDIS_PASSWORD environment variable
// (see util/cache/cache.go), not via the Redis endpoint value, so leaving Aspire's default
// password in place while failing to thread REDIS_PASSWORD would make every component fail Redis
// auth (NOAUTH). Two ways to reconcile this were considered:
//   1. Thread the generated password into every component via REDIS_PASSWORD (matches upstream
//      auth exactly, keeps Redis network-authenticated).
//   2. Disable the password outright with `.WithPassword(null)`, matching the Procfile's own
//      posture: Redis listens only on localhost, with no auth, exactly as `redis-server` run
//      directly (unauthenticated) would for local development.
// This AppHost uses (2): Redis here is local-only (bound to the host loopback interface, never
// exposed to the Kind cluster or any external network), so an unauthenticated local Redis exactly
// mirrors both the Procfile's own security posture and upstream's documented local dev setup. If
// PasswordParameter is non-null for any other reason (e.g. a future Aspire default change), the
// components below still thread it through correctly via REDIS_PASSWORD — see
// ArgoCdComponents.WithRedisPassword — so this remains safe even if that assumption changes.
// ---------------------------------------------------------------------------------------------
var redis = builder
    .AddRedis("redis", port: 6379)
    .WithPassword(null);

var gitVerifyWrapperDirectory = IsTruthy(ArgoCdPrerequisites.SkipEnvironmentVariable)
    ? null
    : GitVerifyWrapperShim.EnsureAvailableIfNeeded(repoRoot);

// ---------------------------------------------------------------------------------------------
// Argo CD components — native host processes, one per Procfile entry, matching commands/env and
// process-owned ports (see ArgoCdComponents.cs). `.WaitFor` below only orders process *launch*;
// like the Procfile itself, nothing here blocks on the target TCP ports actually accepting
// connections. Inter-component addresses are resolved from the owning Aspire resources rather
// than duplicated as localhost literals.
// ---------------------------------------------------------------------------------------------
var repoServer = builder.AddArgoCdRepoServer(redis, gitVerifyWrapperDirectory).WithKindEnvironment(cluster).WaitFor(redis);
var commitServer = builder.AddArgoCdCommitServer();
IResourceBuilder<ContainerResource>? dex = null;

if (enableDex)
{
    // Dex (OIDC provider) is not a component of this repository, so it is modeled the same way
    // the Procfile models it: a config-generation step followed by a published container image.
    // Procfile ground truth (contrib/running-locally, `dex:` line):
    //   ARGOCD_BINARY_NAME=argocd-dex go run github.com/argoproj/argo-cd/v3/cmd gendexcfg \
    //       -o `pwd`/dist/dex.yaml &&
    //     (test -f dist/dex.yaml || { echo 'Failed to generate dex configuration'; exit 1; }) &&
    //     docker run ... -v `pwd`/dist/dex.yaml:/dex.yaml ghcr.io/dexidp/dex:v2.45.1 \
    //         dex serve /dex.yaml
    // Only needed to exercise SSO login flows, so the whole block stays fully opt-in.
    var dexConfigPath = ArgoCdPaths.DexConfigPath();
    ArgoCdPaths.EnsureDirectoryExists(Path.GetDirectoryName(dexConfigPath)!);

    // See ArgoCdDex.cs (AddArgoCdGenDexConfig / AddArgoCdDex) for the exact command/args/env,
    // bind-mount, and WaitFor/WaitForCompletion ordering — extracted there so this resource graph
    // is unit-testable independently of this top-level-statements Program entry point.
    var gendexcfg = builder.AddArgoCdGenDexConfig(dexConfigPath, cluster);
    dex = builder.AddArgoCdDex(dexConfigPath, gendexcfg);
}

var apiServer = builder.AddArgoCdApiServer(redis, repoServer, dex).WithKindEnvironment(cluster).WaitFor(redis).WaitFor(repoServer);
if (dex is not null)
{
    apiServer = apiServer.WaitFor(dex);
}

var applicationController = builder
    .AddArgoCdApplicationController(redis, repoServer, commitServer)
    .WithKindEnvironment(cluster)
    .WaitFor(redis)
    .WaitFor(repoServer)
    .WaitFor(commitServer);
var applicationSetController = builder.AddArgoCdApplicationSetController(repoServer).WithKindEnvironment(cluster).WaitFor(repoServer);
var notificationsController = builder.AddArgoCdNotificationsController().WithKindEnvironment(cluster);

if (enableCmp)
{
    // Safe to call unconditionally even on Windows: AddArgoCdCmpServer itself throws
    // PlatformNotSupportedException with actionable remediation text there. Gated behind
    // ARGOCD_ASPIRE_ENABLE_CMP so the default loop never attempts it.
    builder.AddArgoCdCmpServer().WithKindEnvironment(cluster);
}

// ---------------------------------------------------------------------------------------------
// hack/dev-mounter — syncs the argocd-ssh-known-hosts-cm / argocd-tls-certs-cm /
// argocd-gpg-keys-cm ConfigMaps (created in the Kind cluster by WithManifest) down to
// the cross-platform host files in ArgoCdPaths, so repo-server (a host process, not a Pod) can
// read them exactly like it would from a mounted ConfigMap volume.
// ---------------------------------------------------------------------------------------------
builder
    .AddGoApp(
        "dev-mounter",
        repoRoot,
        "hack/dev-mounter")
    .WithKindEnvironment(cluster)
    .WithAppArgs(
        "--kubeconfig", cluster.Resource.KubeconfigPath,
        "--configmap", $"argocd-ssh-known-hosts-cm={ArgoCdPaths.SshDataPath}",
        "--configmap", $"argocd-tls-certs-cm={ArgoCdPaths.TlsDataPath}",
        "--configmap", $"argocd-gpg-keys-cm={ArgoCdPaths.GpgSourcePath}");

// ---------------------------------------------------------------------------------------------
// UI — real `pnpm start` (webpack-dev-server), preserving Hot Module Replacement.
// ARGOCD_API_URL points it at the host-process API server above instead of an in-cluster Service.
// ---------------------------------------------------------------------------------------------
var uiDir = Path.Combine(repoRoot, "ui");

// Automate the one dependency-install step the UI needs before `pnpm start` can succeed: this
// resolves and installs node_modules via pnpm. Skips entirely when node_modules is already up to
// date with package.json/pnpm-lock.yaml, or when ARGOCD_ASPIRE_SKIP_UI_INSTALL is set — codegen
// itself stays on-demand (`make codegen`), never run automatically here.
ArgoCdUiDependencies.EnsureInstalledOrThrow(uiDir, TimeSpan.FromMinutes(5));
var pnpm = ArgoCdUiDependencies.GetPnpmInvocation();

builder
    .AddExecutable("ui", pnpm.Command, uiDir, pnpm.Arguments.Concat(["start"]).ToArray())
    .WithEnvironment("ARGOCD_API_URL", apiServer.GetEndpoint("http"))
    .WithHttpEndpoint(port: 4000, targetPort: 4000, name: "http", isProxied: false)
    .WithHttpHealthCheck("/")
    .WaitFor(apiServer);

// ---------------------------------------------------------------------------------------------
// Selective restart — editing a component's Go source restarts only that component's host
// process. No full AppHost restart, no docker build/kind load: the fast, targeted inner loop
// this whole design exists to provide.
// ---------------------------------------------------------------------------------------------
builder.Services.AddSingleton<IDistributedApplicationEventingSubscriber>(sp =>
    new ArgoCdSelectiveRestartHook(
        sp.GetRequiredService<ILogger<ArgoCdSelectiveRestartHook>>(),
        sp,
        enableCmp));

builder.Build().Run();

static bool IsTruthy(string environmentVariableName) =>
    string.Equals(
        Environment.GetEnvironmentVariable(environmentVariableName),
        "true",
        StringComparison.OrdinalIgnoreCase);

internal static class KindEnvironmentExtensions
{
    public static IResourceBuilder<T> WithKindEnvironment<T>(
        this IResourceBuilder<T> resource,
        IResourceBuilder<KindClusterResource> cluster)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        return resource
            .WithEnvironment("KUBECONFIG", cluster.Resource.KubeconfigPath)
            .WithEnvironment("K8S_CLUSTER_NAME", cluster.Resource.Name)
            .WaitFor(cluster);
    }
}
