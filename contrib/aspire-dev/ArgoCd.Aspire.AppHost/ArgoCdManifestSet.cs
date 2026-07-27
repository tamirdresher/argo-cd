namespace ArgoCd.Aspire.AppHost;

/// <summary>
/// The explicit, hand-curated list of upstream <c>manifests/</c> files that make up Argo CD's
/// *state* (CustomResourceDefinitions, Namespace, ServiceAccounts, Roles/RoleBindings,
/// ClusterRoles/ClusterRoleBindings, ConfigMaps and Secrets) as opposed to its *workloads*
/// (Deployments, StatefulSets, Services, NetworkPolicies).
///
/// Under the Aspire dev loop every workload runs as a native host process (see
/// <see cref="ArgoCdComponents"/>), so Kind never receives a Deployment/StatefulSet/Service for
/// any Argo CD component — it is used purely to host the Kubernetes control plane and the state
/// listed here. This file intentionally never references a <c>kustomization.yaml</c> (those
/// documents are not valid input for a single <c>kubectl apply -f</c> invocation and, more
/// importantly, several of them also pull in the very Deployment/Service/NetworkPolicy files
/// this dev loop must not create) — every path below is a single concrete manifest file, applied
/// with <c>kubectl apply --server-side --force-conflicts</c> so the CRDs' large embedded OpenAPI
/// schemas don't overflow the client-side last-applied-configuration annotation limit.
/// </summary>
internal static class ArgoCdManifestSet
{
    /// <summary>
    /// CustomResourceDefinitions: Application, ApplicationSet, AppProject. Required before any
    /// component can start (the application controller/API server watch these types).
    /// </summary>
    public static readonly IReadOnlyList<string> Crds =
    [
        Path.Combine("manifests", "crds", "application-crd.yaml"),
        Path.Combine("manifests", "crds", "applicationset-crd.yaml"),
        Path.Combine("manifests", "crds", "appproject-crd.yaml"),
    ];

    /// <summary>
    /// Core ConfigMaps/Secrets (argocd-cm, argocd-cmd-params-cm, argocd-secret, RBAC config,
    /// GPG/SSH/TLS ConfigMaps) plus the notifications ConfigMap/Secret, plus every
    /// ServiceAccount/Role/RoleBinding the host-process components' service-account identity and
    /// namespaced RBAC rely on. Never includes a Deployment, StatefulSet, Service, or
    /// NetworkPolicy file.
    /// </summary>
    public static readonly IReadOnlyList<string> ConfigAndRbac =
    [
        // Core ConfigMaps/Secrets.
        Path.Combine("manifests", "base", "config", "argocd-cm.yaml"),
        Path.Combine("manifests", "base", "config", "argocd-cmd-params-cm.yaml"),
        Path.Combine("manifests", "base", "config", "argocd-gpg-keys-cm.yaml"),
        Path.Combine("manifests", "base", "config", "argocd-rbac-cm.yaml"),
        Path.Combine("manifests", "base", "config", "argocd-secret.yaml"),
        Path.Combine("manifests", "base", "config", "argocd-ssh-known-hosts-cm.yaml"),
        Path.Combine("manifests", "base", "config", "argocd-tls-certs-cm.yaml"),

        // Notifications ConfigMap/Secret.
        Path.Combine("manifests", "base", "notification", "argocd-notifications-cm.yaml"),
        Path.Combine("manifests", "base", "notification", "argocd-notifications-secret.yaml"),

        // Namespaced RBAC (ServiceAccount + Role + RoleBinding triples).
        Path.Combine("manifests", "base", "application-controller-roles", "argocd-application-controller-sa.yaml"),
        Path.Combine("manifests", "base", "application-controller-roles", "argocd-application-controller-role.yaml"),
        Path.Combine("manifests", "base", "application-controller-roles", "argocd-application-controller-rolebinding.yaml"),

        Path.Combine("manifests", "base", "applicationset-controller", "argocd-applicationset-controller-sa.yaml"),
        Path.Combine("manifests", "base", "applicationset-controller", "argocd-applicationset-controller-role.yaml"),
        Path.Combine("manifests", "base", "applicationset-controller", "argocd-applicationset-controller-rolebinding.yaml"),

        Path.Combine("manifests", "base", "notification", "argocd-notifications-controller-sa.yaml"),
        Path.Combine("manifests", "base", "notification", "argocd-notifications-controller-role.yaml"),
        Path.Combine("manifests", "base", "notification", "argocd-notifications-controller-rolebinding.yaml"),

        Path.Combine("manifests", "base", "server", "argocd-server-sa.yaml"),
        Path.Combine("manifests", "base", "server", "argocd-server-role.yaml"),
        Path.Combine("manifests", "base", "server", "argocd-server-rolebinding.yaml"),

        // ServiceAccount-only (no namespaced Role in base for these components).
        Path.Combine("manifests", "base", "commit-server", "argocd-commit-server-sa.yaml"),
        Path.Combine("manifests", "base", "repo-server", "argocd-repo-server-sa.yaml"),

        // Cluster-scoped RBAC (ClusterRole + ClusterRoleBinding only).
        Path.Combine("manifests", "cluster-rbac", "application-controller", "argocd-application-controller-clusterrole.yaml"),
        Path.Combine("manifests", "cluster-rbac", "application-controller", "argocd-application-controller-clusterrolebinding.yaml"),
        Path.Combine("manifests", "cluster-rbac", "applicationset-controller", "argocd-applicationset-controller-clusterrole.yaml"),
        Path.Combine("manifests", "cluster-rbac", "applicationset-controller", "argocd-applicationset-controller-clusterrolebinding.yaml"),
        Path.Combine("manifests", "cluster-rbac", "server", "argocd-server-clusterrole.yaml"),
        Path.Combine("manifests", "cluster-rbac", "server", "argocd-server-clusterrolebinding.yaml"),
    ];

    /// <summary>
    /// Dex's ServiceAccount/Role/RoleBinding. Only applied when Dex is enabled (see
    /// <c>ArgoCdComponents.WithDex</c>); the dev loop defaults to <c>--disable-auth=true</c> and
    /// never starts Dex, so this state is skipped unless explicitly opted in.
    /// </summary>
    public static readonly IReadOnlyList<string> DexRbac =
    [
        Path.Combine("manifests", "base", "dex", "argocd-dex-server-sa.yaml"),
        Path.Combine("manifests", "base", "dex", "argocd-dex-server-role.yaml"),
        Path.Combine("manifests", "base", "dex", "argocd-dex-server-rolebinding.yaml"),
    ];

    private const string ArgoCdNamespace = "argocd";

    private static readonly HashSet<string> NamespacedKinds = new(StringComparer.Ordinal)
    {
        "ConfigMap",
        "Secret",
        "ServiceAccount",
        "Role",
        "RoleBinding",
    };

    public static string RenderStateOnlyManifest(string outputPath, bool enableDex)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var repoRoot = ArgoCdRepository.Root;

        var documents = new List<string>
        {
            $"""
            apiVersion: v1
            kind: Namespace
            metadata:
              name: {ArgoCdNamespace}
            """,
        };

        foreach (var relativePath in EnumerateStateOnlyManifests(enableDex))
        {
            var fullPath = Path.Combine(repoRoot, relativePath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Required Argo CD manifest '{relativePath}' was not found under '{repoRoot}'.", fullPath);
            }

            documents.Add(EnsureNamespace(File.ReadAllText(fullPath), ArgoCdNamespace));
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(outputPath, string.Join($"{Environment.NewLine}---{Environment.NewLine}", documents));
        return outputPath;
    }

    internal static IReadOnlyList<string> EnumerateStateOnlyManifests(bool enableDex)
    {
        var manifests = new List<string>();
        manifests.AddRange(Crds);
        manifests.AddRange(ConfigAndRbac);
        if (enableDex)
        {
            manifests.AddRange(DexRbac);
        }

        return manifests;
    }

    internal static string EnsureNamespace(string yaml, string @namespace)
    {
        var kind = ReadTopLevelScalar(yaml, "kind");
        if (kind is null || !NamespacedKinds.Contains(kind) || yaml.Contains($"\n  namespace: {@namespace}", StringComparison.Ordinal))
        {
            return yaml;
        }

        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var metadataIndex = lines.FindIndex(line => line == "metadata:");
        if (metadataIndex < 0)
        {
            return yaml;
        }

        var insertIndex = metadataIndex + 1;
        while (insertIndex < lines.Count && lines[insertIndex].StartsWith("  #", StringComparison.Ordinal))
        {
            insertIndex++;
        }

        lines.Insert(insertIndex, $"  namespace: {@namespace}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string? ReadTopLevelScalar(string yaml, string key)
    {
        using var reader = new StringReader(yaml);
        string? line;
        var prefix = key + ":";
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
    }

}
