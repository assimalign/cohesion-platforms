using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes.Tests;

/// <summary>
/// One running instance of <c>Web/orders-api</c> with a stateful greeting.
/// </summary>
internal sealed class TestResource : IApplicationResourceInstance, IApplicationInstanceAction
{
    private readonly ApplicationActionState<Greeting> _state = ApplicationActionState<Greeting>.Create(new Greeting("hello"), static _ => { });

    public Greeting Current => _state.Value;

    public ProviderName Provider => "Web";

    public ResourceName Name => "orders-api";

    public InstanceKey Key { get; } = InstanceKey.New();

    public DeploymentId? Deployment => null;

    public IReadOnlyCollection<IApplicationInstanceAction> Actions => [this];

    public IReadOnlyCollection<IApplicationHealthContributor> HealthContributors => [];

    ActionName IApplicationInstanceAction.Name => "greeting";

    ApplicationActionKind IApplicationInstanceAction.Kind => ApplicationActionKind.ReadWrite;

    IApplicationActionState? IApplicationInstanceAction.State => _state;

    public IApplicationInstanceControlPlane CreateControlPlane()
    {
        return ApplicationInstanceControlPlane.Create(this, JsonApplicationContentSerializer.Create(TestJsonContext.Default));
    }

    async ValueTask<ApplicationActionResult> IApplicationInstanceAction.ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken)
    {
        if (context.Operation == ApplicationActionKind.Read)
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

/// <summary>The greeting's content.</summary>
/// <param name="Text">The greeting.</param>
public sealed record Greeting(string Text);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Greeting))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}
