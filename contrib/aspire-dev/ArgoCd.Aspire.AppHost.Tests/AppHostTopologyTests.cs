using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Go;
using Aspire.Hosting.Testing;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

[Collection("AppHostTopology")]
public sealed class AppHostTopologyTests
{
    [Fact]
    public async Task ComponentsThatReadArgocdCm_WaitForTheKindCluster()
    {
        await using var builder = await CreateAppHostBuilderAsync();

        var cluster = Assert.Single(builder.Resources, r => r.Name.StartsWith("argocd-dev-", StringComparison.Ordinal));
        var clusterReaders = builder.Resources
            .OfType<GoAppResource>()
            .Where(ReceivesKubeconfig)
            .ToArray();

        Assert.NotEmpty(clusterReaders);
        Assert.All(clusterReaders, component =>
        {
            Assert.Contains(
                component.Annotations.OfType<WaitAnnotation>(),
                wait => wait.Resource == cluster);
        });
    }

    [Fact]
    public async Task ResourcesWithHttpEndpoints_HaveHealthChecks()
    {
        await using var builder = await CreateAppHostBuilderAsync();

        var httpResources = builder.Resources
            .Where(resource => resource.Annotations
                .OfType<EndpointAnnotation>()
                .Any(endpoint => endpoint.UriScheme == "http"))
            .ToArray();

        Assert.NotEmpty(httpResources);
        Assert.All(httpResources, resource =>
        {
            Assert.Contains(
                resource.Annotations.OfType<HealthCheckAnnotation>(),
                healthCheck => !string.IsNullOrWhiteSpace(healthCheck.Key));
        });
    }

    private static async Task<IDistributedApplicationTestingBuilder> CreateAppHostBuilderAsync()
    {
        using var scope = new EnvironmentVariableScope(
            (ArgoCdPrerequisites.SkipEnvironmentVariable, "true"),
            (ArgoCdUiDependencies.SkipEnvironmentVariable, "true"),
            ("ARGOCD_ASPIRE_ENABLE_DEX", null),
            ("ARGOCD_ASPIRE_ENABLE_CMP", null));

        return await DistributedApplicationTestingBuilder.CreateAsync<Projects.ArgoCd_Aspire_AppHost>();
    }

    private static bool ReceivesKubeconfig(IResource resource)
    {
        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            resource,
            environment,
            CancellationToken.None);

        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            annotation.Callback(context).GetAwaiter().GetResult();
        }

        return environment.ContainsKey("KUBECONFIG");
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

}
