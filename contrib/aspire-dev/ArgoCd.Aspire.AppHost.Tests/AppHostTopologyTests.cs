using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
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
        var componentsThatReadClusterState = new[]
        {
            "repo-server",
            "api-server",
            "application-controller",
            "applicationset-controller",
            "notifications-controller",
            "dev-mounter",
        };

        foreach (var componentName in componentsThatReadClusterState)
        {
            var component = Assert.Single(builder.Resources, r => r.Name == componentName);

            Assert.Contains(
                component.Annotations.OfType<WaitAnnotation>(),
                wait => wait.Resource == cluster);
        }
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
