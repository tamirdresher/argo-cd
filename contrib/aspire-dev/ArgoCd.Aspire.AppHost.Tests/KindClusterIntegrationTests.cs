using System.Diagnostics;
using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHostTests;

/// <summary>
/// Opt-in, live-cluster integration test validating the "Kind holds state only" invariant of the
/// v2 Aspire dev loop: the manifests this AppHost applies to Kind (CRDs, namespace, ConfigMaps,
/// Secrets, RBAC) never create any Deployment or Pod, because every Argo CD component runs as a
/// native host-process Aspire resource instead.
///
/// Disabled by default because it requires a working Docker daemon, <c>kind</c>, and
/// <c>kubectl</c> on PATH, and takes roughly a minute to create/destroy a real cluster. Set
/// <c>ARGOCD_ASPIRE_KIND_INTEGRATION=1</c> to opt in locally or in CI. Not part of the default
/// <c>dotnet test</c>/<c>make test</c> loop.
/// </summary>
[Collection("KindIntegration")]
public sealed class KindClusterIntegrationTests
{
    private const string ClusterName = "argocd-dev-test";

    [Fact]
    public async Task Cluster_HoldsStateOnly_NoWorkloadsCreated()
    {
        if (Environment.GetEnvironmentVariable("ARGOCD_ASPIRE_KIND_INTEGRATION") != "1")
        {
            // Skipped by default: requires Docker + kind + kubectl and takes ~1 minute.
            return;
        }

        var repoRoot = ArgoCdRepoRoot.Resolve(AppContext.BaseDirectory);
        var kubeconfigPath = Path.Combine(Path.GetTempPath(), $"argocd-aspire-test-kubeconfig-{Guid.NewGuid():N}.yaml");

        try
        {
            // Best-effort cleanup of any leftover cluster from a prior aborted run.
            await Run("kind", $"delete cluster --name {ClusterName}");

            await RunOrThrow("kind", $"create cluster --name {ClusterName} --kubeconfig \"{kubeconfigPath}\"");

            await RunOrThrow("kubectl", $"create namespace argocd --kubeconfig \"{kubeconfigPath}\"");

            foreach (var relativePath in ArgoCdManifestSet.Crds)
            {
                var fullPath = Path.Combine(repoRoot, relativePath);
                await RunOrThrow(
                    "kubectl",
                    $"apply --server-side --force-conflicts -f \"{fullPath}\" --kubeconfig \"{kubeconfigPath}\"");
            }

            foreach (var relativePath in ArgoCdManifestSet.ConfigAndRbac)
            {
                var fullPath = Path.Combine(repoRoot, relativePath);
                await RunOrThrow(
                    "kubectl",
                    $"apply --server-side --force-conflicts -n argocd -f \"{fullPath}\" --kubeconfig \"{kubeconfigPath}\"");
            }

            var (_, namespaces, _) = await RunOrThrow("kubectl", $"get namespace argocd -o name --kubeconfig \"{kubeconfigPath}\"");
            Assert.Contains("namespace/argocd", namespaces);

            var (_, crds, _) = await RunOrThrow("kubectl", $"get crd -o name --kubeconfig \"{kubeconfigPath}\"");
            Assert.Contains("applications.argoproj.io", crds);
            Assert.Contains("applicationsets.argoproj.io", crds);
            Assert.Contains("appprojects.argoproj.io", crds);

            var (_, configMaps, _) = await RunOrThrow("kubectl", $"get configmap argocd-cm -n argocd -o name --kubeconfig \"{kubeconfigPath}\"");
            Assert.Contains("configmap/argocd-cm", configMaps);

            // The state-only invariant: no Deployments and no Pods should ever exist in the
            // argocd namespace, because this dev loop never applies workload manifests -- every
            // Argo CD component is launched as a native host-process Aspire resource instead.
            var (_, deployments, _) = await RunOrThrow("kubectl", $"get deployments -n argocd -o name --kubeconfig \"{kubeconfigPath}\"");
            Assert.Empty(deployments.Trim());

            var (_, pods, _) = await RunOrThrow("kubectl", $"get pods -n argocd -o name --kubeconfig \"{kubeconfigPath}\"");
            Assert.Empty(pods.Trim());
        }
        finally
        {
            await Run("kind", $"delete cluster --name {ClusterName}");
            if (File.Exists(kubeconfigPath))
            {
                File.Delete(kubeconfigPath);
            }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> Run(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOrThrow(string fileName, string arguments)
    {
        var result = await Run(fileName, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{fileName} {arguments}' exited with code {result.ExitCode}.\nstdout: {result.Stdout}\nstderr: {result.Stderr}");
        }

        return result;
    }
}
