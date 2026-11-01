using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker.Tests;

/// <summary>
/// One running instance of <c>Web/orders-api</c> with a stateful greeting.
/// </summary>
internal sealed class TestResource : IApplicationInstance, IApplicationInstanceAction
{
    private readonly ApplicationInstanceActionState<Greeting> _state = ApplicationInstanceActionState<Greeting>.Create(new Greeting("hello"), static _ => { });

    public Greeting Current => _state.Value;

    public ProviderName Provider => "Web";

    public ResourceName Name => "orders-api";

    public InstanceKey Key { get; } = InstanceKey.New();

    public DeploymentId? Deployment => null;

    public IReadOnlyCollection<IApplicationInstanceAction> Actions => [this];

    public IReadOnlyCollection<IApplicationInstanceHealthContributor> HealthContributors => [];

    ActionName IApplicationInstanceAction.Name => "greeting";

    ApplicationActionKind IApplicationInstanceAction.Kind => ApplicationActionKind.ReadWrite;

    IApplicationInstanceActionState? IApplicationInstanceAction.State => _state;

    public IApplicationInstanceControlPlane CreateControlPlane()
    {
        return ApplicationInstanceControlPlane.Create(this, JsonApplicationContentSerializer.Create(TestJsonContext.Default));
    }

    async ValueTask<ApplicationActionResult> IApplicationInstanceAction.ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken)
    {
        if (context.Operation == ApplicationActionOperation.Read)
        {
            return ApplicationActionResult.Success(_state.Value, context.Serializer);
        }

        Greeting? greeting = context.ReadInput<Greeting>();
        if (greeting is null || string.IsNullOrWhiteSpace(greeting.Text))
        {
            return ApplicationActionResult.Rejected("A greeting is required.");
        }

        await _state.SetAsync(greeting, cancellationToken);
        return ApplicationActionResult.Success();
    }
}
