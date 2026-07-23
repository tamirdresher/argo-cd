// contrib/aspire-dev/ArgoCd.Aspire.AppHost.Tests/ArgoCdComponentsTests.cs
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

/// <summary>
/// Verifies that each <see cref="ArgoCdComponents"/> extension method reproduces the exact
/// command, arguments, environment variables, and ports of its corresponding <c>Procfile</c>
/// entry (with the documented, intentional cross-platform-path and flag-omission exceptions).
///
/// These tests build a real <see cref="IDistributedApplicationBuilder"/> via
/// <see cref="DistributedApplication.CreateBuilder(string[])"/> (the same factory AppHost.cs
/// itself uses) and invoke the resulting resource's callback annotations directly, without
/// starting the application or requiring Docker/Kind/Node/Go to be installed.
/// </summary>
public sealed class ArgoCdComponentsTests
{
    private static readonly string FakeRepoRoot = Path.Combine(Path.GetTempPath(), "argocd-aspire-tests-repo");

    private static IDistributedApplicationBuilder NewBuilder() =>
        DistributedApplication.CreateBuilder(Array.Empty<string>());

    /// <summary>
    /// Invokes every <see cref="CommandLineArgsCallbackAnnotation"/> on the resource and returns
    /// the fully materialized argument list, in the order the Aspire executor would see them.
    /// </summary>
    private static async Task<List<string>> GetArgsAsync(IResourceBuilder<ExecutableResource> resourceBuilder)
    {
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var args = new List<object>();
        foreach (var annotation in resourceBuilder.Resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            var context = new CommandLineArgsCallbackContext(args, resourceBuilder.Resource, CancellationToken.None)
            {
            };
            await annotation.Callback(context);
        }

        return args.Select(a => a?.ToString() ?? string.Empty).ToList();
    }

    /// <summary>
    /// Invokes every <see cref="EnvironmentCallbackAnnotation"/> on the resource and returns the
    /// fully materialized environment variable dictionary.
    /// </summary>
    private static async Task<Dictionary<string, string>> GetEnvironmentAsync(IResourceBuilder<ExecutableResource> resourceBuilder)
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

    private static List<EndpointAnnotation> GetEndpoints(IResourceBuilder<ExecutableResource> resourceBuilder) =>
        resourceBuilder.Resource.Annotations.OfType<EndpointAnnotation>().ToList();

    [Fact]
    public async Task ApplicationController_MatchesProcfileCommandAndFlags()
    {
        using var builder = NewBuilderDisposable();
        var resource = builder.Builder.AddArgoCdApplicationController(FakeRepoRoot);

        Assert.Equal("application-controller", resource.Resource.Name);
        Assert.Equal("go", resource.Resource.Command);
        Assert.Equal(FakeRepoRoot, resource.Resource.WorkingDirectory);

        var args = await GetArgsAsync(resource);
        Assert.Equal(new[]
        {
            "run", "./cmd/main.go",
            "--loglevel", "debug",
            "--redis", "localhost:6379",
            "--repo-server", "localhost:8081",
            "--commit-server", "localhost:8086",
            "--application-namespaces", "",
            "--server-side-diff-enabled", "false",
            "--hydrator-enabled", "false",
        }, args);

        var env = await GetEnvironmentAsync(resource);
        Assert.Equal("true", env["ARGOCD_FAKE_IN_CLUSTER"]);
        Assert.Equal("argocd-application-controller", env["ARGOCD_BINARY_NAME"]);
        Assert.Equal("1", env["FORCE_LOG_COLORS"]);
        Assert.Equal("testappcontroller-1", env["HOSTNAME"]);
        Assert.True(env.ContainsKey("ARGOCD_TLS_DATA_PATH"));
        Assert.True(env.ContainsKey("ARGOCD_SSH_DATA_PATH"));
        Assert.True(env.ContainsKey("GOCOVERDIR"));

        Assert.Empty(GetEndpoints(resource));
    }

    [Fact]
    public async Task ApiServer_MatchesProcfileCommandFlagsAndPort()
    {
        using var builder = NewBuilderDisposable();
        var resource = builder.Builder.AddArgoCdApiServer(FakeRepoRoot);

        Assert.Equal("api-server", resource.Resource.Name);
        Assert.Equal("go", resource.Resource.Command);

        var args = await GetArgsAsync(resource);
        Assert.Equal(new[]
        {
            "run", "./cmd/main.go",
            "--loglevel", "debug",
            "--redis", "localhost:6379",
            "--disable-auth", "true",
            "--insecure",
            "--dex-server", "http://localhost:5556",
            "--repo-server", "localhost:8081",
            "--port", "8080",
            "--application-namespaces", "",
            "--hydrator-enabled", "false",
        }, args);

        var env = await GetEnvironmentAsync(resource);
        Assert.Equal("true", env["ARGOCD_FAKE_IN_CLUSTER"]);
        Assert.Equal("argocd-server", env["ARGOCD_BINARY_NAME"]);

        var endpoints = GetEndpoints(resource);
        var http = Assert.Single(endpoints);
        Assert.Equal("http", http.Name);
        Assert.Equal(8080, http.Port);
        Assert.Equal(8080, http.TargetPort);
        Assert.False(http.IsProxied);
    }

    [Fact]
    public async Task RepoServer_MatchesProcfileCommandFlagsPortAndGpgPaths()
    {
        using var builder = NewBuilderDisposable();
        var resource = builder.Builder.AddArgoCdRepoServer(FakeRepoRoot);

        Assert.Equal("repo-server", resource.Resource.Name);

        var args = await GetArgsAsync(resource);
        Assert.Equal(new[]
        {
            "run", "./cmd/main.go",
            "--loglevel", "debug",
            "--port", "8081",
            "--redis", "localhost:6379",
        }, args);

        var env = await GetEnvironmentAsync(resource);
        Assert.Equal("true", env["ARGOCD_FAKE_IN_CLUSTER"]);
        Assert.Equal("argocd-repo-server", env["ARGOCD_BINARY_NAME"]);
        Assert.True(env.ContainsKey("ARGOCD_GNUPGHOME"));
        Assert.True(env.ContainsKey("ARGOCD_PLUGINSOCKFILEPATH"));
        Assert.True(env.ContainsKey("ARGOCD_GPG_DATA_PATH"));
        Assert.True(env.ContainsKey("ARGOCD_TLS_DATA_PATH"));
        Assert.True(env.ContainsKey("ARGOCD_SSH_DATA_PATH"));
        Assert.Equal("false", env["ARGOCD_GPG_ENABLED"]);

        var endpoints = GetEndpoints(resource);
        var http = Assert.Single(endpoints);
        Assert.Equal("http", http.Name);
        Assert.Equal(8081, http.Port);
        Assert.Equal(8081, http.TargetPort);
        Assert.False(http.IsProxied);
    }

    [Fact]
    public async Task CommitServer_OmitsFakeInClusterAndTlsSshPerProcfileParity()
    {
        using var builder = NewBuilderDisposable();
        var resource = builder.Builder.AddArgoCdCommitServer(FakeRepoRoot);

        Assert.Equal("commit-server", resource.Resource.Name);

        var args = await GetArgsAsync(resource);
        Assert.Equal(new[] { "run", "./cmd/main.go", "--loglevel", "debug", "--port", "8086" }, args);

        var env = await GetEnvironmentAsync(resource);
        Assert.Equal("argocd-commit-server", env["ARGOCD_BINARY_NAME"]);
        Assert.Equal("1", env["FORCE_LOG_COLORS"]);
        Assert.False(env.ContainsKey("ARGOCD_FAKE_IN_CLUSTER"));
        Assert.False(env.ContainsKey("ARGOCD_TLS_DATA_PATH"));
        Assert.False(env.ContainsKey("ARGOCD_SSH_DATA_PATH"));

        var endpoints = GetEndpoints(resource);
        var http = Assert.Single(endpoints);
        Assert.Equal(8086, http.Port);
        Assert.Equal(8086, http.TargetPort);
    }

    [Fact]
    public async Task ApplicationSetController_HasThreeEndpointsAndProgressiveSyncsDefault()
    {
        using var builder = NewBuilderDisposable();
        var resource = builder.Builder.AddArgoCdApplicationSetController(FakeRepoRoot);

        Assert.Equal("applicationset-controller", resource.Resource.Name);

        var args = await GetArgsAsync(resource);
        Assert.Equal(new[]
        {
            "run", "./cmd/main.go",
            "--loglevel", "debug",
            "--metrics-addr", "localhost:12345",
            "--probe-addr", "localhost:12346",
            "--webhook-addr", "localhost:7001",
            "--argocd-repo-server", "localhost:8081",
        }, args);

        var env = await GetEnvironmentAsync(resource);
        Assert.Equal("true", env["ARGOCD_FAKE_IN_CLUSTER"]);
        Assert.Equal("argocd-applicationset-controller", env["ARGOCD_BINARY_NAME"]);
        Assert.Equal("true", env["ARGOCD_APPLICATIONSET_CONTROLLER_ENABLE_PROGRESSIVE_SYNCS"]);
        Assert.Equal("4", env["FORCE_LOG_COLORS"]);

        var endpoints = GetEndpoints(resource);
        Assert.Equal(3, endpoints.Count);
        Assert.Contains(endpoints, e => e.Name == "metrics" && e.Port == 12345 && e.TargetPort == 12345);
        Assert.Contains(endpoints, e => e.Name == "probe" && e.Port == 12346 && e.TargetPort == 12346);
        Assert.Contains(endpoints, e => e.Name == "webhook" && e.Port == 7001 && e.TargetPort == 7001);
        Assert.All(endpoints, e => Assert.False(e.IsProxied));
    }

    [Fact]
    public async Task NotificationsController_OmitsSshDataPathPerProcfileParity()
    {
        using var builder = NewBuilderDisposable();
        var resource = builder.Builder.AddArgoCdNotificationsController(FakeRepoRoot);

        Assert.Equal("notifications-controller", resource.Resource.Name);

        var args = await GetArgsAsync(resource);
        Assert.Equal(new[]
        {
            "run", "./cmd/main.go",
            "--loglevel", "debug",
            "--application-namespaces", "",
            "--self-service-notification-enabled", "false",
        }, args);

        var env = await GetEnvironmentAsync(resource);
        Assert.Equal("true", env["ARGOCD_FAKE_IN_CLUSTER"]);
        Assert.Equal("argocd-notifications", env["ARGOCD_BINARY_NAME"]);
        Assert.Equal("4", env["FORCE_LOG_COLORS"]);
        Assert.True(env.ContainsKey("ARGOCD_TLS_DATA_PATH"));
        Assert.False(env.ContainsKey("ARGOCD_SSH_DATA_PATH"));

        Assert.Empty(GetEndpoints(resource));
    }

    [Fact]
    public async Task CmpServer_ThrowsPlatformNotSupportedOnWindows_OrConfiguresArgsElsewhere()
    {
        using var builder = NewBuilderDisposable();

        if (OperatingSystem.IsWindows())
        {
            var ex = Assert.Throws<PlatformNotSupportedException>(() => builder.Builder.AddArgoCdCmpServer(FakeRepoRoot));
            Assert.Contains("WSL2", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var resource = builder.Builder.AddArgoCdCmpServer(FakeRepoRoot);
            Assert.Equal("cmp-server", resource.Resource.Name);

            var args = await GetArgsAsync(resource);
            Assert.Equal(new[] { "run", "./cmd/main.go", "--config-dir-path", "./test/cmp", "--loglevel", "debug" }, args);

            var env = await GetEnvironmentAsync(resource);
            Assert.Equal("true", env["ARGOCD_FAKE_IN_CLUSTER"]);
            Assert.Equal("argocd-cmp-server", env["ARGOCD_BINARY_NAME"]);
            Assert.True(env.ContainsKey("ARGOCD_PLUGINSOCKFILEPATH"));
        }
    }

    [Fact]
    public async Task OtlpAddress_IsOmittedByDefault_AndAppendedWhenEnvSet()
    {
        var previous = Environment.GetEnvironmentVariable("ARGOCD_OTLP_ADDRESS");
        try
        {
            Environment.SetEnvironmentVariable("ARGOCD_OTLP_ADDRESS", null);
            using (var builder = NewBuilderDisposable())
            {
                var resource = builder.Builder.AddArgoCdApiServer(FakeRepoRoot);
                var args = await GetArgsAsync(resource);
                Assert.DoesNotContain("--otlp-address", args);
            }

            Environment.SetEnvironmentVariable("ARGOCD_OTLP_ADDRESS", "localhost:4317");
            using (var builder = NewBuilderDisposable())
            {
                var resource = builder.Builder.AddArgoCdApiServer(FakeRepoRoot);
                var args = await GetArgsAsync(resource);
                Assert.Contains("--otlp-address", args);
                var index = args.IndexOf("--otlp-address");
                Assert.Equal("localhost:4317", args[index + 1]);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARGOCD_OTLP_ADDRESS", previous);
        }
    }

    private static DisposableBuilder NewBuilderDisposable() => new(NewBuilder());

    /// <summary>
    /// Wraps an <see cref="IDistributedApplicationBuilder"/> so tests can dispose the underlying
    /// host builder's service provider resources (if any) after each assertion, keeping tests
    /// isolated from one another.
    /// </summary>
    private readonly struct DisposableBuilder : IDisposable
    {
        public DisposableBuilder(IDistributedApplicationBuilder builder)
        {
            Builder = builder;
        }

        public IDistributedApplicationBuilder Builder { get; }

        public void Dispose()
        {
            // IDistributedApplicationBuilder itself is not IDisposable in this Aspire version;
            // nothing to release explicitly. The wrapper exists so tests read symmetrically
            // (using/using var) and so a Dispose seam exists if a future Aspire version adds one.
        }
    }
}
