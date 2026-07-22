using System.Text.RegularExpressions;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a single ordered deploy step registered on a <see cref="KindClusterResource"/>.
/// Steps are executed in registration order during <c>AfterResourcesCreated</c>, which allows
/// manifests and Helm charts to be interleaved (e.g. install a CSI driver chart before applying
/// a manifest that depends on it).
/// </summary>
public abstract record KindDeployStep;

/// <summary>Deploy step that runs <c>kubectl apply -f &lt;path&gt;</c>.</summary>
/// <param name="Path">Absolute path to the manifest file or directory.</param>
public sealed record KindManifestStep(string Path) : KindDeployStep;

/// <summary>Deploy step that installs a Helm chart via <c>helm install</c>.</summary>
/// <param name="Chart">The Helm chart definition to install.</param>
public sealed record KindHelmChartStep(KindHelmChart Chart) : KindDeployStep;

/// <summary>
/// A resource that represents a Kind (Kubernetes in Docker) cluster in a .NET Aspire application.
/// </summary>
public sealed class KindClusterResource : Resource, IResourceWithConnectionString
{
    private static readonly Regex ClusterNameRegex = new(@"^[a-z0-9][a-z0-9\-]*$", RegexOptions.Compiled);

    private readonly List<KindPortMapping> _portMappings = [];
    private readonly List<KindHelmChart> _helmCharts = [];
    private readonly List<string> _manifestPaths = [];
    private readonly List<KindDockerImage> _dockerImages = [];
    private readonly List<ResourcePropertySnapshot> _dashboardProperties = [];
    private readonly List<KindDeployStep> _deploySteps = [];

    /// <summary>
    /// Gets the Kind cluster name passed to <c>kind create/delete cluster --name</c>.
    /// Must contain only lowercase alphanumeric characters and hyphens.
    /// </summary>
    public string ClusterName { get; }

    /// <summary>
    /// Gets or sets the Kubernetes version image tag for cluster nodes (e.g., <c>v1.31.0</c>).
    /// When <see langword="null"/>, Kind uses its bundled default Kubernetes version.
    /// </summary>
    public string? KubernetesVersion { get; set; }

    /// <summary>
    /// Gets or sets the number of additional worker nodes. Defaults to 0 (single control-plane only).
    /// </summary>
    public int NodeCount { get; set; } = 0;

    /// <summary>
    /// Gets or sets the absolute path to a custom Kind cluster configuration file.
    /// When set, this file is passed to <c>kind create cluster --config</c> and takes
    /// precedence over the auto-generated configuration derived from <see cref="NodeCount"/>,
    /// <see cref="KubernetesVersion"/>, and <see cref="PortMappings"/>.
    /// </summary>
    public string? ConfigPath { get; set; }

    /// <summary>
    /// Gets the absolute path where the kubeconfig file will be written by
    /// <c>kind create cluster --kubeconfig</c>.
    /// </summary>
    public string KubeconfigPath { get; }

    /// <summary>
    /// Gets or sets the maximum time to wait for the cluster to become healthy after creation.
    /// Defaults to 5 minutes.
    /// </summary>
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the list of host-to-container port mappings configured on the control-plane node.
    /// </summary>
    public IReadOnlyList<KindPortMapping> PortMappings => _portMappings;

    /// <summary>
    /// Gets the ordered list of deploy steps (manifest applies and Helm chart installs) registered
    /// on this cluster. Steps are executed in registration order, preserving interleaving of
    /// <see cref="KindManifestStep"/> and <see cref="KindHelmChartStep"/> entries.
    /// </summary>
    public IReadOnlyList<KindDeployStep> DeploySteps => _deploySteps;

    /// <summary>
    /// Gets the list of Helm charts to install into the cluster after it becomes healthy.
    /// </summary>
    public IReadOnlyList<KindHelmChart> HelmCharts => _helmCharts;

    /// <summary>
    /// Gets the list of kubectl manifest file paths to apply after the cluster becomes healthy.
    /// </summary>
    public IReadOnlyList<string> ManifestPaths => _manifestPaths;

    /// <summary>
    /// Gets the Docker images to build and load into the Kind cluster before manifests are applied.
    /// </summary>
    public IReadOnlyList<KindDockerImage> DockerImages => _dockerImages;

    /// <summary>
    /// Gets additional static properties to surface on the Kind resource in the Aspire dashboard.
    /// </summary>
    public IReadOnlyList<ResourcePropertySnapshot> DashboardProperties => _dashboardProperties;

    /// <summary>
    /// Returns the kubeconfig file path as the connection string, allowing downstream resources
    /// to reference the cluster (e.g., a Helm chart deployer or a service that needs a kubeconfig).
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{KubeconfigPath}");

    /// <summary>
    /// Initializes a new <see cref="KindClusterResource"/>.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="clusterName">
    /// The Kind cluster name (passed to <c>kind create cluster --name</c>).
    /// Must match <c>^[a-z0-9][a-z0-9\-]*$</c> to prevent command injection.
    /// </param>
    /// <param name="kubeconfigPath">Absolute path where the kubeconfig will be written.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="clusterName"/> is null/empty or contains invalid characters.
    /// </exception>
    public KindClusterResource(string name, string clusterName, string kubeconfigPath)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrEmpty(clusterName);
        ArgumentException.ThrowIfNullOrEmpty(kubeconfigPath);

        if (!ClusterNameRegex.IsMatch(clusterName))
        {
            throw new ArgumentException(
                "Kind cluster name must start with a lowercase alphanumeric character and " +
                "contain only lowercase letters, digits, and hyphens (e.g., 'my-cluster').",
                nameof(clusterName));
        }

        ClusterName = clusterName;
        KubeconfigPath = kubeconfigPath;
    }

    internal void AddPortMapping(KindPortMapping portMapping) => _portMappings.Add(portMapping);
    internal void AddHelmChart(KindHelmChart helmChart)
    {
        _helmCharts.Add(helmChart);
        _deploySteps.Add(new KindHelmChartStep(helmChart));
    }
    internal void AddManifestPath(string path)
    {
        _manifestPaths.Add(path);
        _deploySteps.Add(new KindManifestStep(path));
    }
    internal void AddDockerImage(KindDockerImage image) => _dockerImages.Add(image);
    internal void AddDashboardProperty(string name, string value) => _dashboardProperties.Add(new ResourcePropertySnapshot(name, value));
}

/// <summary>
/// Represents a host-to-container port mapping for a Kind cluster node.
/// </summary>
/// <param name="HostPort">The port on the Docker host to map to.</param>
/// <param name="ContainerPort">The port inside the Kind node container to expose.</param>
/// <param name="Protocol">The network protocol — <c>TCP</c> or <c>UDP</c>. Defaults to <c>TCP</c>.</param>
public record struct KindPortMapping(int HostPort, int ContainerPort, string Protocol = "TCP");

/// <summary>
/// Represents a Helm chart to install into a Kind cluster after it becomes healthy.
/// </summary>
/// <param name="ReleaseName">The Helm release name.</param>
/// <param name="Chart">
/// The chart name (when <see cref="RepoUrl"/> is set) or a repository-qualified name
/// such as <c>ingress-nginx/ingress-nginx</c>, or a local chart path.
/// </param>
/// <param name="Namespace">The Kubernetes namespace for the Helm release. Defaults to <c>default</c>.</param>
/// <param name="ValuesFile">Optional absolute path to a Helm values override file (<c>-f values.yaml</c>).</param>
/// <param name="RepoUrl">
/// Optional Helm chart repository URL. When set, passed as <c>--repo &lt;url&gt;</c> to
/// <c>helm install</c> so that <c>helm repo add</c> is not required.
/// </param>
/// <param name="SetValues">
/// Optional list of <c>key=value</c> overrides passed as <c>--set key=value</c> to
/// <c>helm install</c>. Applied after any <see cref="ValuesFile"/>.
/// </param>
/// <param name="Wait">
/// When <see langword="true"/>, passes <c>--wait</c> to <c>helm install</c> so the call
/// blocks until the chart's workloads (Deployments, DaemonSets, …) are ready.
/// Useful for ordering-sensitive installs such as CSI driver DaemonSets that must be
/// running before later manifests reference them.
/// </param>
public record KindHelmChart(
    string ReleaseName,
    string Chart,
    string Namespace = "default",
    string? ValuesFile = null,
    string? RepoUrl = null,
    IReadOnlyList<string>? SetValues = null,
    bool Wait = false);

/// <summary>
/// Represents a Docker image to build locally and load into a Kind cluster before applying manifests.
/// </summary>
/// <param name="Image">The image reference to build and load, for example <c>echo-log-app:local</c>.</param>
/// <param name="ContextPath">The Docker build context path.</param>
/// <param name="DockerfilePath">Optional Dockerfile path. When null, Docker uses <c>Dockerfile</c> in the context.</param>
public record KindDockerImage(
    string Image,
    string ContextPath,
    string? DockerfilePath = null);
