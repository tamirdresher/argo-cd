using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHostTests;

/// <summary>
/// Exercises <c>ArgoCdManifestRenderer.RunCaptureAsync</c> directly (it's <c>internal</c>,
/// reachable here because this test project compiles ArgoCdManifestRenderer.cs directly into
/// its own assembly — see the .csproj &lt;Compile Include&gt; items). These tests don't need
/// kubectl or Docker: they drive the runner with small, always-available OS commands so the
/// no-deadlock / timeout-kills-the-process-tree behavior can be verified in the fast test suite.
/// </summary>
public class ArgoCdManifestRendererProcessRunnerTests
{
    [Fact]
    public async Task RunCaptureAsync_CapturesBothStdoutAndStderr_WithoutDeadlock()
    {
        var (fileName, args) = EchoBothStreamsCommand();

        var (exitCode, stdout, stderr) = await ArgoCdManifestRenderer.RunCaptureAsync(
            fileName,
            args,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("stdout-line", stdout, StringComparison.Ordinal);
        Assert.Contains("stderr-line", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCaptureAsync_LargeInterleavedOutputOnBothStreams_DoesNotDeadlock()
    {
        // Writes well past a typical OS pipe buffer (usually 64KB) on *both* stdout and stderr.
        // Before the fix, draining stdout fully before starting to read stderr (or vice versa)
        // could hang forever here once the unread pipe's buffer filled up. Bounded by a timeout
        // so a regression fails the test instead of hanging the run.
        var (fileName, args) = LargeDualStreamOutputCommand(lines: 20_000);

        var (exitCode, stdout, stderr) = await ArgoCdManifestRenderer.RunCaptureAsync(
            fileName,
            args,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(stdout.Length > 64 * 1024, $"Expected large stdout, got {stdout.Length} bytes.");
        Assert.True(stderr.Length > 64 * 1024, $"Expected large stderr, got {stderr.Length} bytes.");
    }

    [Fact]
    public async Task RunCaptureAsync_TimesOutAndKillsProcess()
    {
        var (fileName, args) = SleepCommand(seconds: 30);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ArgoCdManifestRenderer.RunCaptureAsync(
                fileName,
                args,
                TimeSpan.FromMilliseconds(300),
                CancellationToken.None));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunCaptureAsync_ExternalCancellation_ThrowsOperationCanceledAndKillsProcess()
    {
        var (fileName, args) = SleepCommand(seconds: 30);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArgoCdManifestRenderer.RunCaptureAsync(
                fileName,
                args,
                TimeSpan.FromSeconds(30),
                cts.Token));
    }

    private static (string FileName, string[] Args) EchoBothStreamsCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            return ("cmd.exe", ["/c", "echo stdout-line & echo stderr-line 1>&2"]);
        }

        return ("/bin/sh", ["-c", "echo stdout-line; echo stderr-line 1>&2"]);
    }

    private static (string FileName, string[] Args) LargeDualStreamOutputCommand(int lines)
    {
        if (OperatingSystem.IsWindows())
        {
            return ("cmd.exe", ["/c", $"for /L %i in (1,1,{lines}) do (echo stdout-line-%i & echo stderr-line-%i 1>&2)"]);
        }

        return ("/bin/sh", ["-c", $"i=0; while [ $i -lt {lines} ]; do echo stdout-line-$i; echo stderr-line-$i 1>&2; i=$((i+1)); done"]);
    }

    private static (string FileName, string[] Args) SleepCommand(int seconds)
    {
        if (OperatingSystem.IsWindows())
        {
            // `ping` with a loopback target is a reliable ~1-second-per-count delay that doesn't
            // depend on console/stdin availability the way `timeout.exe` does under redirection.
            return ("ping.exe", ["-n", (seconds + 1).ToString(), "127.0.0.1"]);
        }

        return ("/bin/sleep", [seconds.ToString()]);
    }
}
