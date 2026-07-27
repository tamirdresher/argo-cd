using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

/// <summary>
/// Proves the fix for the HIGH "persistent Kind collision across worktrees/clones" finding:
/// <see cref="ArgoCdClusterName.Resolve"/> derives a deterministic, per-checkout Kind cluster
/// name instead of the previous hard-coded literal <c>argocd-dev</c> (which, combined with a
/// <c>%TEMP%</c>-rooted kubeconfig path derived from the cluster name, caused every worktree or
/// clone on a machine to silently collide on the same cluster and kubeconfig file).
/// </summary>
public sealed class ArgoCdClusterNameTests
{
    [Fact]
    public void Resolve_IsStable_ForTheSameRepoRootPath()
    {
        WithoutOverride(() =>
        {
            var first = ArgoCdClusterName.Resolve(@"C:\repos\argo-cd");
            var second = ArgoCdClusterName.Resolve(@"C:\repos\argo-cd");
            Assert.Equal(first, second);
        });
    }

    [Fact]
    public void Resolve_Differs_ForDifferentRepoRootPaths()
    {
        WithoutOverride(() =>
        {
            var worktreeOne = ArgoCdClusterName.Resolve(@"C:\repos\argo-cd-worktree-one");
            var worktreeTwo = ArgoCdClusterName.Resolve(@"C:\repos\argo-cd-worktree-two");
            Assert.NotEqual(worktreeOne, worktreeTwo);
        });
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveAndTrailingSeparatorInsensitive_OnTheSamePath()
    {
        WithoutOverride(() =>
        {
            var withTrailingSeparator = ArgoCdClusterName.Resolve(@"C:\repos\argo-cd\");
            var withoutTrailingSeparator = ArgoCdClusterName.Resolve(@"C:\repos\argo-cd");
            Assert.Equal(withoutTrailingSeparator, withTrailingSeparator);

            if (OperatingSystem.IsWindows())
            {
                var differentCasing = ArgoCdClusterName.Resolve(@"C:\REPOS\Argo-Cd");
                Assert.Equal(withoutTrailingSeparator, differentCasing);
            }
        });
    }

    [Fact]
    public void Resolve_ProducesAValidKindClusterName_MatchingLengthAndCharacterConstraints()
    {
        WithoutOverride(() =>
        {
            var name = ArgoCdClusterName.Resolve(@"C:\repos\argo-cd");

            Assert.True(name.Length <= 63, $"'{name}' exceeds the 63-character Kind/Kubernetes name limit.");
            Assert.Matches("^[a-z0-9][a-z0-9-]*$", name);
            Assert.StartsWith("argocd-dev-", name);
        });
    }

    [Fact]
    public void Resolve_UsesTheOverride_WhenEnvironmentVariableIsSet()
    {
        WithOverride("my-shared-cluster", () =>
        {
            var resolved = ArgoCdClusterName.Resolve(@"C:\repos\argo-cd");
            Assert.Equal("my-shared-cluster", resolved);
        });
    }

    [Fact]
    public void Resolve_TrimsWhitespace_AroundAValidOverride()
    {
        WithOverride("  my-shared-cluster  ", () =>
        {
            var resolved = ArgoCdClusterName.Resolve(@"C:\repos\argo-cd");
            Assert.Equal("my-shared-cluster", resolved);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_ThrowsActionableError_WhenOverrideIsEmptyOrWhitespace(string invalidOverride)
    {
        WithOverride(invalidOverride, () =>
        {
            var ex = Assert.Throws<ArgumentException>(() => ArgoCdClusterName.Resolve(@"C:\repos\argo-cd"));
            Assert.Contains(ArgoCdClusterName.OverrideEnvironmentVariableName, ex.Message);
            Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("Has_Underscore")]
    [InlineData("UPPERCASE")]
    [InlineData("-starts-with-hyphen")]
    [InlineData("has a space")]
    [InlineData("has.dot")]
    public void Resolve_ThrowsActionableError_WhenOverrideHasInvalidCharacters(string invalidOverride)
    {
        WithOverride(invalidOverride, () =>
        {
            var ex = Assert.Throws<ArgumentException>(() => ArgoCdClusterName.Resolve(@"C:\repos\argo-cd"));
            Assert.Contains(ArgoCdClusterName.OverrideEnvironmentVariableName, ex.Message);
            Assert.Contains(invalidOverride, ex.Message);
        });
    }

    [Fact]
    public void Resolve_ThrowsActionableError_WhenOverrideExceedsMaxLength()
    {
        var tooLong = new string('a', 64);
        WithOverride(tooLong, () =>
        {
            var ex = Assert.Throws<ArgumentException>(() => ArgoCdClusterName.Resolve(@"C:\repos\argo-cd"));
            Assert.Contains(ArgoCdClusterName.OverrideEnvironmentVariableName, ex.Message);
            Assert.Contains("63", ex.Message);
        });
    }

    [Fact]
    public void Resolve_AcceptsOverride_AtExactlyMaxLength()
    {
        var exactlyMaxLength = "a" + new string('b', 62);
        Assert.Equal(63, exactlyMaxLength.Length);

        WithOverride(exactlyMaxLength, () =>
        {
            var resolved = ArgoCdClusterName.Resolve(@"C:\repos\argo-cd");
            Assert.Equal(exactlyMaxLength, resolved);
        });
    }

    private static void WithoutOverride(Action test)
    {
        var previous = Environment.GetEnvironmentVariable(ArgoCdClusterName.OverrideEnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(ArgoCdClusterName.OverrideEnvironmentVariableName, null);
            test();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArgoCdClusterName.OverrideEnvironmentVariableName, previous);
        }
    }

    private static void WithOverride(string value, Action test)
    {
        var previous = Environment.GetEnvironmentVariable(ArgoCdClusterName.OverrideEnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(ArgoCdClusterName.OverrideEnvironmentVariableName, value);
            test();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArgoCdClusterName.OverrideEnvironmentVariableName, previous);
        }
    }
}
