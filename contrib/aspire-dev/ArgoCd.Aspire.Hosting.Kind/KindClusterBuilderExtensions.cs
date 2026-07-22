using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics;

namespace Aspire.Hosting;

/// <summary>
/// Provides fluent extension methods for adding and configuring Kind cluster resources
/// in a .NET Aspire <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class KindClusterBuilderExtensions
{
    /// <summary>
    /// Adds a Kind (Kubernetes in Docker) cluster resource to the distributed application.
    /// The cluster is created before the application starts and deleted when it stops.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">
    /// The Aspire resource name. Also used as the Kind cluster name when
    /// <paramref name="clusterName"/> is not specified.
    /// </param>
    /// <param name="clusterName">
    /// The Kind cluster name passed to <c>kind create/delete cluster --name</c>.
    /// Defaults to <paramref name="name"/> when not specified.
    /// Must match <c>^[a-z0-9][a-z0-9\-]*$</c>.
    /// </param>
    /// <param name="kubeconfigPath">
    /// Absolute path where the kubeconfig file will be written by Kind.
    /// Defaults to a file in <see cref="Path.GetTempPath"/> when not specified.
    /// </param>
    /// <returns>A builder for the <see cref="KindClusterResource"/> that supports further configuration.</returns>
    /// <example>
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var cluster = builder
    ///     .AddKindCluster("dev-cluster")
    ///     .WithNodeCount(2)
    ///     .WithKubernetesVersion("v1.31.0");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    public static IResourceBuilder<KindClusterResource> AddKindCluster(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string? clusterName = null,
        string? kubeconfigPath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resolvedClusterName = clusterName ?? name;
        var resolvedKubeconfigPath = kubeconfigPath
            ?? Path.Combine(Path.GetTempPath(), $"kind-{resolvedClusterName}-kubeconfig.yaml");

        var resource = new KindClusterResource(name, resolvedClusterName, resolvedKubeconfigPath);

        // TryAddEnumerable is idempotent: if AddKindCluster is called multiple times, only
        // one KindClusterLifecycleHook singleton is registered.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDistributedApplicationLifecycleHook, KindClusterLifecycleHook>());

        var resourceBuilder = builder.AddResource(resource);

        resourceBuilder.WithCommand(
            name: "show-kubectl",
            displayName: "Show kubectl",
            executeCommand: _ =>
            {
                var message = $"kubectl --kubeconfig \"{resource.KubeconfigPath}\" get pods -A -o wide";
                Console.WriteLine(message);
                return Task.FromResult(CommandResults.Success(
                    "kubectl command",
                    message,
                    CommandResultFormat.Text,
                    true));
            },
            new CommandOptions
            {
                Description = "Prints the kubectl command for inspecting all pods in this Kind cluster.",
                IconName = "ClipboardCode",
                IsHighlighted = true,
                UpdateState = _ => ResourceCommandState.Enabled,
            });

        resourceBuilder.WithCommand(
            name: "show-helm",
            displayName: "Show Helm",
            executeCommand: _ =>
            {
                var message = $"helm list -A --kubeconfig \"{resource.KubeconfigPath}\"";
                Console.WriteLine(message);
                return Task.FromResult(CommandResults.Success(
                    "Helm command",
                    message,
                    CommandResultFormat.Text,
                    true));
            },
            new CommandOptions
            {
                Description = "Prints the Helm command for listing releases in this Kind cluster.",
                IconName = "ClipboardCode",
                UpdateState = _ => ResourceCommandState.Enabled,
            });

        resourceBuilder.WithCommand(
            name: "show-kubeconfig",
            displayName: "Show kubeconfig",
            executeCommand: _ =>
            {
                Console.WriteLine(resource.KubeconfigPath);
                return Task.FromResult(CommandResults.Success(
                    "Kubeconfig path",
                    resource.KubeconfigPath,
                    CommandResultFormat.Text,
                    true));
            },
            new CommandOptions
            {
                Description = "Prints the kubeconfig path created for this Kind cluster.",
                IconName = "Document",
                UpdateState = _ => ResourceCommandState.Enabled,
            });

        resourceBuilder.WithCommand(
            name: "open-k9s",
            displayName: "Open k9s",
            executeCommand: _ => OpenK9sAsync(resource),
            new CommandOptions
            {
                Description = "Opens k9s in a new terminal window connected to this Kind cluster.",
                IconName = "WindowConsole",
                IsHighlighted = true,
                UpdateState = _ => ResourceCommandState.Enabled,
            });

        return resourceBuilder;
    }

    /// <summary>
    /// Builds a local Docker image and loads it into this Kind cluster before manifests and Helm charts are applied.
    /// </summary>
    /// <param name="builder">The Kind cluster resource builder.</param>
    /// <param name="image">The image reference to build and load, for example <c>echo-log-app:local</c>.</param>
    /// <param name="contextPath">The Docker build context path.</param>
    /// <param name="dockerfilePath">Optional Dockerfile path. When omitted, Docker uses <c>Dockerfile</c> in the context.</param>
    /// <returns>The original builder for chaining.</returns>
    public static IResourceBuilder<KindClusterResource> WithDockerImage(
        this IResourceBuilder<KindClusterResource> builder,
        string image,
        string contextPath,
        string? dockerfilePath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextPath);

        builder.Resource.AddDockerImage(new KindDockerImage(
            image,
            Path.GetFullPath(contextPath),
            dockerfilePath is null ? null : Path.GetFullPath(dockerfilePath)));

        return builder;
    }

    private static Task<ExecuteCommandResult> OpenK9sAsync(KindClusterResource resource)
    {
        if (!File.Exists(resource.KubeconfigPath))
        {
            var message = $"Kubeconfig was not found: {resource.KubeconfigPath}";
            return Task.FromResult(CommandResults.Failure(message, message, CommandResultFormat.Text));
        }

        var k9sPath = FindExecutable("k9s");
        if (k9sPath is null)
        {
            const string message = "k9s was not found. Install it first, for example: winget install Derailed.k9s, or set K9S_PATH to the full k9s executable path.";
            return Task.FromResult(CommandResults.Failure(message, message, CommandResultFormat.Text));
        }

        try
        {
            var context = $"kind-{resource.ClusterName}";

            if (OperatingSystem.IsWindows())
            {
                var terminal = FindOnPath("wt") ?? FindOnPath("wt.exe");
                var shell = FindOnPath("pwsh") ?? FindOnPath("powershell");

                if (terminal is not null && shell is not null)
                {
                    var script = WriteK9sPowerShellScript(k9sPath, resource.KubeconfigPath, context);
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = terminal,
                        UseShellExecute = false,
                    };
                    startInfo.ArgumentList.Add("new-tab");
                    startInfo.ArgumentList.Add("--title");
                    startInfo.ArgumentList.Add($"k9s {resource.ClusterName}");
                    startInfo.ArgumentList.Add("--");
                    startInfo.ArgumentList.Add(shell);
                    startInfo.ArgumentList.Add("-NoExit");
                    startInfo.ArgumentList.Add("-ExecutionPolicy");
                    startInfo.ArgumentList.Add("Bypass");
                    startInfo.ArgumentList.Add("-File");
                    startInfo.ArgumentList.Add(script);

                    Process.Start(startInfo);

                    return Task.FromResult(CommandResults.Success(
                        "Opened k9s.",
                        $"Opened k9s at '{k9sPath}' for context '{context}' using kubeconfig '{resource.KubeconfigPath}'.",
                        CommandResultFormat.Text,
                        true));
                }
            }

            if (OperatingSystem.IsMacOS())
            {
                var open = FindOnPath("open");
                if (open is not null)
                {
                    var script = WriteK9sShellScript(k9sPath, resource.KubeconfigPath, context);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = open,
                        UseShellExecute = false,
                        ArgumentList = { "-a", "Terminal", script }
                    });

                    return Task.FromResult(CommandResults.Success(
                        "Opened k9s.",
                        $"Opened k9s at '{k9sPath}' for context '{context}' using kubeconfig '{resource.KubeconfigPath}'.",
                        CommandResultFormat.Text,
                        true));
                }
            }

            var unixTerminal = FindOnPath("x-terminal-emulator")
                ?? FindOnPath("gnome-terminal")
                ?? FindOnPath("konsole")
                ?? FindOnPath("xfce4-terminal")
                ?? FindOnPath("xterm");

            if (unixTerminal is not null)
            {
                var script = WriteK9sShellScript(k9sPath, resource.KubeconfigPath, context);
                var startInfo = new ProcessStartInfo
                {
                    FileName = unixTerminal,
                    UseShellExecute = false,
                };

                if (Path.GetFileName(unixTerminal).Contains("gnome-terminal", StringComparison.OrdinalIgnoreCase))
                {
                    startInfo.ArgumentList.Add("--");
                    startInfo.ArgumentList.Add("sh");
                    startInfo.ArgumentList.Add(script);
                }
                else
                {
                    startInfo.ArgumentList.Add("-e");
                    startInfo.ArgumentList.Add("sh");
                    startInfo.ArgumentList.Add(script);
                }

                Process.Start(startInfo);

                return Task.FromResult(CommandResults.Success(
                    "Opened k9s.",
                    $"Opened k9s at '{k9sPath}' for context '{context}' using kubeconfig '{resource.KubeconfigPath}'.",
                    CommandResultFormat.Text,
                    true));
            }

            var manualCommand = $"No supported terminal was found. Run manually: KUBECONFIG=\"{resource.KubeconfigPath}\" \"{k9sPath}\" --kubeconfig \"{resource.KubeconfigPath}\" --context \"{context}\"";
            return Task.FromResult(CommandResults.Failure(manualCommand, manualCommand, CommandResultFormat.Text));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(CommandResults.Failure(
                "Failed to open k9s.",
                ex.ToString(),
                CommandResultFormat.Text));
        }
    }

    private static string WriteK9sPowerShellScript(string k9sPath, string kubeconfigPath, string context)
    {
        var script = Path.Combine(Path.GetTempPath(), $"open-k9s-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(
            script,
            string.Join(
                Environment.NewLine,
                $"$env:KUBECONFIG = '{EscapePowerShell(kubeconfigPath)}'",
                $"& '{EscapePowerShell(k9sPath)}' --kubeconfig '{EscapePowerShell(kubeconfigPath)}' --context '{EscapePowerShell(context)}'",
                "if ($LASTEXITCODE -ne 0) {",
                "    Write-Host \"k9s exited with code $LASTEXITCODE\"",
                "}"));

        return script;
    }

    private static string WriteK9sShellScript(string k9sPath, string kubeconfigPath, string context)
    {
        var script = Path.Combine(Path.GetTempPath(), $"open-k9s-{Guid.NewGuid():N}.sh");
        File.WriteAllText(
            script,
            string.Join(
                Environment.NewLine,
                "#!/usr/bin/env sh",
                $"export KUBECONFIG='{EscapeSingleQuoteForShell(kubeconfigPath)}'",
                $"exec '{EscapeSingleQuoteForShell(k9sPath)}' --kubeconfig '{EscapeSingleQuoteForShell(kubeconfigPath)}' --context '{EscapeSingleQuoteForShell(context)}'"));

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Process.Start("chmod", $"+x \"{script}\"")?.WaitForExit(5000);
            }
            catch
            {
                // The script can still be invoked via "sh script" even if chmod fails.
            }
        }

        return script;
    }

    private static string EscapeSingleQuoteForShell(string value) => value.Replace("'", "'\"'\"'", StringComparison.Ordinal);

    private static string? FindExecutable(string command)
    {
        var explicitPath = Environment.GetEnvironmentVariable("K9S_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var fromPath = FindOnPath(command);
        if (fromPath is not null)
        {
            return fromPath;
        }

        foreach (var candidate in GetCommonExecutableLocations(command))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCommonExecutableLocations(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            var executable = Path.HasExtension(command) ? command : command + ".exe";
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                var wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(wingetPackages))
                {
                    foreach (var file in Directory.EnumerateFiles(wingetPackages, executable, SearchOption.AllDirectories))
                    {
                        yield return file;
                    }
                }

                yield return Path.Combine(localAppData, "Microsoft", "WindowsApps", executable);
            }

            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                yield return Path.Combine(userProfile, "scoop", "shims", executable);
            }

            if (!string.IsNullOrWhiteSpace(programData))
            {
                yield return Path.Combine(programData, "chocolatey", "bin", executable);
            }
        }
        else
        {
            yield return $"/opt/homebrew/bin/{command}";
            yield return $"/usr/local/bin/{command}";
            yield return $"/usr/bin/{command}";
            yield return $"/snap/bin/{command}";
        }
    }

    private static string? FindOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directPath = Path.Combine(directory, command);
            if (File.Exists(directPath))
            {
                return directPath;
            }

            if (Path.HasExtension(command))
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// Sets the number of additional worker nodes in the cluster.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nodeCount">Number of worker nodes (must be ≥ 0).</param>
    /// <returns>The same builder for fluent chaining.</returns>
    public static IResourceBuilder<KindClusterResource> WithNodeCount(
        this IResourceBuilder<KindClusterResource> builder,
        int nodeCount)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (nodeCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nodeCount), nodeCount, "Node count must be non-negative.");
        }

        builder.Resource.NodeCount = nodeCount;
        return builder;
    }

    /// <summary>
    /// Sets the Kubernetes version for all cluster nodes (e.g., <c>"v1.31.0"</c>).
    /// Kind will use the <c>kindest/node:{version}</c> image.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="kubernetesVersion">The Kubernetes version tag, including the leading <c>v</c>.</param>
    /// <returns>The same builder for fluent chaining.</returns>
    public static IResourceBuilder<KindClusterResource> WithKubernetesVersion(
        this IResourceBuilder<KindClusterResource> builder,
        string kubernetesVersion)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(kubernetesVersion);

        builder.Resource.KubernetesVersion = kubernetesVersion;
        return builder;
    }

    /// <summary>
    /// Provides a custom Kind cluster configuration file to pass to
    /// <c>kind create cluster --config</c>.
    /// Takes precedence over any auto-generated configuration.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configPath">Absolute path to the Kind cluster config YAML file.</param>
    /// <returns>The same builder for fluent chaining.</returns>
    public static IResourceBuilder<KindClusterResource> WithConfig(
        this IResourceBuilder<KindClusterResource> builder,
        string configPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(configPath);

        builder.Resource.ConfigPath = configPath;
        return builder;
    }

    /// <summary>
    /// Adds a host-to-container port mapping on the Kind control-plane node.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="hostPort">The port on the Docker host.</param>
    /// <param name="containerPort">The port inside the Kind node container.</param>
    /// <param name="protocol">Network protocol — <c>"TCP"</c> or <c>"UDP"</c>. Defaults to <c>"TCP"</c>.</param>
    /// <returns>The same builder for fluent chaining.</returns>
    public static IResourceBuilder<KindClusterResource> WithPortMapping(
        this IResourceBuilder<KindClusterResource> builder,
        int hostPort,
        int containerPort,
        string protocol = "TCP")
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.AddPortMapping(new KindPortMapping(hostPort, containerPort, protocol));
        return builder;
    }

    /// <summary>
    /// Installs a Helm chart into the cluster after it becomes healthy.
    /// Requires <c>helm</c> to be installed and available on <c>PATH</c>.
    /// <para>
    /// Deploy steps (manifests and charts) are executed in registration order, so place this
    /// call relative to <see cref="WithManifest"/> calls to control ordering — for example,
    /// install a CSI driver chart before applying manifests that depend on it.
    /// </para>
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="releaseName">The Helm release name.</param>
    /// <param name="chart">
    /// The chart name (when <paramref name="repoUrl"/> is provided) or a repository-qualified
    /// name (e.g. <c>"ingress-nginx/ingress-nginx"</c>) or a local chart path.
    /// </param>
    /// <param name="namespace">
    /// The Kubernetes namespace for the release. Defaults to <c>"default"</c>.
    /// The namespace is created automatically if it does not exist.
    /// </param>
    /// <param name="valuesFile">Optional path to a Helm values override file.</param>
    /// <param name="repoUrl">
    /// Optional chart repository URL. When set, the repository is registered first via
    /// <c>helm repo add &lt;alias&gt; &lt;url&gt; --force-update</c> (using a release-scoped
    /// alias), and the resulting repo-qualified chart reference (e.g. <c>alias/chart</c>) is
    /// then passed to a plain <c>helm install</c> — no <c>--repo</c> flag is used.
    /// </param>
    /// <param name="setValues">
    /// Optional <c>key=value</c> overrides passed as <c>--set</c> to <c>helm install</c>.
    /// </param>
    /// <param name="wait">
    /// When <see langword="true"/>, adds <c>--wait</c> so <c>helm install</c> blocks until
    /// the chart's workloads are ready before the next deploy step is executed.
    /// Recommended for infrastructure charts (e.g. CSI drivers) that must be running before
    /// later steps can use them.
    /// </param>
    /// <returns>The same builder for fluent chaining.</returns>
    public static IResourceBuilder<KindClusterResource> WithHelmChart(
        this IResourceBuilder<KindClusterResource> builder,
        string releaseName,
        string chart,
        string @namespace = "default",
        string? valuesFile = null,
        string? repoUrl = null,
        IEnumerable<string>? setValues = null,
        bool wait = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(releaseName);
        ArgumentException.ThrowIfNullOrEmpty(chart);

        builder.Resource.AddHelmChart(new KindHelmChart(
            releaseName, chart, @namespace, valuesFile, repoUrl,
            setValues?.ToList().AsReadOnly(), wait));
        return builder;
    }

    /// <summary>
    /// Applies a kubectl manifest file to the cluster after it becomes healthy.
    /// Requires <c>kubectl</c> to be installed and available on <c>PATH</c>.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="manifestPath">Absolute path to the Kubernetes manifest file or directory.</param>
    /// <returns>The same builder for fluent chaining.</returns>
    public static IResourceBuilder<KindClusterResource> WithManifest(
        this IResourceBuilder<KindClusterResource> builder,
        string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);

        builder.Resource.AddManifestPath(manifestPath);
        return builder;
    }

    /// <summary>
    /// Adds a static property to surface on the Kind resource in the Aspire dashboard.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">Dashboard property name.</param>
    /// <param name="value">Dashboard property value.</param>
    /// <returns>The same builder for fluent chaining.</returns>
    public static IResourceBuilder<KindClusterResource> WithDashboardProperty(
        this IResourceBuilder<KindClusterResource> builder,
        string name,
        string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        builder.Resource.AddDashboardProperty(name, value);
        return builder;
    }

    /// <summary>
    /// Overrides the default 5-minute timeout for waiting until the cluster becomes healthy.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="timeout">The maximum time to wait (must be positive).</param>
    /// <returns>The same builder for fluent chaining.</returns>
    public static IResourceBuilder<KindClusterResource> WithWaitForReady(
        this IResourceBuilder<KindClusterResource> builder,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout), timeout, "Ready timeout must be a positive duration.");
        }

        builder.Resource.ReadyTimeout = timeout;
        return builder;
    }
}
