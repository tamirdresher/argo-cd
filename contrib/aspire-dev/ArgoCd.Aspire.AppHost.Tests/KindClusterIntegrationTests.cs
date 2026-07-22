using System.Diagnostics;
using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHostTests;

/// <summary>
/// Opt-in, serial integration test that exercises the real Kind cluster + baseline Argo CD
/// install + repo-server override flow end to end. Requires Docker, kind, kubectl, helm, and
/// go on PATH, and takes several minutes.
///
/// Disabled by default. Enable explicitly with:
///
///   $env:ARGOCD_ASPIRE_KIND_INTEGRATION = "1"
///   dotnet test contrib/aspire-dev/ArgoCd.Aspire.AppHost.Tests
///
/// Not run as part of `make test` / `make test-local` — this is intentionally outside the
/// Go test suite and is only discovered by `dotnet test` inside contrib/aspire-dev.
/// </summary>
[Collection("KindIntegration")] // serial: only one Kind cluster of this name at a time
public class KindClusterIntegrationTests
{
    private static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable("ARGOCD_ASPIRE_KIND_INTEGRATION") == "1";

    [Fact]
    public async Task Cluster_CanBeCreatedAndDeleted_WithBaselineArgoCdManifests()
    {
        if (!IntegrationEnabled)
        {
            return; // Opt-in only; see class remarks.
        }

        var repoRoot = FindRepoRootFromTestBinary();
        const string clusterName = "argocd-dev-test";

        await RunOrThrow("kind", $"delete cluster --name {clusterName}", repoRoot, allowFailure: true);

        try
        {
            var kubeconfig = Path.Combine(Path.GetTempPath(), $"kind-{clusterName}-kubeconfig-test.yaml");
            await RunOrThrow("kind", $"create cluster --name {clusterName} --kubeconfig \"{kubeconfig}\"", repoRoot);

            await RunOrThrow("kubectl", $"--kubeconfig \"{kubeconfig}\" get nodes", repoRoot);

            var overlayDir = Path.Combine(repoRoot, "contrib", "aspire-dev", "ArgoCd.Aspire.AppHost", "manifests", "argocd-namespaced");
            var generated = Path.Combine(Path.GetTempPath(), "argocd-aspire-kind-tests", "install-integration.yaml");
            await ArgoCdManifestRenderer.RenderNamespacedInstallManifestAsync(overlayDir, generated);

            await RunOrThrow("kubectl", $"--kubeconfig \"{kubeconfig}\" apply -f \"{Path.Combine(repoRoot, "contrib", "aspire-dev", "ArgoCd.Aspire.AppHost", "manifests", "namespace.yaml")}\"", repoRoot);

            // Server-side apply for CRDs — see CrdBootstrapHook.cs for why plain client-side
            // apply fails on these specific CRDs (last-applied-configuration annotation limit).
            var crdsPath = Path.Combine(repoRoot, "manifests", "crds");
            await RunOrThrow("kubectl", $"--kubeconfig \"{kubeconfig}\" apply --server-side --force-conflicts -f \"{crdsPath}\"", repoRoot);

            await RunOrThrow("kubectl", $"--kubeconfig \"{kubeconfig}\" apply -f \"{generated}\"", repoRoot);

            await RunOrThrow(
                "kubectl",
                $"--kubeconfig \"{kubeconfig}\" -n argocd rollout status deployment/argocd-repo-server --timeout=300s",
                repoRoot);

            var (exitCode, stdout, _) = await Run(
                "kubectl", $"--kubeconfig \"{kubeconfig}\" -n argocd get pods -o wide", repoRoot);
            Assert.Equal(0, exitCode);
            Assert.Contains("argocd-repo-server", stdout, StringComparison.Ordinal);
        }
        finally
        {
            await RunOrThrow("kind", $"delete cluster --name {clusterName}", repoRoot, allowFailure: true);
        }
    }

    private static string FindRepoRootFromTestBinary()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (ArgoCdRepoRoot.IsRepoRoot(dir.FullName))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the Argo CD repo root above '{AppContext.BaseDirectory}'.");
    }

    private static async Task RunOrThrow(string fileName, string arguments, string workingDirectory, bool allowFailure = false)
    {
        var (exitCode, stdout, stderr) = await Run(fileName, arguments, workingDirectory);
        if (exitCode != 0 && !allowFailure)
        {
            throw new InvalidOperationException(
                $"'{fileName} {arguments}' failed (exit {exitCode}).\nstdout: {stdout}\nstderr: {stderr}");
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> Run(
        string fileName, string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
