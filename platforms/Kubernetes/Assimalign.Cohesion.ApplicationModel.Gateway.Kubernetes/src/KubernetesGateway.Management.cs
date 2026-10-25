using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

public sealed partial class KubernetesGateway
{
    /// <inheritdoc />
    protected override ValueTask<ResourceInstanceInventory> ObserveResourceInstancesAsync(IApplicationModel model,
        IApplicationResourceDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_observations.GetManagementInventory(model.Name, descriptor.Resource.Name));
    }
}
