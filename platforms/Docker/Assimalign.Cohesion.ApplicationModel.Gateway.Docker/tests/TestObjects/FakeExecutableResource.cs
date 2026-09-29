using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

internal sealed class FakeExecutableResource : IExecutableResource
{
    public FakeExecutableResource(string name)
    {
        Name = name;
    }

    public ResourceName Name { get; }

    public string Artifact => "Assimalign.Cohesion.Test.Application";

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; } =
        new Dictionary<string, string>();
}
