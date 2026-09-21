using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed class KubernetesGatewayTrustKeyRepository : IGatewayTrustKeyRepository
{
    private const int maximumAttempts = 8;
    private readonly KubernetesGatewayOptions _options;
    private readonly IKubernetesResourceApi? _api;
    private readonly string _namespaceName;
    private readonly string _owner;

    public KubernetesGatewayTrustKeyRepository(KubernetesGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _namespaceName = KubernetesMetadata.RequireDnsLabel(options.SystemNamespace, "system namespace");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FieldManager);
        _owner = options.FieldManager;
    }

    internal KubernetesGatewayTrustKeyRepository(KubernetesGatewayOptions options, IKubernetesResourceApi api)
        : this(options)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public Task<ECDsa> LoadOrCreateAsync(
        ApplicationName application,
        ResourceName gateway,
        CancellationToken cancellationToken = default) =>
        AccessAsync(application, gateway, rotate: false, cancellationToken);

    public Task<ECDsa> RotateAsync(
        ApplicationName application,
        ResourceName gateway,
        CancellationToken cancellationToken = default) =>
        AccessAsync(application, gateway, rotate: true, cancellationToken);

    private async Task<ECDsa> AccessAsync(
        ApplicationName application,
        ResourceName gateway,
        bool rotate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(application.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(gateway.Value);
        string applicationName = KubernetesMetadata.NamespaceName(application);
        string gatewayName = KubernetesMetadata.ResourceName(gateway);
        string entry = $"{applicationName}.{gatewayName}.p8";
        cancellationToken.ThrowIfCancellationRequested();
        using IKubernetes? client = _api is null ? KubernetesClientFactory.Create(_options) : null;
        IKubernetesResourceApi api = _api ?? new KubernetesResourceApi(client!, _owner);
        await EnsureNamespaceAsync(api, application, gateway, cancellationToken).ConfigureAwait(false);
        ECDsa? generated = null;
        try
        {
            for (int attempt = 0; attempt < maximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                V1Secret candidate = CreateSecret();
                V1Secret? existing = null;
                try
                {
                    IKubernetesObject<V1ObjectMeta>? observed = await api.ReadAsync(candidate, cancellationToken)
                        .ConfigureAwait(false);
                    if (observed is not null)
                    {
                        existing = observed as V1Secret ?? throw new InvalidDataException(
                            $"Gateway trust storage '{_namespaceName}/{KubernetesSystemInstallation.TrustName}' is not a Secret.");
                        ValidateExisting(existing);
                        if (existing.Data?.TryGetValue(entry, out byte[]? content) is true)
                        {
                            ECDsa loaded = ImportPrivateKey(content, entry);
                            if (!rotate)
                            {
                                return loaded;
                            }

                            loaded.Dispose();
                        }

                        if (string.IsNullOrEmpty(existing.Metadata.ResourceVersion))
                        {
                            throw new InvalidDataException("The gateway trust Secret has no resourceVersion for an atomic update.");
                        }

                        candidate.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                        candidate.Metadata.Uid = existing.Metadata.Uid;
                        candidate.Metadata.Labels = new Dictionary<string, string>(
                            existing.Metadata.Labels ?? new Dictionary<string, string>(), StringComparer.Ordinal);
                        candidate.Metadata.Labels.Remove(KubernetesMetadata.ResourceLabel);
                        candidate.Metadata.Annotations = new Dictionary<string, string>(
                            existing.Metadata.Annotations, StringComparer.Ordinal);
                        if (existing.Data is not null)
                        {
                            foreach ((string key, byte[] value) in existing.Data)
                            {
                                candidate.Data.Add(key, value.AsSpan().ToArray());
                            }
                        }
                    }

                    generated ??= ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    if (candidate.Data.Remove(entry, out byte[]? previous))
                    {
                        CryptographicOperations.ZeroMemory(previous);
                    }

                    candidate.Data.Add(entry, generated.ExportPkcs8PrivateKey());
                    if (existing is null)
                    {
                        if (!await api.TryCreateAsync(candidate, cancellationToken).ConfigureAwait(false))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        try
                        {
                            await api.ApplyAsync(candidate, force: false, cancellationToken).ConfigureAwait(false);
                        }
                        catch (HttpOperationException exception)
                            when (exception.Response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
                        {
                            continue;
                        }
                    }

                    ECDsa result = generated;
                    generated = null;
                    return result;
                }
                finally
                {
                    ClearData(candidate);
                    ClearData(existing);
                }
            }

            throw new InvalidOperationException(
                $"Gateway trust key '{entry}' could not be persisted after {maximumAttempts} concurrent update attempts.");
        }
        finally
        {
            generated?.Dispose();
        }
    }

    private async Task EnsureNamespaceAsync(IKubernetesResourceApi api, ApplicationName application,
        ResourceName gateway, CancellationToken cancellationToken)
    {
        string owner = _namespaceName == KubernetesMetadata.NamespaceName(application) ? $"{application}@{gateway}" : _owner;
        var desired = new V1Namespace
        {
            ApiVersion = "v1", Kind = "Namespace",
            Metadata = new V1ObjectMeta
            {
                Name = _namespaceName,
                Annotations = new Dictionary<string, string> { [KubernetesMetadata.OwnerAnnotation] = owner },
                Labels = new Dictionary<string, string> { [KubernetesMetadata.ManagedByLabel] = KubernetesMetadata.ManagedByValue },
            },
        };
        IKubernetesObject<V1ObjectMeta>? existing = await api.ReadAsync(desired, cancellationToken).ConfigureAwait(false);
        if (existing is null && await api.TryCreateAsync(desired, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        existing ??= await api.ReadAsync(desired, cancellationToken).ConfigureAwait(false);
        if (existing is not V1Namespace || existing.Metadata?.Annotations?.TryGetValue(KubernetesMetadata.OwnerAnnotation, out string? actual) != true || actual != owner)
        {
            throw new InvalidOperationException($"Trust namespace '{_namespaceName}' is not owned by '{owner}'; trust storage cannot adopt it.");
        }
    }

    private V1Secret CreateSecret()
    {
        V1ObjectMeta metadata = KubernetesMetadata.CreateObjectMeta(
            KubernetesSystemInstallation.TrustName, _namespaceName, "cohesion-gateway", "system/v1", _owner);
        metadata.Labels.Remove(KubernetesMetadata.ResourceLabel);
        return new V1Secret
        {
            ApiVersion = V1Secret.KubeApiVersion,
            Kind = V1Secret.KubeKind,
            Metadata = metadata,
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>(StringComparer.Ordinal),
        };
    }

    private void ValidateExisting(V1Secret secret)
    {
        if (secret.Metadata?.Annotations is null
            || !secret.Metadata.Annotations.TryGetValue(KubernetesMetadata.OwnerAnnotation, out string? owner)
            || !string.Equals(owner, _owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Gateway trust Secret '{_namespaceName}/{KubernetesSystemInstallation.TrustName}' " +
                $"is not owned by '{_owner}'; trust-key storage cannot be adopted.");
        }

        if (!string.Equals(secret.Type, "Opaque", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Gateway trust storage must use an Opaque Secret.");
        }
    }

    private static ECDsa ImportPrivateKey(byte[] content, string entry)
    {
        ECDsa? key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(content, out int bytesRead);
            if (bytesRead != content.Length
                || key.KeySize != 256
                || !string.Equals(key.ExportParameters(false).Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Gateway trust key '{entry}' must be one complete NIST P-256 PKCS#8 private key.");
            }

            ECDsa result = key;
            key = null;
            return result;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException($"Gateway trust key '{entry}' is not a valid NIST P-256 PKCS#8 private key.", exception);
        }
        finally
        {
            key?.Dispose();
        }
    }

    private static void ClearData(V1Secret? secret)
    {
        if (secret?.Data is null)
        {
            return;
        }

        foreach (byte[] value in secret.Data.Values)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}
