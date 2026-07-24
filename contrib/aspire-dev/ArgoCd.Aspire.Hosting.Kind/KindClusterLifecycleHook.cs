using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace Aspire.Hosting;

/// <summary>
/// Manages the Kind cluster lifecycle through .NET Aspire application events:
/// <list type="bullet">
///   <item><description><b>BeforeStart:</b> creates all Kind clusters in parallel.</description></item>
///   <item><description><b>AfterResourcesCreated:</b> health-checks each cluster, then applies manifests and Helm charts.</description></item>
///   <item><description><b>Hosted service stop:</b> deletes ephemeral Kind clusters (best-effort) and cleans up kubeconfig files.</description></item>
/// </list>
/// </summary>
internal sealed class KindClusterLifecycleHook :
    IDistributedApplicationEventingSubscriber,
    IHostedService
{
    private readonly ILogger<KindClusterLifecycleHook> _logger;
    private readonly ResourceNotificationService _notifications;
    private readonly IDistributedApplicationEventing _eventing;
    private DistributedApplicationModel? _appModel;

    private const int MaxHealthCheckAttempts = 15;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private const string StateStarting = "Starting";
    private const string StateRunning = "Running";
    private const string StateFailed = "Failed";
    private const string StateStopping = "Stopping";
    private const string StateFinished = "Finished";

    public KindClusterLifecycleHook(
        ILogger<KindClusterLifecycleHook> logger,
        ResourceNotificationService notifications,
        IDistributedApplicationEventing eventing)
    {
        _logger = logger;
        _notifications = notifications;
        _eventing = eventing;
    }

    // ── Eventing entry points ─────────────────────────────────────────────────

    /// <inheritdoc />
    public Task SubscribeAsync(
        IDistributedApplicationEventing eventing,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        eventing.Subscribe<AfterResourcesCreatedEvent>(OnAfterResourcesCreatedAsync);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) =>
        _appModel is null
            ? Task.CompletedTask
            : BeforeStopAsync(_appModel, cancellationToken);

    private Task OnBeforeStartAsync(
        BeforeStartEvent applicationEvent,
        CancellationToken cancellationToken)
    {
        _appModel = applicationEvent.Model;
        return BeforeStartAsync(applicationEvent.Model, cancellationToken);
    }

    private Task OnAfterResourcesCreatedAsync(
        AfterResourcesCreatedEvent applicationEvent,
        CancellationToken cancellationToken) =>
        AfterResourcesCreatedAsync(
            applicationEvent.Model,
            cancellationToken,
            applicationEvent.Services);

    /// <summary>Creates all Kind clusters in parallel before the application starts.</summary>
    public async Task BeforeStartAsync(
        DistributedApplicationModel appModel,
        CancellationToken cancellationToken = default)
    {
        var clusters = appModel.Resources.OfType<KindClusterResource>().ToList();
        if (clusters.Count == 0) return;

        _logger.LogInformation("Creating {Count} Kind cluster(s)...", clusters.Count);

        foreach (var cluster in clusters)
        {
            await PublishClusterStateAsync(
                cluster,
                StateStarting,
                KnownResourceStateStyles.Info,
                cancellationToken,
                message: "Creating Kind cluster");
        }

        // Parallel creation — each cluster is independent.
        await Task.WhenAll(clusters.Select(c => CreateClusterAsync(c, cancellationToken)));
    }

    /// <summary>
    /// Health-checks all clusters after resources are created, then applies manifests and Helm charts.
    /// </summary>
    internal async Task AfterResourcesCreatedAsync(
        DistributedApplicationModel appModel,
        CancellationToken cancellationToken = default,
        IServiceProvider? services = null)
    {
        var clusters = appModel.Resources.OfType<KindClusterResource>().ToList();
        if (clusters.Count == 0) return;

        await Task.WhenAll(clusters.Select(
            c => WaitForReadyThenPostDeployAsync(c, services, cancellationToken)));
    }

    /// <summary>
    /// Deletes non-persistent Kind clusters and removes their kubeconfig files before the
    /// application stops. Clusters marked <see cref="KindClusterResource.Persistent"/> are left
    /// running so the inner dev loop can be restarted without re-creating the cluster and
    /// re-applying CRDs/RBAC/ConfigMaps every time; use the explicit "Delete Kind Cluster"
    /// dashboard command (see <c>DeleteClusterCommand</c>) to tear those down deliberately.
    /// </summary>
    internal async Task BeforeStopAsync(
        DistributedApplicationModel appModel,
        CancellationToken cancellationToken = default)
    {
        var clusters = appModel.Resources.OfType<KindClusterResource>().ToList();
        if (clusters.Count == 0) return;

        var persistentClusters = clusters.Where(c => c.Persistent).ToList();
        var ephemeralClusters = clusters.Where(c => !c.Persistent).ToList();

        foreach (var cluster in persistentClusters)
        {
            _logger.LogInformation(
                "Kind cluster '{ClusterName}' is persistent; leaving it running across AppHost stop. " +
                "Use the 'Delete Kind Cluster' dashboard command to tear it down explicitly.",
                cluster.ClusterName);

            await PublishClusterStateAsync(
                cluster,
                StateFinished,
                KnownResourceStateStyles.Info,
                cancellationToken,
                message: "AppHost stopped; persistent Kind cluster left running");
        }

        if (ephemeralClusters.Count == 0) return;

        _logger.LogInformation("Deleting {Count} Kind cluster(s)...", ephemeralClusters.Count);

        foreach (var cluster in ephemeralClusters)
        {
            await PublishClusterStateAsync(
                cluster,
                StateStopping,
                KnownResourceStateStyles.Info,
                cancellationToken,
                message: "Deleting Kind cluster");
        }

        // Best-effort parallel deletion — don't let one failure block others.
        await Task.WhenAll(ephemeralClusters.Select(c => DeleteClusterAsync(c, cancellationToken)));

        foreach (var cluster in ephemeralClusters)
        {
            await PublishClusterStateAsync(
                cluster,
                StateFinished,
                KnownResourceStateStyles.Info,
                cancellationToken,
                message: "Kind cluster deleted");
        }
    }

    // ── Cluster operations ────────────────────────────────────────────────────

    private async Task CreateClusterAsync(KindClusterResource cluster, CancellationToken cancellationToken)
    {
        if (cluster.Persistent && await TryReuseExistingClusterAsync(cluster, cancellationToken))
        {
            // A healthy cluster from a previous run was found and its kubeconfig re-exported;
            // skip `kind create cluster` (and the CRD/RBAC/ConfigMap re-apply that follows it in
            // AfterResourcesCreatedAsync still runs — bootstrap manifests are safely idempotent).
            return;
        }

        _logger.LogInformation("Creating Kind cluster '{ClusterName}' → kubeconfig: {KubeconfigPath}",
            cluster.ClusterName, cluster.KubeconfigPath);

        await DeleteStaleClusterIfExistsAsync(cluster, cancellationToken);

        var args = new List<string>
        {
            "create", "cluster",
            "--name", cluster.ClusterName,
            "--kubeconfig", cluster.KubeconfigPath,
        };

        string? tempConfigPath = null;
        try
        {
            var configPath = cluster.ConfigPath;

            // Auto-generate a Kind config when the user has set NodeCount/KubernetesVersion/PortMappings
            // but hasn't provided an explicit config file.
            if (configPath is null && NeedsGeneratedConfig(cluster))
            {
                tempConfigPath = Path.GetTempFileName();
                var configYaml = GenerateKindConfig(cluster);
                _logger.LogDebug("Auto-generated Kind config for '{ClusterName}':\n{Config}",
                    cluster.ClusterName, configYaml);
                await File.WriteAllTextAsync(tempConfigPath, configYaml, cancellationToken);
                configPath = tempConfigPath;
            }

            if (configPath is not null)
            {
                args.AddRange(["--config", configPath]);
            }

            var (exitCode, _, stderr) = await RunCommandAsync("kind", args, cancellationToken);

            if (exitCode != 0)
            {
                await PublishClusterStateAsync(
                    cluster,
                    StateFailed,
                    KnownResourceStateStyles.Error,
                    cancellationToken,
                    message: $"kind create cluster failed: {stderr}");

                throw new InvalidOperationException(
                    $"Failed to create Kind cluster '{cluster.ClusterName}' (exit code {exitCode}). " +
                    $"Ensure the 'kind' CLI is installed and Docker is running.\n{stderr}");
            }

            _logger.LogInformation("Kind cluster '{ClusterName}' created successfully.", cluster.ClusterName);

            await PublishClusterStateAsync(
                cluster,
                StateStarting,
                KnownResourceStateStyles.Info,
                cancellationToken,
                message: "Kind cluster created; waiting for readiness");
        }
        finally
        {
            if (tempConfigPath is not null && File.Exists(tempConfigPath))
            {
                try { File.Delete(tempConfigPath); }
                catch { /* not critical */ }
            }
        }
    }

    /// <summary>
    /// Attempts to reuse an already-running Kind cluster for a <see cref="KindClusterResource"/>
    /// marked <see cref="KindClusterResource.Persistent"/>. Re-exports the kubeconfig (the temp
    /// kubeconfig path is per-AppHost-run, so it must be refreshed even when the underlying Kind
    /// cluster is still alive from a previous run) and verifies the cluster actually responds
    /// before declaring it reusable.
    /// </summary>
    /// <returns><see langword="true"/> if an existing, healthy cluster was found and reused.</returns>
    private async Task<bool> TryReuseExistingClusterAsync(
        KindClusterResource cluster,
        CancellationToken cancellationToken)
    {
        var (exportExitCode, _, exportStderr) = await RunCommandAsync(
            "kind",
            ["export", "kubeconfig", "--name", cluster.ClusterName, "--kubeconfig", cluster.KubeconfigPath],
            cancellationToken);

        if (exportExitCode != 0)
        {
            _logger.LogDebug(
                "No existing Kind cluster named '{ClusterName}' to reuse (kind export kubeconfig exit {ExitCode}): {Stderr}",
                cluster.ClusterName,
                exportExitCode,
                exportStderr);
            return false;
        }

        var (healthExitCode, _, healthStderr) = await RunCommandAsync(
            "kubectl",
            ["get", "nodes", "--kubeconfig", cluster.KubeconfigPath, "--request-timeout", "5s"],
            cancellationToken);

        if (healthExitCode != 0)
        {
            _logger.LogWarning(
                "Existing Kind cluster '{ClusterName}' did not respond to 'kubectl get nodes' and will be recreated: {Stderr}",
                cluster.ClusterName,
                healthStderr);
            return false;
        }

        _logger.LogInformation(
            "Reusing existing persistent Kind cluster '{ClusterName}'.",
            cluster.ClusterName);

        await PublishClusterStateAsync(
            cluster,
            StateStarting,
            KnownResourceStateStyles.Info,
            cancellationToken,
            message: "Reusing existing persistent Kind cluster");

        return true;
    }

    private async Task DeleteStaleClusterIfExistsAsync(
        KindClusterResource cluster,
        CancellationToken cancellationToken)
    {
        var (listExitCode, stdout, stderr) = await RunCommandAsync(
            "kind",
            ["get", "clusters"],
            cancellationToken);

        if (listExitCode != 0)
        {
            _logger.LogDebug(
                "Could not list Kind clusters before creating '{ClusterName}' (exit {ExitCode}): {Stderr}",
                cluster.ClusterName,
                listExitCode,
                stderr);
            return;
        }

        var exists = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(cluster.ClusterName, StringComparer.Ordinal);

        if (!exists)
        {
            return;
        }

        _logger.LogWarning(
            "Kind cluster '{ClusterName}' already exists. Deleting the stale managed cluster before recreating it.",
            cluster.ClusterName);

        await PublishClusterStateAsync(
            cluster,
            StateStarting,
            KnownResourceStateStyles.Info,
            cancellationToken,
            message: "Deleting stale Kind cluster before recreate");

        var (deleteExitCode, _, deleteStderr) = await RunCommandAsync(
            "kind",
            ["delete", "cluster", "--name", cluster.ClusterName],
            cancellationToken);

        if (deleteExitCode != 0)
        {
            await PublishClusterStateAsync(
                cluster,
                StateFailed,
                KnownResourceStateStyles.Error,
                cancellationToken,
                message: $"kind delete cluster failed for stale cluster '{cluster.ClusterName}': {deleteStderr}");

            throw new InvalidOperationException(
                $"Kind cluster '{cluster.ClusterName}' already exists, but deleting it failed (exit code {deleteExitCode}).\n{deleteStderr}");
        }
    }

    private async Task WaitForReadyThenPostDeployAsync(
        KindClusterResource cluster,
        IServiceProvider? services,
        CancellationToken cancellationToken)
    {
        try
        {
            await WaitForClusterReadyAsync(cluster, cancellationToken);

            await PublishClusterStateAsync(
                cluster,
                StateStarting,
                KnownResourceStateStyles.Info,
                cancellationToken,
                includeLive: true,
                message: "Kind cluster is ready; applying manifests and Helm charts");

            // Build/load images first so manifests can reference locally-built images without pull failures.
            foreach (var image in cluster.DockerImages)
            {
                await BuildAndLoadDockerImageAsync(cluster, image, cancellationToken);
            }

            // Execute deploy steps in registration order, preserving the interleaving of
            // kubectl manifests and Helm charts as declared in Program.cs.
            // (Previously: all manifests first, then all charts — which caused race conditions
            // when a chart must be ready before a later manifest references it, e.g. the
            // Secrets Store CSI driver DaemonSet must be running before the MDM pod can mount
            // its CSI-backed cert volume.)
            foreach (var step in cluster.DeploySteps)
            {
                switch (step)
                {
                    case KindManifestStep ms:
                        await ApplyManifestAsync(cluster, ms.Path, cancellationToken);
                        break;
                    case KindHelmChartStep hs:
                        await InstallHelmChartAsync(cluster, hs.Chart, cancellationToken);
                        break;
                }
            }

            await WaitForPodsReadyAsync(cluster, cancellationToken);

            await PublishClusterStateAsync(
                cluster,
                StateRunning,
                KnownResourceStateStyles.Success,
                cancellationToken,
                includeLive: true,
                message: "Kind cluster is ready; manifests and Helm charts applied");

            if (services is not null)
            {
                await _eventing.PublishAsync(
                    new KindClusterReadyEvent(cluster, services, _logger),
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await PublishClusterStateAsync(
                cluster,
                StateFailed,
                KnownResourceStateStyles.Error,
                cancellationToken,
                includeLive: true,
                message: ex.Message);
            throw;
        }
    }

    private async Task WaitForClusterReadyAsync(KindClusterResource cluster, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Waiting for Kind cluster '{ClusterName}' to be ready (timeout: {Timeout})...",
            cluster.ClusterName, cluster.ReadyTimeout);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(cluster.ReadyTimeout);

        var delay = InitialRetryDelay;

        for (int attempt = 1; attempt <= MaxHealthCheckAttempts; attempt++)
        {
            try
            {
                var (exitCode, _, _) = await RunCommandAsync(
                    "kubectl",
                    ["get", "nodes", "--kubeconfig", cluster.KubeconfigPath],
                    timeoutCts.Token);

                if (exitCode == 0)
                {
                    _logger.LogInformation("Kind cluster '{ClusterName}' is ready.", cluster.ClusterName);
                    return;
                }

                _logger.LogInformation(
                    "Kind cluster '{ClusterName}' not ready yet (attempt {Attempt}/{Max}), retrying in {Delay:F0}s...",
                    cluster.ClusterName, attempt, MaxHealthCheckAttempts, delay.TotalSeconds);
            }
            catch (OperationCanceledException) when (
                timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Kind cluster '{cluster.ClusterName}' did not become ready within {cluster.ReadyTimeout}.");
            }

            await Task.Delay(delay, timeoutCts.Token);

            // Exponential backoff capped at MaxRetryDelay.
            delay = TimeSpan.FromSeconds(
                Math.Min(delay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));
        }

        _logger.LogWarning(
            "Kind cluster '{ClusterName}' health check did not pass after {Max} attempts. " +
            "Continuing — the cluster may still be initialising.",
            cluster.ClusterName, MaxHealthCheckAttempts);
    }

    private async Task ApplyManifestAsync(
        KindClusterResource cluster, string manifestPath, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Applying manifest '{ManifestPath}' to cluster '{ClusterName}'.",
            manifestPath, cluster.ClusterName);

        var (exitCode, _, stderr) = await RunCommandAsync(
            "kubectl",
            ["apply", "-f", manifestPath, "--kubeconfig", cluster.KubeconfigPath],
            cancellationToken);

        if (exitCode != 0)
        {
            _logger.LogWarning(
                "Failed to apply manifest '{ManifestPath}' to cluster '{ClusterName}' (exit {ExitCode}): {Stderr}",
                manifestPath, cluster.ClusterName, exitCode, stderr);

            await PublishClusterStateAsync(
                cluster,
                StateFailed,
                KnownResourceStateStyles.Error,
                cancellationToken,
                includeLive: true,
                message: $"kubectl apply failed for '{manifestPath}': {stderr}");
        }
    }

    private async Task BuildAndLoadDockerImageAsync(
        KindClusterResource cluster,
        KindDockerImage image,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Building Docker image '{Image}' from '{ContextPath}' for Kind cluster '{ClusterName}'.",
            image.Image, image.ContextPath, cluster.ClusterName);

        var buildArgs = new List<string>
        {
            "build",
            "-t", image.Image,
        };

        if (image.DockerfilePath is not null)
        {
            buildArgs.AddRange(["-f", image.DockerfilePath]);
        }

        buildArgs.Add(image.ContextPath);

        var (buildExitCode, _, buildStderr) = await RunCommandAsync("docker", buildArgs, cancellationToken);
        if (buildExitCode != 0)
        {
            await PublishClusterStateAsync(
                cluster,
                StateFailed,
                KnownResourceStateStyles.Error,
                cancellationToken,
                includeLive: true,
                message: $"docker build failed for '{image.Image}': {buildStderr}");

            throw new InvalidOperationException(
                $"Failed to build Docker image '{image.Image}' (exit code {buildExitCode}).\n{buildStderr}");
        }

        _logger.LogInformation(
            "Loading Docker image '{Image}' into Kind cluster '{ClusterName}'.",
            image.Image, cluster.ClusterName);

        var (loadExitCode, _, loadStderr) = await RunCommandAsync(
            "kind",
            ["load", "docker-image", image.Image, "--name", cluster.ClusterName],
            cancellationToken);

        if (loadExitCode != 0)
        {
            await PublishClusterStateAsync(
                cluster,
                StateFailed,
                KnownResourceStateStyles.Error,
                cancellationToken,
                includeLive: true,
                message: $"kind load docker-image failed for '{image.Image}': {loadStderr}");

            throw new InvalidOperationException(
                $"Failed to load Docker image '{image.Image}' into Kind cluster '{cluster.ClusterName}' (exit code {loadExitCode}).\n{loadStderr}");
        }
    }

    private async Task InstallHelmChartAsync(
        KindClusterResource cluster, KindHelmChart chart, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Installing Helm chart '{Chart}' as release '{ReleaseName}' in namespace '{Namespace}' on cluster '{ClusterName}'.",
            chart.Chart, chart.ReleaseName, chart.Namespace, cluster.ClusterName);

        // When a chart repository URL is provided, register it with helm repo add before installing.
        // This is more reliable than helm install --repo <url> which can fail on some machines when
        // other repositories have stale or missing cache files (helm refreshes all repos on --repo).
        // The alias is scoped to this release name so concurrent installs don't collide.
        // helm repo add is idempotent — if the repo is already registered the command succeeds.
        string chartRef = chart.Chart;
        if (chart.RepoUrl is not null)
        {
            var repoAlias = $"aspire-kind-{chart.ReleaseName}";
            _logger.LogInformation(
                "Adding Helm repo '{Alias}' → {Url} for chart '{Chart}'.",
                repoAlias, chart.RepoUrl, chart.Chart);

            var (repoAddExitCode, _, repoAddStderr) = await RunCommandAsync(
                "helm",
                ["repo", "add", repoAlias, chart.RepoUrl, "--force-update"],
                cancellationToken);

            if (repoAddExitCode != 0)
            {
                _logger.LogWarning(
                    "helm repo add failed for '{Alias}' (exit {ExitCode}): {Stderr} — continuing with bare chart name",
                    repoAlias, repoAddExitCode, repoAddStderr);
                // Fall through with the bare chart name; helm install --repo will also be attempted below.
            }
            else
            {
                // Use repo-qualified chart reference now that the alias is registered.
                chartRef = $"{repoAlias}/{chart.Chart}";
                _logger.LogInformation("Using repo-qualified chart ref '{ChartRef}'.", chartRef);
            }
        }

        var args = new List<string>
        {
            "install", chart.ReleaseName, chartRef,
            "--namespace", chart.Namespace,
            "--create-namespace",
            "--kubeconfig", cluster.KubeconfigPath,
        };

        if (chart.ValuesFile is not null)
        {
            args.AddRange(["-f", chart.ValuesFile]);
        }

        foreach (var setValue in chart.SetValues ?? [])
        {
            args.AddRange(["--set", setValue]);
        }

        if (chart.Wait)
        {
            args.Add("--wait");
        }

        var (exitCode, _, stderr) = await RunCommandAsync("helm", args, cancellationToken);

        if (exitCode != 0)
        {
            _logger.LogWarning(
                "Failed to install Helm chart '{Chart}' (exit {ExitCode}): {Stderr}",
                chart.Chart, exitCode, stderr);

            await PublishClusterStateAsync(
                cluster,
                StateFailed,
                KnownResourceStateStyles.Error,
                cancellationToken,
                includeLive: true,
                message: $"helm install failed for '{chart.ReleaseName}': {stderr}");
        }
    }

    private async Task WaitForPodsReadyAsync(
        KindClusterResource cluster,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Waiting for pods in Kind cluster '{ClusterName}' to become ready...",
            cluster.ClusterName);

        var (exitCode, stdout, stderr) = await RunCommandAsync(
            "kubectl",
            [
                "wait",
                "--for=condition=Ready",
                "pods",
                "--all",
                "--all-namespaces",
                "--timeout=180s",
                "--kubeconfig",
                cluster.KubeconfigPath
            ],
            cancellationToken);

        if (exitCode == 0)
        {
            _logger.LogInformation(
                "All pods in Kind cluster '{ClusterName}' reported ready: {Output}",
                cluster.ClusterName,
                stdout.Trim());
            return;
        }

        _logger.LogWarning(
            "Timed out or failed waiting for all pods in Kind cluster '{ClusterName}' to become ready (exit {ExitCode}): {Output}",
            cluster.ClusterName,
            exitCode,
            string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
    }

    private async Task PublishClusterStateAsync(
        KindClusterResource cluster,
        string state,
        string stateStyle,
        CancellationToken cancellationToken,
        bool includeLive = false,
        string? message = null)
    {
        var properties = BuildStaticProperties(cluster).ToList();

        if (!string.IsNullOrWhiteSpace(message))
        {
            properties.Add(new ResourcePropertySnapshot("kind.status", message));
        }

        if (includeLive)
        {
            properties.AddRange(await BuildLivePropertiesAsync(cluster, cancellationToken));
        }

        await _notifications.PublishUpdateAsync(cluster, snapshot => snapshot with
        {
            State = new ResourceStateSnapshot(state, stateStyle),
            Properties = [.. properties],
        });
    }

    private static IEnumerable<ResourcePropertySnapshot> BuildStaticProperties(KindClusterResource cluster)
    {
        yield return new ResourcePropertySnapshot("kind.clusterName", cluster.ClusterName);
        yield return new ResourcePropertySnapshot("kind.kubeconfig", cluster.KubeconfigPath);
        yield return new ResourcePropertySnapshot("kind.context", $"kind-{cluster.ClusterName}");
        yield return new ResourcePropertySnapshot("kind.kubectl.getPods", $"kubectl --kubeconfig \"{cluster.KubeconfigPath}\" get pods -A -o wide");
        yield return new ResourcePropertySnapshot("kind.helm.list", $"helm list -A --kubeconfig \"{cluster.KubeconfigPath}\"");
        yield return new ResourcePropertySnapshot("kind.kubernetesVersion", cluster.KubernetesVersion ?? "kind default");
        yield return new ResourcePropertySnapshot("kind.workerNodes", cluster.NodeCount.ToString());
        yield return new ResourcePropertySnapshot("kind.configPath", cluster.ConfigPath ?? "(generated/default)");
        yield return new ResourcePropertySnapshot(
            "kind.dockerImages",
            cluster.DockerImages.Count == 0
                ? "(none)"
                : string.Join(Environment.NewLine, cluster.DockerImages.Select(i => $"{i.Image} <- {i.ContextPath}")));
        yield return new ResourcePropertySnapshot("kind.manifests", cluster.ManifestPaths.Count == 0 ? "(none)" : string.Join(Environment.NewLine, cluster.ManifestPaths));
        yield return new ResourcePropertySnapshot(
            "kind.helmCharts",
            cluster.HelmCharts.Count == 0
                ? "(none)"
                : string.Join(Environment.NewLine, cluster.HelmCharts.Select(c => $"{c.ReleaseName} -> {c.Chart} [{c.Namespace}]")));

        foreach (var property in cluster.DashboardProperties)
        {
            yield return property;
        }
    }

    private static async Task<IEnumerable<ResourcePropertySnapshot>> BuildLivePropertiesAsync(
        KindClusterResource cluster,
        CancellationToken cancellationToken)
    {
        var properties = new List<ResourcePropertySnapshot>();

        properties.Add(new ResourcePropertySnapshot(
            "kind.nodes",
            await TryGetCommandOutputAsync("kubectl", ["get", "nodes", "--kubeconfig", cluster.KubeconfigPath, "-o", "wide"], cancellationToken)));

        properties.Add(new ResourcePropertySnapshot(
            "kind.pods",
            await TryGetCommandOutputAsync("kubectl", ["get", "pods", "-A", "--kubeconfig", cluster.KubeconfigPath, "-o", "wide"], cancellationToken)));

        properties.Add(new ResourcePropertySnapshot(
            "kind.helmReleases",
            await TryGetCommandOutputAsync("helm", ["list", "-A", "--kubeconfig", cluster.KubeconfigPath], cancellationToken)));

        return properties;
    }

    private static async Task<string> TryGetCommandOutputAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, stdout, stderr) = await RunCommandAsync(fileName, arguments, cancellationToken);
            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return exitCode == 0
                ? output.Trim()
                : $"exit {exitCode}: {output.Trim()}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    private async Task DeleteClusterAsync(KindClusterResource cluster, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Deleting Kind cluster '{ClusterName}'...", cluster.ClusterName);

        var (exitCode, _, _) = await RunCommandAsync(
            "kind",
            ["delete", "cluster", "--name", cluster.ClusterName],
            cancellationToken);

        if (exitCode != 0)
        {
            _logger.LogWarning(
                "Failed to delete Kind cluster '{ClusterName}'. " +
                "Run 'kind delete cluster --name {ClusterName}' manually to clean up.",
                cluster.ClusterName, cluster.ClusterName);
        }
        else
        {
            _logger.LogInformation("Kind cluster '{ClusterName}' deleted.", cluster.ClusterName);
        }

        // Best-effort kubeconfig cleanup.
        if (File.Exists(cluster.KubeconfigPath))
        {
            try
            {
                File.Delete(cluster.KubeconfigPath);
                _logger.LogDebug("Deleted kubeconfig '{KubeconfigPath}'.", cluster.KubeconfigPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not delete kubeconfig '{KubeconfigPath}'.", cluster.KubeconfigPath);
            }
        }
    }

    // ── Kind config generation ────────────────────────────────────────────────

    private static bool NeedsGeneratedConfig(KindClusterResource cluster) =>
        cluster.NodeCount > 0
        || cluster.KubernetesVersion is not null
        || cluster.PortMappings.Count > 0;

    /// <summary>
    /// Generates a Kind cluster YAML configuration from the resource's properties.
    /// Exposed as <c>internal</c> so tests can verify generation without running <c>kind</c>.
    /// </summary>
    internal static string GenerateKindConfig(KindClusterResource cluster)
    {
        var sb = new StringBuilder();
        sb.AppendLine("kind: Cluster");
        sb.AppendLine("apiVersion: kind.x-k8s.io/v1alpha4");
        sb.AppendLine("nodes:");

        // Control-plane node
        sb.AppendLine("  - role: control-plane");

        if (cluster.KubernetesVersion is not null)
        {
            sb.AppendLine($"    image: kindest/node:{cluster.KubernetesVersion}");
        }

        if (cluster.PortMappings.Count > 0)
        {
            sb.AppendLine("    extraPortMappings:");
            foreach (var pm in cluster.PortMappings)
            {
                sb.AppendLine($"    - containerPort: {pm.ContainerPort}");
                sb.AppendLine($"      hostPort: {pm.HostPort}");
                sb.AppendLine($"      protocol: {pm.Protocol}");
            }
        }

        // Worker nodes
        for (int i = 0; i < cluster.NodeCount; i++)
        {
            sb.AppendLine("  - role: worker");
            if (cluster.KubernetesVersion is not null)
            {
                sb.AppendLine($"    image: kindest/node:{cluster.KubernetesVersion}");
            }
        }

        return sb.ToString();
    }

    // ── Process runner ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs an external process, capturing stdout and stderr without deadlocking on pipe buffers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses <see cref="ProcessStartInfo.ArgumentList"/> (discrete token array) instead of
    /// <see cref="ProcessStartInfo.Arguments"/> (raw string) to prevent command-injection attacks.
    /// </para>
    /// <para>
    /// Drains stdout and stderr concurrently with <see cref="Process.WaitForExitAsync"/> so that
    /// processes that produce large output (e.g., <c>kind create cluster</c>) do not deadlock on
    /// the OS pipe buffer.
    /// </para>
    /// </remarks>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCommandAsync(
        string fileName,
        IEnumerable<string> arguments,
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

        // Use ArgumentList (token array) — never interpolate into Arguments (string).
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain stdout and stderr concurrently with WaitForExitAsync.
        // Not doing so risks filling the OS pipe buffer and deadlocking the process.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Kill the process tree so no orphan child processes are left behind.
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }
}
