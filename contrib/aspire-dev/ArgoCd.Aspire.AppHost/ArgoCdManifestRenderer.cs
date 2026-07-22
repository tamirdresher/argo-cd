using System.Diagnostics;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Renders the namespaced baseline Argo CD install manifest by invoking <c>kubectl kustomize</c>
/// against the <c>manifests/argocd-namespaced</c> overlay (see that directory's kustomization.yaml
/// for why a thin overlay is used instead of re-declaring resources). Pure client-side YAML
/// rendering — does not touch a live cluster and does not require Docker.
/// </summary>
public static class ArgoCdManifestRenderer
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs <c>kubectl kustomize &lt;overlayDirectory&gt;</c> and writes the rendered YAML to
    /// <paramref name="outputPath"/>, creating parent directories as needed.
    /// </summary>
    /// <param name="overlayDirectory">Kustomize overlay directory to render.</param>
    /// <param name="outputPath">File to write the rendered YAML to.</param>
    /// <param name="timeout">
    /// Maximum time to wait for <c>kubectl kustomize</c> to finish before the process (and any
    /// children it spawned) is killed. Defaults to 30 seconds.
    /// </param>
    /// <param name="cancellationToken">Propagated cancellation; also kills the process tree.</param>
    /// <returns><paramref name="outputPath"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>kubectl</c> is missing, the kustomize render fails, or it times out.
    /// </exception>
    public static async Task<string> RenderNamespacedInstallManifestAsync(
        string overlayDirectory,
        string outputPath,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(overlayDirectory);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (!Directory.Exists(overlayDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Kustomize overlay directory not found: '{overlayDirectory}'.");
        }

        var (exitCode, stdout, stderr) = await RunCaptureAsync(
            "kubectl",
            ["kustomize", overlayDirectory],
            timeout ?? DefaultTimeout,
            cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"'kubectl kustomize {overlayDirectory}' failed (exit code {exitCode}). " +
                "Ensure kubectl is installed and on PATH.\n" + stderr);
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(
                $"'kubectl kustomize {overlayDirectory}' produced no output.\n{stderr}");
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(outputPath, stdout, cancellationToken);
        return outputPath;
    }

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
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

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
