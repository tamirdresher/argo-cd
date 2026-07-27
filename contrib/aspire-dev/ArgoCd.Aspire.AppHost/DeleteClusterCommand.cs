using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Adds a "Delete Kind cluster (clean shutdown)" dashboard command that synchronously runs
/// <c>kind delete cluster</c> and best-effort deletes the generated kubeconfig file, matching
/// the CommunityToolkit Kind integration's session-lifetime cleanup.
///
/// Why this exists: the CommunityToolkit Kind integration's session-lifetime lifecycle hook
/// *does* delete the cluster, but on this Aspire CLI/host combination `aspire stop` was
/// observed to hard-kill the AppHost process a few milliseconds after requesting shutdown —
/// well before an async <c>kind delete cluster</c> (which takes several seconds) can complete.
/// (See contrib/aspire-dev/README.md "Known limitations" for the exact log evidence.)
///
/// Resource commands, unlike process shutdown, ARE awaited to completion by the Aspire CLI/
/// dashboard, so this command reliably deletes the cluster. Contributors should run it (via
/// the dashboard or `aspire resource argocd-dev delete-cluster`) before `aspire stop` for a
/// guaranteed clean teardown with no orphaned Kind/Docker containers.
/// </summary>
public static class DeleteClusterCommand
{
    public static IResourceBuilder<KindClusterResource> WithDeleteClusterCommand(
        this IResourceBuilder<KindClusterResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithCommand(
            name: "delete-cluster",
            displayName: "Delete Kind cluster (clean shutdown)",
            executeCommand: async _ =>
            {
                var clusterName = builder.Resource.Name;
                var kubeconfigPath = builder.Resource.KubeconfigPath;
                var (exitCode, stdout, stderr) = await RunAsync("kind", ["delete", "cluster", "--name", clusterName]);

                // Best-effort kubeconfig cleanup — mirrors the generated Kind kubeconfig
                // BeforeStopAsync cleanup (KindClusterLifecycleHook.DeleteClusterAsync). Attempted
                // regardless of the `kind delete cluster` exit code so a stale kubeconfig doesn't
                // linger even if the cluster itself was already gone.
                var kubeconfigCleanupWarning = TryDeleteKubeconfig(kubeconfigPath);

                if (exitCode != 0)
                {
                    var failureText = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    if (kubeconfigCleanupWarning is not null)
                    {
                        failureText = $"{failureText}\n{kubeconfigCleanupWarning}";
                    }

                    return CommandResults.Failure(
                        $"kind delete cluster --name {clusterName} failed.",
                        failureText,
                        CommandResultFormat.Text);
                }

                var successText = kubeconfigCleanupWarning is null
                    ? stdout
                    : $"{stdout}\n{kubeconfigCleanupWarning}";

                return CommandResults.Success(
                    $"Kind cluster '{clusterName}' deleted.",
                    successText,
                    CommandResultFormat.Text,
                    true);
            },
            new CommandOptions
            {
                Description =
                    "Deletes this Kind cluster now (synchronously). Run this BEFORE 'aspire stop' " +
                    "for a guaranteed clean teardown — see README 'Known limitations'.",
                IconName = "Delete",
                UpdateState = _ => ResourceCommandState.Enabled,
            });

        return builder;
    }

    /// <summary>
    /// Best-effort deletes the kubeconfig file at <paramref name="kubeconfigPath"/>. Returns
    /// <see langword="null"/> on success (or if the file didn't exist), or a human-readable
    /// warning message if deletion failed. Never throws. Extracted as a pure, testable helper
    /// (no process I/O) so this cleanup behavior can be unit tested without kubectl/Docker.
    /// </summary>
    internal static string? TryDeleteKubeconfig(string kubeconfigPath)
    {
        if (!File.Exists(kubeconfigPath))
        {
            return null;
        }

        try
        {
            File.Delete(kubeconfigPath);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not delete kubeconfig '{kubeconfigPath}': {ex.Message}";
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
