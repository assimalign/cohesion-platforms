# Cohesion Image Index

This document is the producer/consumer contract for the Cohesion SDK container-publish item and
the Docker and Kubernetes gateways. It is intentionally smaller than an OCI manifest: it names
the one immutable image artifact owned by each resource and, optionally, where a locally
published archive can be found.

## Normative schema

The examples and field table in this section are normative. Both documents are UTF-8 JSON.
Per-resource `image.json` uses the exact schema identifier `cohesion/image/v1`; gateway-level
`application.images.json` uses the exact schema identifier `cohesion/images/v1`. Entries in the
application document use the same image fields as the per-resource document except that they do
not repeat `schema`.

Unknown properties are invalid at both the document root and inside every `images` entry. A reader
must reject them rather than ignore them. Property names and string comparisons are
case-sensitive.

Per-resource `image.json`:

```json
{
  "schema": "cohesion/image/v1",
  "resource": "appa-api",
  "repository": "example/appa-api",
  "registry": null,
  "tag": "1.4.0",
  "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "platform": "linux/amd64",
  "aot": true,
  "baseImage": "mcr.microsoft.com/dotnet/runtime-deps:10.0",
  "archive": "images/appa-api.tar"
}
```

Gateway-level `application.images.json`:

```json
{
  "schema": "cohesion/images/v1",
  "application": "appa",
  "images": [
    {
      "resource": "appa-api",
      "repository": "example/appa-api",
      "registry": null,
      "tag": "1.4.0",
      "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "platform": "linux/amd64",
      "aot": true,
      "baseImage": "mcr.microsoft.com/dotnet/runtime-deps:10.0",
      "archive": "images/appa-api.tar"
    }
  ]
}
```

The fields and their required/optional status are exact:

| Field | Required | Contract |
| --- | --- | --- |
| `schema` | yes, document root | Exactly `cohesion/image/v1` for per-resource `image.json` or `cohesion/images/v1` for gateway-level `application.images.json`. Entries inside `images` must not repeat it. |
| `application` | yes, application index | Non-empty application name. |
| `images` | yes, application index | Array in resource declaration order; `resource` values are unique. An empty array is valid. |
| `resource` | yes | Non-empty resource name that owns this image. |
| `repository` | yes | Non-empty lowercase OCI repository with no registry authority, tag suffix, URI scheme/query/fragment, whitespace, backslash, empty path segment, or `@`. The registry authority is always separate, regardless of whether `registry` is late-bound or pinned. |
| `registry` | no | Omitted or JSON `null` means late-bound; the target may supply the registry authority. Otherwise it is a concrete pinned authority such as `registry.example.test:5000`, with no URI scheme, repository path, credentials, query, or fragment. A target registry cannot override a pinned authority. |
| `tag` | no | Omitted or JSON `null` when unavailable; otherwise non-empty human-readable metadata. It is never used to pull or select an image. |
| `digest` | yes | `sha256:` followed by exactly 64 hexadecimal characters. Readers canonicalize the hexadecimal characters to lowercase. This is the only pull identity. |
| `platform` | yes | Lowercase OCI platform in `os/architecture` or `os/architecture/variant` form, for example `linux/amd64` or `linux/arm64/v8`. Each component begins and ends with a lowercase ASCII letter or digit; interior characters may also contain `.`, `_`, or `-`. |
| `aot` | yes | JSON boolean recording whether the published entry point is NativeAOT. |
| `baseImage` | yes | Non-empty publisher-recorded base-image identity. |
| `archive` | no | Must be omitted when no archive is available; JSON `null` is invalid. When present, it is a non-empty relative path. `/` and `\` are interpreted as path separators on every host. The path is resolved against the directory containing the index document and must not escape that directory. |

`cohesion/plan/v1` has exactly one artifact reference: `ArtifactRef.Self` serialized as `"self"`.
A gateway resolving a plan must select the unique index entry whose `resource` equals that plan's
resource. It must reject a missing entry, a duplicate entry, another artifact reference, a
tag-only manifest image, or a declared manifest repository/digest that differs from the selected entry.
Source manifests may omit the image; the selected index entry then owns the identity.
The per-resource index is never searched for another resource's artifact.

When `registry` is omitted or JSON `null`, a target-supplied authority such as
`registry.example.test:5000` may prefix `repository`; the resulting pull reference is
`registry.example.test:5000/example/appa-api@sha256:…`. When `registry` contains a concrete
authority, that authority is pinned: it prefixes `repository`, and a target-supplied registry is
ignored rather than replacing it. An unresolved late-bound registry is valid only for a verified
local archive that loads by immutable image ID (Docker). Local Kind binds to its provisioned
registry and pushes the verified image by digest. It must not fall through to
a tag or an implicit public registry.

The SDK container-publish item owns writing `image.json` and gathering
`application.images.json`. This package owns strict reading, validation, archive resolution, and
target acquisition. Its shared Local preflight invokes the SDK target, which owns image building
and fingerprint freshness. An explicit pre-published index bypasses this invocation.
