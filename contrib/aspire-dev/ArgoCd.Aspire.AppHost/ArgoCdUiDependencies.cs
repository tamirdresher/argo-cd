namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Automates the one dependency-install step the UI executable resource needs before <c>pnpm
/// start</c> can succeed: installing <c>ui/node_modules</c> via the exact pnpm version pinned by
/// <c>ui/package.json</c>'s <c>"packageManager"</c> field, resolved automatically by
/// <c>corepack</c> (no separate global pnpm version-management step required). Codegen (protobuf,
/// swagger, mocks, etc.) is intentionally NOT run here — it stays on-demand via the repository's
/// existing `make codegen` targets, consistent with the "no make in the default loop" requirement
/// applying to *running* components, not to a one-time, explicit codegen step a contributor opts
/// into separately.
/// </summary>
public static class ArgoCdUiDependencies
{
    /// <summary>
    /// Set to "true" or "1" to skip automatic <c>pnpm install</c> entirely — e.g. for offline
    /// runs, CI environments that pre-install dependencies out of band, or contributors who want
    /// full manual control over when <c>ui/node_modules</c> is refreshed.
    /// </summary>
    public const string SkipEnvironmentVariable = "ARGOCD_ASPIRE_SKIP_UI_INSTALL";

    public readonly record struct PnpmInvocation(string Command, string[] Arguments);

    /// <summary>
    /// True if <c>ui/node_modules</c> is missing, or if <c>ui/package.json</c> or
    /// <c>ui/pnpm-lock.yaml</c> was modified more recently than <c>ui/node_modules</c> (a cheap,
    /// dependency-free proxy for "the manifest changed since the last install" — the same
    /// heuristic tools like Yarn/npm's own "up to date" checks use, without needing to shell out
    /// or parse the lockfile).
    /// </summary>
    public static bool NeedsInstall(string uiDir)
    {
        var nodeModulesPath = Path.Combine(uiDir, "node_modules");
        if (!Directory.Exists(nodeModulesPath))
        {
            return true;
        }

        return IsNewerThanNodeModules(Path.Combine(uiDir, "package.json"), nodeModulesPath)
            || IsNewerThanNodeModules(Path.Combine(uiDir, "pnpm-lock.yaml"), nodeModulesPath);
    }

    private static bool IsNewerThanNodeModules(string dependencyFilePath, string nodeModulesPath)
    {
        if (!File.Exists(dependencyFilePath))
        {
            // No manifest to compare against (unexpected in this repository, but don't force a
            // reinstall just because we can't find it) — node_modules already exists, so assume
            // it's usable.
            return false;
        }

        return File.GetLastWriteTimeUtc(dependencyFilePath) > Directory.GetLastWriteTimeUtc(nodeModulesPath);
    }

    /// <summary>
    /// Runs <c>corepack pnpm install</c> inside <paramref name="uiDir"/> when
    /// <see cref="NeedsInstall"/> is true and installation hasn't been opted out of via
    /// <see cref="SkipEnvironmentVariable"/>. Synchronous and blocking by design: this must
    /// complete before the "ui" executable resource is registered, exactly like
    /// <see cref="ArgoCdPrerequisites.ValidateOrThrow"/> fails fast before any resource is built.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <c>corepack pnpm install</c> exited non-zero, or timed out.
    /// </exception>
    public static void EnsureInstalledOrThrow(string uiDir, TimeSpan timeout)
    {
        if (IsSkipRequested())
        {
            return;
        }

        if (!NeedsInstall(uiDir))
        {
            return;
        }

        var pnpm = GetPnpmInvocation();
        var args = pnpm.Arguments.Concat(["install"]).ToArray();

        var (exitCode, stdout, stderr) = ArgoCdManifestRenderer
            .RunCaptureAsync(
                pnpm.Command,
                args,
                timeout,
                CancellationToken.None,
                workingDirectory: uiDir)
            .GetAwaiter()
            .GetResult();

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"'corepack pnpm install' failed in '{uiDir}' (exit code {exitCode}).{Environment.NewLine}" +
                $"--- stdout ---{Environment.NewLine}{stdout}{Environment.NewLine}" +
                $"--- stderr ---{Environment.NewLine}{stderr}{Environment.NewLine}" +
                "Fix: run 'corepack enable' once (requires Node >= 16.9), then re-run the AppHost; " +
                $"or install dependencies manually with 'corepack pnpm install' in '{uiDir}'; " +
                $"or set {SkipEnvironmentVariable}=true to skip automatic installation.");
        }
    }

    public static PnpmInvocation GetPnpmInvocation()
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var pnpmCjs = Path.Combine(directory, "node_modules", "pnpm", "bin", "pnpm.cjs");
                    if (File.Exists(pnpmCjs))
                    {
                        return new PnpmInvocation("node", [pnpmCjs]);
                    }
                }
            }
        }

        return new PnpmInvocation("pnpm", []);
    }

    private static bool IsSkipRequested()
    {
        var value = Environment.GetEnvironmentVariable(SkipEnvironmentVariable);
        return value is not null
            && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
    }
}
