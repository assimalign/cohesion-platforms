using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ImageIndexDocument
{
    public required string Schema { get; init; }

    public required string Resource { get; init; }

    public required string Repository { get; init; }

    public string? Registry { get; init; }

    public string? Tag { get; init; }

    public required string Digest { get; init; }

    public required string Platform { get; init; }

    public bool? Aot { get; init; }

    public required string BaseImage { get; init; }

    public JsonElement Archive { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ApplicationImageIndexDocument
{
    public required string Schema { get; init; }

    public required string Application { get; init; }

    public required List<ApplicationImageIndexEntryDocument> Images { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ApplicationImageIndexEntryDocument
{
    public required string Resource { get; init; }

    public required string Repository { get; init; }

    public string? Registry { get; init; }

    public string? Tag { get; init; }

    public required string Digest { get; init; }

    public required string Platform { get; init; }

    public bool? Aot { get; init; }

    public required string BaseImage { get; init; }

    public JsonElement Archive { get; init; }
}
