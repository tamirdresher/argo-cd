using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

/// <summary>
/// <see cref="ArgoCdPrerequisites.ValidateOrThrow"/> itself shells out to hard-coded external
/// tool names (docker, kind, kubectl, go, node, corepack, pnpm) with no injectable seam, so
/// exercising it directly in a unit test would be either non-deterministic (its result depends
/// on what happens to be installed on the machine running the tests) or would require mocking
/// process execution, which is out of scope for this targeted change.
///
/// Instead, these tests exercise <see cref="ArgoCdPrerequisites.TryRun"/> -- the internal,
/// reusable "run a command and check its exit code" primitive that every prerequisite check is
/// built on -- using commands that are guaranteed to be available (dotnet, since these tests are
/// themselves running under `dotnet test`) or guaranteed to be absent (an invalid command name)
/// so the assertions are deterministic on every machine and CI runner.
/// </summary>
public class ArgoCdPrerequisitesTests
{
    [Fact]
    public void TryRun_ReturnsTrue_ForKnownWorkingCommand()
    {
        // dotnet must be on PATH for `dotnet test` to have invoked this test at all.
        var result = ArgoCdPrerequisites.TryRun("dotnet", ["--version"]);

        Assert.True(result);
    }

    [Fact]
    public void TryRun_ReturnsFalse_ForNonExistentCommand()
    {
        var result = ArgoCdPrerequisites.TryRun(
            "argocd-aspire-dev-command-that-definitely-does-not-exist-xyz", []);

        Assert.False(result);
    }

    [Fact]
    public void TryRun_ReturnsFalse_WhenCommandExitsNonZero()
    {
        // dotnet with an unrecognized subcommand exits non-zero, exercising the ExitCode == 0
        // branch (as opposed to the "process failed to start" branch above).
        var result = ArgoCdPrerequisites.TryRun(
            "dotnet", ["this-is-not-a-real-dotnet-subcommand"]);

        Assert.False(result);
    }

    [Fact]
    public void ValidateOrThrow_ExceptionMessage_IsActionableWhenThrown()
    {
        // We cannot force ValidateOrThrow to fail deterministically (it depends on the host
        // machine's installed tools), but if it *does* throw -- e.g. on a CI runner missing
        // kind/kubectl -- the exception message must be a single combined, actionable report
        // rather than an opaque failure. This guards the message-shaping contract without
        // asserting on which specific tools are present.
        try
        {
            ArgoCdPrerequisites.ValidateOrThrow();
        }
        catch (InvalidOperationException ex)
        {
            Assert.Contains("prerequisites for the Argo CD Aspire dev loop are missing", ex.Message);
            Assert.Contains("- ", ex.Message);
        }
    }
}
