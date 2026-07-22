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
    /// <summary>
    /// Runs <c>kubectl kustomize &lt;overlayDirectory&gt;</c> and writes the rendered YAML to
    /// <paramref name="outputPath"/>, creating parent directories as needed.
    /// </summary>
    /// <returns><paramref name="outputPath"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>kubectl</c> is missing or the kustomize render fails.
    /// </exception>
    public static string RenderNamespacedInstallManifest(string overlayDirectory, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(overlayDirectory);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (!Directory.Exists(overlayDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Kustomize overlay directory not found: '{overlayDirectory}'.");
        }

        var (exitCode, stdout, stderr) = RunCapture("kubectl", ["kustomize", overlayDirectory]);

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

        File.WriteAllText(outputPath, stdout);
        return outputPath;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCapture(string fileName, IReadOnlyList<string> arguments)
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
        process.Start();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout, stderr);
    }
}
