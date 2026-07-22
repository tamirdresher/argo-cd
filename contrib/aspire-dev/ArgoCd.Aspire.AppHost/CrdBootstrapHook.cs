using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Applies the repository's own <c>manifests/crds</c> (Application/ApplicationSet/AppProject
/// CustomResourceDefinitions) to the Kind cluster with <c>kubectl apply --server-side
/// --force-conflicts</c>, exactly as documented in
/// docs/developer-guide/running-locally.md and manifests/README.md.
///
/// This exists as a small, separate lifecycle hook (rather than being folded into the
/// vendored <c>ArgoCd.Aspire.Hosting.Kind</c> integration) because that vendored code's
/// <c>WithManifest(...)</c> always does a plain client-side <c>kubectl apply -f</c>, which
/// fails on these specific CRDs: their embedded OpenAPI schemas make the
/// <c>kubectl.kubernetes.io/last-applied-configuration</c> annotation exceed the
/// 262144-byte etcd/API-server annotation limit. Server-side apply doesn't hit that limit
/// because it doesn't need to store the full previous-configuration annotation.
///
/// Registered directly by AppHost.cs alongside <c>AddKindCluster</c>; does not modify the
/// vendored Kind integration in any way.
/// </summary>
public sealed class CrdBootstrapHook(ILogger<CrdBootstrapHook> logger, ResourceNotificationService notifications)
    : IDistributedApplicationLifecycleHook
{
    private const string ClusterResourceName = "argocd-dev";
    private const int MaxWaitAttempts = 30;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

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

        var crdsPath = ResolveCrdsPath();
        if (crdsPath is null)
        {
            logger.LogWarning("Could not locate manifests/crds; skipping CRD bootstrap.");
            return;
        }

        // The vendored KindClusterLifecycleHook is already waiting for the cluster and
        // applying its own manifests concurrently (both hooks run for AfterResourcesCreated).
        // We only need "the API server accepts kubectl" here, not "all pods ready" — a much
        // shorter, independent wait so this hook doesn't race ahead of `kind create cluster`.
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

        logger.LogInformation("Applying Argo CD CRDs from '{CrdsPath}' with --server-side --force-conflicts.", crdsPath);

        var (exitCode, _, stderr) = await RunAsync(
            "kubectl",
            ["apply", "--server-side", "--force-conflicts", "-f", crdsPath, "--kubeconfig", cluster.KubeconfigPath],
            cancellationToken);

        if (exitCode != 0)
        {
            logger.LogWarning("kubectl apply --server-side for CRDs failed (exit {ExitCode}): {Stderr}", exitCode, stderr);
            await notifications.PublishUpdateAsync(cluster, snapshot => snapshot with
            {
                Properties = [.. snapshot.Properties, new ResourcePropertySnapshot("argocd.crds.status", $"FAILED: {stderr}")],
            });
            return;
        }

        logger.LogInformation("Argo CD CRDs applied successfully.");
        await notifications.PublishUpdateAsync(cluster, snapshot => snapshot with
        {
            Properties = [.. snapshot.Properties, new ResourcePropertySnapshot("argocd.crds.status", "Applied (server-side)")],
        });
    }

    private static string? ResolveCrdsPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (ArgoCdRepoRoot.IsRepoRoot(dir.FullName))
            {
                var crdsPath = Path.Combine(dir.FullName, "manifests", "crds");
                return Directory.Exists(crdsPath) ? crdsPath : null;
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
