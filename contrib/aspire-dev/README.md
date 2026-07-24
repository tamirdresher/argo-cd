# Argo CD dev loop on .NET Aspire

This is an optional, contributor-maintained inner-loop for running Argo CD **directly on your
machine** — no `make`, no POSIX shell, no Docker image builds, no `kind load`, and no Kubernetes
Deployment patching in the default path. It works the same way on Windows, macOS, and Linux.

It complements [Tilt](../../docs/developer-guide/running-locally.md) — it does **not** replace it.
Tilt builds container images and deploys Argo CD *as Kubernetes workloads* inside the cluster,
which is the right tool when you need to validate manifests, Helm charts, RBAC, or in-cluster
behavior. This Aspire AppHost instead runs the Argo CD Go binaries as **host processes** on your
machine (exactly like `hack/goreman-start.sh` / the repo's `Procfile`), and uses Kind purely as a
**state store** — CRDs, RBAC, the `argocd` namespace, and the ConfigMaps/Secrets Argo CD reads at
runtime. No Argo CD component ever runs *inside* the Kind cluster in this loop. Use this when you
are iterating on Go code and want near-instant rebuild/restart with a real debugger attached; use
Tilt when you need to validate what actually ships in a container.

## TL;DR

```powershell
dotnet run --project contrib/aspire-dev/ArgoCd.Aspire.AppHost
```

or open `contrib/aspire-dev/ArgoCd.Aspire.AppHost.sln` (or the repo root, if it has an
`ArgoCd.Aspire.AppHost` startup project configured) in Visual Studio and press **F5**. The
Aspire dashboard opens automatically and shows every resource, its logs, and its endpoints.

The AppHost validates your toolchain first (Docker, kind, kubectl, Go, Node, corepack — see
[Prerequisites](#prerequisites)) and fails fast with an actionable message if something is
missing, before it tries to start anything.

## What actually starts

Running the AppHost creates the following Aspire resource graph:

```
argocd-dev (Kind cluster, state only — real kind cluster/kubeconfig name is derived per checkout,
            see "The Kind cluster name is derived per-checkout" below; the "argocd-dev" resource
            name is fixed)
  └─ bootstraps CRDs / argocd namespace / RBAC / ConfigMaps / Secrets (server-side apply)
redis                                    (Aspire-managed container, port 6379, no password — see
                                          "Redis: local-only, no password" below)
repo-server        (host process)  ──▶ waits: redis
commit-server       (host process)
api-server          (host process)  ──▶ waits: redis, repo-server
application-controller (host process) ──▶ waits: redis, repo-server, commit-server
applicationset-controller (host process) ──▶ waits: repo-server
notifications-controller (host process)
gendexcfg           (run-to-completion, opt-in) ──▶ ARGOCD_ASPIRE_ENABLE_DEX=true; waits: cluster state
dex                 (container, opt-in) ──▶ ARGOCD_ASPIRE_ENABLE_DEX=true; waits: gendexcfg completion
cmp-server          (host process, opt-in, Windows unsupported) ──▶ ARGOCD_ASPIRE_ENABLE_CMP=true
dev-mounter         (host process)  ──▶ waits: argocd-dev cluster
ui                  (host process, pnpm) ──▶ waits: api-server; ARGOCD_API_URL wired to api-server
```

Every host-process component is launched with the **exact same command, flags, and environment
variables** as the repo's `Procfile` (used by `hack/goreman-start.sh`), plus
`ARGOCD_FAKE_IN_CLUSTER=true` so each binary behaves as if it were running inside the cluster
while actually running on your machine.

### Component command map

| Aspire resource               | Binary invoked (`go run ./cmd/...`) | Key flags (Procfile-matching)                                            | Port(s) |
|--------------------------------|---------------------------------------|---------------------------------------------------------------------------|---------|
| `api-server`                   | `./cmd/argocd api-server`             | `--repo-server localhost:8081 --redis localhost:6379 --disable-auth true --insecure` | 8080 (+8083 metrics) |
| `repo-server`                  | `./cmd/argocd reposerver`             | `--redis localhost:6379`                                                  | 8081 (+8084 metrics) |
| `application-controller`       | `./cmd/argocd-application-controller`| `--repo-server localhost:8081 --redis localhost:6379`                     | 8082 metrics |
| `applicationset-controller`    | `./cmd/argocd-applicationset-controller` | `--argocd-repo-server localhost:8081`                                  | 8080 (webhook) + 8085 metrics |
| `notifications-controller`     | `./cmd/argocd-notification`           | `--argocd-repo-server localhost:8081`                                     | 9001 metrics |
| `commit-server`                | `./cmd/argocd-commitserver`           | (none — matches Procfile)                                                 | 8086 |
| `cmp-server` *(opt-in)*        | `./cmd/argocd-cmp-server`             | plugin socket at `ARGOCD_PLUGINSOCKFILEPATH` (see below)                  | n/a (Unix socket) |
| `gendexcfg` *(opt-in, run-to-completion)* | `./cmd/main.go gendexcfg` | `-o <generated-config-path> --kubeconfig <cluster kubeconfig> -n argocd`  | n/a |
| `dex` *(opt-in)*               | container `ghcr.io/dexidp/dex:v2.45.1`| entrypoint `dex`, args `serve <generated-config-path>` (bind-mounted read-only from `gendexcfg`'s output) | 5556 |

`api-server`, `repo-server`, and `application-controller` also receive a `REDIS_PASSWORD`
environment variable whenever the `redis` resource has a password configured — see
[Redis: local-only, no password](#redis-local-only-no-password) below for why the default dev
loop leaves it unset, and how that credential is threaded through when it isn't.

Every process is launched from the resolved repository root (see [`ArgoCdRepoRoot`](ArgoCd.Aspire.AppHost/ArgoCdRepoRoot.cs),
which walks upward from the AppHost's own build output looking for a directory containing both
`go.mod` and `manifests/install.yaml`), so `go run` always builds against your current working
tree — editing a `.go` file and letting it rebuild is the entire "inner loop."

`redis` is an Aspire-managed container bound to a fixed host port (`6379`), matching the
Procfile's hard-coded `--redis localhost:6379` (Argo CD does not support dynamic Redis discovery
in this dev configuration).

### Redis: local-only, no password

By default the `redis` resource is started **without a password**
(`.WithPassword(null)` in [`AppHost.cs`](ArgoCd.Aspire.AppHost/AppHost.cs)), even though
Aspire's `AddRedis` normally auto-generates and enforces one. This is a deliberate, explicit choice
for this dev loop, not an oversight:

- The `redis` container's port (`6379`) is bound only to `localhost` on your own machine, exactly
  like the Procfile-driven `hack/goreman-start.sh` loop it replaces — nothing upstream of Argo CD's
  own components ever needs to reach it, and nothing outside your machine can.
- Argo CD's real Redis auth mechanism is the `REDIS_PASSWORD` environment variable (see
  `util/cache/cache.go`); there is **no** `--redis-password` CLI flag. The `--redis` flag upstream
  components use is `host:port` only and never carries credentials.
- If Aspire's default password enforcement were left on, every component would need
  `REDIS_PASSWORD` threaded in for `NOAUTH` errors not to occur. [`ArgoCdComponents.cs`](ArgoCd.Aspire.AppHost/ArgoCdComponents.cs)
  implements exactly that threading (`WithRedisPassword`, applied to `api-server`, `repo-server`,
  and `application-controller` — the only components that talk to Redis) so that if you re-enable a
  password on the `redis` resource (or point `AppHost.cs` at a non-local, shared, or
  password-protected Redis instance), every consuming component automatically receives the correct
  credential — you do not need to edit each component individually.
- **Security boundary:** password-less Redis is only safe because this loop is single-user,
  localhost-only, and ephemeral. If you adapt this AppHost to point at a Redis instance that is
  shared, remote, or reachable by anything other than your own local components, you must supply a
  password (e.g. `builder.AddRedis("redis").WithPassword(...)` or point at an externally-managed
  Redis with `AddConnectionString`) — the credential threading in `ArgoCdComponents.cs` will pick it
  up automatically and every component will authenticate correctly. Do not reuse a shared/remote
  Redis instance without a password.

### Dex and CMP are opt-in

Most local development does not need Dex (SSO) or the ConfigManagementPlugin sidecar. Both are
skipped by default and only started when explicitly requested:

```powershell
$env:ARGOCD_ASPIRE_ENABLE_DEX = "true"
$env:ARGOCD_ASPIRE_ENABLE_CMP = "true"
dotnet run --project contrib/aspire-dev/ArgoCd.Aspire.AppHost
```

**CMP on Windows is not supported and fails fast with an accurate diagnostic.** Argo CD's CMP
server communicates over a Unix domain socket
(`ARGOCD_PLUGINSOCKFILEPATH`, default `<repo-root>/test/cmp`), which the Windows filesystem/kernel
does not support in the way `net.Listen("unix", ...)` requires. Setting
`ARGOCD_ASPIRE_ENABLE_CMP=true` on Windows throws a `PlatformNotSupportedException` explaining
exactly why, instead of silently starting a broken resource. On macOS/Linux, CMP starts normally
and listens on that socket path.

**Dex is modeled as two resources so SSO actually works, matching the Procfile's `dex:` line
exactly** (see [`ArgoCdDex.cs`](ArgoCd.Aspire.AppHost/ArgoCdDex.cs)):

1. `gendexcfg` — a run-to-completion executable resource equivalent to the Procfile's
   `ARGOCD_BINARY_NAME=argocd-dex go run github.com/argoproj/argo-cd/v3/cmd gendexcfg -o
   <path>/dex.yaml`: it runs `go run ./cmd/main.go gendexcfg -o <dexConfigPath> --kubeconfig
   <cluster kubeconfig> -n argocd` with `ARGOCD_BINARY_NAME=argocd-dex` set (so `cmd/main.go`
   dispatches into `cmd/argocd-dex/commands/argocd_dex.go`'s `gendexcfg` subcommand), and
   `WaitFor`s the Kind cluster's `argocd-state-bootstrap` health check so it never races the
   CRDs/RBAC that `ArgoCdStateBootstrapHook` applies. Unlike the Procfile (which relies on an
   ambient `kubectl` context), `--kubeconfig` is passed explicitly, because each worktree/clone
   here gets its own Kind cluster and kubeconfig (see [Kind holds state
   only](#kind-holds-state-only)) rather than sharing one ambient context.
2. `dex` — the published `ghcr.io/dexidp/dex:v2.45.1` container image, entrypoint `dex`, args
   `serve /dex.yaml`, with the file `gendexcfg` just generated on the host bind-mounted read-only
   into the container at `/dex.yaml` — the exact same in-container path the Procfile's
   `` -v `pwd`/dist/dex.yaml:/dex.yaml `` bind mount uses. `dex` uses `WaitForCompletion(gendexcfg)`
   (not `WaitFor`), so the container is guaranteed not to start until `gendexcfg` has exited
   successfully and the mounted file exists and is current. Dex listens on `http://localhost:5556`.

Both resources are only added to the app model when `ARGOCD_ASPIRE_ENABLE_DEX=true`; when it's
unset, neither resource exists and no Dex-related ports, processes, or files are touched.

## Kind holds state only

`argocd-dev` is the fixed *Aspire resource* name (what you see in the dashboard and pass to `aspire
resource ... delete-cluster`) — it is **not** the underlying `kind` cluster name. The actual `kind`
cluster (and its kubeconfig) is provisioned once and **kept between runs**
(`WithPersistentCluster()` — Aspire will reuse an existing cluster with a matching name instead of
deleting and recreating it, avoiding a slow teardown/recreate cycle on every `dotnet run`). On
first start (or whenever the cluster is missing), the AppHost:

1. Waits for the Kind cluster to report ready (control-plane node `Ready`, up to 10 minutes).
2. Server-side-applies, **in a fixed dependency-safe order** (see
   [`ArgoCdStateBootstrapHook.BuildApplyPlan`](ArgoCd.Aspire.AppHost/ArgoCdStateBootstrapHook.cs)):
   the `argocd` namespace, then CRDs (`manifests/crds`), then RBAC/ConfigMaps/Secrets from
   `manifests/base`.

No Argo CD Deployment, StatefulSet, Service, or Pod is ever created for `api-server`,
`repo-server`, the controllers, `commit-server`, `redis`, or the UI — those all run as Aspire
resources on the host. The cluster's dashboard properties (`argocd.repoRoot`, `argocd.namespace`,
`argocd.clusterName`, `argocd.note`) make this explicit in the Aspire dashboard so it's obvious at
a glance that the cluster is state-only, and `argocd.clusterName` tells you the real `kind` cluster
name to use with `kind get clusters` / `kind delete cluster --name <that value>` if you ever need
to bypass the dashboard command.

### The Kind cluster name is derived per-checkout, not a shared literal

Earlier revisions of this dev loop used the literal Kind cluster name `argocd-dev` (and a
kubeconfig at a fixed path under `%TEMP%`) for every checkout. That collides the moment you have
more than one clone or worktree of this repo on the same machine — a second checkout would attach
to (and potentially bootstrap-race or delete) the first checkout's cluster. To fix this,
[`ArgoCdClusterName.Resolve`](ArgoCd.Aspire.AppHost/ArgoCdClusterName.cs) derives the real `kind`
cluster name from the resolved repo root:

- **Default (no override):** `argocd-dev-<hash>`, where `<hash>` is the first 12 hex characters
  (48 bits) of the SHA-256 digest of the repo root's normalized absolute path (case-insensitive on
  Windows). This is **stable** for repeated runs from the same checkout (same path → same name
  every time) and **distinct** across different checkouts/worktrees (different path → different
  name), all without any state file to keep in sync. The kubeconfig Aspire generates for the
  cluster is keyed off this same resolved name, so it no longer collides in `%TEMP%` either.
- **Explicit override:** set `ARGOCD_ASPIRE_CLUSTER_NAME` to reuse one cluster across multiple
  checkouts/worktrees on purpose (for example, sharing a single Kind cluster between two worktrees
  you know won't run concurrently). The override is validated with the same RFC 1123 DNS label
  rules Kubernetes/Kind enforce on cluster names — lowercase alphanumerics and hyphens only,
  starting with an alphanumeric (`^[a-z0-9][a-z0-9-]*$`), 63 characters max — and `Resolve` throws
  an `ArgumentException` naming the exact rule violated (empty/whitespace, too long, or invalid
  characters) rather than silently passing a bad name through to `kind create cluster`.
- **Discovering the resolved name:** the running AppHost always publishes the resolved name as the
  `argocd.clusterName` dashboard property on the `argocd-dev` resource, so you never have to
  recompute the hash by hand — check the dashboard, or run `kind get clusters` and look for the
  `argocd-dev-`-prefixed (or your override) entry.

## Editing code triggers a selective restart

[`ArgoCdSelectiveRestartHook`](ArgoCd.Aspire.AppHost/ArgoCdSelectiveRestartHook.cs) attaches a
`FileSystemWatcher` (filtering on `*.go`) to each component's source directory and, after a 500 ms
debounce (to absorb editor/IDE multi-write bursts and `go fmt`/save storms), issues Aspire's
built-in per-resource **restart** command (`KnownResourceCommands.RestartCommand`) — the same
command available from the dashboard's resource context menu — for **only that resource**. No
image is rebuilt, no other component restarts, and the Kind cluster is untouched.

| Source directory watched         | Resource restarted            |
|-----------------------------------|--------------------------------|
| `controller/`                     | `application-controller`      |
| `server/`                          | `api-server`                  |
| `reposerver/`                      | `repo-server`                 |
| `applicationset/`                  | `applicationset-controller`   |
| `notification_controller/`         | `notifications-controller`    |
| `commitserver/`                    | `commit-server`               |
| `cmpserver/` *(only if CMP opt-in)*| `cmp-server`                  |

**Known limitation:** `cmd/` (the shared `main` entrypoints) is not watched, since a change there
could plausibly affect any/all binaries — restart the AppHost itself after editing files directly
under `cmd/`.

## UI: HMR is preserved

The UI resource runs `pnpm start` (webpack dev server, unchanged from
[`ui/src/app/webpack.config.js`](../../ui/src/app/webpack.config.js)) as a host process, with
`ARGOCD_API_URL` wired directly to the running `api-server` resource's endpoint. Webpack's normal
Hot Module Replacement continues to work exactly as it does when you run `pnpm start` by hand —
editing a `.tsx`/`.scss` file under `ui/src` hot-reloads in the browser without any Aspire
involvement or process restart.

Before first start, [`ArgoCdUiDependencies.EnsureInstalledOrThrow`](ArgoCd.Aspire.AppHost/ArgoCdUiDependencies.cs)
runs a pinned `corepack pnpm install` if `ui/node_modules` is missing or older than
`ui/pnpm-lock.yaml`, so you don't need to remember to install UI dependencies by hand. Set
`ARGOCD_ASPIRE_SKIP_UI_INSTALL=true` to skip this check (e.g. if you're managing `node_modules`
yourself or working offline with dependencies already installed).

## Debugging in Visual Studio

Press **F5** on the AppHost project. Because every Argo CD component runs as a real `go run`
host process (not inside a container), you can additionally attach Delve or VS Code's Go debugger
to any component's process the moment it starts — no `kubectl exec`, no remote debug proxy,
no container. The Aspire dashboard's **Console logs** view streams stdout/stderr per resource; use
the **Traces**/**Structured logs** views for OpenTelemetry data on components that emit OTLP (the
API server, controllers, and repo-server export standard Argo CD metrics/traces when OTLP
environment variables are set, same as running them by hand).

## Admin credentials

The dev loop runs `api-server` with `--disable-auth true --insecure` (matching the Procfile), so
there is normally **no password required** to sign in locally. If you need to check anyway (for
example, after manually enabling auth), use the **`Show admin credentials`** dashboard command on
the `argocd-dev` cluster resource — it reads the `argocd-initial-admin-secret` Secret via
`kubectl` and decodes the password in managed C# (no `base64 -d` shell-out, so it works
identically on Windows/macOS/Linux). If the Secret doesn't exist (the normal case for this dev
loop), the command reports "no admin password required" instead of an error.

## Cleanup

The Kind cluster is deliberately **persistent** across `dotnet run` invocations (see
[Kind holds state only](#kind-holds-state-only)) so you don't pay cluster-creation cost every
time you iterate. To delete it, use the **`Delete Kind cluster (clean shutdown)`** dashboard
command on the `argocd-dev` resource (or `aspire resource argocd-dev delete-cluster` from the CLI)
**before** stopping the AppHost.

> **Why not just stop the AppHost?** The Kind hosting integration's own shutdown hook does delete
> the cluster on `BeforeStopAsync`, but on the Aspire CLI/host combination used here, `aspire stop`
> has been observed to hard-kill the AppHost process a few milliseconds after requesting shutdown
> — before that asynchronous `kind delete cluster` (which takes several seconds) can finish. Aspire
> resource **commands**, unlike process shutdown, are awaited to completion by the CLI/dashboard,
> so running `delete-cluster` explicitly avoids that race and reliably deletes the cluster and its
> kubeconfig file. If you forget and just stop the AppHost, first find the real `kind` cluster name
> — it is **not** the literal `argocd-dev` (see
> [The Kind cluster name is derived per-checkout](#the-kind-cluster-name-is-derived-per-checkout-not-a-shared-literal)) —
> via the `argocd.clusterName` dashboard property, `kind get clusters` (look for the
> `argocd-dev-`-prefixed entry, or your `ARGOCD_ASPIRE_CLUSTER_NAME` override value), and then run
> `kind delete cluster --name <that value>` by hand.

Host processes (all Argo CD components, Redis, dev-mounter, UI) are regular child processes of the
AppHost and terminate deterministically when the AppHost exits — there is no async teardown race
for those, only for the Kind cluster itself.

## Cross-platform paths

Every path that upstream scripts (`hack/goreman-start.sh`, `hack/dev-mounter/main.go`) default to
under Linux-only `/tmp/argocd-local` is replaced with a temp-directory-relative path computed via
`Path.GetTempPath()`, and each remains independently overridable:

| Purpose                          | Environment variable         | Default (all platforms)                          |
|-----------------------------------|-------------------------------|----------------------------------------------------|
| Root of all local dev data        | *(no direct override)*        | `%TEMP%/argocd-local` (or platform temp dir)       |
| TLS certs synced by dev-mounter   | `ARGOCD_TLS_DATA_PATH`        | `<local-data-root>/tls`                             |
| SSH known_hosts synced by dev-mounter | `ARGOCD_SSH_DATA_PATH`     | `<local-data-root>/ssh`                             |
| GPG public keyring (repo-server reads) | `ARGOCD_GNUPGHOME`       | `<local-data-root>/gpg/keys`                        |
| GPG keys synced by dev-mounter    | `ARGOCD_GPG_DATA_PATH`         | `<local-data-root>/gpg/source`                      |
| CMP plugin Unix socket dir *(opt-in, non-Windows only)* | `ARGOCD_PLUGINSOCKFILEPATH` | `<repo-root>/test/cmp` |
| Coverage output root              | `ARGOCD_COVERAGE_DIR` (per-component) | `%TEMP%/argocd-coverage/<component>`         |

The AppHost pre-creates the TLS/SSH/GPG-keys/GPG-source directories on startup so `dev-mounter` and
`repo-server` never fail on a missing directory on first run.

## `hack/dev-mounter`

[`hack/dev-mounter/main.go`](../../hack/dev-mounter/main.go) is launched as a host-process Aspire
resource (`go run hack/dev-mounter/main.go`) once the Kind cluster's kubeconfig is available. It
watches the `argocd-ssh-known-hosts-cm`, `argocd-tls-certs-cm`, and `argocd-gpg-keys-cm`
ConfigMaps in the cluster and mirrors their contents to the cross-platform host paths above
(`ARGOCD_SSH_DATA_PATH`, `ARGOCD_TLS_DATA_PATH`, `ARGOCD_GPG_DATA_PATH`), exactly like it does in
the Tilt/Procfile loop — just reading from the state-only Kind cluster instead of a fully deployed
one.

## Optional: repo-server image override for in-cluster validation

If you need to validate a `repo-server` change **as it would run inside a real cluster** (image
build, RBAC, resource limits, sidecar interactions, etc.) rather than as a host process, the
**`Rebuild repo-server (source override)`** dashboard command on the `argocd-dev` resource
(see [`RepoServerOverride.cs`](ArgoCd.Aspire.AppHost/RepoServerOverride.cs)) will:

1. Cross-compile `repo-server` for Linux and `docker build` a throwaway image tagged from your
   current git state.
2. `kind load docker-image` that image into the real `kind` cluster (looked up via
   `cluster.ClusterName` — see [The Kind cluster name is derived
   per-checkout](#the-kind-cluster-name-is-derived-per-checkout-not-a-shared-literal) — not the
   literal `argocd-dev`).
3. `kubectl patch`/roll out an in-cluster `repo-server` Deployment using that image, then tail its
   logs.

This is the **only** place in the AppHost that performs a Docker image build, `kind load`, or a
Kubernetes Deployment patch — it is a manual, opt-in dashboard command, never invoked automatically
by `dotnet run`, and it's independent of (and doesn't interfere with) the host-process
`repo-server` resource that runs by default.

## Prerequisites

[`ArgoCdPrerequisites.ValidateOrThrow()`](ArgoCd.Aspire.AppHost/ArgoCdPrerequisites.cs) runs
before any resource is defined and fails fast with a single combined, actionable error message
(including install links) if any of the following are missing — no half-started resource graph:

| Tool     | Check              | Required for                                   |
|----------|---------------------|-------------------------------------------------|
| Docker   | `docker info`       | Running the Kind cluster and Redis container    |
| kind     | `kind version`      | Creating/managing the state-only cluster        |
| kubectl  | `kubectl version --client` | State bootstrap, dev-mounter, dashboard commands |
| Go       | `go version`        | `go run`-ing every Argo CD component             |
| Node.js  | `node --version`    | Running the UI                                   |
| corepack | `corepack --version`| Pinned pnpm install/execution                   |

`pnpm --version` is checked on a best-effort basis only (a missing pnpm shim is expected until
`corepack` materializes it on first use with the version pinned in `package.json`, so this check
never blocks startup).

## Layout

```
contrib/aspire-dev/
├── README.md                              — this file
├── ArgoCd.Aspire.AppHost/
│   ├── AppHost.cs                         — resource graph entry point
│   ├── ArgoCdComponents.cs                — per-component host-process definitions (Procfile parity)
│   ├── ArgoCdPaths.cs                     — cross-platform path resolution
│   ├── ArgoCdPrerequisites.cs             — toolchain validation (fail-fast)
│   ├── ArgoCdRepoRoot.cs                  — repo-root discovery (go.mod + manifests/install.yaml)
│   ├── ArgoCdManifestSet.cs               — manifest file-set discovery for state bootstrap
│   ├── ArgoCdManifestRenderer.cs          — shared process-execution helper
│   ├── ArgoCdStateBootstrapHook.cs        — server-side-apply plan for CRDs/RBAC/ConfigMaps/Secrets
│   ├── ArgoCdSelectiveRestartHook.cs      — FileSystemWatcher-based per-component restart
│   ├── ArgoCdUiDependencies.cs            — pinned pnpm install automation for the UI
│   ├── AdminCredentialCommand.cs          — "Show admin credentials" dashboard command
│   ├── DeleteClusterCommand.cs            — "Delete Kind cluster (clean shutdown)" dashboard command
│   └── RepoServerOverride.cs              — optional in-cluster repo-server image override command
├── ArgoCd.Aspire.Hosting.Kind/            — vendored/local Kind hosting integration
│   ├── KindClusterBuilderExtensions.cs
│   ├── KindClusterLifecycleHook.cs
│   └── KindClusterResource.cs
└── ArgoCd.Aspire.AppHost.Tests/           — unit tests (see below)
```

## Tests

```powershell
dotnet test contrib/aspire-dev/ArgoCd.Aspire.AppHost.Tests
```

Covers: exact Procfile command/flag/env/port mapping per component
(`ArgoCdComponentsTests`), cross-platform path resolution and env-var overrides
(`ArgoCdPathsTests`), prerequisite tool-check behavior (`ArgoCdPrerequisitesTests`), the
state-bootstrap apply-order plan (`ArgoCdStateBootstrapHookTests`), selective-restart
directory-to-resource mapping and debounce logic (`ArgoCdSelectiveRestartHookTests`), UI
dependency install-needed detection (`ArgoCdUiDependenciesTests`), repo-root resolution
(`ArgoCdRepoRootTests`), the repo-server override command's tag computation
(`RepoServerOverrideTests` — where present), the delete-cluster command's kubeconfig cleanup
logic (`DeleteClusterCommandTests`), and the vendored Kind hosting integration's process-runner
behavior (`KindClusterIntegrationTests`). All tests run entirely in-process (no Docker/Kind/Go
required) by linking the AppHost's `.cs` files directly into the test assembly and exercising
their `internal` static/pure-logic entry points.

## Known limitations

- CMP is unsupported on Windows (see [Dex and CMP are opt-in](#dex-and-cmp-are-opt-in)) — this is
  an accurate reflection of a real Unix-domain-socket dependency in Argo CD's CMP protocol, not an
  Aspire limitation.
- The `cmd/` shared entrypoints are not watched for selective restart (see
  [Editing code triggers a selective restart](#editing-code-triggers-a-selective-restart)).
- `aspire stop` alone does not reliably delete the Kind cluster before the process exits; use the
  `delete-cluster` dashboard command first (see [Cleanup](#cleanup)).
- This loop assumes a local Docker daemon capable of running Kind's `kindest/node` container; it
  does not manage remote/cloud Docker contexts.
