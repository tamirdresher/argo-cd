// ─────────────────────────────────────────────────────────────────────────────
// TEMPORARY: this extension fills a gap in CommunityToolkit.Aspire.Hosting.Kind
// 13.4.1-beta.687.
//
//   WithManifest → upstreamed in https://github.com/CommunityToolkit/Aspire/pull/1481
//                  (as AddManifest / AddManifestFromContent). Delete this file
//                  and bump the package version once that PR merges and ships.
// ─────────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Additions on top of <c>CommunityToolkit.Aspire.Hosting.Kind</c>: apply raw kubectl manifests
/// (CRDs, RBAC, ConfigMaps, Secrets) to the cluster after it becomes ready.
/// </summary>
public static class KindClusterManifestExtensions
{
    /// <summary>
    /// Applies a kubectl manifest file to the Kind cluster once it is ready.
    /// </summary>
    /// <param name="builder">The Kind cluster resource builder.</param>
    /// <param name="manifestPath">Absolute path to a Kubernetes manifest file or directory.</param>
    /// <returns>The same builder for fluent chaining.</returns>
    /// <remarks>
    /// Requires <c>kubectl</c> on <c>PATH</c>. Runs <c>kubectl apply -f {manifestPath} --kubeconfig {cluster.KubeconfigPath}</c>.
    /// Multiple calls compose in registration order. Manifest apply errors are logged and re-thrown so
    /// downstream resources that <c>.WaitFor(cluster)</c> see the failure and do not start against a
    /// half-bootstrapped cluster.
    /// </remarks>
    public static IResourceBuilder<KindClusterResource> WithManifest(
        this IResourceBuilder<KindClusterResource> builder,
        string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);

        var resource = builder.Resource;

        builder.ApplicationBuilder.Eventing.Subscribe<ResourceReadyEvent>(resource, async (evt, ct) =>
        {
            var services = evt.Services;
            var loggerService = services.GetRequiredService<ResourceLoggerService>();
            var logger = loggerService.GetLogger(resource);

            if (!File.Exists(manifestPath) && !Directory.Exists(manifestPath))
            {
                var msg = $"Manifest path not found: '{manifestPath}'.";
                logger.LogError(msg);
                throw new FileNotFoundException(msg, manifestPath);
            }

            logger.LogInformation("Applying manifest {ManifestPath} to Kind cluster '{Cluster}'…",
                manifestPath, resource.Name);

            var (exitCode, stdout, stderr) = await RunKubectlAsync(
                ["apply", "-f", manifestPath, "--kubeconfig", resource.KubeconfigPath],
                ct);

            if (exitCode != 0)
            {
                var err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                logger.LogError("kubectl apply failed (exit {ExitCode}): {Error}", exitCode, err);
                throw new InvalidOperationException(
                    $"'kubectl apply -f {manifestPath}' failed with exit code {exitCode}. {err}");
            }

            var oneLine = (stdout ?? string.Empty).Trim().Replace("\r\n", " · ").Replace("\n", " · ");
            logger.LogInformation("Manifest applied: {Result}", oneLine);
        });

        return builder;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunKubectlAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "kubectl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
