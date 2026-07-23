using System.Diagnostics;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Low-level process execution helper shared by the state-only bootstrap hook
/// (<see cref="ArgoCdStateBootstrapHook"/>) and the optional in-cluster repo-server override
/// command. Historically this class also rendered the full namespaced Argo CD install manifest
/// via <c>kubectl kustomize</c> for deployment as workload Pods inside Kind; that mechanism has
/// been removed now that all core components run as native host processes and Kind holds only
/// state (CRDs/RBAC/ConfigMaps/Secrets) — see <c>ArgoCdManifestSet.cs</c> for the explicit file
/// list that replaced it.
/// </summary>
public static class ArgoCdManifestRenderer
{
    /// <summary>
    /// Runs a process to completion and captures stdout/stderr without risking the classic
    /// redirected-pipe deadlock: both streams are read asynchronously starting immediately after
    /// <c>Start()</c>, concurrently with waiting for exit, instead of reading one stream fully
    /// before the other (which can hang forever if the child fills the unread pipe's OS buffer
    /// while blocked writing to it). On timeout or external cancellation, the entire process tree
    /// is killed (best-effort) before the failure is surfaced.
    /// </summary>
    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunCaptureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        process.Start();

        // Begin both reads immediately (before awaiting exit) so neither pipe's OS buffer can
        // fill up and block the child while we're only draining the other one.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked token was cancelled but the caller's own token wasn't — this was our
            // internal timeout firing, not an external cancellation request.
            KillProcessTree(process);
            throw new InvalidOperationException(
                $"'{fileName} {string.Join(' ', arguments)}' timed out after {timeout} and was killed.");
        }
        catch (OperationCanceledException)
        {
            // Caller explicitly cancelled; kill the tree and let OperationCanceledException
            // propagate unchanged so callers can distinguish this from other failures.
            KillProcessTree(process);
            throw;
        }
    }

    /// <summary>
    /// Best-effort kill of the process and any children it spawned. Swallows exceptions because
    /// the process may have already exited between the caller noticing a timeout/cancellation and
    /// this call running (a benign race, not an error worth surfacing).
    /// </summary>
    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort: the process may have exited concurrently, or the OS may refuse the
            // kill for a process that's already tearing down. Either way, there's nothing more
            // useful this helper can do.
        }
    }
}
