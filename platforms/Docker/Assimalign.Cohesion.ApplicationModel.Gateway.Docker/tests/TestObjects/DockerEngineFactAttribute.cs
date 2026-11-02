using System;

using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

/// <summary>
/// A test that runs against a real Docker-compatible engine: skipped unless <c>COHESION_DOCKER_E2E</c>
/// is <c>1</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DockerEngineFactAttribute : FactAttribute
{
    public DockerEngineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(DockerEngine.EnableVariable) is not ("1" or "true"))
        {
            Skip = $"Set {DockerEngine.EnableVariable}=1 to run it against the Docker-compatible engine DOCKER_HOST or the platform's default names.";
        }
    }
}
