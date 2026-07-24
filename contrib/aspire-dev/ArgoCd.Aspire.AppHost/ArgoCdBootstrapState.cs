namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Thread-safe signal, shared as a singleton between <see cref="ArgoCdStateBootstrapHook"/> and the
/// "argocd-state-bootstrap" health check registered in AppHost.cs, that tracks whether the
/// state-only <c>manifests/</c> apply performed by <see cref="ArgoCdStateBootstrapHook"/> has
/// finished, and whether every step in it succeeded.
///
/// The Kind cluster resource associates that health check via <c>WithHealthCheck("argocd-state-bootstrap")</c>,
/// so anything that depends on the cluster with <c>WaitFor(cluster)</c> — most importantly the
/// <c>gendexcfg</c> executable in the optional Dex flow, which hard-fails if the <c>argocd-cm</c>/
/// <c>argocd-secret</c> ConfigMap/Secret it reads via <c>util/settings/settings.go</c> do not yet
/// exist — is guaranteed to only start once this bootstrap has actually applied those objects.
///
/// The health check must report Unhealthy for the entire window between "cluster process started"
/// and "bootstrap finished applying successfully": reporting Healthy merely because the bootstrap
/// hook has *attempted* the apply (regardless of outcome) would defeat the purpose of gating
/// consumers on it, so <see cref="Completed"/> and <see cref="Succeeded"/> are exposed as two
/// separate flags rather than a single "done" flag.
/// </summary>
public sealed class ArgoCdBootstrapState
{
    /// <summary>
    /// The health check key this state is associated with via
    /// <c>cluster.WithHealthCheck(ArgoCdBootstrapState.HealthCheckKey)</c> in AppHost.cs, and that
    /// downstream resources (e.g. the optional <c>gendexcfg</c> executable) transitively wait on
    /// via <c>WaitFor(cluster)</c>.
    /// </summary>
    public const string HealthCheckKey = "argocd-state-bootstrap";

    private volatile bool _completed;
    private volatile bool _succeeded;

    /// <summary>
    /// <see langword="true"/> once <see cref="ArgoCdStateBootstrapHook"/> has finished its
    /// <c>AfterResourcesCreatedAsync</c> pass (successfully or not). <see langword="false"/> for the
    /// entire window before that, including while the hook is still waiting for <c>kubectl</c> to be
    /// able to reach the cluster's API server.
    /// </summary>
    public bool Completed => _completed;

    /// <summary>
    /// <see langword="true"/> only if <see cref="Completed"/> is also <see langword="true"/> and
    /// every manifest group in <see cref="ArgoCdStateBootstrapHook.BuildApplyPlan"/> applied
    /// successfully. Meaningless (and always <see langword="false"/>) while <see cref="Completed"/>
    /// is still <see langword="false"/>.
    /// </summary>
    public bool Succeeded => _succeeded;

    /// <summary>
    /// Called exactly once by <see cref="ArgoCdStateBootstrapHook.AfterResourcesCreatedAsync"/>,
    /// from every one of its return points, to record the final outcome.
    /// </summary>
    public void MarkCompleted(bool succeeded)
    {
        _succeeded = succeeded;
        _completed = true;
    }
}
