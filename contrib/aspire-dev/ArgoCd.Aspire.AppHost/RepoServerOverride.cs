using System.Diagnostics;
using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// Computes the Docker image tag used for a locally rebuilt <c>argocd-repo-server</c> override
/// image. Kept as a small pure function so it can be unit tested without Docker/git.
/// </summary>
public static class RepoServerImageTag
{
    public const string ImageName = "argocd-aspire-repo-server-dev";

    /// <summary>
    /// Builds a tag such as <c>7ca0120-dirty-20260722T193201Z</c> from a short git commit hash,
    /// whether the working tree has local modifications, and a UTC timestamp. Including the
    /// timestamp guarantees a new, distinct tag on every rebuild (so Kind always loads the new
    /// image rather than reusing a cached layer keyed only by content-addressed tag reuse).
    /// </summary>
    public static string ComputeTag(string shortGitCommit, bool dirty, DateTimeOffset utcNow)
    {
        ArgumentException.ThrowIfNullOrEmpty(shortGitCommit);

        var suffix = dirty ? "dirty" : "clean";
        var timestamp = utcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return $"{shortGitCommit}-{suffix}-{timestamp}";
    }

    public static string FullImageReference(string tag) => $"{ImageName}:{tag}";
}

/// <summary>
/// Adds a "Rebuild repo-server (source override)" dashboard command to a Kind cluster resource.
/// This is the AppHost-side implementation of deliverable #5: build the current working tree's
/// <c>argocd-repo-server</c> using the same tooling as <c>make image DEV_IMAGE=true</c>
/// (see docs/developer-guide/running-locally.md), load the resulting image into the Kind
/// cluster, patch and restart the <c>argocd-repo-server</c> Deployment, and prove the new
/// binary is running.
///
/// This intentionally re-executes the exact commands the Makefile's <c>image</c> target (DEV_IMAGE
/// branch) documents rather than inventing new build logic:
///   1. docker build --target argocd-base .            (same as `make image DEV_IMAGE=true`)
///   2. go build ... -o dist/argocd ./cmd               (same ldflags/gcflags as the Makefile)
///   3. docker build -f Dockerfile.dev                  (same Dockerfile.dev the Makefile copies)
/// `make`/bash are not invoked directly because a POSIX shell is not reliably available on
/// Windows; see contrib/aspire-dev/README.md for details and the exact equivalent `make` commands
/// for contributors on macOS/Linux.
/// </summary>
public static class RepoServerOverrideCommands
{
    private const string RepoServerDeploymentName = "argocd-repo-server";
    private const string RepoServerContainerName = "argocd-repo-server";
    private const string ArgoCdNamespace = "argocd";

    public static IResourceBuilder<KindClusterResource> WithRepoServerOverrideCommand(
        this IResourceBuilder<KindClusterResource> builder,
        string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);

        builder.WithCommand(
            name: "rebuild-repo-server",
            displayName: "Rebuild repo-server (source override)",
            executeCommand: _ => RebuildAndDeployAsync(builder.Resource, repoRoot),
            new CommandOptions
            {
                Description =
                    "Builds argocd-repo-server from the current working tree (same tooling as " +
                    "'make image DEV_IMAGE=true'), loads it into this Kind cluster, patches and " +
                    "restarts the argocd-repo-server Deployment, and confirms the new binary is running.",
                IconName = "ArrowSync",
                IsHighlighted = true,
                UpdateState = _ => ResourceCommandState.Enabled,
            });

        return builder;
    }

    private static async Task<ExecuteCommandResult> RebuildAndDeployAsync(
        KindClusterResource cluster,
        string repoRoot)
    {
        var log = new System.Text.StringBuilder();

        try
        {
            var (commitExit, commitStdout, _) = await RunAsync("git", ["rev-parse", "--short", "HEAD"], repoRoot);
            var shortCommit = commitExit == 0 ? commitStdout.Trim() : "unknown";

            var (statusExit, statusStdout, _) = await RunAsync("git", ["status", "--porcelain"], repoRoot);
            var dirty = statusExit != 0 || !string.IsNullOrWhiteSpace(statusStdout);

            var tag = RepoServerImageTag.ComputeTag(shortCommit, dirty, DateTimeOffset.UtcNow);
            var imageRef = RepoServerImageTag.FullImageReference(tag);

            var buildDir = Path.Combine(repoRoot, "dist", "aspire-repo-server-override");
            Directory.CreateDirectory(buildDir);

            log.AppendLine($"Image reference: {imageRef}");

            // 1. Base image (helm/kustomize/git-lfs/tini/etc). Cheap when cached.
            log.AppendLine("Step 1/5: docker build --target argocd-base (reused from Dockerfile) ...");
            var baseExit = await RunStreamedAsync(
                "docker",
                ["build", "--platform=linux/amd64", "-t", "argocd-base", "--target", "argocd-base", "."],
                repoRoot,
                log);
            if (baseExit != 0)
            {
                return Failure(log, "docker build --target argocd-base failed.");
            }

            // 2. Cross-compile the argocd binary from the current working tree — identical
            //    go build invocation to the Makefile's `image:` target (DEV_IMAGE branch).
            log.AppendLine("Step 2/5: go build (cross-compiled linux/amd64, same ldflags as Makefile) ...");
            var binaryPath = Path.Combine(buildDir, "argocd");
            var ldflags = BuildLdFlags(shortCommit, dirty);
            var goExit = await RunStreamedAsync(
                "go",
                ["build", "-v", "-ldflags", ldflags, "-gcflags=all=-N -l", "-o", binaryPath, "./cmd"],
                repoRoot,
                log,
                extraEnv: new Dictionary<string, string>
                {
                    ["GOOS"] = "linux",
                    ["GOARCH"] = "amd64",
                    ["CGO_ENABLED"] = "0",
                    ["GODEBUG"] = "tarinsecurepath=0,zipinsecurepath=0",
                });
            if (goExit != 0)
            {
                return Failure(log, "go build failed.");
            }

            // 3. Same Dockerfile.dev the Makefile copies into dist/ before building.
            File.Copy(Path.Combine(repoRoot, "Dockerfile.dev"), Path.Combine(buildDir, "Dockerfile.dev"), overwrite: true);
            log.AppendLine("Step 3/5: docker build -f Dockerfile.dev (packages the compiled binary) ...");
            var imageExit = await RunStreamedAsync(
                "docker",
                ["build", "--platform=linux/amd64", "-t", imageRef, "-f", "Dockerfile.dev", "."],
                buildDir,
                log);
            if (imageExit != 0)
            {
                return Failure(log, "docker build -f Dockerfile.dev failed.");
            }

            // 4. Load into the running Kind cluster (no registry push required).
            log.AppendLine("Step 4/5: kind load docker-image ...");
            var loadExit = await RunStreamedAsync(
                "kind",
                ["load", "docker-image", imageRef, "--name", cluster.ClusterName],
                repoRoot,
                log);
            if (loadExit != 0)
            {
                return Failure(log, "kind load docker-image failed.");
            }

            // 5. Patch + restart + wait + verify.
            log.AppendLine("Step 5/5: patch argocd-repo-server Deployment, restart, and verify ...");
            var patch = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{{\"spec\":{{\"template\":{{\"spec\":{{\"containers\":[{{\"name\":\"{0}\",\"image\":\"{1}\",\"imagePullPolicy\":\"IfNotPresent\"}}]}}}}}}}}",
                RepoServerContainerName,
                imageRef);
            var patchExit = await RunStreamedAsync(
                "kubectl",
                ["--kubeconfig", cluster.KubeconfigPath, "-n", ArgoCdNamespace, "patch", "deployment", RepoServerDeploymentName,
                 "--type", "strategic", "-p", patch],
                repoRoot,
                log);
            if (patchExit != 0)
            {
                return Failure(log, "kubectl patch deployment/argocd-repo-server failed.");
            }

            var rolloutExit = await RunStreamedAsync(
                "kubectl",
                ["--kubeconfig", cluster.KubeconfigPath, "-n", ArgoCdNamespace, "rollout", "status",
                 $"deployment/{RepoServerDeploymentName}", "--timeout=180s"],
                repoRoot,
                log);
            if (rolloutExit != 0)
            {
                return Failure(log, "Rollout of argocd-repo-server did not complete in time.");
            }

            var (logsExit, logsStdout, logsStderr) = await RunAsync(
                "kubectl",
                ["--kubeconfig", cluster.KubeconfigPath, "-n", ArgoCdNamespace, "logs",
                 $"deployment/{RepoServerDeploymentName}", "--tail=20"],
                repoRoot);

            var logsText = logsExit == 0 ? logsStdout : logsStderr;
            var confirmed = logsText.Contains($"\"commit\":\"{shortCommit}", StringComparison.Ordinal)
                || logsText.Contains(shortCommit, StringComparison.Ordinal);

            log.AppendLine();
            log.AppendLine(confirmed
                ? $"CONFIRMED: argocd-repo-server logs reference commit '{shortCommit}' — the running pod is the rebuilt image."
                : $"WARNING: could not find commit '{shortCommit}' in the last 20 log lines; check the log tail below.");
            log.AppendLine("--- argocd-repo-server log tail ---");
            log.AppendLine(logsText);

            return CommandResults.Success(
                confirmed ? "repo-server override deployed and verified." : "repo-server override deployed (verification inconclusive).",
                log.ToString(),
                CommandResultFormat.Text,
                true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.AppendLine($"EXCEPTION: {ex}");
            return CommandResults.Failure(ex.Message, log.ToString(), CommandResultFormat.Text);
        }
    }

    private static string BuildLdFlags(string gitCommit, bool dirty)
    {
        const string package = "github.com/argoproj/argo-cd/v3/common";
        var treeState = dirty ? "dirty" : "clean";
        var buildDate = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return $"-X {package}.version=aspire-dev -X {package}.buildDate={buildDate} " +
               $"-X {package}.gitCommit={gitCommit} -X {package}.gitTreeState={treeState} -extldflags \"-static\"";
    }

    private static ExecuteCommandResult Failure(System.Text.StringBuilder log, string message)
    {
        log.AppendLine($"FAILED: {message}");
        return CommandResults.Failure(message, log.ToString(), CommandResultFormat.Text);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// Runs a process and appends combined stdout/stderr to <paramref name="log"/> as it
    /// completes (not truly streamed line-by-line, to keep this helper simple — good enough
    /// for a manually triggered dashboard command where total output is small).
    /// </summary>
    private static async Task<int> RunStreamedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        System.Text.StringBuilder log,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv) psi.Environment[key] = value;
        }

        log.AppendLine($"$ {fileName} {string.Join(' ', arguments)}");

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stdout)) log.AppendLine(Tail(stdout, 4000));
        if (!string.IsNullOrWhiteSpace(stderr)) log.AppendLine(Tail(stderr, 4000));

        return process.ExitCode;
    }

    private static string Tail(string text, int maxChars) =>
        text.Length <= maxChars ? text : "...(truncated)...\n" + text[^maxChars..];
}
