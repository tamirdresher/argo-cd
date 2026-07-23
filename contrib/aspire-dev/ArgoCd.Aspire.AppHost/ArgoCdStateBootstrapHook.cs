using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Applies the state-only subset of the repository's own <c>manifests/</c> tree (CRDs, Namespace-scoped
/// ServiceAccounts/Roles/RoleBindings, ClusterRoles/ClusterRoleBindings, ConfigMaps and Secrets — see
/// <see cref="ArgoCdManifestSet"/> for the exact file list) to the Kind cluster with
/// <c>kubectl apply --server-side --force-conflicts</c>, exactly as documented in
/// docs/developer-guide/running-locally.md and manifests/README.md.
///
/// This is the cornerstone of the "Kind holds state only" architecture: every Argo CD component
/// (API server, repo-server, application controller, ApplicationSet controller, notifications
/// controller, commit-server, and optionally Dex/CMP) runs as a native host process outside the
/// cluster (see ArgoCdComponents.cs), so this hook never applies a Deployment,
/// StatefulSet, Service, or NetworkPolicy manifest — only the Kubernetes objects those host
/// processes need to find in the API server (CRD schemas, their own ServiceAccount/RBAC identity,
/// and the ConfigMaps/Secrets they read via <c>ARGOCD_FAKE_IN_CLUSTER=true</c> in-process client).
///
/// This exists as a small, separate lifecycle hook (rather than being folded into the vendored
/// <c>ArgoCd.Aspire.Hosting.Kind</c> integration) because that vendored code's
/// <c>WithManifest(...)</c> always does a plain client-side <c>kubectl apply -f</c>, which fails on
/// the CRDs in particular: their embedded OpenAPI schemas make the
/// <c>kubectl.kubernetes.io/last-applied-configuration</c> annotation exceed the 262144-byte
/// etcd/API-server annotation limit. Server-side apply doesn't hit that limit because it doesn't
/// need to store the full previous-configuration annotation. Every manifest directory in this
/// repository also ships a <c>kustomization.yaml</c> alongside the real objects, so every group
/// below is applied as an explicit list of individual file paths — never a bare
/// <c>-f &lt;directory&gt;</c>, which would make kubectl try (and fail) to parse
/// <c>kustomization.yaml</c> as a Kubernetes resource.
///
/// AppHost.cs never calls the vendored <c>WithManifest(...)</c>/<c>WithHelmChart(...)</c>
/// extensions on the Kind cluster builder, so <c>KindClusterLifecycleHook</c> applies nothing on
/// its own — it only iterates whatever steps were explicitly registered on that builder (none,
/// here). None of the <see cref="ArgoCdManifestSet"/> files declare a <c>namespace:</c> (they are
/// namespace-agnostic Kustomize bases — see manifests/base/config/argocd-cm.yaml), and Kubernetes
/// does not implicitly create a namespace referenced by an applied object, so this hook explicitly
/// applies a minimal <c>Namespace/argocd</c> object first, before any namespaced ConfigMap,
/// Secret, or RBAC object, so the subsequent server-side applies do not fail with
/// "namespaces \"argocd\" not found".
///
/// Registered directly by AppHost.cs alongside <c>AddKindCluster</c>; does not modify the vendored
/// Kind integration in any way.
/// </summary>
public sealed class ArgoCdStateBootstrapHook(
    ILogger<ArgoCdStateBootstrapHook> logger,
    ResourceNotificationService notifications,
    bool enableDex = false)
    : IDistributedApplicationLifecycleHook
{
    private const string ClusterResourceName = "argocd-dev";
    private const string ArgoCdNamespace = "argocd";
    private const int MaxWaitAttempts = 30;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Minimal inline Namespace manifest for <c>argocd</c>. None of the checked-in manifest files
    /// under <c>manifests/</c> declare a Namespace object suitable for this namespace-agnostic
    /// Kustomize base (the only <c>namespace.yaml</c> in the tree belongs to
    /// <c>manifests/dev-tilt</c>, a different, workload-deploying manifest tree), so this is
    /// applied from an in-memory literal via stdin rather than referencing a repo file.
    /// </summary>
    private static readonly string NamespaceManifestYaml =
        $"""
        apiVersion: v1
        kind: Namespace
        metadata:
          name: {ArgoCdNamespace}
        """;

    public async Task AfterResourcesCreatedAsync(
        DistributedApplicationModel appModel,
        CancellationToken cancellationToken = default)
    {
        var cluster = appModel.Resources
            .OfType<KindClusterResource>()
            .FirstOrDefault(r => r.Name == ClusterResourceName);

        if (cluster is null)
        {
            return;
        }

        var repoRoot = ResolveRepoRoot();
        if (repoRoot is null)
        {
            logger.LogWarning("Could not locate the repository root from '{BaseDirectory}'; skipping state bootstrap.", AppContext.BaseDirectory);
            return;
        }

        // AppHost.cs never registers a WithManifest/WithHelmChart step on this cluster builder, so
        // KindClusterLifecycleHook's own AfterResourcesCreated pass applies nothing — this hook is
        // the only thing that populates the cluster. We only need "the API server accepts kubectl"
        // here, not "all pods ready" (there are no workload pods in this architecture at all), so
        // this is a short, independent wait that avoids racing ahead of `kind create cluster`.
        for (var attempt = 1; attempt <= MaxWaitAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(cluster.KubeconfigPath))
            {
                var (probeExit, _, _) = await RunAsync(
                    "kubectl", ["get", "nodes", "--kubeconfig", cluster.KubeconfigPath], cancellationToken);
                if (probeExit == 0)
                {
                    break;
                }
            }

            await Task.Delay(RetryDelay, cancellationToken);
        }

        var allSucceeded = true;
        foreach (var step in BuildApplyPlan(enableDex))
        {
            allSucceeded &= step.Kind == ApplyStepKind.Namespace
                ? await EnsureNamespaceAsync(cluster, cancellationToken)
                : await ApplyGroupAsync(step.GroupName, step.RelativeFilePaths!, repoRoot, cluster, cancellationToken);
        }

        await notifications.PublishUpdateAsync(cluster, snapshot => snapshot with
        {
            Properties =
            [
                .. snapshot.Properties,
                new ResourcePropertySnapshot(
                    "argocd.state.status",
                    allSucceeded ? "Applied (server-side)" : "FAILED — see component logs"),
            ],
        });
    }

    /// <summary>
    /// Which kind of <c>kubectl apply --server-side --force-conflicts</c> a given <see cref="ApplyStep"/>
    /// performs: the single inline <see cref="NamespaceManifestYaml"/> literal (<see cref="EnsureNamespaceAsync"/>),
    /// or a named group of on-disk manifest files from <see cref="ArgoCdManifestSet"/> (<see cref="ApplyGroupAsync"/>).
    /// </summary>
    internal enum ApplyStepKind
    {
        Namespace,
        ManifestGroup,
    }

    /// <summary>
    /// One ordered step of the state-bootstrap plan built by <see cref="BuildApplyPlan"/>.
    /// <paramref name="RelativeFilePaths"/> is <see langword="null"/> for the <see cref="ApplyStepKind.Namespace"/>
    /// step (which has no on-disk manifest file — see <see cref="NamespaceManifestYaml"/>) and non-null for every
    /// <see cref="ApplyStepKind.ManifestGroup"/> step.
    /// </summary>
    internal readonly record struct ApplyStep(string GroupName, ApplyStepKind Kind, IReadOnlyList<string>? RelativeFilePaths = null);

    /// <summary>
    /// Builds the ordered, declarative list of apply steps <see cref="AfterResourcesCreatedAsync"/> executes
    /// against the Kind cluster. The <c>namespace</c> step is always first: every subsequent step applies
    /// namespaced objects (CRDs are cluster-scoped and would apply fine either way, but <c>config-rbac</c> and
    /// <c>dex-rbac</c> contain namespaced ConfigMaps/Secrets/Roles/RoleBindings that would fail with
    /// "namespaces \"argocd\" not found" if applied before the namespace exists — see the type-level remarks
    /// on <see cref="EnsureNamespaceAsync"/>). Extracted as a separate, side-effect-free method (rather than
    /// inlined into <see cref="AfterResourcesCreatedAsync"/>) so the exact step order — the fix for that bug —
    /// is independently unit-testable without invoking any real <c>kubectl</c> process.
    /// </summary>
    internal static IReadOnlyList<ApplyStep> BuildApplyPlan(bool enableDex)
    {
        var steps = new List<ApplyStep>
        {
            new("namespace", ApplyStepKind.Namespace),
            new("crds", ApplyStepKind.ManifestGroup, ArgoCdManifestSet.Crds),
            new("config-rbac", ApplyStepKind.ManifestGroup, ArgoCdManifestSet.ConfigAndRbac),
        };

        if (enableDex)
        {
            steps.Add(new ApplyStep("dex-rbac", ApplyStepKind.ManifestGroup, ArgoCdManifestSet.DexRbac));
        }

        return steps;
    }

    /// <summary>
    /// Applies one named group of manifest files (see <see cref="ArgoCdManifestSet"/>) with a
    /// single <c>kubectl apply --server-side --force-conflicts</c> invocation and publishes a
    /// per-group status property on the cluster resource. Returns <see langword="false"/> (and
    /// logs a warning, without throwing) if the apply fails, so the caller can decide whether to
    /// continue applying subsequent groups.
    /// </summary>
    private async Task<bool> ApplyGroupAsync(
        string groupName,
        IReadOnlyList<string> relativeFilePaths,
        DirectoryInfo repoRoot,
        KindClusterResource cluster,
        CancellationToken cancellationToken)
    {
        var missing = relativeFilePaths
            .Select(relative => Path.Combine(repoRoot.FullName, relative))
            .Where(fullPath => !File.Exists(fullPath))
            .ToList();

        if (missing.Count > 0)
        {
            logger.LogWarning(
                "Skipping manifest group '{Group}': {Count} file(s) not found: {Files}",
                groupName, missing.Count, string.Join(", ", missing));
            return false;
        }

        var arguments = new List<string> { "apply", "--server-side", "--force-conflicts" };
        foreach (var relative in relativeFilePaths)
        {
            arguments.Add("-f");
            arguments.Add(Path.Combine(repoRoot.FullName, relative));
        }
        arguments.Add("--kubeconfig");
        arguments.Add(cluster.KubeconfigPath);

        logger.LogInformation(
            "Applying Argo CD state group '{Group}' ({Count} file(s)) with --server-side --force-conflicts.",
            groupName, relativeFilePaths.Count);

        var (exitCode, _, stderr) = await RunAsync("kubectl", arguments, cancellationToken);

        if (exitCode != 0)
        {
            logger.LogWarning(
                "kubectl apply --server-side for group '{Group}' failed (exit {ExitCode}): {Stderr}",
                groupName, exitCode, stderr);
            await notifications.PublishUpdateAsync(cluster, snapshot => snapshot with
            {
                Properties = [.. snapshot.Properties, new ResourcePropertySnapshot($"argocd.state.{groupName}.status", $"FAILED: {stderr}")],
            });
            return false;
        }

        logger.LogInformation("Argo CD state group '{Group}' applied successfully.", groupName);
        await notifications.PublishUpdateAsync(cluster, snapshot => snapshot with
        {
            Properties = [.. snapshot.Properties, new ResourcePropertySnapshot($"argocd.state.{groupName}.status", "Applied (server-side)")],
        });
        return true;
    }

    /// <summary>
    /// Applies the minimal inline <see cref="NamespaceManifestYaml"/> via
    /// <c>kubectl apply --server-side --force-conflicts -f -</c> (manifest piped over stdin, since
    /// it has no file on disk), so that every subsequent namespaced apply (CRDs carry no namespace,
    /// but ConfigMaps/Secrets/Roles/RoleBindings under <c>config-rbac</c> and <c>dex-rbac</c> do)
    /// does not fail with "namespaces \"argocd\" not found". Idempotent: server-side apply of an
    /// already-existing Namespace is a no-op.
    /// </summary>
    private async Task<bool> EnsureNamespaceAsync(KindClusterResource cluster, CancellationToken cancellationToken)
    {
        logger.LogInformation("Ensuring namespace '{Namespace}' exists via --server-side apply.", ArgoCdNamespace);

        var (exitCode, _, stderr) = await RunAsync(
            "kubectl",
            ["apply", "--server-side", "--force-conflicts", "-f", "-", "--kubeconfig", cluster.KubeconfigPath],
            cancellationToken,
            stdin: NamespaceManifestYaml);

        if (exitCode != 0)
        {
            logger.LogWarning(
                "kubectl apply --server-side for namespace '{Namespace}' failed (exit {ExitCode}): {Stderr}",
                ArgoCdNamespace, exitCode, stderr);
            await notifications.PublishUpdateAsync(cluster, snapshot => snapshot with
            {
                Properties = [.. snapshot.Properties, new ResourcePropertySnapshot("argocd.state.namespace.status", $"FAILED: {stderr}")],
            });
            return false;
        }

        logger.LogInformation("Namespace '{Namespace}' applied successfully.", ArgoCdNamespace);
        await notifications.PublishUpdateAsync(cluster, snapshot => snapshot with
        {
            Properties = [.. snapshot.Properties, new ResourcePropertySnapshot("argocd.state.namespace.status", "Applied (server-side)")],
        });
        return true;
    }

    private static DirectoryInfo? ResolveRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (ArgoCdRepoRoot.IsRepoRoot(dir.FullName))
            {
                return dir;
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
