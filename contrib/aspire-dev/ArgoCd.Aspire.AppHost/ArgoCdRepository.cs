using System.Reflection;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Structurally-known path to the enclosing Argo CD repository, stamped by MSBuild from the
/// in-tree AppHost location (<c>contrib/aspire-dev/ArgoCd.Aspire.AppHost</c>).
/// </summary>
public static class ArgoCdRepository
{
    private const string MetadataKey = "ArgoCdRepoRoot";
    private static readonly string SanityCheckManifest = Path.Combine("manifests", "crds", "application-crd.yaml");

    public static string Root { get; } = ResolveFromAssemblyMetadata();

    public static string PathTo(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return Path.Combine([Root, .. segments]);
    }

    private static string ResolveFromAssemblyMetadata()
    {
        var repoRoot = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, MetadataKey, StringComparison.Ordinal))
            ?.Value;

        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            throw new InvalidOperationException(
                $"Assembly metadata '{MetadataKey}' is missing. Build the AppHost through MSBuild so the in-tree repository root can be stamped.");
        }

        var fullPath = Path.GetFullPath(repoRoot);
        var sentinel = Path.Combine(fullPath, SanityCheckManifest);
        if (!File.Exists(sentinel))
        {
            throw new FileNotFoundException(
                $"Expected Argo CD manifest '{SanityCheckManifest}' under repository root '{fullPath}'.",
                sentinel);
        }

        return fullPath;
    }
}
