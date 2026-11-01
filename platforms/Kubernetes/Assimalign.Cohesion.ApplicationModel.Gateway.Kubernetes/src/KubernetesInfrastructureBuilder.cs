using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

using Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Composes a <see cref="KubernetesInfrastructure"/>: the cluster and the shape of what runs there,
/// and the image bound to each resource.
/// </summary>
/// <remarks>
/// The gateway builder's <c>UseKubernetes(configure)</c> hands one to its callback when the gateway is
/// built, and the gateway owns the infrastructure it builds. <see cref="Build"/> creates an infrastructure of its own, which its caller disposes.
/// </remarks>
[DebuggerDisplay("{Options.Namespace}")]
public sealed class KubernetesInfrastructureBuilder
{
    private readonly Dictionary<(ProviderName Provider, ResourceName Name), string> _images = [];

    /// <summary>
    /// Gets the cluster and the shape of what runs there. <see cref="Build"/> copies them.
    /// </summary>
    public KubernetesInfrastructureOptions Options { get; } = new();

    // The last infrastructure Build created, so a test reaches the platform a gateway owns.
    internal KubernetesInfrastructure? Built { get; private set; }

    /// <summary>
    /// Binds a resource to the container image that runs it, such as
    /// <c>localhost:5001/orders-api:1.0</c>.
    /// </summary>
    /// <param name="provider">The resource's provider, such as <c>Web</c>.</param>
    /// <param name="name">The resource's name.</param>
    /// <param name="image">The image reference the cluster pulls.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="provider"/> or <paramref name="name"/> is <see langword="default"/>, the image is
    /// empty, or the resource already has an image.
    /// </exception>
    public KubernetesInfrastructureBuilder AddImage(ProviderName provider, ResourceName name, string image)
    {
        KubernetesInfrastructure.CheckImage(provider, name, image);
        if (!_images.TryAdd((provider, name), image))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ImageDuplicate, provider, name), nameof(name));
        }

        return this;
    }

    /// <summary>
    /// Creates the infrastructure for the cluster <see cref="Options"/> names, with every image bound.
    /// </summary>
    /// <returns>A new infrastructure, which the caller disposes.</returns>
    /// <exception cref="ArgumentException">The namespace is not a DNS label, or a port is out of range.</exception>
    /// <exception cref="System.IO.FileNotFoundException">The kubeconfig file the options name does not exist.</exception>
    /// <exception cref="InvalidOperationException">No cluster configuration could be resolved.</exception>
    public KubernetesInfrastructure Build()
    {
        KubernetesInfrastructure infrastructure = KubernetesInfrastructure.Create(Options);

        // Every image was checked as it was added, so binding them to a new infrastructure cannot fail.
        foreach (KeyValuePair<(ProviderName Provider, ResourceName Name), string> binding in _images)
        {
            infrastructure.AddImage(binding.Key.Provider, binding.Key.Name, binding.Value);
        }

        Built = infrastructure;
        return infrastructure;
    }
}
