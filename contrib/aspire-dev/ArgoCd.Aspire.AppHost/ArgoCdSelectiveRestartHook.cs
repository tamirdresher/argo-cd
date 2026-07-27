using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Watches each Argo CD Go component's own source directory and, on a debounced file-write,
/// invokes that single resource's built-in <c>restart</c> command (the same command the Aspire
/// dashboard's "Restart" button invokes) — never the process-wide <c>aspire start</c> for the
/// AppHost itself, and never a rebuild/redeploy of a container image.
///
/// This is what makes "editing repo-server's Go source restarts only repo-server" true: each
/// component is registered by <see cref="ArgoCdComponents"/> as an official
/// <c>GoAppResource</c>, so Aspire's
/// built-in restart command for that resource is exactly "stop the process, start it again" —
/// which re-runs <c>go run</c> and therefore picks up the edited source with no separate build
/// step, no image, and no effect on any other component's already-running process.
///
/// There is no built-in Aspire "hot reload on file change" feature for executable resources (as
/// of Aspire.Hosting 13.4.6) — the closest equivalents are dotnet-watch (dotnet-only) and Tilt's
/// own live_update (which this repository's Tiltfile already provides, and which this AppHost is
/// explicitly not trying to replace — see README's "Tilt boundary" section). This hook is a
/// small, self-contained substitute scoped to exactly this repository's Go component layout.
///
/// Directory-to-resource-name mapping matches <see cref="ArgoCdComponents"/>'s Aspire resource
/// names one-for-one (confirmed against the repository's own package layout, not the Procfile's
/// <c>ARGOCD_BINARY_NAME</c> values, which name binaries rather than resources):
/// <c>controller/</c> → <c>application-controller</c>, <c>server/</c> → <c>api-server</c>,
/// <c>reposerver/</c> → <c>repo-server</c>, <c>applicationset/</c> → <c>applicationset-controller</c>,
/// <c>notification_controller/</c> → <c>notifications-controller</c>, <c>commitserver/</c> →
/// <c>commit-server</c>, and (opt-in only, matching <see cref="ArgoCdComponents"/>'s CMP opt-in)
/// <c>cmpserver/</c> → <c>cmp-server</c>. The shared <c>cmd/</c> entrypoint package is intentionally
/// not watched: it is common dispatch code for every binary (selected via
/// <c>ARGOCD_BINARY_NAME</c>), so a change there cannot be attributed to a single component —
/// editing it and needing every component restarted is treated as an accepted, documented
/// limitation (see README) rather than something this hook silently guesses about.
///
/// Registered directly by AppHost.cs as an
/// <see cref="IDistributedApplicationEventingSubscriber"/>.
/// does not modify <see cref="ArgoCdComponents"/> or any vendored Aspire integration.
/// </summary>
public sealed class ArgoCdSelectiveRestartHook(
    ILogger<ArgoCdSelectiveRestartHook> logger,
    IServiceProvider serviceProvider,
    bool enableCmp = false)
    : IDistributedApplicationEventingSubscriber, IAsyncDisposable
{
    /// <summary>
    /// Coalescing window for bursts of filesystem events (editors frequently emit several
    /// write/rename events for a single logical save). A single edit should trigger exactly one
    /// restart, not one per underlying OS event.
    ///
    /// <c>internal</c> (rather than <c>private</c>) solely so <c>ArgoCdSelectiveRestartHookTests</c>
    /// can assert the documented coalescing window value; the Tests project links this source file
    /// directly (see the AppHost source-linked <c>&lt;Compile&gt;</c> items in the Tests
    /// <c>.csproj</c>), so no <c>InternalsVisibleTo</c> attribute is required. No behavior change.
    /// </summary>
    internal static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Repository-root-relative Go package directory → Aspire resource name, for the six
    /// always-on host-process components. CMP is appended separately only when opted in, so that
    /// a watcher for a component that was never started (and therefore has no restart command
    /// annotation) is never created.
    ///
    /// <c>internal</c> so tests can assert this mapping matches <see cref="ArgoCdComponents"/>'s
    /// Aspire resource names exactly, without needing a running AppHost. No behavior change.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> CoreComponentDirectories =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["controller"] = "application-controller",
            ["server"] = "api-server",
            ["reposerver"] = "repo-server",
            ["applicationset"] = "applicationset-controller",
            ["notification_controller"] = "notifications-controller",
            ["commitserver"] = "commit-server",
        };

    internal const string CmpDirectory = "cmpserver";
    internal const string CmpResourceName = "cmp-server";

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, Timer> _pendingRestarts = new(StringComparer.Ordinal);
    private DistributedApplicationModel? _appModel;
    private CancellationToken _shutdownToken;

    /// <summary>
    /// Number of active <see cref="FileSystemWatcher"/> instances currently watching a component
    /// directory. Zero before <see cref="AfterResourcesCreatedAsync"/> runs (or if the repository
    /// root could not be located); otherwise up to 6 (or 7 with CMP opted in) — one per component
    /// directory that exists on disk. Exposed purely for test observability; not used by any
    /// production code path.
    /// </summary>
    internal int WatcherCount => _watchers.Count;

    /// <summary>
    /// Number of debounce timers currently pending (i.e. a file-change event has fired but the
    /// <see cref="DebounceWindow"/> has not yet elapsed). Exposed purely for test observability.
    /// </summary>
    internal int PendingRestartCount
    {
        get
        {
            lock (_pendingRestarts)
            {
                return _pendingRestarts.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task SubscribeAsync(
        IDistributedApplicationEventing eventing,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        eventing.Subscribe<AfterResourcesCreatedEvent>(
            (applicationEvent, token) =>
                AfterResourcesCreatedAsync(applicationEvent.Model, token));
        return Task.CompletedTask;
    }

    public Task AfterResourcesCreatedAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
    {
        _appModel = appModel;
        _shutdownToken = cancellationToken;

        var repoRoot = ArgoCdRepository.Root;

        var directories = new Dictionary<string, string>(CoreComponentDirectories, StringComparer.Ordinal);
        if (enableCmp)
        {
            directories[CmpDirectory] = CmpResourceName;
        }

        foreach (var (relativeDir, resourceName) in directories)
        {
            StartWatching(repoRoot, relativeDir, resourceName);
        }

        return Task.CompletedTask;
    }

    private void StartWatching(string repoRoot, string relativeDir, string resourceName)
    {
        var fullDir = Path.Combine(repoRoot, relativeDir);
        if (!Directory.Exists(fullDir))
        {
            logger.LogWarning(
                "Go source directory '{Directory}' does not exist; skipping selective-restart watch for resource '{Resource}'.",
                fullDir, resourceName);
            return;
        }

        var watcher = new FileSystemWatcher(fullDir)
        {
            Filter = "*.go",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };

        FileSystemEventHandler onChanged = (_, _) => ScheduleRestart(resourceName);
        RenamedEventHandler onRenamed = (_, _) => ScheduleRestart(resourceName);

        watcher.Changed += onChanged;
        watcher.Created += onChanged;
        watcher.Deleted += onChanged;
        watcher.Renamed += onRenamed;
        watcher.Error += (_, e) => logger.LogWarning(
            e.GetException(), "File watcher for '{Directory}' (resource '{Resource}') reported an error; it may stop reporting further changes.",
            fullDir, resourceName);

        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);

        logger.LogInformation(
            "Watching '{Directory}' for *.go changes — edits restart only the '{Resource}' resource.",
            fullDir, resourceName);
    }

    /// <summary>
    /// Coalesces a burst of filesystem events for one resource into a single restart, fired
    /// <see cref="DebounceWindow"/> after the most recent event.
    /// </summary>
    private void ScheduleRestart(string resourceName)
    {
        lock (_pendingRestarts)
        {
            if (_pendingRestarts.TryGetValue(resourceName, out var existingTimer))
            {
                existingTimer.Change(DebounceWindow, Timeout.InfiniteTimeSpan);
                return;
            }

            var timer = new Timer(
                _ => OnDebouncedRestart(resourceName),
                state: null,
                dueTime: DebounceWindow,
                period: Timeout.InfiniteTimeSpan);

            _pendingRestarts[resourceName] = timer;
        }
    }

    private void OnDebouncedRestart(string resourceName)
    {
        lock (_pendingRestarts)
        {
            if (_pendingRestarts.Remove(resourceName, out var timer))
            {
                timer.Dispose();
            }
        }

        // Fire-and-forget from the timer callback thread; RestartResourceAsync never throws.
        _ = RestartResourceAsync(resourceName);
    }

    /// <summary>
    /// Invokes the target resource's own <see cref="KnownResourceCommands.RestartCommand"/>
    /// annotation — the identical mechanism the Aspire dashboard's per-resource "Restart" button
    /// uses — so only that one resource's process is stopped and re-started. Never throws: any
    /// failure is logged and the watcher keeps running so a subsequent edit can be retried.
    /// </summary>
    private async Task RestartResourceAsync(string resourceName)
    {
        var appModel = _appModel;
        if (appModel is null)
        {
            return;
        }

        var resource = appModel.Resources.FirstOrDefault(r => r.Name == resourceName);
        if (resource is null)
        {
            logger.LogWarning(
                "Selective restart: resource '{Resource}' was not found in the application model (it may not have been added to this AppHost run).",
                resourceName);
            return;
        }

        var restartAnnotation = resource.Annotations
            .OfType<ResourceCommandAnnotation>()
            .FirstOrDefault(a => a.Name == KnownResourceCommands.RestartCommand);

        if (restartAnnotation is null)
        {
            logger.LogWarning(
                "Selective restart: resource '{Resource}' does not expose a restart command; it may not be a resource type Aspire can restart on its own.",
                resourceName);
            return;
        }

        logger.LogInformation("Go source change detected for '{Resource}'; restarting only this resource.", resourceName);

        try
        {
            // InteractionInputCollection/InteractionInput are experimental Aspire APIs (ASPIREINTERACTION001).
            // We only need an empty argument set to invoke the built-in restart command programmatically,
            // so the experimental-API warning is suppressed locally rather than at the project level.
#pragma warning disable ASPIREINTERACTION001
            var context = new ExecuteCommandContext
            {
                ServiceProvider = serviceProvider,
                ResourceName = resourceName,
                CancellationToken = _shutdownToken,
                Logger = logger,
                Arguments = new InteractionInputCollection(Array.Empty<InteractionInput>()),
            };
#pragma warning restore ASPIREINTERACTION001

            var result = await restartAnnotation.ExecuteCommand(context);

            if (result.Success)
            {
                logger.LogInformation("Selective restart of '{Resource}' completed successfully.", resourceName);
            }
            else
            {
                logger.LogWarning(
                    "Selective restart of '{Resource}' did not succeed: {Error}",
                    resourceName, result.Message ?? "(no error message)");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Selective restart of '{Resource}' threw an exception.", resourceName);
        }
    }

    /// <summary>
    /// Disposes every <see cref="FileSystemWatcher"/> and any still-pending debounce timer.
    /// Called automatically by the DI container when registered as an eventing subscriber
    /// as the AppHost's own host shuts down, so watcher threads never outlive the AppHost process.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        lock (_pendingRestarts)
        {
            foreach (var timer in _pendingRestarts.Values)
            {
                timer.Dispose();
            }
            _pendingRestarts.Clear();
        }

        return ValueTask.CompletedTask;
    }
}
