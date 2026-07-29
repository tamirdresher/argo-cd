using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

/// <summary>
/// Opt-in, real GitOps round-trip test for the Aspire dev loop.
///
/// This starts the AppHost with a real Kind cluster, creates Argo CD's canonical
/// <c>argoproj/argocd-example-apps/guestbook</c> Application, lets the host-process Argo CD
/// controller sync it into Kind, then asserts both halves of the story:
/// Argo CD's API reports the Application as <c>Synced</c>/<c>Healthy</c>, and the guestbook
/// Deployment really exists in the cluster.
///
/// Disabled by default because it needs Docker, kind, kubectl, Go, Node/pnpm, and outbound
/// network access to GitHub, and a cold run builds the Argo CD Go components before syncing.
/// Set <c>ARGOCD_ASPIRE_E2E=1</c> to run it.
/// </summary>
[Collection("AppHostE2E")]
public sealed class ArgoCdGitOpsEndToEndTests
{
    private const string E2EEnvironmentVariable = "ARGOCD_ASPIRE_E2E";
    private const string ClusterName = "argocd-dev-e2e";
    private const string ApplicationName = "aspire-e2e-guestbook";
    private const string ApplicationNamespace = "default";
    private const string WorkloadNamespace = "argocd-aspire-e2e-guestbook";
    private static readonly TimeSpan AppHostStartupTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan WorkloadTimeout = TimeSpan.FromMinutes(3);

    private readonly ITestOutputHelper output;

    public ArgoCdGitOpsEndToEndTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task AppHost_SyncsCanonicalGuestbookApplication_AndReportsItHealthy()
    {
        var isE2e = Environment.GetEnvironmentVariable(E2EEnvironmentVariable) == "1";
        if (!isE2e)
        {
            Assert.Skip(
                $"Set {E2EEnvironmentVariable}=1 to run this real end-to-end test. " +
                "Prerequisites: Docker, kind, kubectl, Go, Node/pnpm, and outbound network " +
                "access to https://github.com/argoproj/argocd-example-apps.");
        }

        using var testTimeout = new CancellationTokenSource(
            AppHostStartupTimeout + SyncTimeout + WorkloadTimeout + TimeSpan.FromMinutes(2));
        var cancellationToken = testTimeout.Token;

        var repoRoot = global::ArgoCd.Aspire.AppHost.ArgoCdRepository.Root;
        var scratchDirectory = Path.Combine(repoRoot, "contrib", "aspire-dev", ".e2e");
        Directory.CreateDirectory(scratchDirectory);
        var applicationManifestPath = Path.Combine(scratchDirectory, $"{ApplicationName}.yaml");

        await DeleteKindClusterIfPresentAsync(cancellationToken);

        await using var builder = await CreateAppHostBuilderAsync();
        var cluster = builder.Resources.OfType<KindClusterResource>().Single();

        await using var app = await builder.BuildAsync(cancellationToken);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            await app.StartAsync(cancellationToken);

            await WaitForHealthyResourceAsync(app, "repo-server", AppHostStartupTimeout, cancellationToken);
            await WaitForHealthyResourceAsync(app, "commit-server", AppHostStartupTimeout, cancellationToken);
            await WaitForHealthyResourceAsync(app, "api-server", AppHostStartupTimeout, cancellationToken);
            await WaitForRunningResourceAsync(app, "application-controller", AppHostStartupTimeout, cancellationToken);
            stopwatch.Stop();
            output.WriteLine($"ASSERT AppHost started, API server healthy, and application-controller running in {stopwatch.Elapsed}.");

            WriteApplicationManifest(applicationManifestPath);
            await RunOrThrowAsync(
                "kubectl",
                [
                    "apply",
                    "-f", applicationManifestPath,
                    "--kubeconfig", cluster.KubeconfigPath,
                ],
                TimeSpan.FromMinutes(1),
                "creating the Argo CD Application",
                cancellationToken);
            output.WriteLine($"ASSERT created Application {ApplicationNamespace}/{ApplicationName} with kubectl apply.");

            var httpClient = app.CreateHttpClient("api-server", "http");
            var applicationStatus = await WaitForApplicationHealthyAsync(
                httpClient,
                SyncTimeout,
                cancellationToken);
            Assert.Equal("Synced", applicationStatus.SyncStatus);
            Assert.Equal("Healthy", applicationStatus.HealthStatus);
            output.WriteLine(
                $"ASSERT Argo CD API: status.sync.status={applicationStatus.SyncStatus}, " +
                $"status.health.status={applicationStatus.HealthStatus}.");

            await WaitForGuestbookDeploymentAsync(cluster.KubeconfigPath, cancellationToken);
            output.WriteLine(
                $"ASSERT Kind cluster: deployment/guestbook-ui exists in namespace {WorkloadNamespace}.");
        }
        finally
        {
            await CleanupGuestbookAsync(cluster.KubeconfigPath, cancellationToken);
            if (File.Exists(applicationManifestPath))
            {
                File.Delete(applicationManifestPath);
            }

            await DeleteKindClusterIfPresentAsync(CancellationToken.None);
        }
    }

    private static async Task<IDistributedApplicationTestingBuilder> CreateAppHostBuilderAsync()
    {
        using var scope = new EnvironmentVariableScope(
            ("ARGOCD_ASPIRE_CLUSTER_NAME", ClusterName),
            ("ARGOCD_ASPIRE_ENABLE_DEX", null),
            ("ARGOCD_ASPIRE_ENABLE_CMP", null));

        return await DistributedApplicationTestingBuilder.CreateAsync<Projects.ArgoCd_Aspire_AppHost>();
    }

    private static async Task WaitForHealthyResourceAsync(
        DistributedApplication app,
        string resourceName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync(resourceName, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {timeout} waiting for Aspire resource '{resourceName}' to become healthy.");
        }
    }

    private static async Task WaitForRunningResourceAsync(
        DistributedApplication app,
        string resourceName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await app.ResourceNotifications.WaitForResourceAsync(resourceName, KnownResourceStates.Running, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {timeout} waiting for Aspire resource '{resourceName}' to be running.");
        }
    }

    private async Task<ApplicationStatus> WaitForApplicationHealthyAsync(
        HttpClient httpClient,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        ApplicationStatus? lastStatus = null;
        ApplicationStatus? lastWrittenStatus = null;
        try
        {
            while (!timeoutSource.IsCancellationRequested)
            {
                var requestUri = $"/api/v1/applications/{ApplicationName}?appNamespace={ApplicationNamespace}&refresh=normal";
                using var response = await httpClient.GetAsync(requestUri, timeoutSource.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
                    lastStatus = await ReadApplicationStatusAsync(stream, timeoutSource.Token);
                    if (lastStatus != lastWrittenStatus)
                    {
                        output.WriteLine(
                            $"Observed Argo CD Application status: sync={lastStatus.SyncStatus}, " +
                            $"health={lastStatus.HealthStatus}, conditions={lastStatus.Conditions}.");
                        lastWrittenStatus = lastStatus;
                    }

                    if (lastStatus is { SyncStatus: "Synced", HealthStatus: "Healthy" })
                    {
                        return lastStatus;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(5), timeoutSource.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Re-throw below with the last observed Argo CD status, not a bare cancellation.
        }

        throw new TimeoutException(
            $"Timed out after {timeout} waiting for Argo CD Application '{ApplicationName}' to become " +
            $"Synced/Healthy. Last observed status: sync={lastStatus?.SyncStatus ?? "<not found>"}, " +
            $"health={lastStatus?.HealthStatus ?? "<not found>"}, " +
            $"conditions={lastStatus?.Conditions ?? "<not found>"}.");
    }

    private static async Task<ApplicationStatus> ReadApplicationStatusAsync(
        Stream json,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(json, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("status", out var status))
        {
            var response = document.RootElement.GetRawText();
            throw new InvalidOperationException(
                $"Argo CD returned HTTP 200 for Application '{ApplicationName}', but the response did " +
                $"not contain an Application status object. Response: {response}");
        }
        var syncStatus = TryGetNestedString(status, "sync", "status");
        var healthStatus = TryGetNestedString(status, "health", "status");
        var conditions = ReadConditions(status);

        return new ApplicationStatus(syncStatus ?? string.Empty, healthStatus ?? string.Empty, conditions);
    }

    private static string? TryGetNestedString(JsonElement element, string firstProperty, string secondProperty)
    {
        return element.TryGetProperty(firstProperty, out var first) &&
            first.TryGetProperty(secondProperty, out var second)
                ? second.GetString()
                : null;
    }

    private static string ReadConditions(JsonElement status)
    {
        if (!status.TryGetProperty("conditions", out var conditions) ||
            conditions.ValueKind != JsonValueKind.Array ||
            conditions.GetArrayLength() == 0)
        {
            return "<none>";
        }

        return string.Join(
            " | ",
            conditions.EnumerateArray().Select(condition =>
            {
                var type = condition.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString()
                    : "<unknown>";
                var message = condition.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : "<no message>";
                return $"{type}: {message}";
            }));
    }

    private static async Task WaitForGuestbookDeploymentAsync(
        string kubeconfigPath,
        CancellationToken cancellationToken)
    {
        var result = await RunOrThrowAsync(
            "kubectl",
            [
                "wait",
                "--for=condition=available",
                "deployment/guestbook-ui",
                "-n", WorkloadNamespace,
                $"--timeout={(int)WorkloadTimeout.TotalSeconds}s",
                "--kubeconfig", kubeconfigPath,
            ],
            WorkloadTimeout + TimeSpan.FromSeconds(30),
            "waiting for the guestbook Deployment to become available",
            cancellationToken);

        Assert.Contains("deployment.apps/guestbook-ui condition met", result.Stdout);
    }

    private static async Task CleanupGuestbookAsync(
        string kubeconfigPath,
        CancellationToken cancellationToken)
    {
        await RunAsync(
            "kubectl",
            [
                "delete",
                "application", ApplicationName,
                "-n", ApplicationNamespace,
                "--ignore-not-found=true",
                "--kubeconfig", kubeconfigPath,
            ],
            TimeSpan.FromMinutes(1),
            cancellationToken);

        await RunAsync(
            "kubectl",
            [
                "delete",
                "namespace", WorkloadNamespace,
                "--ignore-not-found=true",
                "--kubeconfig", kubeconfigPath,
            ],
            TimeSpan.FromMinutes(1),
            cancellationToken);
    }

    private static async Task DeleteKindClusterIfPresentAsync(CancellationToken cancellationToken)
    {
        await RunAsync(
            "kind",
            ["delete", "cluster", "--name", ClusterName],
            TimeSpan.FromMinutes(2),
            cancellationToken);
    }

    private static void WriteApplicationManifest(string path)
    {
        File.WriteAllText(
            path,
            $$"""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata:
              name: {{ApplicationName}}
              namespace: {{ApplicationNamespace}}
            spec:
              project: default
              source:
                repoURL: https://github.com/argoproj/argocd-example-apps
                targetRevision: HEAD
                path: guestbook
              destination:
                server: https://kubernetes.default.svc
                namespace: {{WorkloadNamespace}}
              syncPolicy:
                automated:
                  prune: true
                  selfHeal: true
                syncOptions:
                - CreateNamespace=true
            """);
    }

    private static async Task<CommandResult> RunOrThrowAsync(
        string fileName,
        string[] arguments,
        TimeSpan timeout,
        string stage,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(fileName, arguments, timeout, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command failed while {stage}: {fileName} {string.Join(' ', arguments)} " +
                $"exited with code {result.ExitCode}.\nstdout: {result.Stdout}\nstderr: {result.Stderr}");
        }

        return result;
    }

    private static async Task<CommandResult> RunAsync(
        string fileName,
        string[] arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var processTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        processTimeout.CancelAfter(timeout);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(processTimeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(processTimeout.Token);

        try
        {
            await process.WaitForExitAsync(processTimeout.Token);
            return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
            }

            throw new TimeoutException(
                $"Timed out after {timeout} running command: {fileName} {string.Join(' ', arguments)}.");
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly (string Name, string? PreviousValue)[] previousValues;

        public EnvironmentVariableScope(params (string Name, string? Value)[] variables)
        {
            previousValues = variables
                .Select(variable => (variable.Name, Environment.GetEnvironmentVariable(variable.Name)))
                .ToArray();

            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in previousValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private sealed record ApplicationStatus(string SyncStatus, string HealthStatus, string Conditions);

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
}
