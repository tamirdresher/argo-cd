// contrib/aspire-dev/ArgoCd.Aspire.AppHost/ArgoCdDex.cs
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Go;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Extension methods that model the Procfile's opt-in <c>dex:</c> entry (contrib/running-locally)
/// as two Aspire resources: a run-to-completion config-generation step, and the published Dex
/// container image consuming the config it produced.
///
/// Procfile ground truth (contrib/running-locally, <c>dex:</c> line):
/// <code>
/// dex: ARGOCD_BINARY_NAME=argocd-dex go run github.com/argoproj/argo-cd/v3/cmd gendexcfg \
///     -o `pwd`/dist/dex.yaml &amp;&amp;
///   (test -f dist/dex.yaml || { echo 'Failed to generate dex configuration'; exit 1; }) &amp;&amp;
///   docker run ... -v `pwd`/dist/dex.yaml:/dex.yaml ghcr.io/dexidp/dex:v2.45.1 \
///       dex serve /dex.yaml
/// </code>
///
/// Extracted out of <c>AppHost.cs</c> (a top-level-statements Program entry point that cannot
/// itself be unit tested) purely so the resulting resource graph — command, args, environment,
/// bind mount, and <c>WaitFor</c>/<c>WaitForCompletion</c> ordering — can be asserted directly;
/// this is a pure refactor with no behavioral change.
///
/// Dex is only needed to exercise SSO login flows, so both resources stay fully opt-in, gated by
/// <c>ARGOCD_ASPIRE_ENABLE_DEX</c> in <c>AppHost.cs</c>.
/// </summary>
internal static class ArgoCdDex
{
    /// <summary>
    /// Run-to-completion step equivalent to the Procfile's <c>gendexcfg -o ...</c>. Uses
    /// <c>ARGOCD_BINARY_NAME=argocd-dex</c> (see <c>cmd/main.go</c>) to dispatch into
    /// <c>cmd/argocd-dex/commands/argocd_dex.go</c>'s <c>gendexcfg</c> subcommand. Unlike the
    /// Procfile (which relies on ambient kubectl context), <c>--kubeconfig</c> and <c>-n</c> are
    /// passed explicitly — the same convention every other kubectl-invoking resource in this repo
    /// follows (see <c>RepoServerOverride</c>/dev-mounter) — because each worktree here gets its
    /// own Kind cluster and kubeconfig rather than sharing an ambient context.
    ///
    /// <c>WaitFor</c> gates this on the Kind resource after its configured
    /// <c>WithManifest</c> state bootstrap has completed.
    /// </summary>
    public static IResourceBuilder<GoAppResource> AddArgoCdGenDexConfig(
        this IDistributedApplicationBuilder builder,
        string dexConfigPath,
        IResourceBuilder<KindClusterResource> cluster)
    {
        return builder
            .AddGoApp("gendexcfg", ArgoCdRepository.Root, "./cmd")
            .WithAppArgs(
                "gendexcfg",
                "-o", dexConfigPath,
                "--kubeconfig", cluster.Resource.KubeconfigPath,
                "-n", ArgoCdManifestSet.ArgoCdNamespace)
            .WithEnvironment("ARGOCD_BINARY_NAME", "argocd-dex")
            .WithEnvironment("KUBECONFIG", cluster.Resource.KubeconfigPath)
            .WithEnvironment("K8S_CLUSTER_NAME", cluster.Resource.Name)
            .WaitFor(cluster)
            ;
    }

    /// <summary>
    /// <c>dex serve &lt;mounted-config&gt;</c> — bind-mounts the config
    /// <see cref="AddArgoCdGenDexConfig"/> just generated on the host into the well-known
    /// <c>/dex.yaml</c> path inside the container, matching the Procfile's
    /// <c>-v `pwd`/dist/dex.yaml:/dex.yaml</c> bind mount exactly.
    ///
    /// <c>WaitForCompletion</c> (rather than <c>WaitFor</c>) ensures the container does not start
    /// until <paramref name="gendexcfg"/> has exited successfully, so the mounted file is
    /// guaranteed to exist and be current.
    /// </summary>
    public static IResourceBuilder<ContainerResource> AddArgoCdDex(
        this IDistributedApplicationBuilder builder,
        string dexConfigPath,
        IResourceBuilder<GoAppResource> gendexcfg)
    {
        return builder
            .AddContainer("dex", "ghcr.io/dexidp/dex", "v2.45.1")
            .WithEntrypoint("dex")
            .WithArgs("serve", "/dex.yaml")
            .WithBindMount(dexConfigPath, "/dex.yaml", isReadOnly: true)
            .WaitForCompletion(gendexcfg)
            .WithHttpEndpoint(port: 5556, targetPort: 5556, name: "http", isProxied: false);
    }
}
