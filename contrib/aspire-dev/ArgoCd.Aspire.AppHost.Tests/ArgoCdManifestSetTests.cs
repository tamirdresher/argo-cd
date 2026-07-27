using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

public class ArgoCdManifestSetTests
{
    [Fact]
    public void EnumerateStateOnlyManifests_WithoutDex_ExcludesDexRbac()
    {
        var manifests = ArgoCdManifestSet.EnumerateStateOnlyManifests(enableDex: false);

        Assert.Contains(ArgoCdManifestSet.Crds[0], manifests);
        Assert.Contains(ArgoCdManifestSet.ConfigAndRbac[0], manifests);
        Assert.DoesNotContain(ArgoCdManifestSet.DexRbac[0], manifests);
    }

    [Fact]
    public void EnumerateStateOnlyManifests_WithDex_IncludesDexRbacLast()
    {
        var manifests = ArgoCdManifestSet.EnumerateStateOnlyManifests(enableDex: true);

        Assert.Equal(ArgoCdManifestSet.DexRbac[^1], manifests[^1]);
    }

    [Fact]
    public void RenderStateOnlyManifest_LocatesInTreeArgoCdManifests()
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "generated-tests", $"argocd-state-{Guid.NewGuid():N}.yaml");
        try
        {
            var renderedPath = ArgoCdManifestSet.RenderStateOnlyManifest(outputPath, enableDex: false);

            Assert.Equal(outputPath, renderedPath);
            var rendered = File.ReadAllText(renderedPath);
            Assert.Contains("kind: Namespace", rendered);
            Assert.Contains("applications.argoproj.io", rendered);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public void EnsureNamespace_AddsArgocdNamespaceToNamespacedKinds()
    {
        var yaml = """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: argocd-cm
            data:
              url: http://localhost:8080
            """;

        var rendered = ArgoCdManifestSet.EnsureNamespace(yaml, "argocd");

        Assert.Contains("metadata:", rendered);
        Assert.Contains("  namespace: argocd", rendered);
        Assert.Contains("  name: argocd-cm", rendered);
    }

    [Fact]
    public void EnsureNamespace_LeavesClusterScopedKindsUnchanged()
    {
        var yaml = """
            apiVersion: apiextensions.k8s.io/v1
            kind: CustomResourceDefinition
            metadata:
              name: applications.argoproj.io
            """;

        var rendered = ArgoCdManifestSet.EnsureNamespace(yaml, "argocd");

        Assert.DoesNotContain("namespace: argocd", rendered);
    }
}
