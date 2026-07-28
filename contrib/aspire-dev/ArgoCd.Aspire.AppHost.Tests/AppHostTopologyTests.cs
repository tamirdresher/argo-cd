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
    public async Task GoComponentsThatReadClusterConfig_WaitForTheKindCluster()
    {
        await using var builder = await CreateAppHostBuilderAsync();

        var cluster = Assert.Single(builder.Resources, r => r.Name.StartsWith("argocd-dev-", StringComparison.Ordinal));
        var clusterReaders = builder.Resources
            .OfType<GoAppResource>()
            .Where(HasKubeconfigEnvironment)
            .ToArray();

        Assert.NotEmpty(clusterReaders);
        Assert.All(clusterReaders, resource =>
            Assert.Contains(resource.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource == cluster));
    }

    [Fact]
    public async Task CommitServer_IsPureGoAndDoesNotWaitForKubernetes()
    {
        await using var builder = await CreateAppHostBuilderAsync();

        var cluster = Assert.Single(builder.Resources, r => r.Name.StartsWith("argocd-dev-", StringComparison.Ordinal));
        var commitServer = Assert.IsType<GoAppResource>(Assert.Single(builder.Resources, r => r.Name == "commit-server"));

        Assert.False(HasKubeconfigEnvironment(commitServer));
        Assert.DoesNotContain(commitServer.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource == cluster);
    }

    [Fact]
    public async Task EveryGoResourceUsesAPackageDirectory_NotASingleGoFile()
    {
        await using var builder = await CreateAppHostBuilderAsync();

        var goResources = builder.Resources.OfType<GoAppResource>().ToArray();

        Assert.NotEmpty(goResources);
        Assert.All(goResources, resource =>
        {
            var args = GetCommandLineArguments(resource);
            var runIndex = args.IndexOf("run");

            Assert.True(runIndex >= 0 && runIndex + 1 < args.Count, $"{resource.Name} does not declare a `go run` package path.");
            var packagePath = args[runIndex + 1];

            Assert.False(
                packagePath.EndsWith(".go", StringComparison.OrdinalIgnoreCase),
                $"{resource.Name} must use a package directory so `dlv debug` can launch it; found '{packagePath}'.");
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

    private static bool HasKubeconfigEnvironment(IResource resource) =>
        resource.Annotations
            .OfType<EnvironmentCallbackAnnotation>()
            .Any(annotation => EnvironmentCallbackSetsKey(annotation, "KUBECONFIG"));

    private static bool EnvironmentCallbackSetsKey(EnvironmentCallbackAnnotation annotation, string key)
    {
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new TestResource("environment-probe"),
            new Dictionary<string, object>(),
            CancellationToken.None);

        annotation.Callback(context).GetAwaiter().GetResult();

        return context.EnvironmentVariables.ContainsKey(key);
    }

    private static List<string> GetCommandLineArguments(GoAppResource resource)
    {
        var context = new CommandLineArgsCallbackContext([], resource, CancellationToken.None);

        foreach (var annotation in resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            annotation.Callback(context).GetAwaiter().GetResult();
        }

        return context.Args.Select(arg => arg?.ToString() ?? string.Empty).ToList();
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

    private sealed class TestResource(string name) : IResource
    {
        public string Name { get; } = name;

        public ResourceAnnotationCollection Annotations { get; } = [];
    }
}
