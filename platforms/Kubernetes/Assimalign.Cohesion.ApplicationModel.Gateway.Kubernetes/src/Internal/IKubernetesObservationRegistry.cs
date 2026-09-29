using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal interface IKubernetesObservationRegistry
{
    Task<IDisposable> EnterMutationAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default);

    void BeginTeardown(IResourceControlContext context);

    void Register(IResourceControlContext context, KubernetesPlanCompilation compilation);

    void Stop(IResourceControlContext context);

    void Unregister(IResourceControlContext context);
}
