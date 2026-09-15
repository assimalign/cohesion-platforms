using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>Imports a model and discovers its gateway address over the Kubernetes operator channel.</summary>
public interface IKubernetesApplicationModelResolver : IApplicationModelResolver
{
    /// <summary>The last discovered address, suitable for <c>remote.Gateway(url)</c>.</summary>
    Uri? ControlPlaneAddress { get; }

    /// <summary>Reads the peer gateway address from its public discovery ConfigMap.</summary>
    /// <param name="cancellationToken">Cancels the Kubernetes read.</param>
    /// <returns>The advertised control-plane address.</returns>
    /// <exception cref="System.IO.InvalidDataException">Discovery metadata is absent or malformed.</exception>
    ValueTask<Uri> ResolveControlPlaneAddressAsync(CancellationToken cancellationToken = default);
}
