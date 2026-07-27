using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using ArgoCd.Aspire.AppHost;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

/// <summary>
/// Covers <see cref="ArgoCdSelectiveRestartHook"/>'s directory-to-resource-name mapping (the exact
/// data that makes "editing repo-server's Go source restarts only repo-server" true) and its
/// <see cref="IDistributedApplicationEventingSubscriber"/> handler behavior against
/// this repository's real, on-disk component directories.
///
/// <see cref="FileSystemWatcher"/>-driven event delivery and the DI-resolved restart-command
/// invocation are intentionally not exercised end-to-end here: they depend on OS file-event timing
/// and a fully constructed <see cref="DistributedApplicationModel"/> with live resource command
/// annotations, which only exist in a running AppHost. What is verified here — the mapping table,
/// the debounce window value, and that watchers are actually created for every directory this
/// repository ships — are the parts of the class most likely to silently drift or regress.
/// </summary>
public sealed class ArgoCdSelectiveRestartHookTests
{
    private static readonly string RepoRoot = ArgoCdRepository.Root;

    /// <summary>
    /// A no-op <see cref="IServiceProvider"/> sufficient for constructing the hook and calling
    /// <see cref="ArgoCdSelectiveRestartHook.AfterResourcesCreatedAsync"/>. The restart-command
    /// path (which is the only consumer of the service provider) is never reached in these tests
    /// because the fake <see cref="DistributedApplicationModel"/> has no resources to restart.
    /// </summary>
    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public void CoreComponentDirectories_MapsExactlySixDirectories_ToDocumentedResourceNames()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["controller"] = "application-controller",
            ["server"] = "api-server",
            ["reposerver"] = "repo-server",
            ["applicationset"] = "applicationset-controller",
            ["notification_controller"] = "notifications-controller",
            ["commitserver"] = "commit-server",
        };

        Assert.Equal(expected.Count, ArgoCdSelectiveRestartHook.CoreComponentDirectories.Count);
        foreach (var (directory, resourceName) in expected)
        {
            Assert.True(
                ArgoCdSelectiveRestartHook.CoreComponentDirectories.TryGetValue(directory, out var actual),
                $"Expected a mapping for directory '{directory}'.");
            Assert.Equal(resourceName, actual);
        }
    }

    [Fact]
    public void CmpDirectoryAndResourceName_MatchArgoCdComponentsCmpOptIn()
    {
        Assert.Equal("cmpserver", ArgoCdSelectiveRestartHook.CmpDirectory);
        Assert.Equal("cmp-server", ArgoCdSelectiveRestartHook.CmpResourceName);
        Assert.False(
            ArgoCdSelectiveRestartHook.CoreComponentDirectories.ContainsKey(ArgoCdSelectiveRestartHook.CmpDirectory),
            "CMP must not be part of the always-on core mapping: it is only watched when explicitly opted in.");
    }

    [Fact]
    public void DebounceWindow_Is500Milliseconds()
    {
        // A single edit typically emits several OS-level file events; this window is what
        // coalesces them into exactly one restart. Asserting the exact value guards against an
        // accidental change silently making restarts noticeably slower or too eager.
        Assert.Equal(TimeSpan.FromMilliseconds(500), ArgoCdSelectiveRestartHook.DebounceWindow);
    }

    [Fact]
    public void Hook_UsesCurrentAspireEventingSubscriberShape()
    {
        Assert.True(
            typeof(IDistributedApplicationEventingSubscriber)
                .IsAssignableFrom(typeof(ArgoCdSelectiveRestartHook)));
    }

    [Fact]
    public void AllCoreComponentDirectories_ExistInThisRepositoryCheckout()
    {
        // If any of these directories were ever renamed or removed upstream, the corresponding
        // watcher would silently never fire (StartWatching logs a warning and returns) rather than
        // failing loudly - so this test locks in that today's mapping matches today's on-disk
        // layout, using the same repo-root resolution the hook itself uses at runtime.
        foreach (var directory in ArgoCdSelectiveRestartHook.CoreComponentDirectories.Keys)
        {
            var fullPath = Path.Combine(RepoRoot, directory);
            Assert.True(Directory.Exists(fullPath), $"Expected component directory '{fullPath}' to exist.");
        }

        var cmpPath = Path.Combine(RepoRoot, ArgoCdSelectiveRestartHook.CmpDirectory);
        Assert.True(Directory.Exists(cmpPath), $"Expected CMP directory '{cmpPath}' to exist.");
    }

    [Fact]
    public async Task AfterResourcesCreatedAsync_CreatesOneWatcherPerExistingCoreDirectory_WhenCmpDisabled()
    {
        var hook = new ArgoCdSelectiveRestartHook(
            NullLogger<ArgoCdSelectiveRestartHook>.Instance,
            new NullServiceProvider(),
            enableCmp: false);

        await using (hook)
        {
            var appModel = new DistributedApplicationModel(Array.Empty<IResource>());

            // Must not throw even though the fake application model has no matching resources:
            // AfterResourcesCreatedAsync only sets up filesystem watchers, it never looks up
            // resources by name (that only happens later, on an actual debounced file event).
            await hook.AfterResourcesCreatedAsync(appModel, CancellationToken.None);

            Assert.Equal(ArgoCdSelectiveRestartHook.CoreComponentDirectories.Count, hook.WatcherCount);
            Assert.Equal(0, hook.PendingRestartCount);
        }
    }

    [Fact]
    public async Task AfterResourcesCreatedAsync_AddsOneMoreWatcher_WhenCmpEnabled()
    {
        var hook = new ArgoCdSelectiveRestartHook(
            NullLogger<ArgoCdSelectiveRestartHook>.Instance,
            new NullServiceProvider(),
            enableCmp: true);

        await using (hook)
        {
            var appModel = new DistributedApplicationModel(Array.Empty<IResource>());
            await hook.AfterResourcesCreatedAsync(appModel, CancellationToken.None);

            Assert.Equal(ArgoCdSelectiveRestartHook.CoreComponentDirectories.Count + 1, hook.WatcherCount);
        }
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentAndDoesNotThrow_AfterWatchersWereCreated()
    {
        var hook = new ArgoCdSelectiveRestartHook(
            NullLogger<ArgoCdSelectiveRestartHook>.Instance,
            new NullServiceProvider(),
            enableCmp: false);

        var appModel = new DistributedApplicationModel(Array.Empty<IResource>());
        await hook.AfterResourcesCreatedAsync(appModel, CancellationToken.None);

        // Disposing twice must be safe: AppHost shutdown paths should never crash even if a hook
        // is disposed more than once (e.g. once by the DI container, once by a using block in a
        // caller). ArgoCdSelectiveRestartHook.DisposeAsync clears its internal lists, so a second
        // call is a cheap no-op rather than a double-dispose exception.
        await hook.DisposeAsync();
        await hook.DisposeAsync();

        Assert.Equal(0, hook.WatcherCount);
        Assert.Equal(0, hook.PendingRestartCount);
    }
}
