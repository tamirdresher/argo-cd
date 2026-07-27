// ─────────────────────────────────────────────────────────────────────────────
// TEMPORARY: this extension fills a gap in CommunityToolkit.Aspire.Hosting.Kind
// 13.4.1-beta.687.
//
//   WithPortMapping → not yet upstreamed. Convenience wrapper over the package's
//                     WithKindConfig(Action<KindConfigModel>).
// ─────────────────────────────────────────────────────────────────────────────

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Additions on top of <c>CommunityToolkit.Aspire.Hosting.Kind</c>: fluent shorthands for
/// scenarios upstream currently only exposes through its lower-level callback API.
/// </summary>
public static class KindClusterPortMappingExtensions
{
    /// <summary>
    /// Adds a host-to-container port mapping on the Kind control-plane node.
    /// </summary>
    /// <param name="builder">The Kind cluster resource builder.</param>
    /// <param name="hostPort">The port on the Docker host.</param>
    /// <param name="containerPort">The port inside the Kind node container.</param>
    /// <param name="protocol">Network protocol — <c>"TCP"</c> (default), <c>"UDP"</c>, or <c>"SCTP"</c>.</param>
    /// <returns>The same builder for fluent chaining.</returns>
    /// <remarks>
    /// This is a fluent shorthand over upstream's <c>WithKindConfig(Action&lt;KindConfigModel&gt;)</c>.
    /// Multiple calls compose. If the config has no nodes yet, a default control-plane node is added.
    /// </remarks>
    public static IResourceBuilder<KindClusterResource> WithPortMapping(
        this IResourceBuilder<KindClusterResource> builder,
        int hostPort,
        int containerPort,
        string protocol = "TCP")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hostPort);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(containerPort);
        ArgumentException.ThrowIfNullOrEmpty(protocol);

        return builder.WithKindConfig(config =>
        {
            if (config.Nodes.Count == 0)
            {
                config.Nodes.Add(new KindNodeModel { Role = "control-plane" });
            }

            var controlPlane = config.Nodes.FirstOrDefault(n =>
                string.Equals(n.Role, "control-plane", StringComparison.OrdinalIgnoreCase))
                ?? config.Nodes[0];

            controlPlane.ExtraPortMappings ??= new List<KindPortMappingModel>();
            controlPlane.ExtraPortMappings.Add(new KindPortMappingModel
            {
                HostPort = hostPort,
                ContainerPort = containerPort,
                Protocol = protocol,
            });
        });
    }
}
