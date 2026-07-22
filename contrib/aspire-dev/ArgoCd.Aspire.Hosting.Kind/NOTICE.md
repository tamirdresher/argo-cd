# NOTICE — Provenance of vendored Kind hosting integration

The C# source files in this directory (`KindClusterResource.cs`,
`KindClusterBuilderExtensions.cs`, `KindClusterLifecycleHook.cs`) are **vendored,
unpublished** source, copied verbatim (no modifications) from a private,
unpublished Aspire hosting integration for [Kind](https://kind.sigs.k8s.io/)
authored by the same contributor who added this `contrib/aspire-dev`
environment to the Argo CD repository.

## Why vendored instead of a NuGet package reference

This integration has **not** been published to nuget.org or to the
[CommunityToolkit/Aspire](https://github.com/CommunityToolkit/Aspire) project.
It originated as a private sample used to validate a different, unrelated
internal project, and no public package exists to reference. To keep this
Argo CD contribution self-contained and reproducible without relying on an
unpublished/private feed, the minimum necessary source files were copied
directly into this repository.

## Exact source

| Field | Value |
|---|---|
| Original namespace/package name | `CommunityToolkit.Aspire.Hosting.Kind` (aspirational name chosen by the original author; **this package has not been submitted to or accepted by the real CommunityToolkit/Aspire project** — the name is retained here only inside doc comments copied verbatim from the source) |
| Author | Tamir Dresher (same person who authored this `contrib/aspire-dev` contribution) |
| Origin | Private, internal-only sample repository (not publicly hosted; not affiliated with or reviewed by CommunityToolkit/Aspire) |
| Commit copied from | `f066472f288989f06b226bd61fc492be8a7229ad` |
| Files copied | `src/CommunityToolkit.Aspire.Hosting.Kind/KindClusterResource.cs`, `KindClusterBuilderExtensions.cs`, `KindClusterLifecycleHook.cs` |
| Modifications made when vendoring | None — the three `.cs` files are byte-for-byte copies. Only the `.csproj` (this project's own build file, not part of the vendored source) and this NOTICE were authored fresh for this repository. |

## License

The original source carries an aspirational `[MIT](...)` license header pointing at
`CommunityToolkit/Aspire`'s LICENSE, written in anticipation of eventual upstream
contribution. Since the code has **not actually been published or accepted upstream**,
that pointer is not a valid license grant from a third party. The vendored copy here
is contributed under the same license as the rest of this Argo CD repository (Apache
License 2.0) by its original author, who is the sole copyright holder and is
contributing it to this repository under that license.

## API surface actually used

Only the following public API, exactly as present in the copied commit, is used by
`ArgoCd.Aspire.AppHost`:

- `AddKindCluster(name)`
- `WithConfig(path)`
- `WithDockerImage(image, contextPath, dockerfilePath?)`
- `WithManifest(path)`
- `WithDashboardProperty(name, value)`
- `WithWaitForReady(timeout)`

APIs that do **not** exist in the copied commit (e.g. `WithWorkerNodes`,
`WithClusterLifetime`) are intentionally **not** used anywhere in this contribution.
`WithNodeCount`, `WithKubernetesVersion`, `WithHelmChart`, and `WithPortMapping` exist
in the copied source but are not currently used by the Argo CD AppHost; they remain
available for future use (e.g. Helm-based scenarios) without further vendoring work.

## Maintenance

If this integration is ever published as a real NuGet package (by CommunityToolkit/Aspire
or otherwise), this vendored copy should be replaced with a normal `PackageReference` and
this directory removed.
