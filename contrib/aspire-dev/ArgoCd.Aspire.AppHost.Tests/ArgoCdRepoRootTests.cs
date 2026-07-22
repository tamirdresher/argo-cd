using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHostTests;

/// <summary>
/// Fast, no-Docker tests for repo-root resolution. Uses temp directories with the marker
/// files ArgoCdRepoRoot looks for, so no real Argo CD checkout is required.
/// </summary>
public class ArgoCdRepoRootTests : IDisposable
{
    private readonly string _tempRoot;

    public ArgoCdRepoRootTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "argocd-repo-root-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void IsRepoRoot_ReturnsFalse_WhenNeitherMarkerFilePresent()
    {
        Assert.False(ArgoCdRepoRoot.IsRepoRoot(_tempRoot));
    }

    [Fact]
    public void IsRepoRoot_ReturnsFalse_WhenOnlyGoModPresent()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "go.mod"), "module github.com/argoproj/argo-cd/v3\n");
        Assert.False(ArgoCdRepoRoot.IsRepoRoot(_tempRoot));
    }

    [Fact]
    public void IsRepoRoot_ReturnsTrue_WhenBothMarkersPresent()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "go.mod"), "module github.com/argoproj/argo-cd/v3\n");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "manifests"));
        File.WriteAllText(Path.Combine(_tempRoot, "manifests", "install.yaml"), "# placeholder\n");

        Assert.True(ArgoCdRepoRoot.IsRepoRoot(_tempRoot));
    }

    [Fact]
    public void Resolve_WalksUpFromNestedStartDirectory()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "go.mod"), "module github.com/argoproj/argo-cd/v3\n");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "manifests"));
        File.WriteAllText(Path.Combine(_tempRoot, "manifests", "install.yaml"), "# placeholder\n");

        var nested = Path.Combine(_tempRoot, "contrib", "aspire-dev", "ArgoCd.Aspire.AppHost", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(nested);

        var resolved = ArgoCdRepoRoot.Resolve(nested);

        Assert.Equal(
            Path.GetFullPath(_tempRoot).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(resolved).TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Resolve_Throws_WhenNoAncestorMatches()
    {
        var nested = Path.Combine(_tempRoot, "a", "b", "c");
        Directory.CreateDirectory(nested);

        // _tempRoot itself has no markers and (in CI/dev sandboxes) its ancestors up to the
        // filesystem root won't either, so this should throw rather than false-positive on
        // an unrelated ancestor directory.
        Assert.Throws<DirectoryNotFoundException>(() => ArgoCdRepoRoot.Resolve(nested));
    }
}
