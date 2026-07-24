using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Event published after a Kind cluster is reachable and all registered post-deploy
/// images, manifests, Helm charts, and pod readiness checks have completed.
/// </summary>
public sealed record KindClusterReadyEvent(
    KindClusterResource Cluster,
    IServiceProvider Services,
    ILogger Logger) : IDistributedApplicationResourceEvent
{
    /// <inheritdoc />
    public IResource Resource => Cluster;
}
