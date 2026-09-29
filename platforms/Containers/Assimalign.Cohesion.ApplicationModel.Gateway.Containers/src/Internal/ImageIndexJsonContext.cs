using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ImageIndexDocument))]
[JsonSerializable(typeof(ApplicationImageIndexDocument))]
internal sealed partial class ImageIndexJsonContext : JsonSerializerContext
{
}
