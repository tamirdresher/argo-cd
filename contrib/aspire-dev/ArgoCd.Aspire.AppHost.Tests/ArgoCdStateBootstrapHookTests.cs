using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

/// <summary>
/// Regression tests for the namespace-creation-order bug fix in
/// <see cref="ArgoCdStateBootstrapHook"/>: applying any of the namespaced ConfigMaps/Secrets/RBAC
/// objects in <see cref="ArgoCdManifestSet.ConfigAndRbac"/> (or, when Dex is enabled,
/// <see cref="ArgoCdManifestSet.DexRbac"/>) before the <c>argocd</c> Namespace object exists fails
/// server-side apply with "namespaces \"argocd\" not found". These tests assert the fix — the
/// <c>namespace</c> step is always first in <see cref="ArgoCdStateBootstrapHook.BuildApplyPlan"/>'s
/// returned plan — as a fast, pure-data check with zero process execution, no real
/// <c>kubectl</c>/cluster, and no need to construct <see cref="ArgoCdStateBootstrapHook"/> itself or
/// any Aspire hosting built-in (e.g. <c>ResourceNotificationService</c>).
/// </summary>
public class ArgoCdStateBootstrapHookTests
{
    [Fact]
    public void BuildApplyPlan_WithoutDex_ReturnsExactlyThreeStepsInOrder()
    {
        var plan = ArgoCdStateBootstrapHook.BuildApplyPlan(enableDex: false);

        Assert.Equal(3, plan.Count);
        Assert.Equal(ArgoCdStateBootstrapHook.ApplyStepKind.Namespace, plan[0].Kind);
        Assert.Equal("namespace", plan[0].GroupName);

        Assert.Equal(ArgoCdStateBootstrapHook.ApplyStepKind.ManifestGroup, plan[1].Kind);
        Assert.Equal("crds", plan[1].GroupName);

        Assert.Equal(ArgoCdStateBootstrapHook.ApplyStepKind.ManifestGroup, plan[2].Kind);
        Assert.Equal("config-rbac", plan[2].GroupName);
    }

    [Fact]
    public void BuildApplyPlan_WithDex_ReturnsExactlyFourStepsEndingWithDexRbac()
    {
        var plan = ArgoCdStateBootstrapHook.BuildApplyPlan(enableDex: true);

        Assert.Equal(4, plan.Count);
        Assert.Equal(ArgoCdStateBootstrapHook.ApplyStepKind.ManifestGroup, plan[3].Kind);
        Assert.Equal("dex-rbac", plan[3].GroupName);
        Assert.Same(ArgoCdManifestSet.DexRbac, plan[3].RelativeFilePaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildApplyPlan_NamespaceStepIsAlwaysFirst_RegardlessOfDex(bool enableDex)
    {
        var plan = ArgoCdStateBootstrapHook.BuildApplyPlan(enableDex);

        Assert.NotEmpty(plan);
        Assert.Equal(ArgoCdStateBootstrapHook.ApplyStepKind.Namespace, plan[0].Kind);
    }

    [Fact]
    public void BuildApplyPlan_NamespaceStep_HasNoManifestFilePaths()
    {
        var plan = ArgoCdStateBootstrapHook.BuildApplyPlan(enableDex: false);

        Assert.Null(plan[0].RelativeFilePaths);
    }

    [Fact]
    public void BuildApplyPlan_ManifestGroupSteps_ReferenceExactArgoCdManifestSetLists()
    {
        var plan = ArgoCdStateBootstrapHook.BuildApplyPlan(enableDex: true);

        var crdsStep = Assert.Single(plan, s => s.GroupName == "crds");
        Assert.Same(ArgoCdManifestSet.Crds, crdsStep.RelativeFilePaths);

        var configRbacStep = Assert.Single(plan, s => s.GroupName == "config-rbac");
        Assert.Same(ArgoCdManifestSet.ConfigAndRbac, configRbacStep.RelativeFilePaths);

        var dexRbacStep = Assert.Single(plan, s => s.GroupName == "dex-rbac");
        Assert.Same(ArgoCdManifestSet.DexRbac, dexRbacStep.RelativeFilePaths);
    }

    [Fact]
    public void BuildApplyPlan_WithoutDex_NeverIncludesDexRbacStep()
    {
        var plan = ArgoCdStateBootstrapHook.BuildApplyPlan(enableDex: false);

        Assert.DoesNotContain(plan, s => s.GroupName == "dex-rbac");
    }
}
