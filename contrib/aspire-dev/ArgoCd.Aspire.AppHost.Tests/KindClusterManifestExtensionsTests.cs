using Aspire.Hosting;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

public sealed class KindClusterManifestExtensionsTests
{
    [Fact]
    public void CreateKubectlApplyArguments_UsesServerSideApplyWithForceConflicts()
    {
        var args = KindClusterManifestExtensions.CreateKubectlApplyArguments(
            @"C:\repo\generated\argocd-state.yaml",
            @"C:\repo\.kube\config");

        Assert.Equal(
            [
                "apply",
                "--server-side",
                "--force-conflicts",
                "-f",
                @"C:\repo\generated\argocd-state.yaml",
                "--kubeconfig",
                @"C:\repo\.kube\config",
            ],
            args);
    }
}
