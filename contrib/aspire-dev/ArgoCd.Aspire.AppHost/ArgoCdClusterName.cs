using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Derives the Kind cluster name (and, transitively, the kubeconfig path that Aspire's Kind
/// hosting integration generates from it) so that concurrent worktrees/clones of this repository
/// never collide on a single hard-coded cluster name or a single <c>%TEMP%</c>/<c>$TMPDIR</c>
/// kubeconfig file.
/// </summary>
/// <remarks>
/// Without this, every checkout defaulted to the literal Kind cluster name <c>argocd-dev</c> and
/// therefore to the same kubeconfig path (<c>AddKindCluster</c> derives the kubeconfig path from
/// the cluster name when one isn't explicitly supplied). Two AppHost instances running from two
/// different worktrees/clones on the same machine would silently stomp on each other's cluster
/// and kubeconfig.
/// </remarks>
public static class ArgoCdClusterName
{
    /// <summary>
    /// Set this to explicitly pin the Kind cluster name instead of using the name derived from
    /// the repository checkout path — e.g. to deliberately share one cluster across multiple
    /// worktrees/clones, or to use a memorable name instead of the derived hash-suffixed one.
    /// Must satisfy the same constraints as any other Kind/Kubernetes name (see
    /// <see cref="Resolve"/>).
    /// </summary>
    public const string OverrideEnvironmentVariableName = "ARGOCD_ASPIRE_CLUSTER_NAME";

    private const string DerivedNamePrefix = "argocd-dev-";

    // The number of hex characters (from a SHA-256 digest) appended after DerivedNamePrefix.
    // 12 hex characters (48 bits) makes accidental collisions between checkout paths
    // astronomically unlikely while keeping the resulting name short and readable.
    private const int HashSuffixLength = 12;

    // Kind cluster names flow into Kubernetes context/cluster/user names and Docker container
    // names, so they must satisfy the RFC 1123 DNS label rules those systems enforce: this is
    // the exact same pattern KindClusterResource itself validates in its constructor. Unlike
    // KindClusterResource, we additionally enforce the RFC 1123 63-character length limit,
    // because KindClusterResource does not check that on its own.
    private static readonly Regex ValidNamePattern = new(@"^[a-z0-9][a-z0-9\-]*$", RegexOptions.Compiled);

    private const int MaxLength = 63;

    /// <summary>
    /// Resolves the Kind cluster name to use for the checkout rooted at <paramref name="repoRoot"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If <see cref="OverrideEnvironmentVariableName"/> is set (to any non-null value, including
    /// an invalid one), its trimmed value is validated and returned — this is the explicit,
    /// intentional-sharing/reuse escape hatch. An invalid override throws
    /// <see cref="ArgumentException"/> with a message naming the environment variable, the value
    /// that was rejected, and exactly which constraint it violated, rather than silently falling
    /// back to the derived name or producing an opaque failure later inside <c>kind</c>.
    /// </para>
    /// <para>
    /// Otherwise, a name is deterministically derived from the absolute, normalized
    /// <paramref name="repoRoot"/> path: hashing the path means the *same* checkout always
    /// resolves to the *same* cluster name (so the Kind cluster and its bootstrapped state
    /// persist and are reused across AppHost restarts, exactly like the previous hard-coded
    /// name did for a single checkout), while *different* checkouts (a second worktree, a second
    /// clone, a CI checkout) resolve to *different* names, without any file-based or
    /// network-based coordination between them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="OverrideEnvironmentVariableName"/> is set to a value that, once
    /// trimmed, is empty, longer than 63 characters, or does not match the Kind/Kubernetes name
    /// pattern <c>^[a-z0-9][a-z0-9-]*$</c>.
    /// </exception>
    public static string Resolve(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);

        var overrideValue = Environment.GetEnvironmentVariable(OverrideEnvironmentVariableName);
        return overrideValue is null
            ? DeriveFromRepoRoot(repoRoot)
            : ValidateOverride(overrideValue);
    }

    private static string ValidateOverride(string rawValue)
    {
        var trimmed = rawValue.Trim();

        if (trimmed.Length == 0)
        {
            throw new ArgumentException(
                $"{OverrideEnvironmentVariableName} is set but empty (or whitespace-only). " +
                "Unset it to use the automatically derived, per-checkout cluster name, or set it " +
                "to a valid Kind cluster name matching ^[a-z0-9][a-z0-9-]*$.");
        }

        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException(
                $"{OverrideEnvironmentVariableName}='{trimmed}' is {trimmed.Length} characters " +
                $"long, exceeding the {MaxLength}-character Kind/Kubernetes name limit. Choose a " +
                "shorter name.");
        }

        if (!ValidNamePattern.IsMatch(trimmed))
        {
            throw new ArgumentException(
                $"{OverrideEnvironmentVariableName}='{trimmed}' is not a valid Kind cluster name. " +
                "Names must start with a lowercase letter or digit and contain only lowercase " +
                "letters, digits, and hyphens (pattern: ^[a-z0-9][a-z0-9-]*$).");
        }

        return trimmed;
    }

    private static string DeriveFromRepoRoot(string repoRoot)
    {
        // Normalize so the same checkout resolves to the same name regardless of a trailing
        // directory separator, relative-vs-absolute invocation, or (on Windows, where paths are
        // case-insensitive) drive-letter/segment casing.
        var fullPath = Path.GetFullPath(repoRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = OperatingSystem.IsWindows() ? fullPath.ToLowerInvariant() : fullPath;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hashSuffix = Convert.ToHexString(hash)[..HashSuffixLength].ToLowerInvariant();

        return DerivedNamePrefix + hashSuffix;
    }
}
