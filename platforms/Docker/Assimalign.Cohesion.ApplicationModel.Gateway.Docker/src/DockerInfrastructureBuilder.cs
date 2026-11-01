using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

using Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

/// <summary>
/// Composes a <see cref="DockerInfrastructure"/>: the engine and the shape of what runs there, and the
/// image bound to each resource.
/// </summary>
/// <remarks>
/// The gateway builder's <c>UseDocker(configure)</c> hands one to its callback when the gateway is
/// built, and the gateway owns the infrastructure it builds. <see cref="Build"/> creates an
/// infrastructure of its own, which its caller disposes.
/// </remarks>
[DebuggerDisplay("{Options.Network}")]
public sealed class DockerInfrastructureBuilder
{
    private readonly Dictionary<(ProviderName Provider, ResourceName Name), string> _images = [];

    /// <summary>
    /// Gets the engine and the shape of what runs there. <see cref="Build"/> copies them.
    /// </summary>
    public DockerInfrastructureOptions Options { get; } = new();

    // The last infrastructure Build created, so a test reaches the platform a gateway owns.
    internal DockerInfrastructure? Built { get; private set; }

    /// <summary>
    /// Binds a resource to the container image that runs it, such as <c>cohesion-docker-web:e2e</c>.
    /// </summary>
    /// <param name="provider">The resource's provider, such as <c>Web</c>.</param>
    /// <param name="name">The resource's name.</param>
    /// <param name="image">The image reference the engine runs, and pulls when it has none by that reference.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="provider"/> or <paramref name="name"/> is <see langword="default"/>, the image is
    /// empty, or the resource already has an image.
    /// </exception>
    public DockerInfrastructureBuilder AddImage(ProviderName provider, ResourceName name, string image)
    {
        DockerInfrastructure.CheckImage(provider, name, image);
        if (!_images.TryAdd((provider, name), image))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ImageDuplicate, provider, name), nameof(name));
        }

        return this;
    }

    /// <summary>
    /// Creates the infrastructure for the engine <see cref="Options"/> names, with every image bound.
    /// </summary>
    /// <returns>A new infrastructure, which the caller disposes. It reaches the engine only when a gateway first needs it.</returns>
    /// <exception cref="ArgumentException">
    /// The network is not a DNS label, a port is out of range, the image pull policy is unknown, or the
    /// endpoint is not of a form the docker CLI accepts.
    /// </exception>
    /// <exception cref="NotSupportedException">The endpoint asks for TLS or SSH, which the platform does not speak yet.</exception>
    public DockerInfrastructure Build()
    {
        DockerInfrastructure infrastructure = DockerInfrastructure.Create(Options);

        // Every image was checked as it was added, so binding them to a new infrastructure cannot fail.
        foreach (KeyValuePair<(ProviderName Provider, ResourceName Name), string> binding in _images)
        {
            infrastructure.AddImage(binding.Key.Provider, binding.Key.Name, binding.Value);
        }

        Built = infrastructure;
        return infrastructure;
    }
}
