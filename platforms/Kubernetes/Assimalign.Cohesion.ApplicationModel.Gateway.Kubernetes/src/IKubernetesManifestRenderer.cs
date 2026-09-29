using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>
/// Renders the Kubernetes objects for one resolved Cohesion resource without contacting a
/// cluster.
/// </summary>
/// <remarks>
/// This is the platform seam consumed by a future upstream <c>--mode render</c> dispatcher. JSON
/// documents are emitted because JSON is valid YAML 1.2 and preserves binary Secret data without
/// a second serializer.
/// </remarks>
public interface IKubernetesManifestRenderer
{
    /// <summary>Compiles and serializes one resource's Kubernetes object set.</summary>
    /// <param name="plan">The validated, immutable realization plan.</param>
    /// <param name="artifact">The resolved, digest-pinned container image.</param>
    /// <param name="inputs">The resolved mount and credential inputs for this render.</param>
    /// <param name="dependencies">The immutable observed-dependency snapshot.</param>
    /// <param name="application">The application whose namespace contains the objects.</param>
    /// <param name="owner">The owner identity written to the rendered objects.</param>
    /// <returns>One JSON-as-YAML document per compiled object, separated by YAML delimiters.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="artifact"/>, <paramref name="inputs"/>, or
    /// <paramref name="dependencies"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.ArgumentException">
    /// The image artifact, application, owner, or a registered patch is invalid.
    /// </exception>
    /// <exception cref="System.IO.InvalidDataException">
    /// The plan schema or specification cannot be compiled for Kubernetes, or a resolved input
    /// violates its plan binding.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// A compiled Kubernetes name is invalid or a registered patch removes required structure.
    /// </exception>
    string Render(
        ResourcePlan plan,
        IContainerImageArtifact artifact,
        ResourceInputs inputs,
        IReadOnlyList<ResourceDependencyObservation> dependencies,
        ApplicationName application,
        string owner);
}
