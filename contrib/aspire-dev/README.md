# Aspire + Kind development environment for Argo CD (contrib)

This directory contains an optional, contributor-facing [.NET Aspire](https://aspire.dev)
AppHost that provisions a deterministic [Kind](https://kind.sigs.k8s.io/)
(Kubernetes-in-Docker) cluster, deploys the checked-in baseline Argo CD
[install manifest](../../manifests/install.yaml), and gives you a one-click way to rebuild
and redeploy a single Argo CD component's source — starting with `argocd-repo-server` —
without hand-typing `docker build` / `kind load` / `kubectl patch` every time.

> [!IMPORTANT]
> This **complements**, and does **not replace**, Argo CD's official
> [Tiltfile](../../Tiltfile)-based inner loop described in
> [docs/developer-guide/running-locally.md](../../docs/developer-guide/running-locally.md).
> Tilt remains the primary, live-reloading workflow for iterating on **all** Argo CD
> components at once with fast rebuild-on-save. This Aspire environment targets a narrower
> scenario: a single reproducible command that stands up a throwaway cluster plus an
> Aspire-dashboard view (pods, logs, resource commands) for contributors who want that, and a
> guided one-component source-override loop for validating a single change end to end.

## Aspire vs. Tilt — when to use which

| | Tilt (`tilt up`) | This Aspire environment (`aspire start`) |
|---|---|---|
| Components live-synced on save | All of them | None (deliberately out of scope for this pass — see "Next steps") |
| UI dev server | Yes (`pnpm start`, port 4000) | Not deployed |
| Debugger attach (delve) | Yes, per-component | No |
| Cluster | Whatever `kubectl` currently points at | Dedicated, disposable Kind cluster (`argocd-dev`) |
| Rebuild single component from source | Automatic, on file save | Manual, one dashboard command (`rebuild-repo-server`) |
| Dashboard / OTel traces / structured logs | No | Yes (Aspire dashboard) |

If you're doing active day-to-day development across the whole stack, use Tilt. Use this
environment when you want a disposable, from-scratch cluster with baseline Argo CD, or you
want to validate one specific rebuilt binary (e.g. a repo-server change) in isolation.

## Prerequisites

- Docker Desktop (or another Docker-compatible daemon) — running
- [`kind`](https://kind.sigs.k8s.io/docs/user/quick-start/#installation) on `PATH`
- [`kubectl`](https://kubernetes.io/docs/tasks/tools/) on `PATH`
- [`helm`](https://helm.sh/docs/intro/install/) on `PATH` (not required for the current
  AppHost, which uses `WithManifest` only, but the vendored Kind integration also supports
  `WithHelmChart` for future use)
- Go toolchain on `PATH` (same version the top-level `Dockerfile`/`go.mod` require) — used to
  cross-compile `argocd-repo-server` for the source-override command
- [.NET SDK 10.0.302+](https://dotnet.microsoft.com/download) (pinned via `global.json` in
  this directory)
- [Aspire CLI](https://aspire.dev) 13.4.6 (`aspire --version`)

Verify your environment with `aspire doctor` before starting.

> [!NOTE]
> **Windows + `core.autocrlf=true`:** if your git config converts checked-in shell scripts to
> CRLF line endings, `docker build` on the top-level `Dockerfile` will fail with
> `/bin/sh: 1: ./install.sh: not found` (exit code 127) when building the `builder` stage. This
> is a pre-existing, Windows-specific checkout issue unrelated to this contribution — fix it
> once per checkout with:
> ```powershell
> git config --local core.autocrlf false
> git checkout -- hack/ entrypoint.sh
> ```
> This does not affect this contribution's own C# files.

## Quick start

From the repository root:

```powershell
aspire start --non-interactive --apphost contrib/aspire-dev/ArgoCd.Aspire.AppHost/ArgoCd.Aspire.AppHost.csproj
```

This will (in order):
1. Create a Kind cluster named `argocd-dev` (deletes any stale cluster of the same name first).
2. Apply the `argocd` Namespace.
3. Server-side apply the Application/ApplicationSet/AppProject CRDs from
   [`manifests/crds`](../../manifests/crds) (see "Why CRDs are applied separately" below).
4. Apply the rest of the baseline install manifest (Deployments, Services, RBAC, ConfigMaps,
   ...), rendered from [`manifests/namespace-install`](../../manifests/namespace-install) +
   [`manifests/cluster-rbac`](../../manifests/cluster-rbac) via a thin kustomize overlay (see
   [`ArgoCd.Aspire.AppHost/manifests/argocd-namespaced/kustomization.yaml`](ArgoCd.Aspire.AppHost/manifests/argocd-namespaced/kustomization.yaml)).
5. Start the Aspire dashboard so you can watch cluster/pod status, logs, and run resource
   commands.

Open the dashboard URL printed by the command above. The `argocd-dev` resource exposes:

- **show-kubectl** / **show-helm** / **show-kubeconfig** — copyable commands (from the
  vendored Kind integration).
- **open-k9s** — launches [k9s](https://k9scli.io/) against this cluster in a new terminal, if
  installed.
- **rebuild-repo-server** — the source-override command (see below).
- **delete-cluster** — deletes the Kind cluster immediately (see "Known limitations").

To confirm Argo CD is reachable:

```powershell
$kubeconfig = "$env:TEMP\kind-argocd-dev-kubeconfig.yaml"   # printed as `kind.kubeconfig` in the dashboard
kubectl --kubeconfig $kubeconfig -n argocd get pods
kubectl --kubeconfig $kubeconfig -n argocd port-forward svc/argocd-server 8080:443
# in another shell:
curl -k https://localhost:8080/healthz   # -> ok
```

Get the initial admin password:

```powershell
kubectl --kubeconfig $kubeconfig -n argocd get secret argocd-initial-admin-secret -o jsonpath="{.data.password}" | % { [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($_)) }
```

### Repo-server source override

Trigger the **rebuild-repo-server** command from the dashboard (or
`aspire resource argocd-dev rebuild-repo-server --non-interactive`). This:

1. `docker build --target argocd-base .` — the same base-image step
   `make image DEV_IMAGE=true` performs (helm/kustomize/git-lfs/tini/etc., cached after the
   first run).
2. Cross-compiles `argocd` for `linux/amd64` from your **current working tree** with the same
   `ldflags`/`gcflags` as the Makefile's `image` target (DEV_IMAGE branch) — i.e. exactly what
   `docs/developer-guide/running-locally.md` documents, just invoked directly instead of
   through `make` (see "Why not just call `make`" below).
3. `docker build -f Dockerfile.dev` — packages the compiled binary, tagged
   `argocd-aspire-repo-server-dev:<short-sha>-<clean|dirty>-<UTC timestamp>`.
4. `kind load docker-image` into the `argocd-dev` cluster.
5. `kubectl patch deployment/argocd-repo-server` to the new image + `imagePullPolicy:
   IfNotPresent`, `kubectl rollout status --timeout=180s`, then greps the new pod's logs for
   the git commit hash to prove the running binary matches your working tree.

### Cleanup

```powershell
aspire resource argocd-dev delete-cluster --non-interactive   # deletes the Kind cluster now (recommended)
aspire stop --non-interactive                                  # stops the AppHost/dashboard
```

> [!WARNING]
> **Known limitation:** on this Aspire CLI/host combination, `aspire stop` was observed to
> hard-kill the AppHost process (`ProcessExecutionFactory ... wait was canceled, killing it`
> in `~/.aspire/logs/cli_*.log`) a few milliseconds after requesting shutdown — well before the
> vendored Kind integration's `BeforeStopAsync` cluster deletion (an async `kind delete
> cluster`, which takes several seconds) can complete. **Always run `delete-cluster` before
> `aspire stop`** for a guaranteed clean teardown; otherwise you may be left with an orphaned
> `argocd-dev-control-plane` Docker container. If you forget, clean up manually with:
> ```powershell
> kind delete cluster --name argocd-dev
> ```

## What was actually measured in this environment

(Windows 11 host, Docker Desktop 29.6.1 with WSL2 backend, warm Docker layer cache for the
`argocd-base` stage, warm Go module/build cache.)

- `aspire start` → Kind cluster `Ready` node: **~35 seconds**.
- Kind cluster `Ready` → all 7 `argocd` namespace pods `1/1 Running`: **~65 more seconds**
  (~100 seconds total from `aspire start` to a fully healthy baseline install).
- `GET https://localhost:8080/healthz` via `kubectl port-forward svc/argocd-server`: **200 ok**.
- `rebuild-repo-server`: completed successfully end to end (build → load → patch → rollout →
  verify), confirmed by the new pod's startup log line containing the exact working-tree
  commit hash, e.g. `{"built":"...","commit":"7ca01207e",...,"msg":"ArgoCD Repository Server
  is starting",...}`. Wall-clock was dominated by the Go compile step (a few minutes with a
  warm build cache; longer on a cold cache or first run).
- `delete-cluster` command + `aspire stop`: Kind cluster removed, no orphaned
  `argocd-dev-control-plane` container left behind.

## Why CRDs are applied separately (server-side apply)

The official docs apply `manifests/install.yaml` with `kubectl apply --server-side
--force-conflicts` specifically because the Application/ApplicationSet CRDs' embedded OpenAPI
schemas exceed kubectl's 262144-byte `kubectl.kubernetes.io/last-applied-configuration`
annotation limit under plain client-side apply. The vendored Kind integration's
`WithManifest(...)` always does a plain client-side `kubectl apply -f` (no flag to opt into
server-side), so this AppHost applies `manifests/crds` separately via a small, purpose-built
lifecycle hook — see
[`ArgoCd.Aspire.AppHost/CrdBootstrapHook.cs`](ArgoCd.Aspire.AppHost/CrdBootstrapHook.cs) — and
lets the vendored integration handle everything else (Deployments, Services, RBAC, ...) via
its normal `WithManifest` path, which is small enough to never hit that limit.

## Why not just call `make`

`make image DEV_IMAGE=true` is the officially documented equivalent of the
`rebuild-repo-server` command's steps 1–3. On this Windows host, however, the repository's
`Makefile` requires POSIX shell built-ins (`ln -sfn`, `uname`, `rev`, `test [ ]`, ...) that a
native Windows `make.exe` cannot supply — running it directly errors on `uname`/`rev` not
being recognized. Rather than reinventing a build system, `rebuild-repo-server` re-issues the
**exact same commands** the Makefile documents (`docker build --target argocd-base`, the
Makefile's own `go build` invocation with identical `ldflags`, `docker build -f
Dockerfile.dev`), just directly from C# instead of through `make`. On macOS/Linux, where a
POSIX shell is standard, you can use `make image DEV_IMAGE=true
IMAGE_NAMESPACE=<you> IMAGE_TAG=<tag>` directly and `kind load docker-image` the result
yourself if you prefer not to use this AppHost at all.

## Layout

```
contrib/aspire-dev/
├── global.json                              # pins the .NET SDK feature band
├── ArgoCd.Aspire.Hosting.Kind/               # vendored (unpublished) Kind hosting integration — see NOTICE.md
├── ArgoCd.Aspire.AppHost/                    # the AppHost itself
│   ├── AppHost.cs                            # entry point
│   ├── ArgoCdRepoRoot.cs                      # locates the repo root from any start directory
│   ├── ArgoCdManifestRenderer.cs              # `kubectl kustomize` wrapper (no cluster/Docker needed)
│   ├── CrdBootstrapHook.cs                    # server-side CRD apply (see above)
│   ├── RepoServerOverride.cs                  # the rebuild-repo-server command
│   ├── DeleteClusterCommand.cs                # the delete-cluster command
│   └── manifests/
│       ├── namespace.yaml                     # creates the `argocd` namespace
│       └── argocd-namespaced/kustomization.yaml  # namespaces the baseline install (excl. CRDs)
└── ArgoCd.Aspire.AppHost.Tests/               # xunit tests (see below)
```

## Tests

**Fast tests (no Docker required)** — repo-root resolution, image-tag computation, and a
`kubectl kustomize` render-and-assert test (skips quietly if `kubectl` isn't on `PATH`):

```powershell
cd contrib/aspire-dev
dotnet test ArgoCd.Aspire.AppHost.Tests
```

**Opt-in serial integration test** — creates a real, separate Kind cluster
(`argocd-dev-test`), applies the baseline manifests, and tears it down again. Requires Docker,
`kind`, `kubectl`, and takes several minutes:

```powershell
$env:ARGOCD_ASPIRE_KIND_INTEGRATION = "1"
cd contrib/aspire-dev
dotnet test ArgoCd.Aspire.AppHost.Tests --filter "FullyQualifiedName~KindClusterIntegrationTests"
```

This test suite is **not** part of `make test` / `make test-local` (the Go test suite) — it's
discovered only via `dotnet test` inside this directory, consistent with keeping this
contribution additive and opt-in.

## Limitations / next steps

- **No live component rebuild loop.** Unlike Tilt, editing Go source doesn't automatically
  redeploy anything; you re-run the `rebuild-repo-server` command explicitly. Extending this
  pattern to other components (server, application-controller, ...) would mean adding one more
  `With...OverrideCommand` call each, following the same pattern as `RepoServerOverride.cs`.
- **`aspire stop` cluster cleanup** — see the "Known limitation" callout above; always run
  `delete-cluster` first.
- **The local-OCI-Helm-chart, same-version-republish scenario** (inspired by
  [argoproj/argo-cd#18000](https://github.com/argoproj/argo-cd/issues/18000)) is a clearly
  documented **next phase**, not implemented in this pass. A follow-up AppHost command could:
  push a locally-built chart to an in-cluster (or Kind-loaded) OCI registry under the same
  version Argo CD already cached, and verify Argo CD picks up the new content (which is the
  crux of #18000) rather than serving a stale cache entry. This wasn't implemented here to
  avoid speculative, unvalidated behavior claims about that specific bug's root cause.
- **UI is not deployed.** This AppHost only stands up the Kubernetes-facing components from
  `manifests/install.yaml`; there is no `argocd-server` UI dev loop here (use Tilt or
  `make start`/`start-local` for that, or `kubectl port-forward` to reach the deployed
  `argocd-server`'s bundled UI, though it's the release image, not equivalent to `ui/`
  under active development).
- **Windows-only validation.** This environment was built and fully exercised (cluster up,
  baseline install healthy, `/healthz` reachable, repo-server override deployed and verified,
  cluster torn down cleanly) on Windows with Docker Desktop + WSL2. It should work unmodified
  on macOS/Linux (the vendored Kind integration and this AppHost's own process-launching code
  are cross-platform), but that has not been separately verified in this pass.

## Provenance

The Kind hosting integration in `ArgoCd.Aspire.Hosting.Kind/` is vendored, unpublished source —
see [`ArgoCd.Aspire.Hosting.Kind/NOTICE.md`](ArgoCd.Aspire.Hosting.Kind/NOTICE.md) for exact
provenance, license, and the specific API surface used.
