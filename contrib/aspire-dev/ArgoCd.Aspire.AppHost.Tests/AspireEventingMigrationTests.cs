using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using ArgoCd.Aspire.AppHost;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

public sealed class AspireEventingMigrationTests
{
    [Fact]
    public void StateBootstrap_UsesCurrentEventingSubscriberShape()
    {
        Assert.True(
            typeof(IDistributedApplicationEventingSubscriber)
                .IsAssignableFrom(typeof(ArgoCdStateBootstrapHook)));
    }

    [Fact]
    public void AddKindCluster_RegistersOneSharedEventingSubscriber_WhenCalledMoreThanOnce()
    {
        var builder = DistributedApplication.CreateBuilder(Array.Empty<string>());
        var baseline = CountEventingSubscribers(builder);

        builder.AddKindCluster("kind-one", "kind-one");
        var afterFirst = CountEventingSubscribers(builder);

        builder.AddKindCluster("kind-two", "kind-two");
        var afterSecond = CountEventingSubscribers(builder);

        Assert.Equal(baseline + 1, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public void KindClusterReadyEvent_IsScopedToItsCluster()
    {
        var cluster = new KindClusterResource(
            "kind",
            "kind-test",
            Path.Combine(Path.GetTempPath(), "kind-test-kubeconfig"));
        var services = new NullServiceProvider();
        var applicationEvent = new KindClusterReadyEvent(
            cluster,
            services,
            NullLogger.Instance);

        Assert.Same(cluster, applicationEvent.Cluster);
        Assert.Same(cluster, applicationEvent.Resource);
        Assert.Same(services, applicationEvent.Services);
    }

    private static int CountEventingSubscribers(IDistributedApplicationBuilder builder) =>
        builder.Services.Count(
            descriptor =>
                descriptor.ServiceType == typeof(IDistributedApplicationEventingSubscriber));

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
