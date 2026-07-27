using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHostTests;

/// <summary>Fast, no-Docker tests for the repo-server override image tag computation.</summary>
public class RepoServerImageTagTests
{
    [Fact]
    public void ComputeTag_IncludesCommitDirtySuffixAndTimestamp()
    {
        var utcNow = new DateTimeOffset(2026, 7, 22, 19, 32, 1, TimeSpan.Zero);

        var tag = RepoServerImageTag.ComputeTag("7ca0120", dirty: true, utcNow);

        Assert.Equal("7ca0120-dirty-20260722T193201Z", tag);
    }

    [Fact]
    public void ComputeTag_UsesCleanSuffix_WhenNotDirty()
    {
        var utcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var tag = RepoServerImageTag.ComputeTag("abcdef1", dirty: false, utcNow);

        Assert.Equal("abcdef1-clean-20260101T000000Z", tag);
    }

    [Fact]
    public void ComputeTag_Throws_ForEmptyCommit()
    {
        Assert.Throws<ArgumentException>(() =>
            RepoServerImageTag.ComputeTag(string.Empty, dirty: false, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("abc123")]
    [InlineData("7ca0120-dirty-20260722T193201Z")]
    public void FullImageReference_PrependsImageName(string tag)
    {
        var reference = RepoServerImageTag.FullImageReference(tag);

        Assert.StartsWith(RepoServerImageTag.ImageName + ":", reference, StringComparison.Ordinal);
        Assert.EndsWith(tag, reference, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeTag_ProducesDistinctTags_ForDifferentTimestamps()
    {
        var first = RepoServerImageTag.ComputeTag("7ca0120", dirty: true, DateTimeOffset.UtcNow);
        var second = RepoServerImageTag.ComputeTag("7ca0120", dirty: true, DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BuildLdFlags_EmbedsFullShaVerbatim_NotShortSha()
    {
        const string fullSha = "7ca0120abcdef1234567890abcdef1234567890";

        var ldflags = RepoServerOverrideCommands.BuildLdFlags(fullSha, dirty: false);

        Assert.Contains($"common.gitCommit={fullSha}", ldflags, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLdFlags_EmbedsGitTreeState_MatchingDirtyFlag()
    {
        var cleanFlags = RepoServerOverrideCommands.BuildLdFlags("deadbeef", dirty: false);
        var dirtyFlags = RepoServerOverrideCommands.BuildLdFlags("deadbeef", dirty: true);

        Assert.Contains("common.gitTreeState=clean", cleanFlags, StringComparison.Ordinal);
        Assert.Contains("common.gitTreeState=dirty", dirtyFlags, StringComparison.Ordinal);
    }
}
