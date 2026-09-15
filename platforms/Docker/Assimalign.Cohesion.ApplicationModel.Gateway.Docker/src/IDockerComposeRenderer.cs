using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

/// <summary>
/// Renders the Docker object set for one resolved Cohesion resource without contacting a daemon.
/// </summary>
public interface IDockerComposeRenderer
{
    /// <summary>Compiles and renders one resource as a Compose-style YAML document.</summary>
    /// <param name="plan">The validated platform-neutral resource plan.</param>
    /// <param name="artifact">The digest-pinned image artifact.</param>
    /// <param name="inputs">The resolved mount and credential inputs.</param>
    /// <param name="dependencies">The immutable observed dependency snapshot.</param>
    /// <param name="application">The application that owns the Docker object set.</param>
    /// <param name="owner">The gateway ownership identity.</param>
    /// <returns>A deterministic Compose-style YAML document.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="artifact"/>, <paramref name="inputs"/>, or
    /// <paramref name="dependencies"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.ArgumentException"><paramref name="owner"/> is empty.</exception>
    /// <exception cref="System.IO.InvalidDataException">
    /// The plan schema or specification cannot be compiled for Docker.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// A resolved input is absent or unresolved, or a Docker object name is invalid.
    /// </exception>
    string Render(
        ResourcePlan plan,
        IContainerImageArtifact artifact,
        ResourceInputs inputs,
        IReadOnlyList<ResourceDependencyObservation> dependencies,
        ApplicationName application,
        string owner);
}
