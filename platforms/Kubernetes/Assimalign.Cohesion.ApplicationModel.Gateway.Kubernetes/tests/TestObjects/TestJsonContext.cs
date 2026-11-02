using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Tests;

/// <summary>The fixture program's <c>greeting</c> action content: on the wire, <c>{ "text": "hello" }</c>.</summary>
/// <param name="Text">The greeting.</param>
internal sealed record Greeting(string Text);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Greeting))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}
