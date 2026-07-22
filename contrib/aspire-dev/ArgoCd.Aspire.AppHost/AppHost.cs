// contrib/aspire-dev/ArgoCd.Aspire.AppHost/AppHost.cs
//
// Aspire AppHost that stands up a deterministic Kind (Kubernetes-in-Docker) cluster and
// deploys the checked-in baseline Argo CD install manifest (manifests/install.yaml, via a
// thin namespaced kustomize overlay — see manifests/argocd-namespaced/kustomization.yaml).
//
// This complements, and does NOT replace, Argo CD's official Tilt-based inner-loop workflow
// (see ./Tiltfile and docs/developer-guide/running-locally.md). Tilt remains the primary,
// live-reloading development loop for iterating on all Argo CD components at once. This
// AppHost targets a narrower, opt-in scenario: a single, reproducible `aspire start` command
// that provisions a throwaway Kind cluster with baseline Argo CD for contributors who want an
// Aspire-dashboard-driven view of the cluster (pods, logs, traces, resource commands) and a
// one-click way to rebuild + redeploy a single component's source (starting with repo-server)
// without hand-rolling kubectl/docker commands each time.
//
// Run with:  aspire start --apphost contrib/aspire-dev/ArgoCd.Aspire.AppHost/ArgoCd.Aspire.AppHost.csproj
// Stop with: aspire stop
//
// See contrib/aspire-dev/README.md for prerequisites, exact commands, and known limitations.

using Aspire.Hosting;
using Aspire.Hosting.Lifecycle;
using ArgoCd.Aspire.AppHost;
using Microsoft.Extensions.DependencyInjection;

var builder = DistributedApplication.CreateBuilder(args);

builder.Services.AddSingleton<IDistributedApplicationLifecycleHook, CrdBootstrapHook>();

var repoRoot = ArgoCdRepoRoot.Resolve(AppContext.BaseDirectory);

// Read manifests from the source tree (via repoRoot), not from the Content-copied output
// directory: the kustomize overlay's "../../../.." relative paths are only valid relative to
// their location in the source tree, and reading from source also means editing manifests/
// takes effect on the next `aspire start` without rebuilding the AppHost.
var appHostSourceDir = Path.Combine(repoRoot, "contrib", "aspire-dev", "ArgoCd.Aspire.AppHost");
var namespaceManifestPath = Path.Combine(appHostSourceDir, "manifests", "namespace.yaml");
var namespacedOverlayDir = Path.Combine(appHostSourceDir, "manifests", "argocd-namespaced");
var generatedManifestPath = Path.Combine(Path.GetTempPath(), "argocd-aspire-kind", "argocd-install.yaml");

// Client-side render only (kubectl kustomize) — no live cluster or Docker required at this
// point. Regenerated on every AppHost start so local edits to manifests/ are always reflected.
ArgoCdManifestRenderer.RenderNamespacedInstallManifest(namespacedOverlayDir, generatedManifestPath);

const string ClusterName = "argocd-dev";

var cluster = builder
    .AddKindCluster(ClusterName)
    .WithWaitForReady(TimeSpan.FromMinutes(10))
    .WithDashboardProperty("argocd.repoRoot", repoRoot)
    .WithDashboardProperty("argocd.namespace", "argocd")
    // Namespace first, then the rest of the baseline install (registration order == apply order).
    .WithManifest(namespaceManifestPath)
    .WithManifest(generatedManifestPath);

// These reference cluster.Resource.KubeconfigPath, which is only available once the builder
// above returns, so they're added as a second WithDashboardProperty pass rather than inline.
cluster.WithDashboardProperty(
    "argocd.portForward.server",
    $"kubectl --kubeconfig \"{cluster.Resource.KubeconfigPath}\" -n argocd port-forward svc/argocd-server 8080:443");
cluster.WithDashboardProperty(
    "argocd.initialAdminPassword",
    $"kubectl --kubeconfig \"{cluster.Resource.KubeconfigPath}\" -n argocd get secret argocd-initial-admin-secret " +
    "-o jsonpath=\"{.data.password}\" | base64 -d");

// Deliverable #5: one-click repo-server source override (build -> load -> patch -> restart -> verify).
cluster.WithRepoServerOverrideCommand(repoRoot);

// Guaranteed-clean teardown safety net — see DeleteClusterCommand.cs for why this is needed
// in addition to the vendored integration's own BeforeStopAsync cluster deletion.
cluster.WithDeleteClusterCommand();

builder.Build().Run();
