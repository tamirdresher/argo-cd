using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHost.Tests;

public sealed class ArgoCdUiDependenciesTests : IDisposable
{
    private readonly string _uiDir;

    public ArgoCdUiDependenciesTests()
    {
        _uiDir = Path.Combine(Path.GetTempPath(), "argocd-aspire-ui-deps-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_uiDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_uiDir))
        {
            Directory.Delete(_uiDir, recursive: true);
        }
    }

    [Fact]
    public void NeedsInstall_ReturnsTrue_WhenNodeModulesMissing()
    {
        // node_modules absent, no lockfile either -- missing node_modules always wins.
        Assert.True(ArgoCdUiDependencies.NeedsInstall(_uiDir));
    }

    [Fact]
    public void NeedsInstall_ReturnsFalse_WhenNodeModulesPresentAndNoLockFile()
    {
        Directory.CreateDirectory(Path.Combine(_uiDir, "node_modules"));

        // No pnpm-lock.yaml to compare against: assume the existing node_modules is usable rather
        // than forcing an unnecessary reinstall.
        Assert.False(ArgoCdUiDependencies.NeedsInstall(_uiDir));
    }

    [Fact]
    public void NeedsInstall_ReturnsFalse_WhenNodeModulesNewerThanLockFile()
    {
        var lockFile = Path.Combine(_uiDir, "pnpm-lock.yaml");
        File.WriteAllText(lockFile, "lockfileVersion: '9.0'");
        File.SetLastWriteTimeUtc(lockFile, DateTime.UtcNow.AddMinutes(-10));

        var nodeModules = Path.Combine(_uiDir, "node_modules");
        Directory.CreateDirectory(nodeModules);
        Directory.SetLastWriteTimeUtc(nodeModules, DateTime.UtcNow);

        Assert.False(ArgoCdUiDependencies.NeedsInstall(_uiDir));
    }

    [Fact]
    public void NeedsInstall_ReturnsTrue_WhenLockFileNewerThanNodeModules()
    {
        var nodeModules = Path.Combine(_uiDir, "node_modules");
        Directory.CreateDirectory(nodeModules);
        Directory.SetLastWriteTimeUtc(nodeModules, DateTime.UtcNow.AddMinutes(-10));

        var lockFile = Path.Combine(_uiDir, "pnpm-lock.yaml");
        File.WriteAllText(lockFile, "lockfileVersion: '9.0'");
        File.SetLastWriteTimeUtc(lockFile, DateTime.UtcNow);

        Assert.True(ArgoCdUiDependencies.NeedsInstall(_uiDir));
    }

    [Fact]
    public void EnsureInstalledOrThrow_SkipsEntirely_WhenSkipEnvironmentVariableSet()
    {
        // node_modules missing (would normally require an install), but the skip variable must
        // short-circuit before any process is even attempted -- if it didn't, this test would hang
        // or fail attempting to invoke a real `corepack pnpm install` in a directory with no
        // package.json.
        Environment.SetEnvironmentVariable(ArgoCdUiDependencies.SkipEnvironmentVariable, "true");
        try
        {
            var exception = Record.Exception(() =>
                ArgoCdUiDependencies.EnsureInstalledOrThrow(_uiDir, TimeSpan.FromSeconds(5)));

            Assert.Null(exception);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArgoCdUiDependencies.SkipEnvironmentVariable, null);
        }
    }

    [Fact]
    public void EnsureInstalledOrThrow_SkipsEntirely_WhenInstallNotNeeded()
    {
        // node_modules already present with no lockfile: NeedsInstall is false, so no process
        // should be spawned at all.
        Directory.CreateDirectory(Path.Combine(_uiDir, "node_modules"));

        var exception = Record.Exception(() =>
            ArgoCdUiDependencies.EnsureInstalledOrThrow(_uiDir, TimeSpan.FromSeconds(5)));

        Assert.Null(exception);
    }
}
