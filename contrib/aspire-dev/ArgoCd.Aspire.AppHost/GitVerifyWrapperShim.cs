using System.Diagnostics;

namespace ArgoCd.Aspire.AppHost;

internal static class GitVerifyWrapperShim
{
    private const string WrapperName = "git-verify-wrapper.sh";

    public static string? EnsureAvailableIfNeeded(string repoRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "generated", "windows-git-verify-wrapper");
        Directory.CreateDirectory(directory);

        var sourcePath = Path.Combine(directory, "git-verify-wrapper.go");
        var wrapperPath = Path.Combine(directory, WrapperName);
        File.WriteAllText(sourcePath, Source);

        if (!File.Exists(wrapperPath) || File.GetLastWriteTimeUtc(wrapperPath) < File.GetLastWriteTimeUtc(sourcePath))
        {
            RunGoBuild(repoRoot, sourcePath, wrapperPath);
        }

        return directory;
    }

    private static void RunGoBuild(string repoRoot, string sourcePath, string wrapperPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "go",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }.AddArguments("build", "-o", wrapperPath, sourcePath))
            ?? throw new InvalidOperationException("Failed to start 'go build' for the Windows git verify wrapper.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to build Windows {WrapperName} shim with exit code {process.ExitCode}.\nstdout: {stdout}\nstderr: {stderr}");
        }
    }

    private const string Source =
        """
        package main

        import (
        	"fmt"
        	"os"
        	"os/exec"
        	"strings"
        )

        func main() {
        	if len(os.Args) < 2 || os.Args[1] == "" {
        		fmt.Fprintln(os.Stderr, "Wrong usage of git-verify-wrapper.sh")
        		os.Exit(1)
        	}

        	revision := os.Args[1]
        	kind := "commit"
        	if exec.Command("git", "describe", "--exact-match", revision).Run() == nil {
        		kind = "tag"
        	}

        	args := []string{"verify-commit", revision}
        	if kind == "tag" {
        		args = []string{"verify-tag", revision}
        	}

        	output, err := exec.Command("git", args...).CombinedOutput()
        	exitCode := 0
        	if err != nil {
        		exitCode = 1
        		if exitErr, ok := err.(*exec.ExitError); ok {
        			exitCode = exitErr.ExitCode()
        		}
        	}

        	text := string(output)
        	switch exitCode {
        	case 0:
        		fmt.Print(text)
        	case 1:
        		if kind == "tag" && strings.HasPrefix(text, "error:") {
        			text = ""
        		}
        		fmt.Print(text)
        		exitCode = 0
        	default:
        		fmt.Fprint(os.Stderr, text)
        	}

        	os.Exit(exitCode)
        }
        """;

    private static ProcessStartInfo AddArguments(this ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
