using ArgoCd.Aspire.AppHost;
using Xunit;

namespace ArgoCd.Aspire.AppHostTests;

/// <summary>
/// Fast, no-Docker/no-kubectl tests for <see cref="DeleteClusterCommand.TryDeleteKubeconfig"/>,
/// the best-effort kubeconfig cleanup helper used by the "delete-cluster" dashboard command.
/// </summary>
public class DeleteClusterCommandTests
{
    [Fact]
    public void TryDeleteKubeconfig_DeletesExistingFile_AndReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"argocd-dev-kubeconfig-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, "fake-kubeconfig-contents");
        Assert.True(File.Exists(path));

        var warning = DeleteClusterCommand.TryDeleteKubeconfig(path);

        Assert.Null(warning);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryDeleteKubeconfig_ReturnsNull_WhenFileDoesNotExist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"argocd-dev-kubeconfig-missing-{Guid.NewGuid():N}.yaml");
        Assert.False(File.Exists(path));

        var warning = DeleteClusterCommand.TryDeleteKubeconfig(path);

        Assert.Null(warning);
    }

    [Fact]
    public void TryDeleteKubeconfig_ReturnsWarning_WhenDeletionThrows()
    {
        // NOTE: File.Exists(...) returns false for directory paths, so a directory can't be used
        // to exercise the catch branch (TryDeleteKubeconfig would take the "does not exist"
        // early-return instead of ever calling File.Delete). Use a real file and force File.Delete
        // itself to throw instead.
        var dirPath = Path.Combine(Path.GetTempPath(), $"argocd-dev-kubeconfig-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dirPath);
        var filePath = Path.Combine(dirPath, "kubeconfig.yaml");
        File.WriteAllText(filePath, "fake-kubeconfig-contents");

        FileStream? exclusiveLock = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // On Windows, holding the file open without FileShare.Delete makes File.Delete
                // throw IOException ("being used by another process").
                exclusiveLock = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            else
            {
                // On Unix, deletion is governed by the containing directory's write permission
                // rather than the file itself, so remove write/execute access from the directory.
                File.SetUnixFileMode(
                    dirPath,
                    UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            var warning = DeleteClusterCommand.TryDeleteKubeconfig(filePath);

            Assert.NotNull(warning);
            Assert.Contains(filePath, warning, StringComparison.Ordinal);
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            exclusiveLock?.Dispose();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    dirPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            Directory.Delete(dirPath, recursive: true);
        }
    }
}
