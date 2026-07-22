using System.Diagnostics;
using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHostTests;

/// <summary>
/// Renders the real manifests/argocd-namespaced overlay via `kubectl kustomize`. This is a
/// pure client-side YAML render — no Docker, no live cluster — so it stays in the fast test
/// suite. Skips automatically when kubectl isn't on PATH (e.g. some CI images) rather than
/// failing the whole run.
/// </summary>
public class ArgoCdManifestRendererTests
{
    private static bool KubectlAvailable => TryRun("kubectl", "version --client");

    [Fact]
    public async Task RenderNamespacedInstallManifest_ProducesNamespacedResources()
    {
        if (!KubectlAvailable)
        {
            // Environment-gated: kubectl isn't required to build/test this project, only to
            // exercise this specific pure client-side render. Skip quietly rather than failing
            // CI environments that don't have kubectl on PATH.
            return;
        }
        var repoRoot = FindRepoRootFromTestBinary();
        var overlayDir = Path.Combine(repoRoot, "contrib", "aspire-dev", "ArgoCd.Aspire.AppHost", "manifests", "argocd-namespaced");
        var outputPath = Path.Combine(Path.GetTempPath(), "argocd-aspire-kind-tests", $"install-{Guid.NewGuid():N}.yaml");

        try
        {
            var result = await ArgoCdManifestRenderer.RenderNamespacedInstallManifestAsync(overlayDir, outputPath);

            Assert.True(File.Exists(result));
            var content = File.ReadAllText(result);

            // CRDs are intentionally excluded from this overlay (applied separately via
            // server-side apply — see CrdBootstrapHook.cs and the kustomization.yaml comment).
            Assert.DoesNotContain("kind: CustomResourceDefinition", content, StringComparison.Ordinal);
            Assert.Contains("kind: Deployment", content, StringComparison.Ordinal);
            Assert.Contains("kind: ClusterRole", content, StringComparison.Ordinal);
            Assert.Contains("name: argocd-repo-server", content, StringComparison.Ordinal);
            Assert.Contains("namespace: argocd", content, StringComparison.Ordinal);

            // Every namespace-scoped object kustomize emits should be stamped "argocd" — spot
            // check there's no leftover unqualified Deployment (which would land in "default").
            Assert.DoesNotContain("namespace: default", content, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
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

    private static bool TryRun(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            process?.WaitForExit(5000);
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }
}
