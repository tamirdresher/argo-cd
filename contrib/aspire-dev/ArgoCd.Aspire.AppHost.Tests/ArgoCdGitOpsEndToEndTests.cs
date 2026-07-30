using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Playwright;
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
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(90);
    private static readonly int[] FixedHostPorts = [8080, 8084, 8087, 12345, 12346, 7001, 4000];
    private static readonly ResourceIdentity[] ExpectedGuestbookResources =
    [
        new("apps", "Deployment", WorkloadNamespace, "guestbook-ui"),
        new("", "Service", WorkloadNamespace, "guestbook-ui"),
    ];

    private readonly ITestOutputHelper output;

    public ArgoCdGitOpsEndToEndTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task WhenCanonicalGuestbookApplicationIsApplied_ThenArgoCdSyncsItAndKindRunsIt()
    {
        var applicationName = CreateApplicationName();
        await RunGuestbookScenarioAsync(applicationName, async (_, cluster, httpClient, _, cancellationToken) =>
        {
            await ThenArgoCdReportsExpectedGuestbookResourcesAsync(httpClient, applicationName, cancellationToken);
            await ThenKindRunsGuestbookWorkloadAsync(cluster.KubeconfigPath, cancellationToken);
        });
    }

    [Fact]
    public async Task WhenCanonicalGuestbookApplicationIsApplied_ThenArgoCdUiShowsItHealthyAndSynced()
    {
        await EnsurePlaywrightChromiumAvailableAsync();

        var applicationName = CreateApplicationName();
        await RunGuestbookScenarioAsync(applicationName, async (app, _, _, _, cancellationToken) =>
        {
            await ThenArgoCdUiShowsApplicationHealthyAndSyncedAsync(app, applicationName, cancellationToken);
        });
    }

    private static string CreateApplicationName() => $"{ApplicationName}-{Guid.NewGuid():N}"[..31];

    private async Task RunGuestbookScenarioAsync(
        string applicationName,
        Func<DistributedApplication, KindClusterResource, HttpClient, ApplicationStatus, CancellationToken, Task> assertAsync)
    {
        var isE2e = Environment.GetEnvironmentVariable(E2EEnvironmentVariable) == "1";
        if (!isE2e)
        {
            Assert.Skip(
                $"Set {E2EEnvironmentVariable}=1 to run this real end-to-end test. " +
                "Prerequisites: Docker, kind, kubectl, Go, Node/pnpm, Playwright Chromium browsers " +
                "(`pwsh bin\\Debug\\net10.0\\playwright.ps1 install chromium` after build), " +
                "outbound network access to https://github.com/argoproj/argocd-example-apps, and free " +
                "host ports 8080, 8084, 8087, 12345, 12346, 7001, and 4000.");
        }

        using var testTimeout = new CancellationTokenSource(
            AppHostStartupTimeout + SyncTimeout + WorkloadTimeout + UiTimeout + TimeSpan.FromMinutes(2));
        var cancellationToken = testTimeout.Token;

        var repoRoot = global::ArgoCd.Aspire.AppHost.ArgoCdRepository.Root;
        var scratchDirectory = Path.Combine(repoRoot, "contrib", "aspire-dev", ".e2e");
        Directory.CreateDirectory(scratchDirectory);
        var applicationManifestPath = Path.Combine(scratchDirectory, $"{applicationName}.yaml");

        AssertFixedHostPortsAvailable();
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

            WriteApplicationManifest(applicationManifestPath, applicationName);
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
            output.WriteLine($"ASSERT created Application {ApplicationNamespace}/{applicationName} with kubectl apply.");

            var httpClient = app.CreateHttpClient("api-server", "http");
            var applicationStatus = await WaitForApplicationHealthyAsync(
                httpClient,
                applicationName,
                SyncTimeout,
                cancellationToken);
            Assert.Equal("Synced", applicationStatus.SyncStatus);
            Assert.Equal("Healthy", applicationStatus.HealthStatus);
            output.WriteLine(
                $"ASSERT Argo CD API: status.sync.status={applicationStatus.SyncStatus}, " +
                $"status.health.status={applicationStatus.HealthStatus}.");

            await assertAsync(app, cluster, httpClient, applicationStatus, cancellationToken);
        }
        finally
        {
            await CleanupGuestbookAsync(cluster.KubeconfigPath, applicationName, cancellationToken);
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
        string applicationName,
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
                var requestUri = $"/api/v1/applications/{applicationName}?appNamespace={ApplicationNamespace}&refresh=normal";
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
            $"Timed out after {timeout} waiting for Argo CD Application '{applicationName}' to become " +
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
                $"Argo CD returned HTTP 200 for Application, but the response did " +
                $"not contain an Application status object. Response: {response}");
        }
        var syncStatus = TryGetNestedString(status, "sync", "status");
        var healthStatus = TryGetNestedString(status, "health", "status");
        var conditions = ReadConditions(status);

        return new ApplicationStatus(syncStatus ?? string.Empty, healthStatus ?? string.Empty, conditions);
    }

    private async Task ThenArgoCdReportsExpectedGuestbookResourcesAsync(
        HttpClient httpClient,
        string applicationName,
        CancellationToken cancellationToken)
    {
        var statusResources = await WaitForApplicationManagedResourcesAsync(httpClient, applicationName, cancellationToken);
        AssertExpectedArgoCdResources(
            statusResources,
            "Application status.resources[]",
            requireExactSet: true);

        // Do not use /resource-tree here. In this host-process AppHost path the Application can
        // be Synced/Healthy while the cached resource tree endpoint repeatedly returns
        // "error getting cached app resource tree: EOF"; status.resources[] is the stable Argo CD
        // Application API surface for the same declared managed resources.
        output.WriteLine(
            "ASSERT Argo CD API: status.resources[] includes exactly " +
            "Deployment/guestbook-ui and Service/guestbook-ui as Synced, with Healthy health where reported.");
    }

    private static async Task<IReadOnlyList<ArgoCdResource>> WaitForApplicationManagedResourcesAsync(
        HttpClient httpClient,
        string applicationName,
        CancellationToken cancellationToken)
    {
        var requestUri = $"/api/v1/applications/{applicationName}?appNamespace={ApplicationNamespace}&refresh=normal";
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(WorkloadTimeout);

        string? lastError = null;
        try
        {
            while (!timeoutSource.IsCancellationRequested)
            {
                using var response = await httpClient.GetAsync(requestUri, timeoutSource.Token);
                var responseText = await response.Content.ReadAsStringAsync(timeoutSource.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    using var document = JsonDocument.Parse(responseText);
                    if (document.RootElement.TryGetProperty("status", out var status) &&
                        status.TryGetProperty("resources", out var resources) &&
                        resources.ValueKind == JsonValueKind.Array)
                    {
                        return ReadResources(resources);
                    }

                    lastError = $"Application response did not contain status.resources[]. Response: {responseText}";
                }
                else
                {
                    lastError =
                        $"Application GET returned {(int)response.StatusCode} {response.StatusCode}. Response: {responseText}";
                }

                await Task.Delay(TimeSpan.FromSeconds(5), timeoutSource.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Re-throw below with the last status.resources[] error, not a bare cancellation.
        }

        throw new TimeoutException(
            $"Timed out after {WorkloadTimeout} waiting for Argo CD Application status.resources[] to be readable. " +
            $"Last error: {lastError ?? "<none>"}.");
    }

    private static IReadOnlyList<ArgoCdResource> ReadResources(JsonElement resources)
    {
        return resources.EnumerateArray()
            .Select(resource =>
            {
                var group = ReadString(resource, "group") ?? string.Empty;
                var kind = ReadString(resource, "kind") ?? string.Empty;
                var name = ReadString(resource, "name") ?? string.Empty;
                var @namespace = ReadString(resource, "namespace") ?? string.Empty;
                var status = ReadString(resource, "status");
                var health = TryGetNestedString(resource, "health", "status") ?? ReadString(resource, "health");
                return new ArgoCdResource(new ResourceIdentity(group, kind, @namespace, name), status, health);
            })
            .ToArray();
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static void AssertExpectedArgoCdResources(
        IReadOnlyList<ArgoCdResource> resources,
        string source,
        bool requireExactSet)
    {
        var byIdentity = resources.ToLookup(resource => resource.Identity);
        var actualExpectedKeys = ExpectedGuestbookResources
            .Where(expected => byIdentity.Contains(expected))
            .ToArray();
        var missing = ExpectedGuestbookResources.Except(actualExpectedKeys).ToArray();
        Assert.True(
            missing.Length == 0,
            $"{source} resource set is missing expected guestbook object(s): {string.Join(", ", missing.Select(FormatResource))}. " +
            $"Actual {source} set: {FormatResourceSet(resources.Select(resource => resource.Identity))}.");

        if (requireExactSet)
        {
            var extra = resources
                .Select(resource => resource.Identity)
                .Except(ExpectedGuestbookResources)
                .ToArray();
            Assert.True(
                extra.Length == 0,
                $"{source} resource set had unexpected managed object(s): {string.Join(", ", extra.Select(FormatResource))}. " +
                $"Expected exactly: {FormatResourceSet(ExpectedGuestbookResources)}.");
        }

        foreach (var expected in ExpectedGuestbookResources)
        {
            var matches = byIdentity[expected].ToArray();
            Assert.True(
                matches.Length > 0,
                $"{source} did not contain expected {FormatResource(expected)}.");

            foreach (var resource in matches)
            {
                Assert.True(
                    string.Equals(resource.Status, "Synced", StringComparison.Ordinal),
                    $"Expected {source} {FormatResource(expected)} to report status Synced, but it reported '{resource.Status ?? "<missing>"}'.");

                if (!string.IsNullOrWhiteSpace(resource.Health))
                {
                    Assert.True(
                        string.Equals(resource.Health, "Healthy", StringComparison.Ordinal),
                        $"Expected {source} {FormatResource(expected)} health to be Healthy when reported, but it reported '{resource.Health}'.");
                }
            }
        }
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

    private async Task ThenKindRunsGuestbookWorkloadAsync(
        string kubeconfigPath,
        CancellationToken cancellationToken)
    {
        var waitResult = await RunOrThrowAsync(
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

        Assert.Contains("deployment.apps/guestbook-ui condition met", waitResult.Stdout);

        var deployment = await GetKubernetesObjectAsync(
            kubeconfigPath,
            "deployment",
            "guestbook-ui",
            cancellationToken);
        var desiredReplicas = ReadInt32(deployment.RootElement, "spec", "replicas");
        var readyReplicas = ReadInt32(deployment.RootElement, "status", "readyReplicas") ?? 0;
        var availableReplicas = ReadInt32(deployment.RootElement, "status", "availableReplicas") ?? 0;
        var selector = ReadString(deployment.RootElement, "spec", "selector", "matchLabels", "app");
        Assert.True(
            desiredReplicas == 1,
            $"Expected cluster Deployment {WorkloadNamespace}/guestbook-ui spec.replicas to be 1, but it was {desiredReplicas?.ToString() ?? "<missing>"}.");
        Assert.True(
            readyReplicas == 1,
            $"Expected cluster Deployment {WorkloadNamespace}/guestbook-ui status.readyReplicas to be 1, but it was {readyReplicas}.");
        Assert.True(
            availableReplicas >= 1,
            $"Expected cluster Deployment {WorkloadNamespace}/guestbook-ui status.availableReplicas to be at least 1, but it was {availableReplicas}.");
        Assert.True(
            selector == "guestbook-ui",
            $"Expected cluster Deployment {WorkloadNamespace}/guestbook-ui selector app=guestbook-ui, but it was '{selector ?? "<missing>"}'.");

        var service = await GetKubernetesObjectAsync(
            kubeconfigPath,
            "service",
            "guestbook-ui",
            cancellationToken);
        var serviceSelector = ReadString(service.RootElement, "spec", "selector", "app");
        Assert.True(
            serviceSelector == "guestbook-ui",
            $"Expected cluster Service {WorkloadNamespace}/guestbook-ui selector app=guestbook-ui, but it was '{serviceSelector ?? "<missing>"}'.");

        var hasExpectedPort = service.RootElement
            .GetProperty("spec")
            .GetProperty("ports")
            .EnumerateArray()
            .Any(port =>
                ReadInt32(port, "port") == 80 &&
                ReadInt32(port, "targetPort") == 80);
        Assert.True(
            hasExpectedPort,
            $"Expected cluster Service {WorkloadNamespace}/guestbook-ui to expose port 80 targeting 80, but its ports were " +
            $"{service.RootElement.GetProperty("spec").GetProperty("ports").GetRawText()}.");

        output.WriteLine(
            "ASSERT Kind cluster: deployment/guestbook-ui has 1 desired replica, 1 ready replica, " +
            "selector app=guestbook-ui, and service/guestbook-ui selects app=guestbook-ui on port 80 -> 80.");
    }

    private static async Task<JsonDocument> GetKubernetesObjectAsync(
        string kubeconfigPath,
        string kind,
        string name,
        CancellationToken cancellationToken)
    {
        var result = await RunOrThrowAsync(
            "kubectl",
            [
                "get",
                kind,
                name,
                "-n", WorkloadNamespace,
                "-o", "json",
                "--kubeconfig", kubeconfigPath,
            ],
            TimeSpan.FromMinutes(1),
            $"reading Kubernetes {kind}/{name}",
            cancellationToken);

        return JsonDocument.Parse(result.Stdout);
    }

    private async Task ThenArgoCdUiShowsApplicationHealthyAndSyncedAsync(
        DistributedApplication app,
        string applicationName,
        CancellationToken cancellationToken)
    {
        using var uiClient = app.CreateHttpClient("ui", "http");
        var uiBaseAddress = uiClient.BaseAddress ?? throw new InvalidOperationException("Aspire did not provide a base address for the ui/http endpoint.");
        output.WriteLine($"ASSERT Aspire model resolved Argo CD UI endpoint to {uiBaseAddress}.");

        var scratchDirectory = Path.Combine(
            global::ArgoCd.Aspire.AppHost.ArgoCdRepository.Root,
            "contrib",
            "aspire-dev",
            ".e2e");
        Directory.CreateDirectory(scratchDirectory);
        var screenshotPath = Path.Combine(scratchDirectory, $"{applicationName}-argocd-ui.png");

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        page.SetDefaultTimeout((float)UiTimeout.TotalMilliseconds);

        try
        {
            await page.GotoAsync(
                new Uri(uiBaseAddress, "applications").ToString(),
                new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = (float)UiTimeout.TotalMilliseconds });

            Assert.True(
                !page.Url.Contains("/login", StringComparison.OrdinalIgnoreCase),
                $"Expected the Argo CD UI to skip login because api-server runs with --disable-auth=true, but browser URL was {page.Url}.");

            var appLink = page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(applicationName, RegexOptions.IgnoreCase) }).First;
            await Microsoft.Playwright.Assertions.Expect(appLink).ToBeVisibleAsync(new() { Timeout = (float)UiTimeout.TotalMilliseconds });
            await appLink.ClickAsync();

            await Microsoft.Playwright.Assertions.Expect(page.GetByText(applicationName).First)
                .ToBeVisibleAsync(new() { Timeout = (float)UiTimeout.TotalMilliseconds });
            await Microsoft.Playwright.Assertions.Expect(page.GetByText("Synced").First)
                .ToBeVisibleAsync(new() { Timeout = (float)UiTimeout.TotalMilliseconds });
            await Microsoft.Playwright.Assertions.Expect(page.GetByText("Healthy").First)
                .ToBeVisibleAsync(new() { Timeout = (float)UiTimeout.TotalMilliseconds });
            await Microsoft.Playwright.Assertions.Expect(page.GetByText("guestbook-ui").First)
                .ToBeVisibleAsync(new() { Timeout = (float)UiTimeout.TotalMilliseconds });

            output.WriteLine(
                $"ASSERT Argo CD UI: applications list linked to {applicationName}; detail view showed " +
                $"{applicationName}, Synced, Healthy, and guestbook-ui.");
        }
        finally
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshotPath,
                FullPage = true,
            });
            output.WriteLine($"ASSERT Argo CD UI screenshot saved to {screenshotPath}.");
        }
    }

    private static async Task EnsurePlaywrightChromiumAvailableAsync()
    {
        try
        {
            using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (PlaywrightException ex) when (
            ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip(
                "Playwright Chromium browser is not installed. Build the test project, then run " +
                "`pwsh bin\\Debug\\net10.0\\playwright.ps1 install chromium` from contrib\\aspire-dev\\ArgoCd.Aspire.AppHost.Tests.");
        }
    }

    private static void AssertFixedHostPortsAvailable()
    {
        var listeners = IPGlobalProperties
            .GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Where(endpoint => FixedHostPorts.Contains(endpoint.Port))
            .Select(endpoint => endpoint.Port)
            .Distinct()
            .Order()
            .ToArray();

        Assert.True(
            listeners.Length == 0,
            $"Cannot start the Aspire AppHost because fixed host port(s) {string.Join(", ", listeners)} are already listening. " +
            $"Stop the other AppHost/process and wait for ports {string.Join(", ", FixedHostPorts.Order())} to be released before running this E2E test.");
    }

    private static int? ReadInt32(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Number when current.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(current.GetString(), out var value) => value,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string FormatResource(ResourceIdentity resource)
    {
        var group = string.IsNullOrEmpty(resource.Group) ? "core" : resource.Group;
        return $"{group}/{resource.Kind} {resource.Namespace}/{resource.Name}";
    }

    private static string FormatResourceSet(IEnumerable<ResourceIdentity> resources) =>
        string.Join(", ", resources.OrderBy(FormatResource).Select(FormatResource));

    private static async Task CleanupGuestbookAsync(
        string kubeconfigPath,
        string applicationName,
        CancellationToken cancellationToken)
    {
        await RunAsync(
            "kubectl",
            [
                "delete",
                "application", applicationName,
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

    private static void WriteApplicationManifest(string path, string applicationName)
    {
        File.WriteAllText(
            path,
            $$"""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata:
              name: {{applicationName}}
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

    private sealed record ResourceIdentity(string Group, string Kind, string Namespace, string Name);

    private sealed record ArgoCdResource(ResourceIdentity Identity, string? Status, string? Health);

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
}

[CollectionDefinition("AppHostE2E", DisableParallelization = true)]
public sealed class AppHostE2ECollection;
