# Argo CD Aspire dev loop

This directory contains an Aspire-based inner loop for developing Argo CD itself from this checkout. It stands up the local state Argo CD components need, then runs each component as a native host process so source edits restart only the affected process.

## Prerequisites

- .NET 10 SDK
- Docker or Podman
- kind
- kubectl
- Go
- Node.js with pnpm (Corepack is fine)

## Quick start

```powershell
cd contrib\aspire-dev
aspire run
```

Or run the AppHost project directly:

```powershell
dotnet run --project ArgoCd.Aspire.AppHost
```

No second repository clone or path wiring is required; the AppHost resolves the repository root from its in-tree location.

## Debugging Go components

Prerequisites:

- .NET 10 SDK.
- Go 1.26+ on `PATH`.
- Docker Desktop running.
- `kind` and `kubectl` on `PATH`.
- Delve (`dlv`) on `PATH`. Install it with `go install github.com/go-delve/delve/cmd/dlv@latest`; on Windows it is normally written to `$(go env GOPATH)\bin`, so make sure that directory is on `PATH`.
- VS Code with the recommended `microsoft-aspire.aspire-vscode` and `golang.go` extensions.

Debug flow:

1. Open the repository root in VS Code.
2. Install the recommended extensions when prompted.
3. Set a breakpoint in the Argo CD Go component you want to inspect.
4. Press F5 and choose `Debug Aspire AppHost`.
5. Wait for the Aspire dashboard to show the relevant component resource as running.
6. Exercise that component through the UI, API, or dashboard commands.
7. VS Code should pause the nested Go debug session on the breakpoint with locals populated.

### Gotchas

`AddGoApp`'s third parameter is `packagePath`: a Go package directory relative to `appDirectory`, not an entry file. In this checkout the root `go.mod` owns the Argo CD component package under `cmd/`, so the package path is `"./cmd"`, not `"./cmd/main.go"`. The `hack/dev-mounter` helper is its own package directory, so its package path is `"hack/dev-mounter"`, not `"hack/dev-mounter/main.go"`.

`go run` forgives a filename, so the process may start normally, but the same value is passed to `dlv-dap`, where a file path is invalid. The debugger symptom is `Invalid debug adapter` / HTTP 500 from `/run_session`; DCP can then fall back to launching `go` with no args, which exits 2 after printing the Go usage banner.

## What it does

- Creates a local Kind cluster with `CommunityToolkit.Aspire.Hosting.Kind`.
- Applies Argo CD's own state manifests: CRDs, namespace, RBAC, ConfigMaps and Secrets. It does not apply Argo CD workload Deployments, StatefulSets, Services, or NetworkPolicies.
- Runs every Argo CD component as a native host process matching the repository Procfile's command, environment, and ports.
- Runs the UI with `pnpm start` so webpack HMR remains intact.
- Watches component Go source directories and restarts only the matching Aspire resource after edits.

## Relationship to Tilt

This complements, not replaces, Argo CD's existing Tilt workflow. Tilt remains the recommended workflow when iterating simultaneously on Argo CD and the manifests or CRDs it manages, or when in-cluster component images are required. This Aspire loop targets host-process-first, selective-restart iteration on Argo CD's Go and TypeScript source.

## Kind extensions project

`ArgoCd.Aspire.Hosting.Kind.Extensions` contains two small APIs not yet shipped in `CommunityToolkit.Aspire.Hosting.Kind`: `WithManifest(path)` and `WithPortMapping(hostPort, containerPort)`. `WithManifest` tracks upstream PR [CommunityToolkit/Aspire#1481](https://github.com/CommunityToolkit/Aspire/pull/1481) as `AddManifest` / `AddManifestFromContent`. Delete these extensions once the package includes equivalent APIs.
