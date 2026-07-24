// contrib/aspire-dev/ArgoCd.Aspire.AppHost/AppHost.cs
//
// Aspire-managed Argo CD development loop.
//
// Architecture: the Kind cluster holds STATE ONLY — CRDs, RBAC, ConfigMaps, and Secrets (see
// ArgoCdManifestSet and ArgoCdStateBootstrapHook). It never runs a released Argo CD Deployment,
// StatefulSet, or Service. Instead, every Argo CD component that the repository's own Procfile
// would launch runs here as a native Aspire executable resource: the exact same `go run
// ./cmd/main.go ...` command, environment variables (including ARGOCD_FAKE_IN_CLUSTER=true), and
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
// Run with:            aspire start contrib/aspire-dev/ArgoCd.Aspire.AppHost/ArgoCd.Aspire.AppHost.csproj
// Or in Visual Studio:  F5 with ArgoCd.Aspire.AppHost as the startup project.
//
// See contrib/aspire-dev/README.md for the full workflow, resource graph, and troubleshooting.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Go;
using Aspire.Hosting.Lifecycle;
using ArgoCd.Aspire.AppHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

// Fail fast with actionable remediation text before creating any resources: Docker, kind,
// kubectl, and Go are hard prerequisites; Node/corepack are checked (pnpm soft-checked) since
// the UI resource below needs them.
ArgoCdPrerequisites.ValidateOrThrow();

var builder = DistributedApplication.CreateBuilder(args);

var repoRoot = ArgoCdRepoRoot.Resolve(AppContext.BaseDirectory);

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
// Kind cluster — state only. No Argo CD workload manifests are ever applied here; see
// ArgoCdStateBootstrapHook (registered below) for the exact CRD/RBAC/ConfigMap/Secret set that
// is applied via `kubectl apply --server-side --force-conflicts`. WithPersistentCluster reuses an
// existing healthy cluster across AppHost restarts and leaves it running on normal stop, avoiding
// the create/delete race a from-scratch cluster would hit on every inner-loop iteration; use the
// "Delete Kind Cluster" dashboard command for deterministic teardown.
//
// The underlying `kind` cluster name (and, transitively, its generated kubeconfig path — see
// AddKindCluster) is *not* the hard-coded literal "argocd-dev" used below as the Aspire *resource*
// name. It is derived per-checkout by ArgoCdClusterName so that two worktrees/clones of this
// repository on the same machine never collide on one shared cluster/kubeconfig; set
// ARGOCD_ASPIRE_CLUSTER_NAME to override it (e.g. to deliberately share one cluster across
// checkouts). See ArgoCdClusterName for the exact derivation and validation rules.
// ---------------------------------------------------------------------------------------------
var kindClusterName = ArgoCdClusterName.Resolve(repoRoot);

var cluster = builder
    .AddKindCluster("argocd-dev", clusterName: kindClusterName)
    .WithPersistentCluster()
    .WithWaitForReady(TimeSpan.FromMinutes(10))
    .WithHealthCheck(ArgoCdBootstrapState.HealthCheckKey)
    .WithDashboardProperty("argocd.repoRoot", repoRoot)
    .WithDashboardProperty("argocd.namespace", "argocd")
    .WithDashboardProperty("argocd.clusterName", kindClusterName)
    .WithDashboardProperty(
        "argocd.note",
        "State only: CRDs/RBAC/ConfigMaps/Secrets. Every Argo CD component runs as a native " +
        "host-process resource in this Aspire app graph, not as a Kubernetes workload.");

// Shared, thread-safe signal that lets the "argocd-state-bootstrap" health check (below) report
// unhealthy until ArgoCdStateBootstrapHook has actually finished applying the state-only manifest
// set — and unhealthy (not just "pending") if that apply failed. gendexcfg (added later, only
// when Dex is enabled) waits on this health check so it never races the cluster's CRDs/RBAC.
var bootstrapState = new ArgoCdBootstrapState();
builder.Services.AddSingleton(bootstrapState);
builder.Services
    .AddHealthChecks()
    .AddCheck(ArgoCdBootstrapState.HealthCheckKey, () => bootstrapState.Succeeded
        ? HealthCheckResult.Healthy()
        : bootstrapState.Completed
            ? HealthCheckResult.Unhealthy("Argo CD state-only manifest bootstrap failed.")
            : HealthCheckResult.Unhealthy("Argo CD state-only manifest bootstrap has not completed yet."));

builder.Services.AddSingleton<IDistributedApplicationEventingSubscriber>(sp =>
    new ArgoCdStateBootstrapHook(
        sp.GetRequiredService<ILogger<ArgoCdStateBootstrapHook>>(),
        sp.GetRequiredService<ResourceNotificationService>(),
        bootstrapState,
        cluster.Resource,
        enableDex));

cluster
    .WithRepoServerOverrideCommand(repoRoot)
    .WithDeleteClusterCommand()
    .WithAdminCredentialCommand();

// ---------------------------------------------------------------------------------------------
// Redis — Aspire-managed, pinned to the Procfile's hard-coded localhost:6379 (the Go components
// below do not read a Redis connection string from configuration; they hard-code
// `--redis localhost:6379`, matching the Procfile exactly — a plain host:port with no embedded
// credentials).
//
// SECURITY BOUNDARY: builder.AddRedis(...) generates and enforces a random password by default.
// The real Argo CD components authenticate to Redis via the REDIS_PASSWORD environment variable
// (see util/cache/cache.go), never via the --redis flag, so leaving Aspire's default password in
// place while every component's --redis flag stays a bare host:port would make every component
// fail Redis auth (NOAUTH). Two ways to reconcile this were considered:
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
    .AddRedis("redis")
    .WithHostPort(6379)
    .WithPassword(null);

// ---------------------------------------------------------------------------------------------
// Argo CD components — native host processes, one per Procfile entry, matching commands/env/ports
// exactly (see ArgoCdComponents.cs). `.WaitFor` below only orders process *launch*; like the
// Procfile itself, nothing here blocks on the target TCP ports actually accepting connections.
// ---------------------------------------------------------------------------------------------
var repoServer = builder.AddArgoCdRepoServer(repoRoot, redis).WaitFor(redis);
var commitServer = builder.AddArgoCdCommitServer(repoRoot);
var apiServer = builder.AddArgoCdApiServer(repoRoot, redis).WaitFor(redis).WaitFor(repoServer);
var applicationController = builder
    .AddArgoCdApplicationController(repoRoot, redis)
    .WaitFor(redis)
    .WaitFor(repoServer)
    .WaitFor(commitServer);
var applicationSetController = builder.AddArgoCdApplicationSetController(repoRoot).WaitFor(repoServer);
var notificationsController = builder.AddArgoCdNotificationsController(repoRoot);

if (enableCmp)
{
    // Safe to call unconditionally even on Windows: AddArgoCdCmpServer itself throws
    // PlatformNotSupportedException with actionable remediation text there. Gated behind
    // ARGOCD_ASPIRE_ENABLE_CMP so the default loop never attempts it.
    builder.AddArgoCdCmpServer(repoRoot);
}

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
    var dexConfigPath = ArgoCdPaths.DexConfigPath(repoRoot);
    ArgoCdPaths.EnsureDirectoryExists(Path.GetDirectoryName(dexConfigPath)!);

    // See ArgoCdDex.cs (AddArgoCdGenDexConfig / AddArgoCdDex) for the exact command/args/env,
    // bind-mount, and WaitFor/WaitForCompletion ordering — extracted there so this resource graph
    // is unit-testable independently of this top-level-statements Program entry point.
    var gendexcfg = builder.AddArgoCdGenDexConfig(repoRoot, dexConfigPath, cluster);
    builder.AddArgoCdDex(dexConfigPath, gendexcfg);
}

// ---------------------------------------------------------------------------------------------
// hack/dev-mounter — syncs the argocd-ssh-known-hosts-cm / argocd-tls-certs-cm /
// argocd-gpg-keys-cm ConfigMaps (created in the Kind cluster by ArgoCdStateBootstrapHook) down to
// the cross-platform host files in ArgoCdPaths, so repo-server (a host process, not a Pod) can
// read them exactly like it would from a mounted ConfigMap volume.
// ---------------------------------------------------------------------------------------------
builder
    .AddGoApp(
        "dev-mounter",
        repoRoot,
        "hack/dev-mounter/main.go")
    .WithAppArgs(
        "--kubeconfig", cluster.Resource.KubeconfigPath,
        "--configmap", $"argocd-ssh-known-hosts-cm={ArgoCdPaths.SshDataPath}",
        "--configmap", $"argocd-tls-certs-cm={ArgoCdPaths.TlsDataPath}",
        "--configmap", $"argocd-gpg-keys-cm={ArgoCdPaths.GpgSourcePath}")
    .WaitFor(cluster);

// ---------------------------------------------------------------------------------------------
// UI — real `pnpm start` (webpack-dev-server), preserving Hot Module Replacement.
// ARGOCD_API_URL points it at the host-process API server above instead of an in-cluster Service.
// ---------------------------------------------------------------------------------------------
var uiDir = Path.Combine(repoRoot, "ui");

// Automate the one dependency-install step the UI needs before `pnpm start` can succeed: this
// resolves and installs node_modules via the exact pnpm version ui/package.json pins, using
// corepack (already validated as a hard prerequisite above). Skips entirely when node_modules is
// already up to date with pnpm-lock.yaml, or when ARGOCD_ASPIRE_SKIP_UI_INSTALL is set — codegen
// itself stays on-demand (`make codegen`), never run automatically here.
ArgoCdUiDependencies.EnsureInstalledOrThrow(uiDir, TimeSpan.FromMinutes(5));

builder
    .AddExecutable("ui", "pnpm", uiDir, "start")
    .WithEnvironment("ARGOCD_API_URL", apiServer.GetEndpoint("http"))
    .WithHttpEndpoint(port: 4000, targetPort: 4000, name: "http", isProxied: false)
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
