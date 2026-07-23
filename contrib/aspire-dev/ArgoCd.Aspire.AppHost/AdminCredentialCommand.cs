using System.Diagnostics;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Adds a "Show admin credentials" dashboard command that reads the
/// <c>argocd-initial-admin-secret</c> Kubernetes Secret and decodes its <c>password</c> field in
/// managed C# code, never by piping through a shell.
///
/// The dev loop's API server runs with <c>--disable-auth true</c> (see
/// <see cref="ArgoCdComponents.AddArgoCdApiServer"/>, matching the Procfile entry verbatim), so no
/// login is actually required to browse the UI/API — the command reports that fact when the
/// Secret does not exist (or is not yet populated) instead of failing. If a contributor
/// re-enables auth (e.g. via a customized Dex flow) the real decoded password is still available
/// through this command without ever shelling out to <c>base64 -d</c> or a PowerShell pipe, so it
/// works identically on Windows, Linux, and macOS.
/// </summary>
public static class AdminCredentialCommand
{
    private const string Namespace = "argocd";
    private const string SecretName = "argocd-initial-admin-secret";

    public static IResourceBuilder<KindClusterResource> WithAdminCredentialCommand(
        this IResourceBuilder<KindClusterResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithCommand(
            name: "show-admin-credentials",
            displayName: "Show admin credentials",
            executeCommand: async _ =>
            {
                var kubeconfigPath = builder.Resource.KubeconfigPath;
                var (exitCode, stdout, stderr) = await RunAsync(
                    "kubectl",
                    [
                        "--kubeconfig", kubeconfigPath,
                        "-n", Namespace,
                        "get", "secret", SecretName,
                        "-o", "jsonpath={.data.password}",
                    ]);

                if (exitCode != 0)
                {
                    var failureText = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;

                    // The dev loop always starts the API server with --disable-auth true, so this
                    // Secret is never created by this dev loop's own bootstrap. Report that
                    // clearly instead of surfacing a scary-looking "NotFound" kubectl error.
                    if (failureText.Contains("NotFound", StringComparison.OrdinalIgnoreCase))
                    {
                        return CommandResults.Success(
                            "No admin password required.",
                            $"Secret '{SecretName}' was not found in namespace '{Namespace}'. " +
                            "The dev loop's api-server runs with --disable-auth true (matching the " +
                            "Procfile entry), so the UI and API can be used without logging in. If " +
                            "you re-enabled auth manually, create the secret and re-run this command.",
                            CommandResultFormat.Text,
                            true);
                    }

                    return CommandResults.Failure(
                        $"kubectl get secret {SecretName} -n {Namespace} failed.",
                        failureText,
                        CommandResultFormat.Text);
                }

                var encodedPassword = stdout.Trim();
                if (encodedPassword.Length == 0)
                {
                    return CommandResults.Success(
                        "No admin password required.",
                        $"Secret '{SecretName}' exists but its 'password' field is empty. The dev " +
                        "loop's api-server runs with --disable-auth true, so no login is required.",
                        CommandResultFormat.Text,
                        true);
                }

                string decodedPassword;
                try
                {
                    decodedPassword = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPassword));
                }
                catch (FormatException ex)
                {
                    return CommandResults.Failure(
                        "Could not base64-decode the admin password.",
                        ex.Message,
                        CommandResultFormat.Text);
                }

                return CommandResults.Success(
                    "Admin credentials",
                    $"Username: admin\nPassword: {decodedPassword}",
                    CommandResultFormat.Text,
                    true);
            },
            new CommandOptions
            {
                Description =
                    "Decodes and shows the Argo CD admin password from the " +
                    "argocd-initial-admin-secret Secret (cross-platform, no shell pipe). Reports " +
                    "that no password is needed when the dev loop's --disable-auth true is active.",
                IconName = "Key",
                UpdateState = _ => ResourceCommandState.Enabled,
            });

        return builder;
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
