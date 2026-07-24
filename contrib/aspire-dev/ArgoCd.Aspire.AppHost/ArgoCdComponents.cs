// contrib/aspire-dev/ArgoCd.Aspire.AppHost/ArgoCdComponents.cs
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Go;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Extension methods that add the native, working-tree Argo CD Go components as Aspire
/// Go application resources.
///
/// Each method mirrors the corresponding <c>Procfile</c> entry as closely as possible: the same
/// <c>go run ./cmd/main.go</c> invocation, the same environment variables, and the same literal
/// ports — except that the Procfile's hardcoded <c>/tmp/argocd-local</c>-style defaults are
/// replaced with the cross-platform temp paths from <see cref="ArgoCdPaths"/> so the loop works
/// on Windows/macOS/Linux alike.
///
/// These are official Aspire <see cref="GoAppResource"/> host processes: no Docker image build,
/// no cross-compilation, no <c>make</c>, and no POSIX shell wrapper are involved. Aspire launches
/// the exact <c>go run ./cmd/main.go</c> package and provides its standard Delve/VS Code debugging
/// integration. Go's build cache makes repeated invocations fast after the first run.
///
/// Two Procfile behaviors are intentionally simplified for the local dev loop and documented in
/// the README:
///   * <c>--otlp-address</c> is only added when <c>ARGOCD_OTLP_ADDRESS</c> is set (the Procfile
///     always passes the flag, empty or not; passing it empty has the same effect as omitting
///     it).
///   * The Procfile's <c>BIN_MODE</c>/pre-built-binary branch is not offered — the dev loop
///     always runs from source via <c>go run</c>, which is the whole point of "edit source,
///     Aspire restarts only that component".
/// </summary>
internal static class ArgoCdComponents
{
    private const string PackagePath = "./cmd/main.go";

    /// <summary>
    /// Application controller (Procfile: <c>controller</c>). Source: <c>controller/</c>.
    /// </summary>
    public static IResourceBuilder<GoAppResource> AddArgoCdApplicationController(
        this IDistributedApplicationBuilder builder, string repoRoot, IResourceBuilder<RedisResource> redis)
    {
        const string component = "app-controller";
        var args = new List<string>
        {
            "--loglevel", "debug",
            "--redis", "localhost:6379",
            "--repo-server", "localhost:8081",
            "--commit-server", "localhost:8086",
        };
        AppendFlagIfEnvSet(args, "--otlp-address", "ARGOCD_OTLP_ADDRESS");
        AppendFlagWithDefault(args, "--application-namespaces", "ARGOCD_APPLICATION_NAMESPACES", "");
        AppendFlagWithDefault(args, "--server-side-diff-enabled", "ARGOCD_APPLICATION_CONTROLLER_SERVER_SIDE_DIFF", "false");
        AppendFlagWithDefault(args, "--hydrator-enabled", "ARGOCD_HYDRATOR_ENABLED", "false");

        var coverageDir = ArgoCdPaths.CoverageDir(component);
        ArgoCdPaths.EnsureDirectoryExists(coverageDir);

        return builder.AddGoApp("application-controller", repoRoot, PackagePath)
            .WithAppArgs(args.Cast<object>().ToArray())
            .WithEnvironment("ARGOCD_FAKE_IN_CLUSTER", "true")
            .WithEnvironment("ARGOCD_TLS_DATA_PATH", ArgoCdPaths.TlsDataPath)
            .WithEnvironment("ARGOCD_SSH_DATA_PATH", ArgoCdPaths.SshDataPath)
            .WithEnvironment("ARGOCD_BINARY_NAME", "argocd-application-controller")
            .WithEnvironment("GOCOVERDIR", coverageDir)
            .WithEnvironment("FORCE_LOG_COLORS", "1")
            .WithEnvironment("HOSTNAME", "testappcontroller-1")
            .WithRedisPassword(builder, redis);
    }

    /// <summary>
    /// API server (Procfile: <c>api-server</c>). Source: <c>server/</c>.
    /// </summary>
    public static IResourceBuilder<GoAppResource> AddArgoCdApiServer(
        this IDistributedApplicationBuilder builder, string repoRoot, IResourceBuilder<RedisResource> redis)
    {
        const string component = "api-server";
        var args = new List<string>
        {
            "--loglevel", "debug",
            "--redis", "localhost:6379",
            "--disable-auth=true",
            "--insecure",
            "--dex-server", "http://localhost:5556",
            "--repo-server", "localhost:8081",
            "--port", "8080",
        };
        AppendFlagIfEnvSet(args, "--otlp-address", "ARGOCD_OTLP_ADDRESS");
        AppendFlagWithDefault(args, "--application-namespaces", "ARGOCD_APPLICATION_NAMESPACES", "");
        AppendFlagWithDefault(args, "--hydrator-enabled", "ARGOCD_HYDRATOR_ENABLED", "false");

        var coverageDir = ArgoCdPaths.CoverageDir(component);
        ArgoCdPaths.EnsureDirectoryExists(coverageDir);

        return builder.AddGoApp("api-server", repoRoot, PackagePath)
            .WithAppArgs(args.Cast<object>().ToArray())
            .WithEnvironment("ARGOCD_FAKE_IN_CLUSTER", "true")
            .WithEnvironment("ARGOCD_TLS_DATA_PATH", ArgoCdPaths.TlsDataPath)
            .WithEnvironment("ARGOCD_SSH_DATA_PATH", ArgoCdPaths.SshDataPath)
            .WithEnvironment("ARGOCD_BINARY_NAME", "argocd-server")
            .WithEnvironment("GOCOVERDIR", coverageDir)
            .WithEnvironment("FORCE_LOG_COLORS", "1")
            .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http", isProxied: false)
            .WithRedisPassword(builder, redis);
    }

    /// <summary>
    /// Repo server (Procfile: <c>repo-server</c>). Source: <c>reposerver/</c>.
    /// </summary>
    public static IResourceBuilder<GoAppResource> AddArgoCdRepoServer(
        this IDistributedApplicationBuilder builder, string repoRoot, IResourceBuilder<RedisResource> redis)
    {
        const string component = "repo-server";
        var args = new List<string>
        {
            "--loglevel", "debug",
            "--port", "8081",
            "--redis", "localhost:6379",
        };
        AppendFlagIfEnvSet(args, "--otlp-address", "ARGOCD_OTLP_ADDRESS");

        var coverageDir = ArgoCdPaths.CoverageDir(component);
        ArgoCdPaths.EnsureDirectoryExists(coverageDir);
        ArgoCdPaths.EnsureDirectoryExists(ArgoCdPaths.GpgKeysPath);
        ArgoCdPaths.EnsureDirectoryExists(ArgoCdPaths.GpgSourcePath);
        ArgoCdPaths.EnsureDirectoryExists(ArgoCdPaths.TlsDataPath);
        ArgoCdPaths.EnsureDirectoryExists(ArgoCdPaths.SshDataPath);

        var resource = builder.AddGoApp("repo-server", repoRoot, PackagePath)
            .WithAppArgs(args.Cast<object>().ToArray())
            .WithEnvironment("ARGOCD_FAKE_IN_CLUSTER", "true")
            .WithEnvironment("ARGOCD_GNUPGHOME", ArgoCdPaths.GpgKeysPath)
            .WithEnvironment("ARGOCD_PLUGINSOCKFILEPATH", ArgoCdPaths.CmpPluginSocketPath(repoRoot))
            .WithEnvironment("ARGOCD_GPG_DATA_PATH", ArgoCdPaths.GpgSourcePath)
            .WithEnvironment("ARGOCD_TLS_DATA_PATH", ArgoCdPaths.TlsDataPath)
            .WithEnvironment("ARGOCD_SSH_DATA_PATH", ArgoCdPaths.SshDataPath)
            .WithEnvironment("ARGOCD_BINARY_NAME", "argocd-repo-server")
            .WithEnvironment("ARGOCD_GPG_ENABLED", Environment.GetEnvironmentVariable("ARGOCD_GPG_ENABLED") is { Length: > 0 } gpgEnabled ? gpgEnabled : "false")
            .WithEnvironment("GOCOVERDIR", coverageDir)
            .WithEnvironment("FORCE_LOG_COLORS", "1")
            .WithHttpEndpoint(port: 8081, targetPort: 8081, name: "http", isProxied: false)
            .WithRedisPassword(builder, redis);

        // Optional passthrough matching the Procfile's `export GIT_CONFIG_GLOBAL=$ARGOCD_GIT_CONFIG`
        // behavior — only wired when the developer has opted in via ARGOCD_GIT_CONFIG.
        var gitConfig = Environment.GetEnvironmentVariable("ARGOCD_GIT_CONFIG");
        if (!string.IsNullOrEmpty(gitConfig))
        {
            resource = resource
                .WithEnvironment("GIT_CONFIG_GLOBAL", gitConfig)
                .WithEnvironment("GIT_CONFIG_NOSYSTEM", "1");
        }

        return resource;
    }

    /// <summary>
    /// Commit server (Procfile: <c>commit-server</c>). Source: <c>commitserver/</c>.
    /// Note: the Procfile entry for commit-server does not set <c>ARGOCD_FAKE_IN_CLUSTER</c> or
    /// the TLS/SSH data paths, so this method matches that omission exactly.
    /// </summary>
    public static IResourceBuilder<GoAppResource> AddArgoCdCommitServer(
        this IDistributedApplicationBuilder builder, string repoRoot)
    {
        const string component = "commit-server";
        var args = new List<string>
        {
            "--loglevel", "debug",
            "--port", "8086",
        };

        var coverageDir = ArgoCdPaths.CoverageDir(component);
        ArgoCdPaths.EnsureDirectoryExists(coverageDir);

        return builder.AddGoApp("commit-server", repoRoot, PackagePath)
            .WithAppArgs(args.Cast<object>().ToArray())
            .WithEnvironment("ARGOCD_BINARY_NAME", "argocd-commit-server")
            .WithEnvironment("GOCOVERDIR", coverageDir)
            .WithEnvironment("FORCE_LOG_COLORS", "1")
            .WithHttpEndpoint(port: 8086, targetPort: 8086, name: "http", isProxied: false);
    }

    /// <summary>
    /// ApplicationSet controller (Procfile: <c>applicationset-controller</c>). Source: <c>applicationset/</c>.
    /// </summary>
    public static IResourceBuilder<GoAppResource> AddArgoCdApplicationSetController(
        this IDistributedApplicationBuilder builder, string repoRoot)
    {
        const string component = "applicationset-controller";
        var args = new List<string>
        {
            "--loglevel", "debug",
            "--metrics-addr", "localhost:12345",
            "--probe-addr", "localhost:12346",
            "--webhook-addr", "localhost:7001",
            "--argocd-repo-server", "localhost:8081",
        };

        var coverageDir = ArgoCdPaths.CoverageDir(component);
        ArgoCdPaths.EnsureDirectoryExists(coverageDir);

        return builder.AddGoApp("applicationset-controller", repoRoot, PackagePath)
            .WithAppArgs(args.Cast<object>().ToArray())
            .WithEnvironment("ARGOCD_FAKE_IN_CLUSTER", "true")
            .WithEnvironment("ARGOCD_TLS_DATA_PATH", ArgoCdPaths.TlsDataPath)
            .WithEnvironment("ARGOCD_SSH_DATA_PATH", ArgoCdPaths.SshDataPath)
            .WithEnvironment("ARGOCD_BINARY_NAME", "argocd-applicationset-controller")
            .WithEnvironment("ARGOCD_APPLICATIONSET_CONTROLLER_ENABLE_PROGRESSIVE_SYNCS",
                Environment.GetEnvironmentVariable("ARGOCD_APPLICATIONSET_CONTROLLER_ENABLE_PROGRESSIVE_SYNCS") is { Length: > 0 } progressiveSyncs ? progressiveSyncs : "true")
            .WithEnvironment("GOCOVERDIR", coverageDir)
            .WithEnvironment("FORCE_LOG_COLORS", "4")
            .WithHttpEndpoint(port: 12345, targetPort: 12345, name: "metrics", isProxied: false)
            .WithHttpEndpoint(port: 12346, targetPort: 12346, name: "probe", isProxied: false)
            .WithHttpEndpoint(port: 7001, targetPort: 7001, name: "webhook", isProxied: false);
    }

    /// <summary>
    /// Notifications controller (Procfile: <c>notification</c>). Source: <c>notification_controller/</c>.
    /// Note: the Procfile entry for notifications does not set <c>ARGOCD_SSH_DATA_PATH</c>, so
    /// this method matches that omission exactly.
    /// </summary>
    public static IResourceBuilder<GoAppResource> AddArgoCdNotificationsController(
        this IDistributedApplicationBuilder builder, string repoRoot)
    {
        const string component = "notification";
        var args = new List<string>
        {
            "--loglevel", "debug",
        };
        AppendFlagWithDefault(args, "--application-namespaces", "ARGOCD_APPLICATION_NAMESPACES", "");
        AppendFlagWithDefault(args, "--self-service-notification-enabled", "ARGOCD_NOTIFICATION_CONTROLLER_SELF_SERVICE_NOTIFICATION_ENABLED", "false");

        var coverageDir = ArgoCdPaths.CoverageDir(component);
        ArgoCdPaths.EnsureDirectoryExists(coverageDir);

        return builder.AddGoApp("notifications-controller", repoRoot, PackagePath)
            .WithAppArgs(args.Cast<object>().ToArray())
            .WithEnvironment("ARGOCD_FAKE_IN_CLUSTER", "true")
            .WithEnvironment("ARGOCD_TLS_DATA_PATH", ArgoCdPaths.TlsDataPath)
            .WithEnvironment("ARGOCD_BINARY_NAME", "argocd-notifications")
            .WithEnvironment("GOCOVERDIR", coverageDir)
            .WithEnvironment("FORCE_LOG_COLORS", "4");
    }

    /// <summary>
    /// Config Management Plugin server (Procfile: <c>cmp-server</c>). Source: <c>cmpserver/</c>.
    ///
    /// The real cmp-server communicates over a Unix domain socket
    /// (<c>ARGOCD_PLUGINSOCKFILEPATH</c>), which .NET/Windows cannot reliably create/listen on in
    /// the same way as Linux/macOS. This resource is therefore opt-in only (never added by
    /// default) and is refused with an actionable error on Windows. See README "CMP" section.
    /// </summary>
    public static IResourceBuilder<GoAppResource> AddArgoCdCmpServer(
        this IDistributedApplicationBuilder builder, string repoRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The cmp-server component uses a Unix domain socket (ARGOCD_PLUGINSOCKFILEPATH) " +
                "for repo-server<->cmp-server communication, which is not supported by this dev " +
                "loop on Windows. Run the Aspire dev loop under WSL2/Linux/macOS to opt into " +
                "ARGOCD_ASPIRE_ENABLE_CMP=true, or omit it on Windows (repo-server still runs " +
                "fine without it — only custom config-management-plugin sources are affected).");
        }

        var args = new List<string>
        {
            "--config-dir-path", "./test/cmp",
            "--loglevel", "debug",
        };
        AppendFlagIfEnvSet(args, "--otlp-address", "ARGOCD_OTLP_ADDRESS");

        return builder.AddGoApp("cmp-server", repoRoot, PackagePath)
            .WithAppArgs(args.Cast<object>().ToArray())
            .WithEnvironment("ARGOCD_FAKE_IN_CLUSTER", "true")
            .WithEnvironment("ARGOCD_BINARY_NAME", "argocd-cmp-server")
            .WithEnvironment("ARGOCD_PLUGINSOCKFILEPATH", ArgoCdPaths.CmpPluginSocketPath(repoRoot))
            .WithEnvironment("FORCE_LOG_COLORS", "1");
    }

    /// <summary>
    /// Always appends <c>--flag=value</c>, using the environment variable's value when set,
    /// otherwise <paramref name="defaultValue"/>. Mirrors Procfile <c>${VAR:-default}</c> bash
    /// expansions, which always pass the flag.
    /// </summary>
    private static void AppendFlagWithDefault(List<string> args, string flagName, string envVarName, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(envVarName);
        args.Add($"{flagName}={(string.IsNullOrEmpty(value) ? defaultValue : value)}");
    }

    /// <summary>
    /// Appends <c>--flag value</c> only when the environment variable is set and non-empty.
    /// Used for flags such as <c>--otlp-address</c> that the Procfile always technically passes
    /// (possibly empty) but where omission has an identical effect.
    /// </summary>
    private static void AppendFlagIfEnvSet(List<string> args, string flagName, string envVarName)
    {
        var value = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrEmpty(value))
        {
            args.Add(flagName);
            args.Add(value);
        }
    }

    /// <summary>
    /// Threads the Redis resource's generated credential (if any) into a component as the
    /// <c>REDIS_PASSWORD</c> environment variable — the only authentication mechanism Argo CD's
    /// Redis client recognizes (see <c>util/cache/cache.go</c>; there is no <c>--redis-password</c>
    /// CLI flag). When the Redis resource has no password configured (the canonical local,
    /// password-less development default configured in <c>AppHost.cs</c>), this is a no-op,
    /// matching the Procfile's own password-less local default.
    /// </summary>
    private static IResourceBuilder<GoAppResource> WithRedisPassword(
        this IResourceBuilder<GoAppResource> resource,
        IDistributedApplicationBuilder builder,
        IResourceBuilder<RedisResource> redis)
    {
        if (redis.Resource.PasswordParameter is { } password)
        {
            resource = resource.WithEnvironment("REDIS_PASSWORD", builder.CreateResourceBuilder(password));
        }

        return resource;
    }
}
