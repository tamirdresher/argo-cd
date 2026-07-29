// contrib/aspire-dev/ArgoCd.Aspire.AppHost.Tests/ArgoCdDexTests.cs
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Go;
using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

/// <summary>
/// Verifies that <see cref="ArgoCdDex"/> reproduces the exact upstream Dex SSO bootstrap flow:
/// a <c>gendexcfg</c> run-to-completion step (mirroring the Procfile's <c>dex:</c> entry and
/// <c>cmd/argocd-dex/commands/argocd_dex.go</c>'s <c>gendexcfg</c> subcommand) that generates a
/// Dex config file, followed by a <c>ghcr.io/dexidp/dex:v2.45.1</c> container that bind-mounts
/// that generated config and only starts once <c>gendexcfg</c> has completed.
///
/// These tests build a real <see cref="IDistributedApplicationBuilder"/> via
/// <see cref="DistributedApplication.CreateBuilder(string[])"/> and inspect the resulting
/// resources' callback annotations directly, without starting the application or requiring
/// Docker/Kind/Go to be installed.
/// </summary>
public sealed class ArgoCdDexTests
{
    
    private static IDistributedApplicationBuilder NewBuilder() =>
        DistributedApplication.CreateBuilder(Array.Empty<string>());

    private readonly struct DisposableBuilder : IDisposable
    {
        public DisposableBuilder(IDistributedApplicationBuilder builder) => Builder = builder;

        public IDistributedApplicationBuilder Builder { get; }

        // IDistributedApplicationBuilder is not itself IDisposable in this Aspire version; this
        // wrapper exists purely so tests can use a symmetric `using` block, matching the pattern
        // established in ArgoCdComponentsTests.cs.
        public void Dispose()
        {
        }
    }

    private static DisposableBuilder NewBuilderDisposable() => new(NewBuilder());

    /// <summary>
    /// Invokes every <see cref="CommandLineArgsCallbackAnnotation"/> on an
    /// <see cref="GoAppResource"/> and returns the fully materialized argument list.
    /// </summary>
    private static async Task<List<string>> GetArgsAsync(IResourceBuilder<GoAppResource> resourceBuilder)
    {
        var args = new List<object>();
        foreach (var annotation in resourceBuilder.Resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            var context = new CommandLineArgsCallbackContext(args, resourceBuilder.Resource, CancellationToken.None);
            await annotation.Callback(context);
        }

        return args.Select(a => a?.ToString() ?? string.Empty).ToList();
    }

    /// <summary>
    /// Invokes every <see cref="CommandLineArgsCallbackAnnotation"/> on a
    /// <see cref="ContainerResource"/> and returns the fully materialized argument list.
    /// </summary>
    private static async Task<List<string>> GetArgsAsync(IResourceBuilder<ContainerResource> resourceBuilder)
    {
        var args = new List<object>();
        foreach (var annotation in resourceBuilder.Resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            var context = new CommandLineArgsCallbackContext(args, resourceBuilder.Resource, CancellationToken.None);
            await annotation.Callback(context);
        }

        return args.Select(a => a?.ToString() ?? string.Empty).ToList();
    }

    /// <summary>
    /// Invokes every <see cref="EnvironmentCallbackAnnotation"/> on an
    /// <see cref="GoAppResource"/> and returns the fully materialized environment dictionary.
    /// </summary>
    private static async Task<Dictionary<string, string>> GetEnvironmentAsync(IResourceBuilder<GoAppResource> resourceBuilder)
    {
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var env = new Dictionary<string, object>();
        foreach (var annotation in resourceBuilder.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            var context = new EnvironmentCallbackContext(executionContext, resourceBuilder.Resource, env, CancellationToken.None);
            await annotation.Callback(context);
        }

        return env.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);
    }

    private static List<EndpointAnnotation> GetEndpoints(IResourceBuilder<ContainerResource> resourceBuilder) =>
        resourceBuilder.Resource.Annotations.OfType<EndpointAnnotation>().ToList();

    private static WaitAnnotation? GetWaitFor(IResource resource, IResource target) =>
        resource.Annotations.OfType<WaitAnnotation>().FirstOrDefault(w => ReferenceEquals(w.Resource, target));

    [Fact]
    public async Task GenDexConfig_MatchesProcfileCommandAndFlags()
    {
        using var builder = NewBuilderDisposable();
        var cluster = builder.Builder.AddKindCluster("test-cluster");
        var dexConfigPath = Path.Combine(Path.GetTempPath(), "argocd-aspire-dex-tests-dex.yaml");

        var gendexcfg = builder.Builder.AddArgoCdGenDexConfig(dexConfigPath, cluster);

        Assert.Equal("go", gendexcfg.Resource.Command);
        Assert.Equal(ArgoCdRepository.Root, gendexcfg.Resource.WorkingDirectory);

        var args = await GetArgsAsync(gendexcfg);
        Assert.Equal(
            new[] { "run", "./cmd", "gendexcfg", "-o", dexConfigPath, "--kubeconfig", cluster.Resource.KubeconfigPath, "-n", "default" },
            args);

        var env = await GetEnvironmentAsync(gendexcfg);
        Assert.Equal("argocd-dex", env["ARGOCD_BINARY_NAME"]);
    }

    [Fact]
    public async Task GenDexConfig_ReceivesKindEnvironment()
    {
        using var builder = NewBuilderDisposable();
        var cluster = builder.Builder.AddKindCluster("test-cluster");
        var dexConfigPath = Path.Combine(Path.GetTempPath(), "argocd-aspire-dex-tests-dex.yaml");

        var gendexcfg = builder.Builder.AddArgoCdGenDexConfig(dexConfigPath, cluster);

        var env = await GetEnvironmentAsync(gendexcfg);
        Assert.Equal(cluster.Resource.KubeconfigPath, env["KUBECONFIG"]);
        Assert.Equal(cluster.Resource.Name, env["K8S_CLUSTER_NAME"]);
    }

    [Fact]
    public void GenDexConfig_WaitsForKindCluster()
    {
        using var builder = NewBuilderDisposable();
        var cluster = builder.Builder.AddKindCluster("test-cluster");
        var dexConfigPath = Path.Combine(Path.GetTempPath(), "argocd-aspire-dex-tests-dex.yaml");

        var gendexcfg = builder.Builder.AddArgoCdGenDexConfig(dexConfigPath, cluster);

        var wait = GetWaitFor(gendexcfg.Resource, cluster.Resource);
        Assert.NotNull(wait);
        Assert.Equal(WaitType.WaitUntilHealthy, wait!.WaitType);
    }

    [Fact]
    public async Task Dex_MatchesProcfileImageEntrypointAndArgs()
    {
        using var builder = NewBuilderDisposable();
        var cluster = builder.Builder.AddKindCluster("test-cluster");
        var dexConfigPath = Path.Combine(Path.GetTempPath(), "argocd-aspire-dex-tests-dex.yaml");
        var gendexcfg = builder.Builder.AddArgoCdGenDexConfig(dexConfigPath, cluster);

        var dex = builder.Builder.AddArgoCdDex(dexConfigPath, gendexcfg);

        Assert.Equal("dex", dex.Resource.Entrypoint);

        var args = await GetArgsAsync(dex);
        Assert.Equal(new[] { "serve", "/dex.yaml" }, args);

        var imageAnnotation = dex.Resource.Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("v2.45.1", imageAnnotation.Tag);
        Assert.Contains("dexidp/dex", imageAnnotation.Image);
    }

    [Fact]
    public void Dex_BindMountsGeneratedConfigAsReadOnly()
    {
        using var builder = NewBuilderDisposable();
        var cluster = builder.Builder.AddKindCluster("test-cluster");
        var dexConfigPath = Path.Combine(Path.GetTempPath(), "argocd-aspire-dex-tests-dex.yaml");
        var gendexcfg = builder.Builder.AddArgoCdGenDexConfig(dexConfigPath, cluster);

        var dex = builder.Builder.AddArgoCdDex(dexConfigPath, gendexcfg);

        var mount = dex.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();
        Assert.Equal(dexConfigPath, mount.Source);
        Assert.Equal("/dex.yaml", mount.Target);
        Assert.True(mount.IsReadOnly);
    }

    [Fact]
    public void Dex_WaitsForGenDexConfigCompletion()
    {
        using var builder = NewBuilderDisposable();
        var cluster = builder.Builder.AddKindCluster("test-cluster");
        var dexConfigPath = Path.Combine(Path.GetTempPath(), "argocd-aspire-dex-tests-dex.yaml");
        var gendexcfg = builder.Builder.AddArgoCdGenDexConfig(dexConfigPath, cluster);

        var dex = builder.Builder.AddArgoCdDex(dexConfigPath, gendexcfg);

        var wait = GetWaitFor(dex.Resource, gendexcfg.Resource);
        Assert.NotNull(wait);
        Assert.Equal(WaitType.WaitForCompletion, wait!.WaitType);
    }

    [Fact]
    public void Dex_ExposesHttpEndpointWithContainerTargetPort5556()
    {
        using var builder = NewBuilderDisposable();
        var cluster = builder.Builder.AddKindCluster("test-cluster");
        var dexConfigPath = Path.Combine(Path.GetTempPath(), "argocd-aspire-dex-tests-dex.yaml");
        var gendexcfg = builder.Builder.AddArgoCdGenDexConfig(dexConfigPath, cluster);

        var dex = builder.Builder.AddArgoCdDex(dexConfigPath, gendexcfg);

        var endpoint = Assert.Single(GetEndpoints(dex));
        Assert.Equal("http", endpoint.Name);
        Assert.Null(endpoint.Port);
        Assert.Equal(5556, endpoint.TargetPort);
        Assert.True(endpoint.IsProxied);
    }

    [Fact]
    public async Task GenDexConfigAndDex_UseTheSameConfigPath()
    {
        // Proves the two resources are wired together consistently: the path gendexcfg is told
        // to write to is exactly the path dex bind-mounts into the container, so a functional
        // Dex config always exists by the time the dex container starts.
        using var builder = NewBuilderDisposable();
        var cluster = builder.Builder.AddKindCluster("test-cluster");
        var dexConfigPath = Path.Combine(Path.GetTempPath(), "argocd-aspire-dex-tests-dex.yaml");
        var gendexcfg = builder.Builder.AddArgoCdGenDexConfig(dexConfigPath, cluster);
        var dex = builder.Builder.AddArgoCdDex(dexConfigPath, gendexcfg);

        var gendexcfgArgs = await GetArgsAsync(gendexcfg);
        var mount = dex.Resource.Annotations.OfType<ContainerMountAnnotation>().Single();

        var outputFlagIndex = gendexcfgArgs.IndexOf("-o");
        Assert.True(outputFlagIndex >= 0 && outputFlagIndex + 1 < gendexcfgArgs.Count);
        Assert.Equal(gendexcfgArgs[outputFlagIndex + 1], mount.Source);
    }
}
