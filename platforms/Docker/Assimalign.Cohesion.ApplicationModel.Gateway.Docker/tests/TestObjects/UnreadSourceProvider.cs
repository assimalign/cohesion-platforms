using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

/// <summary>
/// A mount-source provider registered only so that <c>Build()</c> accepts a
/// <c>&lt;source&gt;:&lt;key&gt;</c> mount. It counts every read and fails it, so a test can prove
/// a code path (such as offline render) never resolves mount sources.
/// </summary>
internal sealed class UnreadSourceProvider : IResourceSourceProvider
{
    private int _reads;

    public string? ResourceKind => null;

    public int Reads => Volatile.Read(ref _reads);

    public ValueTask<ReadOnlyMemory<byte>> ReadSecretAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default) =>
        throw Fail(request);

    public ValueTask<ResourceCertificate> ReadCertificateAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default) =>
        throw Fail(request);

    public ValueTask<IReadOnlyDictionary<string, string?>> ReadConfigurationAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default) =>
        throw Fail(request);

    private InvalidOperationException Fail(ResourceSourceRequest request)
    {
        Interlocked.Increment(ref _reads);
        return new InvalidOperationException(
            $"Mount '{request.Mount}' of '{request.Consumer}' must not be resolved by this test.");
    }
}
