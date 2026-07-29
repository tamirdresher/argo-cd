// contrib/aspire-dev/ArgoCd.Aspire.AppHost.Tests/ArgoCdComponentsTests.cs
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Go;
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
    
    private static IDistributedApplicationBuilder NewBuilder() =>
        DistributedApplication.CreateBuilder(Array.Empty<string>());

    /// <summary>
    /// Invokes every <see cref="CommandLineArgsCallbackAnnotation"/> on the resource and returns
    /// the fully materialized argument list, in the order the Aspire executor would see them.
    /// </summary>
    private static async Task<List<string>> GetArgsAsync(IResourceBuilder<GoAppResource> resourceBuilder)
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

    private static List<EndpointAnnotation> GetEndpoints(IResourceBuilder<GoAppResource> resourceBuilder) =>
        resourceBuilder.Resource.Annotations.OfType<EndpointAnnotation>().ToList();

    private static bool HasHealthCheck(IResourceBuilder<GoAppResource> resourceBuilder) =>
        resourceBuilder.Resource.Annotations.OfType<HealthCheckAnnotation>().Any();

    private static string HostAndPort<T>(IResourceBuilder<T> resourceBuilder, string endpointName)
        where T : IResourceWithEndpoints =>
        ReferenceExpression.Create($"{resourceBuilder.Resource.GetEndpoint(endpointName).Property(EndpointProperty.HostAndPort)}").ToString()!;

    private static string Url<T>(IResourceBuilder<T> resourceBuilder, string endpointName)
        where T : IResourceWithEndpoints =>
        ReferenceExpression.Create($"{resourceBuilder.Resource.GetEndpoint(endpointName).Property(EndpointProperty.Url)}").ToString()!;

    private static string TargetPort<T>(IResourceBuilder<T> resourceBuilder, string endpointName)
        where T : IResourceWithEndpoints =>
        ReferenceExpression.Create($"{resourceBuilder.Resource.GetEndpoint(endpointName).Property(EndpointProperty.TargetPort)}").ToString()!;

    /// <summary>
    /// Like <see cref="GetEnvironmentAsync"/>, but returns the raw (unstringified) values placed
    /// into the environment dictionary by callback annotations. This is required to verify
    /// <c>REDIS_PASSWORD</c> wiring, since <c>WithEnvironment(string, IResourceBuilder&lt;ParameterResource&gt;)</c>
    /// stores the live <see cref="ParameterResource"/> reference (for lazy resolution at run/publish
    /// time) rather than a materialized string — stringifying it here would defeat the purpose of
    /// proving the *same* parameter instance backs both the Redis resource and the component.
    /// </summary>
    private static async Task<Dictionary<string, object?>> GetRawEnvironmentAsync(IResourceBuilder<GoAppResource> resourceBuilder)
    {
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var env = new Dictionary<string, object>();
        foreach (var annotation in resourceBuilder.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            var context = new EnvironmentCallbackContext(executionContext, resourceBuilder.Resource, env, CancellationToken.None);
            await annotation.Callback(context);
        }

        return env.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
    }

    [Fact]
    public async Task ApplicationController_MatchesProcfileCommandAndFlags()
    {
        using var builder = NewBuilderDisposable();
        var redis = builder.Builder.AddRedis("redis").WithPassword(null);
        var repoServer = builder.Builder.AddArgoCdRepoServer(redis);
        var commitServer = builder.Builder.AddArgoCdCommitServer();
        var resource = builder.Builder.AddArgoCdApplicationController(redis, repoServer, commitServer);

        Assert.Equal("application-controller", resource.Resource.Name);
        Assert.Equal("go", resource.Resource.Command);
        Assert.Equal(ArgoCdRepository.Root, resource.Resource.WorkingDirectory);

        var args = await GetArgsAsync(resource);
        Assert.Equal(new[]
        {
            "run", "./cmd",
            "--loglevel", "debug",
            "--repo-server", HostAndPort(repoServer, "http"),
            "--commit-server", HostAndPort(commitServer, "http"),
            "--application-namespaces=",
            "--server-side-diff-enabled=false",
            "--hydrator-enabled=false",
        }, args);

        var env = await GetEnvironmentAsync(resource);
        Assert.Equal("true", env["ARGOCD_FAKE_IN_CLUSTER"]);
        Assert.Equal("argocd-application-controller", env["ARGOCD_BINARY_NAME"]);
        Assert.Equal("1", env["FORCE_LOG_COLORS"]);
        Assert.Equal("testappcontroller-1", env["HOSTNAME"]);
        Assert.True(env.ContainsKey("ARGOCD_TLS_DATA_PATH"));
        Assert.True(env.ContainsKey("ARGOCD_SSH_DATA_PATH"));
        Assert.True(env.ContainsKey("GOCOVERDIR"));
        Assert.True(env.ContainsKey("REDIS_SERVER"));

        Assert.Empty(GetEndpoints(resource));
    }

    [Fact]
    public async Task ApiServer_MatchesProcfileCommandFlagsAndPort()
    {
        using var builder = NewBuilderDisposable();
        var redis = builder.Builder.AddRedis("redis").WithPassword(null);
        var repoServer = builder.Builder.AddArgoCdRepoServer(redis);
        var resource = builder.Builder.AddArgoCdApiServer(redis, repoServer);

        Assert.Equal("api-server", resource.Resource.Name);
        Assert.Equal("go", resource.Resource.Command);

        var args = await GetArgsAsync(resource);
        Assert.Equal(new[]
        {
            "run", "./cmd",
            "--loglevel", "debug",
            "--disable-auth=true",
            "--insecure",
            "--dex-server", "http://localhost:5556",
            "--repo-server", HostAndPort(repoServer, "http"),
            "--port", "8080",
            "--application-namespaces=",
            "--hydrator-enabled=false",
        }, args);

        var env = await GetEnvironmentAsync(resource);
        Assert.Equal("true", env["ARGOCD_FAKE_IN_CLUSTER"]);
        Assert.Equal("argocd-server", env["ARGOCD_BINARY_NAME"]);
        Assert.True(env.ContainsKey("REDIS_SERVER"));

        var endpoints = GetEndpoints(resource);
        var http = Assert.Single(endpoints);
        Assert.Equal("http", http.Name);
        Assert.Equal(8080, http.Port);
        Assert.Equal(8080, http.TargetPort);
        Assert.False(http.IsProxied);
        Assert.True(HasHealthCheck(resource));
    }

    [Fact]
    public async Task ApiServer_UsesDexEndpointReference_WhenDexResourceIsProvided()
    {
        using var builder = NewBuilderDisposable();
        var redis = builder.Builder.AddRedis("redis").WithPassword(null);
        var repoServer = builder.Builder.AddArgoCdRepoServer(redis);
        var gendexcfg = builder.Builder.AddGoApp("gendexcfg", ArgoCdRepository.Root, "./cmd");
        var dex = builder.Builder.AddArgoCdDex(ArgoCdPaths.DexConfigPath(), gendexcfg);
        var resource = builder.Builder.AddArgoCdApiServer(redis, repoServer, dex);

        var args = await GetArgsAsync(resource);
        var index = args.IndexOf("--dex-server");
        Assert.True(index >= 0, "api-server should pass --dex-server");
        Assert.Equal(Url(dex, "http"), args[index + 1]);
    }

    [Fact]
    public async Task RepoServer_MatchesProcfileCommandFlagsPortAndGpgPaths()
    {
        using var builder = NewBuilderDisposable();
        var redis = builder.Builder.AddRedis("redis").WithPassword(null);
        var resource = builder.Builder.AddArgoCdRepoServer(redis);

        Assert.Equal("repo-server", resource.Resource.Name);

        var args = await GetArgsAsync(resource);
        Assert.Equal(new[]
        {
            "run", "./cmd",
            "--loglevel", "debug",
            "--port", TargetPort(resource, "http"),
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
        Assert.True(env.ContainsKey("REDIS_SERVER"));

        var endpoints = GetEndpoints(resource);
        Assert.Equal(2, endpoints.Count);
        var httpEndpoint = Assert.Single(endpoints, e => e.Name == "http");
        Assert.True(httpEndpoint.IsProxied);
        Assert.NotEqual(8081, httpEndpoint.Port);
        Assert.NotEqual(8081, httpEndpoint.TargetPort);
        Assert.Contains(endpoints, e => e.Name == "metrics" && e.Port == 8084 && e.TargetPort == 8084 && !e.IsProxied);
        Assert.True(HasHealthCheck(resource));
    }

    [Fact]
    public async Task CommitServer_OmitsFakeInClusterAndTlsSshPerProcfileParity()
    {
        using var builder = NewBuilderDisposable();
        var resource = builder.Builder.AddArgoCdCommitServer();

        Assert.Equal("commit-server", resource.Resource.Name);

        var args = await GetArgsAsync(resource);
        Assert.Equal(new[] { "run", "./cmd", "--loglevel", "debug", "--port", TargetPort(resource, "http") }, args);

        var env = await GetEnvironmentAsync(resource);
        Assert.Equal("argocd-commit-server", env["ARGOCD_BINARY_NAME"]);
        Assert.Equal("1", env["FORCE_LOG_COLORS"]);
        Assert.False(env.ContainsKey("ARGOCD_FAKE_IN_CLUSTER"));
        Assert.False(env.ContainsKey("ARGOCD_TLS_DATA_PATH"));
        Assert.False(env.ContainsKey("ARGOCD_SSH_DATA_PATH"));

        var endpoints = GetEndpoints(resource);
        Assert.Equal(2, endpoints.Count);
        var httpEndpoint = Assert.Single(endpoints, e => e.Name == "http");
        Assert.True(httpEndpoint.IsProxied);
        Assert.NotEqual(8086, httpEndpoint.Port);
        Assert.NotEqual(8086, httpEndpoint.TargetPort);
        Assert.Contains(endpoints, e => e.Name == "metrics" && e.Port == 8087 && e.TargetPort == 8087 && !e.IsProxied);
        Assert.True(HasHealthCheck(resource));
    }

    [Fact]
    public async Task ApplicationSetController_HasThreeEndpointsAndProgressiveSyncsDefault()
    {
        using var builder = NewBuilderDisposable();
        var redis = builder.Builder.AddRedis("redis").WithPassword(null);
        var repoServer = builder.Builder.AddArgoCdRepoServer(redis);
        var resource = builder.Builder.AddArgoCdApplicationSetController(repoServer);

        Assert.Equal("applicationset-controller", resource.Resource.Name);

        var args = await GetArgsAsync(resource);
        Assert.Equal(new[]
        {
            "run", "./cmd",
            "--loglevel", "debug",
            "--metrics-addr", "localhost:12345",
            "--probe-addr", "localhost:12346",
            "--webhook-addr", "localhost:7001",
            "--argocd-repo-server", HostAndPort(repoServer, "http"),
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
        Assert.True(HasHealthCheck(resource));
    }

    [Fact]
    public async Task NotificationsController_OmitsSshDataPathPerProcfileParity()
    {
        using var builder = NewBuilderDisposable();
        var resource = builder.Builder.AddArgoCdNotificationsController();

        Assert.Equal("notifications-controller", resource.Resource.Name);

        var args = await GetArgsAsync(resource);
        Assert.Equal(new[]
        {
            "run", "./cmd",
            "--loglevel", "debug",
            "--application-namespaces=",
            "--self-service-notification-enabled=false",
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
            var ex = Assert.Throws<PlatformNotSupportedException>(() => builder.Builder.AddArgoCdCmpServer());
            Assert.Contains("WSL2", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var resource = builder.Builder.AddArgoCdCmpServer();
            Assert.Equal("cmp-server", resource.Resource.Name);

            var args = await GetArgsAsync(resource);
            Assert.Equal(new[] { "run", "./cmd", "--config-dir-path", "./test/cmp", "--loglevel", "debug" }, args);

            var env = await GetEnvironmentAsync(resource);
            Assert.Equal("true", env["ARGOCD_FAKE_IN_CLUSTER"]);
            Assert.Equal("argocd-cmp-server", env["ARGOCD_BINARY_NAME"]);
            Assert.True(env.ContainsKey("ARGOCD_PLUGINSOCKFILEPATH"));
        }
    }

    [Fact]
    public void CoreComponents_UseOfficialGoAppResourceShape()
    {
        using var builder = NewBuilderDisposable();
        var redis = builder.Builder.AddRedis("redis").WithPassword(null);
        var repoServer = builder.Builder.AddArgoCdRepoServer(redis);
        var commitServer = builder.Builder.AddArgoCdCommitServer();

        IResourceBuilder<GoAppResource>[] resources =
        [
            builder.Builder.AddArgoCdApplicationController(redis, repoServer, commitServer),
            builder.Builder.AddArgoCdApiServer(redis, repoServer),
            repoServer,
            commitServer,
            builder.Builder.AddArgoCdApplicationSetController(repoServer),
            builder.Builder.AddArgoCdNotificationsController(),
        ];

        Assert.All(resources, resource => Assert.IsType<GoAppResource>(resource.Resource));
        Assert.All(resources, resource => Assert.Equal("go", resource.Resource.Command));
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
                var redis = builder.Builder.AddRedis("redis").WithPassword(null);
                var repoServer = builder.Builder.AddArgoCdRepoServer(redis);
                var resource = builder.Builder.AddArgoCdApiServer(redis, repoServer);
                var args = await GetArgsAsync(resource);
                Assert.DoesNotContain("--otlp-address", args);
            }

            Environment.SetEnvironmentVariable("ARGOCD_OTLP_ADDRESS", "localhost:4317");
            using (var builder = NewBuilderDisposable())
            {
                var redis = builder.Builder.AddRedis("redis").WithPassword(null);
                var repoServer = builder.Builder.AddArgoCdRepoServer(redis);
                var resource = builder.Builder.AddArgoCdApiServer(redis, repoServer);
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

    /// <summary>
    /// Proves the fix for the CRITICAL "Redis auth mismatch" finding: when the Redis resource is
    /// configured with a generated password (Aspire's own default — <c>AddRedis</c> without
    /// <c>.WithPassword(null)</c>), every component that talks to Redis receives that *exact same*
    /// <see cref="ParameterResource"/> instance via the <c>REDIS_PASSWORD</c> environment variable
    /// (the only mechanism <c>util/cache/cache.go</c> recognizes — <c>--redis</c> is host:port only
    /// and never carries credentials). This guarantees the resource and the components can never
    /// drift out of sync, unlike the previous no-op wiring that produced NOAUTH at runtime.
    /// </summary>
    [Fact]
    public async Task RedisCredentials_AreConsistentAcrossComponents_WhenRedisHasGeneratedPassword()
    {
        using var builder = NewBuilderDisposable();
        var redis = builder.Builder.AddRedis("redis");
        Assert.NotNull(redis.Resource.PasswordParameter);

        var repoServer = builder.Builder.AddArgoCdRepoServer(redis);
        var commitServer = builder.Builder.AddArgoCdCommitServer();
        var apiServer = builder.Builder.AddArgoCdApiServer(redis, repoServer);
        var applicationController = builder.Builder.AddArgoCdApplicationController(redis, repoServer, commitServer);

        foreach (var resource in new[] { apiServer, repoServer, applicationController })
        {
            var rawEnv = await GetRawEnvironmentAsync(resource);
            Assert.True(rawEnv.TryGetValue("REDIS_PASSWORD", out var value), $"{resource.Resource.Name} is missing REDIS_PASSWORD");
            Assert.Same(redis.Resource.PasswordParameter, value);

            // The Redis endpoint is supplied through REDIS_SERVER so Aspire can resolve the
            // dynamically allocated TCP endpoint; credentials must never leak into argv.
            var args = await GetArgsAsync(resource);
            Assert.DoesNotContain("--redis", args);
            Assert.True(rawEnv.ContainsKey("REDIS_SERVER"), $"{resource.Resource.Name} is missing REDIS_SERVER");
        }
    }

    /// <summary>
    /// Complements <see cref="RedisCredentials_AreConsistentAcrossComponents_WhenRedisHasGeneratedPassword"/>:
    /// when Redis is explicitly configured password-less (the canonical local-dev default this repo
    /// ships in <c>AppHost.cs</c>, matching the Procfile's own security posture), no component should
    /// receive a <c>REDIS_PASSWORD</c> environment variable — there is no credential to thread, and
    /// asserting its absence guards against a future regression that fabricates one.
    /// </summary>
    [Fact]
    public async Task RedisCredentials_AreOmittedAcrossComponents_WhenRedisIsExplicitlyPasswordless()
    {
        using var builder = NewBuilderDisposable();
        var redis = builder.Builder.AddRedis("redis").WithPassword(null);
        Assert.Null(redis.Resource.PasswordParameter);

        var repoServer = builder.Builder.AddArgoCdRepoServer(redis);
        var commitServer = builder.Builder.AddArgoCdCommitServer();
        var apiServer = builder.Builder.AddArgoCdApiServer(redis, repoServer);
        var applicationController = builder.Builder.AddArgoCdApplicationController(redis, repoServer, commitServer);

        foreach (var resource in new[] { apiServer, repoServer, applicationController })
        {
            var env = await GetEnvironmentAsync(resource);
            Assert.False(env.ContainsKey("REDIS_PASSWORD"), $"{resource.Resource.Name} unexpectedly has REDIS_PASSWORD set for a password-less Redis resource");
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
