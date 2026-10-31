using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Kubernetes.Fixtures;

/// <summary>
/// An action the program adds to its resource: a stateful control, so a write through the gateway is
/// recorded and restored on the resource's other instances.
/// </summary>
internal sealed class GreetingAction : IApplicationInstanceAction
{
    private readonly ApplicationActionState<Greeting> _state = ApplicationActionState<Greeting>.Create(new Greeting("hello"), static _ => { });

    public ActionName Name => "greeting";

    public ApplicationActionKind Kind => ApplicationActionKind.ReadWrite;

    public IApplicationActionState? State => _state;

    public async ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationActionContext context, CancellationToken cancellationToken = default)
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

/// <summary>The greeting's content: on the wire, <c>{ "text": "hello" }</c>.</summary>
/// <param name="Text">The greeting.</param>
internal sealed record Greeting(string Text);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Greeting))]
internal sealed partial class GreetingJsonContext : JsonSerializerContext
{
}
