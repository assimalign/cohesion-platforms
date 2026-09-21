using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

internal sealed class TestGatewayTrustKeys : IGatewayTrustKeyRepository, IDisposable
{
    private readonly byte[] _key;
    internal TestGatewayTrustKeys()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _key = key.ExportPkcs8PrivateKey();
    }
    public Task<ECDsa> LoadOrCreateAsync(ApplicationName application, ResourceName gateway, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(_key, out _);
        return Task.FromResult(key);
    }
    public Task<ECDsa> RotateAsync(ApplicationName application, ResourceName gateway, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}
