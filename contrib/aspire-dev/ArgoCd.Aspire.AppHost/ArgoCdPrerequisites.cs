using System.Diagnostics;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Validates that every tool the Aspire-managed dev loop depends on is installed and usable
/// before any resources are registered, so a missing prerequisite produces a single actionable
/// error message instead of a confusing failure deep inside resource startup.
/// </summary>
public static class ArgoCdPrerequisites
{
    public const string SkipEnvironmentVariable = "ARGOCD_ASPIRE_SKIP_PREREQUISITE_CHECKS";

    /// <summary>
    /// Runs all prerequisite checks and throws <see cref="InvalidOperationException"/> with a
    /// combined, actionable message if any are missing. Intended to be called once, synchronously,
    /// at the very start of <c>AppHost.cs</c> before <see cref="Aspire.Hosting.DistributedApplication"/>
    /// building begins.
    /// </summary>
    public static void ValidateOrThrow()
    {
        var failures = new List<string>();

        CheckTool(failures, "docker", ["info"], "Docker daemon",
            "Install Docker Desktop (or another Docker-compatible daemon) and ensure it is running. " +
            "Kind creates its cluster nodes as Docker containers, so this dev loop cannot start without it.");

        CheckTool(failures, "kind", ["version"], "kind",
            "Install kind: https://kind.sigs.k8s.io/docs/user/quick-start/#installation");

        CheckTool(failures, "kubectl", ["version", "--client"], "kubectl",
            "Install kubectl: https://kubernetes.io/docs/tasks/tools/#kubectl");

        CheckTool(failures, "go", ["version"], "Go toolchain",
            "Install Go (matching go.mod's declared version): https://go.dev/dl/");

        CheckTool(failures, "node", ["--version"], "Node.js",
            "Install Node.js (LTS): https://nodejs.org/");

        CheckTool(failures, "corepack", ["--version"], "corepack",
            "corepack ships with Node.js >= 16.9. If missing, run: npm install -g corepack");

        // pnpm itself is only required to be resolvable via corepack, not necessarily on PATH
        // directly, so this check is best-effort and non-fatal even if it fails.
        CheckToolSoft(failures, "pnpm", ["--version"]);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "One or more prerequisites for the Argo CD Aspire dev loop are missing or not working:" +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine + Environment.NewLine, failures));
        }
    }

    private static void CheckTool(
        List<string> failures,
        string command,
        string[] args,
        string displayName,
        string remediation)
    {
        if (!TryRun(command, args))
        {
            failures.Add($"- {displayName} ('{command}') is not installed or not working.{Environment.NewLine}  {remediation}");
        }
    }

    private static void CheckToolSoft(List<string> failures, string command, string[] args)
    {
        // Intentionally does not add to failures: pnpm may be installed under a corepack shim
        // that only materializes on first invocation with a pinned version, which the
        // prerequisite check should not attempt to trigger.
        _ = TryRun(command, args);
    }

    // internal (not private) so ArgoCdPrerequisitesTests, which is compiled directly into the
    // Tests assembly via a linked <Compile Include>, can exercise the process-invocation logic
    // without needing to mock the hard-coded external tool commands in ValidateOrThrow().
    internal static bool TryRun(string command, string[] args)
    {
        try
        {
            var startInfo = CreateStartInfo(command, args);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string command, string[] args)
    {
        command = ResolveWindowsCommandPath(command);

        if (OperatingSystem.IsWindows()
            && Path.GetExtension(command) is var extension
            && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            return startInfo;
        }

        var directStartInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            directStartInfo.ArgumentList.Add(arg);
        }

        return directStartInfo;
    }

    private static string ResolveWindowsCommandPath(string command)
    {
        if (!OperatingSystem.IsWindows() || Path.GetExtension(command).Length > 0)
        {
            return command;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return command;
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory, command + extension.ToLowerInvariant());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return command;
    }
}
