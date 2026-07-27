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
