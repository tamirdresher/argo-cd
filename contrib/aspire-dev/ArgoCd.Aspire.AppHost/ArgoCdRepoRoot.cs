namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Locates the root of the Argo CD git checkout that this AppHost lives inside of, so that
/// paths to <c>manifests/</c>, the top-level <c>Dockerfile</c>, and <c>go.mod</c> can be
/// resolved regardless of whether the AppHost is launched via <c>aspire start</c> from the
/// repo root, from <c>contrib/aspire-dev</c>, or from a built output directory.
/// </summary>
public static class ArgoCdRepoRoot
{
    /// <summary>
    /// Walks upward from <paramref name="startDirectory"/> looking for the Argo CD repo root,
    /// identified by the presence of both <c>go.mod</c> (declaring the
    /// <c>github.com/argoproj/argo-cd</c> module) and <c>manifests/install.yaml</c>
    /// (the checked-in baseline install manifest this AppHost applies to Kind).
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">
    /// Thrown when no ancestor directory of <paramref name="startDirectory"/> matches.
    /// </exception>
    public static string Resolve(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(startDirectory);

        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (IsRepoRoot(dir.FullName))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the Argo CD repository root above '{startDirectory}'. " +
            "Expected an ancestor directory containing both 'go.mod' and 'manifests/install.yaml'. " +
            "Run 'aspire start' from within the argo-cd checkout (e.g. from contrib/aspire-dev).");
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="directory"/> looks like the root of
    /// the Argo CD checkout. Exposed for testing without needing a real repository on disk.
    /// </summary>
    public static bool IsRepoRoot(string directory) =>
        File.Exists(Path.Combine(directory, "go.mod"))
        && File.Exists(Path.Combine(directory, "manifests", "install.yaml"));
}
