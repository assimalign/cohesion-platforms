using System;
using System.Collections.Generic;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Options controlling how the <see cref="KubernetesGateway"/> connects to a cluster,
/// registers controller overrides, applies objects, and bounds readiness and teardown.
/// </summary>
public sealed class KubernetesGatewayOptions : ApplicationGatewayOptions
{
    private readonly Dictionary<ResourceName, List<Action<IKubernetesObject<V1ObjectMeta>>>> _patches = new();

    /// <summary>
    /// An explicit kubeconfig file path. When <see langword="null"/>, the gateway resolves
    /// a configuration from the <c>KUBECONFIG</c> environment variable, then the default
    /// kubeconfig file (<c>~/.kube/config</c>), then the in-cluster service account.
    /// </summary>
    public string? KubeConfigPath { get; set; }

    /// <summary>
    /// The kubeconfig context to use, overriding the file's <c>current-context</c>.
    /// Ignored when the configuration is resolved in-cluster. When <see langword="null"/>,
    /// the kubeconfig's own current context is used.
    /// </summary>
    public string? ContextName { get; set; }

    /// <summary>
    /// Gets or sets the optional <c>application.images.json</c> path used to gather resource
    /// images. When specified, the index entry for the resource must match the digest-pinned
    /// image declared by its manifest.
    /// </summary>
    public string? ImageIndexPath { get; set; }

    /// <summary>
    /// Gets or sets the optional registry authority applied to image-index entries that omit or
    /// null their registry. A pinned entry registry takes precedence. Specify an authority such
    /// as <c>registry.example.test:5000</c>, without a URI scheme or repository path.
    /// </summary>
    public string? ContainerRegistry { get; set; }

    /// <summary>
    /// The fallback server-side-apply field manager for gateway-scoped bootstrap objects.
    /// Application namespaces and resource objects always use
    /// <c>&lt;application&gt;@&lt;gateway-identity&gt;</c>. Defaults to <c>cohesion-gateway</c>.
    /// </summary>
    public string FieldManager { get; set; } = "cohesion-gateway";

    /// <summary>
    /// Gets or sets the optional image realizer used to acquire a digest-pinned manifest image
    /// when <see cref="ImageIndexPath"/> is not specified. Its result must preserve the manifest
    /// repository and digest. When omitted, the validated manifest image is used directly.
    /// </summary>
    public IImageRealizer? ImageRealizer { get; set; }

    /// <summary>
    /// Gets or sets the sink for compiler warnings. Each compilation reports every distinct
    /// unknown plan-hint key; reconciliation suppresses repeated keys within one gateway session.
    /// Defaults to standard error.
    /// </summary>
    public Action<string> WarningHandler { get; set; } = Console.Error.WriteLine;

    /// <summary>
    /// The maximum time allowed for owned namespace deletion after successful teardown.
    /// Normal observer shutdown never deletes a namespace. Defaults to 30&#160;seconds.
    /// </summary>
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Registers a Kubernetes-native patch for objects of type <typeparamref name="TResource"/>
    /// compiled for one resource. Patches run in registration order before mandatory Cohesion
    /// ownership, resource-label, and plan-hash metadata is restored. Controller-managed rollout
    /// revisions are assigned after patches during reconciliation.
    /// </summary>
    /// <typeparam name="TResource">The Kubernetes object type to patch.</typeparam>
    /// <param name="resource">The Cohesion resource whose compiled objects may be patched.</param>
    /// <param name="patch">The platform-specific mutation to apply.</param>
    /// <returns>These options, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="patch"/> is <see langword="null"/>.</exception>
    public KubernetesGatewayOptions Patch<TResource>(
        ResourceName resource,
        Action<TResource> patch)
        where TResource : class, IKubernetesObject<V1ObjectMeta>
    {
        ArgumentNullException.ThrowIfNull(patch);

        if (!_patches.TryGetValue(resource, out List<Action<IKubernetesObject<V1ObjectMeta>>>? registrations))
        {
            registrations = new List<Action<IKubernetesObject<V1ObjectMeta>>>();
            _patches.Add(resource, registrations);
        }

        registrations.Add(candidate =>
        {
            if (candidate is TResource typed)
            {
                patch(typed);
            }
        });
        return this;
    }

    internal void ApplyPatches(
        ResourceName resource,
        IKubernetesObject<V1ObjectMeta> candidate)
    {
        if (!_patches.TryGetValue(resource, out List<Action<IKubernetesObject<V1ObjectMeta>>>? registrations))
        {
            return;
        }

        for (int index = 0; index < registrations.Count; index++)
        {
            registrations[index](candidate);
        }
    }

    internal void ValidateKubernetes()
    {
        if (KubeConfigPath is not null && string.IsNullOrWhiteSpace(KubeConfigPath))
        {
            throw new ArgumentException("KubeConfigPath must not be empty when specified.", nameof(KubeConfigPath));
        }

        if (ContextName is not null && string.IsNullOrWhiteSpace(ContextName))
        {
            throw new ArgumentException("ContextName must not be empty when specified.", nameof(ContextName));
        }

        if (ImageIndexPath is not null && string.IsNullOrWhiteSpace(ImageIndexPath))
        {
            throw new ArgumentException(
                "ImageIndexPath must not be empty when specified.",
                nameof(ImageIndexPath));
        }

        if (ContainerRegistry is not null && !IsRegistryAuthority(ContainerRegistry))
        {
            throw new ArgumentException(
                "ContainerRegistry must be a registry authority without a URI scheme or repository path.",
                nameof(ContainerRegistry));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(FieldManager);
        ArgumentNullException.ThrowIfNull(WarningHandler);
        if (StopGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StopGrace), "StopGrace must be greater than zero.");
        }
    }

    private static bool IsRegistryAuthority(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains("://", StringComparison.Ordinal)
            || value.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0)
        {
            return false;
        }

        return Uri.TryCreate($"http://{value}", UriKind.Absolute, out Uri? uri)
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal);
    }
}
