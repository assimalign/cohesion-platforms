using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

/// <summary>
/// A minimal executable resource for wiring tests: a name, a conventional artifact
/// identity, and no environment variables.
/// </summary>
public sealed class FakeExecutableResource : IExecutableResource
{
    /// <summary>
    /// Initializes the fake with the given resource name.
    /// </summary>
    /// <param name="name">The resource name.</param>
    public FakeExecutableResource(string name)
    {
        Name = name;
    }

    /// <inheritdoc/>
    public ResourceName Name { get; }

    /// <inheritdoc/>
    public string Artifact => "Assimalign.Cohesion.Test.Application";

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>();
}
