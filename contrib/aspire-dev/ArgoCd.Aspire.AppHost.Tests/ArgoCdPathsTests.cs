using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

/// <summary>
/// Verifies the cross-platform path defaults in <see cref="ArgoCdPaths"/> replace every
/// <c>/tmp/argocd-local</c>- and <c>/tmp/coverage</c>-rooted Procfile default with a location
/// under <see cref="Path.GetTempPath"/>, and that the two methods which re-read their
/// environment-variable override on every call (<see cref="ArgoCdPaths.CmpPluginSocketPath"/>
/// and <see cref="ArgoCdPaths.CoverageDir"/>) honor an override when present.
/// </summary>
/// <remarks>
/// <see cref="ArgoCdPaths.TlsDataPath"/>, <see cref="ArgoCdPaths.SshDataPath"/>,
/// <see cref="ArgoCdPaths.GpgKeysPath"/>, and <see cref="ArgoCdPaths.GpgSourcePath"/> are
/// <c>static readonly</c> properties resolved once at type-initialization time, so their
/// environment-variable overrides cannot be reliably exercised from a test that runs after the
/// type has already been touched elsewhere in the process; only their default (no-override)
/// structure is asserted here.
/// </remarks>
public sealed class ArgoCdPathsTests
{
    [Fact]
    public void LocalDataRoot_IsRootedUnderOsTempPath()
    {
        var expectedRoot = Path.Combine(Path.GetTempPath(), "argocd-local");
        Assert.Equal(expectedRoot, ArgoCdPaths.LocalDataRoot);
        Assert.DoesNotContain("/tmp/argocd-local", ArgoCdPaths.LocalDataRoot);
    }

    [Fact]
    public void CoverageRoot_IsRootedUnderOsTempPath()
    {
        var expectedRoot = Path.Combine(Path.GetTempPath(), "argocd-coverage");
        Assert.Equal(expectedRoot, ArgoCdPaths.CoverageRoot);
    }

    [Fact]
    public void TlsDataPath_DefaultsUnderLocalDataRoot()
    {
        Assert.StartsWith(ArgoCdPaths.LocalDataRoot, ArgoCdPaths.TlsDataPath);
        Assert.EndsWith("tls", ArgoCdPaths.TlsDataPath);
    }

    [Fact]
    public void SshDataPath_DefaultsUnderLocalDataRoot()
    {
        Assert.StartsWith(ArgoCdPaths.LocalDataRoot, ArgoCdPaths.SshDataPath);
        Assert.EndsWith("ssh", ArgoCdPaths.SshDataPath);
    }

    [Fact]
    public void GpgKeysPath_DefaultsUnderLocalDataRoot_GpgKeysSubdirectory()
    {
        Assert.StartsWith(ArgoCdPaths.LocalDataRoot, ArgoCdPaths.GpgKeysPath);
        Assert.Equal(Path.Combine(ArgoCdPaths.LocalDataRoot, "gpg", "keys"), ArgoCdPaths.GpgKeysPath);
    }

    [Fact]
    public void GpgSourcePath_DefaultsUnderLocalDataRoot_GpgSourceSubdirectory()
    {
        Assert.StartsWith(ArgoCdPaths.LocalDataRoot, ArgoCdPaths.GpgSourcePath);
        Assert.Equal(Path.Combine(ArgoCdPaths.LocalDataRoot, "gpg", "source"), ArgoCdPaths.GpgSourcePath);
    }

    [Fact]
    public void CmpPluginSocketPath_Defaults_ToRepoRootRelative_TestCmp()
    {
        Environment.SetEnvironmentVariable("ARGOCD_PLUGINSOCKFILEPATH", null);

        var repoRoot = Path.Combine(Path.GetTempPath(), "argocd-repo-root-fixture");
        var expected = Path.Combine(repoRoot, "test", "cmp");

        Assert.Equal(expected, ArgoCdPaths.CmpPluginSocketPath(repoRoot));
    }

    [Fact]
    public void CmpPluginSocketPath_HonorsEnvironmentOverride()
    {
        var overridePath = Path.Combine(Path.GetTempPath(), "custom-cmp-socket-dir");
        Environment.SetEnvironmentVariable("ARGOCD_PLUGINSOCKFILEPATH", overridePath);
        try
        {
            Assert.Equal(overridePath, ArgoCdPaths.CmpPluginSocketPath("/any/repo/root"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARGOCD_PLUGINSOCKFILEPATH", null);
        }
    }

    [Fact]
    public void CoverageDir_Defaults_ToCoverageRoot_PerComponentSubdirectory()
    {
        Environment.SetEnvironmentVariable("ARGOCD_COVERAGE_DIR", null);

        var expected = Path.Combine(ArgoCdPaths.CoverageRoot, "repo-server");
        Assert.Equal(expected, ArgoCdPaths.CoverageDir("repo-server"));
    }

    [Fact]
    public void CoverageDir_HonorsEnvironmentOverride_VerbatimForEveryComponent()
    {
        // Matches the upstream Procfile's exact (quirky) behavior: if ARGOCD_COVERAGE_DIR is set,
        // it is used verbatim for every component -- it is NOT joined with the component name.
        var overrideDir = Path.Combine(Path.GetTempPath(), "custom-coverage-dir");
        Environment.SetEnvironmentVariable("ARGOCD_COVERAGE_DIR", overrideDir);
        try
        {
            Assert.Equal(overrideDir, ArgoCdPaths.CoverageDir("repo-server"));
            Assert.Equal(overrideDir, ArgoCdPaths.CoverageDir("api-server"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARGOCD_COVERAGE_DIR", null);
        }
    }

    [Fact]
    public void EnsureDirectoryExists_CreatesNestedDirectory_AndIsIdempotent()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "argocd-aspire-paths-tests-" + Guid.NewGuid().ToString("N"),
            "nested",
            "child");

        try
        {
            Assert.False(Directory.Exists(dir));

            ArgoCdPaths.EnsureDirectoryExists(dir);
            Assert.True(Directory.Exists(dir));

            // Calling again must not throw.
            ArgoCdPaths.EnsureDirectoryExists(dir);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            var root = Path.Combine(Path.GetTempPath(), dir.Split(Path.DirectorySeparatorChar)[^3]);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
