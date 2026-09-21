using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using k8s;
using k8s.Autorest;
using k8s.Models;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

public sealed partial class KubernetesGateway
{
    private async Task<Uri?> ReadSystemDiscoveryAddressAsync(CancellationToken cancellationToken, IKubernetesResourceApi? resources = null)
    {
        V1Service? allocated = null;
        if (_options.SystemExposure == KubernetesSystemExposure.LoadBalancer)
        {
            var desired = new V1Service
            {
                ApiVersion = "v1",
                Kind = "Service",
                Metadata = KubernetesMetadata.CreateObjectMeta(KubernetesSystemInstallation.PublicServiceName,
                    _options.SystemNamespace, "cohesion-gateway", "system/v1", _options.FieldManager),
            };
            allocated = await (resources ?? new KubernetesResourceApi(GetRequiredClient(), _options.FieldManager))
                .ReadAsync(desired, cancellationToken).ConfigureAwait(false) as V1Service;
        }
        return KubernetesControlPlaneDiscovery.Address(_options, allocated);
    }

    private async Task RefreshSystemDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (_options.SystemExposure != KubernetesSystemExposure.LoadBalancer)
        {
            return;
        }

        await RefreshSystemDiscoveryAsync(_namespaces.Values.ToArray(),
            new KubernetesGatewayResourceApi(GetRequiredClient), cancellationToken).ConfigureAwait(false);
    }

    internal async Task RefreshSystemDiscoveryAsync(
        IReadOnlyList<NamespaceRegistration> registrations, IKubernetesResourceApi resources,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Uri? address = await ReadSystemDiscoveryAddressAsync(cancellationToken, resources).ConfigureAwait(false);
            foreach (NamespaceRegistration registration in registrations)
            {
                await registration.ExportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (registration.LastExport is { } document && !Equals(registration.LastDiscoveryAddress, address))
                    {
                        await ApplyApplicationExportAsync(registration, document, cancellationToken, address, true, resources).ConfigureAwait(false);
                    }
                }
                finally { registration.ExportGate.Release(); }
            }
        }
        catch (HttpOperationException exception)
        {
            _options.WarningHandler($"Kubernetes control-plane discovery refresh failed; retrying at the next observer resync: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            _options.WarningHandler($"Kubernetes control-plane discovery transport failed; retrying at the next observer resync: {exception.Message}");
        }
        catch (KubernetesExportPublicationRaceException exception)
        {
            _options.WarningHandler($"Kubernetes control-plane discovery changed during publication; retrying at the next observer resync: {exception.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task RenderAsync(
        IReadOnlyList<IApplicationModel> models,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(output);
        ValidateNormalizedNames(models);
        // Assemble every document before writing, so invalid resource plans cannot produce
        // a seemingly usable partial installation. Rendering never gathers or contacts Kubernetes.
        var objects = new List<IKubernetesObject<V1ObjectMeta>>();
        bool includeSystem = _options.SystemImage is not null || _options.SystemStorageSize is not null;
        if (includeSystem)
        {
            objects.AddRange(KubernetesSystemInstallation.Create(_options, models));
        }

        IApplicationImageIndex? index = _options.ImageIndexPath is null ? null
            : await ContainerImageIndexes.ReadApplicationAsync(_options.ImageIndexPath, cancellationToken).ConfigureAwait(false);
        foreach (IApplicationModel model in models)
        {
            string namespaceName = KubernetesMetadata.NamespaceName(model.Name);
            if (!includeSystem || namespaceName != _options.SystemNamespace)
            {
                V1ObjectMeta metadata = KubernetesMetadata.CreateObjectMeta(namespaceName, namespaceName, "cohesion-gateway", "namespace/v1", model.Owner);
                metadata.NamespaceProperty = null;
                metadata.Labels.Remove(KubernetesMetadata.ResourceLabel);
                objects.Add(new V1Namespace { ApiVersion = "v1", Kind = "Namespace", Metadata = metadata });
            }
            foreach (IApplicationResourceDescriptor descriptor in model.Descriptors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (descriptor.Resource is IExternalResource)
                {
                    continue;
                }

                ResourcePlan plan = descriptor.Plan ?? throw new InvalidOperationException($"Resource '{descriptor.Resource.Name}' has no plan.");
                IContainerImageArtifact artifact = ResolveRenderArtifact(model, descriptor.Resource, plan, index);
                KubernetesPlanCompilation compilation = _compiler.Compile(plan, artifact, ResourceInputs.Empty, [],
                    KubernetesMetadata.NamespaceName(model.Name), model.Owner, _options, preview: true);
                foreach (string warning in compilation.Warnings)
                {
                    _options.WarningHandler(warning);
                }

                objects.AddRange(compilation.Objects);
            }
        }
        await output.WriteAsync(KubernetesPlanRenderer.Render(objects).AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes system installation manifests and, by default, applies them.</summary>
    /// <param name="models">The application models the installation will reconcile.</param>
    /// <param name="output">The installation document destination.</param>
    /// <param name="cancellationToken">Cancels rendering or Kubernetes calls.</param>
    /// <returns>A task completing after emission and any requested application.</returns>
    /// <exception cref="ArgumentNullException">The models or output are null.</exception>
    /// <exception cref="ArgumentException">A required system image or storage option is missing.</exception>
    /// <exception cref="InvalidOperationException">An existing object has a foreign owner and adoption was not requested.</exception>
    /// <remarks>Set <see cref="KubernetesGatewayOptions.BootstrapApply"/> to false for offline emission.</remarks>
    // Deviates from upstream bootstrap XML's offline-only wording per the owner-approved
    // emit-or-apply mode contract. The emitted installation is always reviewable in output.
    public async Task BootstrapAsync(
        IReadOnlyList<IApplicationModel> models,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(output);
        IReadOnlyList<IKubernetesObject<V1ObjectMeta>> objects = KubernetesSystemInstallation.Create(_options, models);
        await output.WriteAsync(KubernetesPlanRenderer.Render(objects).AsMemory(), cancellationToken).ConfigureAwait(false);
        if (!_options.BootstrapApply)
        {
            return;
        }

        RequireSingleSystemApplication(models);
        using IKubernetes client = KubernetesClientFactory.Create(_options);
        await ApplySystemInstallationAsync(objects, models, new KubernetesResourceApi(client, _options.FieldManager), cancellationToken).ConfigureAwait(false);
    }

    internal async Task BootstrapAsync(
        IReadOnlyList<IApplicationModel> models, TextWriter output, IKubernetesResourceApi api,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IKubernetesObject<V1ObjectMeta>> objects = KubernetesSystemInstallation.Create(_options, models);
        await output.WriteAsync(KubernetesPlanRenderer.Render(objects).AsMemory(), cancellationToken).ConfigureAwait(false);
        if (_options.BootstrapApply)
        {
            RequireSingleSystemApplication(models);
            await ApplySystemInstallationAsync(objects, models, api, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RequireSingleSystemApplication(IReadOnlyList<IApplicationModel> models)
    {
        if (models.Count > 1)
        {
            throw new InvalidOperationException("Applying a multi-application system installation requires a root/member control-plane addressing topology. Use emit-only bootstrap or render for review.");
        }
    }

    private async Task ApplySystemInstallationAsync(
        IReadOnlyList<IKubernetesObject<V1ObjectMeta>> objects, IReadOnlyList<IApplicationModel> models,
        IKubernetesResourceApi api, CancellationToken cancellationToken)
    {
        bool adopt = models.Count > 0 && models.All(model => model.Adopt);
        // Refuse any known foreign owner before the first mutation, including objects late in
        // the ordered graph such as the Deployment. Each update retains an optimistic RV guard.
        var existingObjects = new List<IKubernetesObject<V1ObjectMeta>?>();
        foreach (IKubernetesObject<V1ObjectMeta> desired in objects)
        {
            IKubernetesObject<V1ObjectMeta>? existing = await api.ReadAsync(desired, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                RequireObjectOwnership(existing.Metadata, $"{desired.Kind}/{desired.Metadata.Name}", desired.Metadata.Annotations[KubernetesMetadata.OwnerAnnotation], adopt);
            }

            existingObjects.Add(existing);
        }
        for (int index = 0; index < objects.Count; index++)
        {
            IKubernetesObject<V1ObjectMeta> desired = objects[index];
            IKubernetesObject<V1ObjectMeta>? existing = existingObjects[index];
            if (existing is null)
            {
                if (!await api.TryCreateAsync(desired, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException($"Kubernetes {desired.Kind}/{desired.Metadata.Name} appeared during ownership validation. Retry bootstrap.");
                }

                existing = await api.ReadAsync(desired, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Kubernetes {desired.Kind}/{desired.Metadata.Name} disappeared after creation. Retry bootstrap.");
            }
            if (existing is not null)
            {
                RequireObjectOwnership(existing.Metadata, $"{desired.Kind}/{desired.Metadata.Name}", desired.Metadata.Annotations[KubernetesMetadata.OwnerAnnotation], adopt);
                desired.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                // A reserved projection never erases operator-provided or upstream-owned keys.
                if (desired is V1Secret secret && existing is V1Secret previous)
                {
                    secret.Data = previous.Data;
                }
            }
            // The resourceVersion precondition protects checked updates against intervening writes.
            await api.ApplyAsync(desired, force: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    protected override ValueTask<Uri> ResolveControlPlaneAddressAsync(
        IApplicationModel model, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Uri.CreateEndpoint("http", "0.0.0.0", KubernetesSystemInstallation.ControlPort));
    }

    private IContainerImageArtifact ResolveRenderArtifact(
        IApplicationModel model, IApplicationResource resource, ResourcePlan plan, IApplicationImageIndex? index)
    {
        if (resource is not IManifestResource manifest)
        {
            throw new InvalidOperationException($"Resource '{resource.Name}' has no manifest artifact.image.");
        }

        IContainerImageArtifact? expected = manifest.Manifest.Artifact.Image is string image
            ? ContainerImageArtifacts.Create(resource.Id, image) : null;
        if (index is null)
        {
            return expected ?? throw new InvalidDataException($"Resource '{resource.Name}' requires a pre-published ImageIndexPath for offline rendering.");
        }

        if (index.Application != model.Name)
        {
            throw new InvalidDataException($"Image index belongs to '{index.Application}', not '{model.Name}'.");
        }

        IContainerImageIndexEntry entry = ContainerImageIndexes.Resolve(index, resource.Name, plan.Container.Artifact);
        if (expected is not null && (entry.Repository != expected.Repository || entry.Digest != expected.Digest))
        {
            throw new InvalidDataException($"Image index for '{resource.Name}' does not match the manifest repository and digest.");
        }

        return ContainerImageIndexes.CreateArtifact(resource.Id, entry, _options.ContainerRegistry);
    }
}
