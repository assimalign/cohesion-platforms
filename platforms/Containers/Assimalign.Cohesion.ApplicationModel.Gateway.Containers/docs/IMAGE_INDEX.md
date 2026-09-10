# Cohesion Image Index

This document is the producer/consumer contract for the Cohesion SDK container-publish item and
the Docker and Kubernetes gateways. It is intentionally smaller than an OCI manifest: it names
the one immutable image artifact owned by each resource and, optionally, where a locally
published archive can be found.

## Normative schema

Both documents are UTF-8 JSON and use the exact schema identifier `cohesion/images/v1`. Unknown
properties are invalid. Property names and string comparisons are case-sensitive.

Per-resource `image.json`:

```json
{
  "schema": "cohesion/images/v1",
  "resource": "appa-api",
  "repository": "example/appa-api",
  "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "tag": "1.4.0",
  "archivePath": "images/appa-api.tar",
  "aot": true,
  "baseImage": "mcr.microsoft.com/dotnet/runtime-deps:10.0",
  "registry": "<late-bound>"
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
      "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "tag": "1.4.0",
      "archivePath": "images/appa-api.tar",
      "aot": true,
      "baseImage": "mcr.microsoft.com/dotnet/runtime-deps:10.0",
      "registry": "<late-bound>"
    }
  ]
}
```

The fields are exact:

| Field | Required | Contract |
| --- | --- | --- |
| `schema` | document only | Exactly `cohesion/images/v1`; entries inside `images` do not repeat it. |
| `application` | application index only | Non-empty application name. |
| `images` | application index only | Array in resource declaration order; `resource` values are unique. An empty array is valid. |
| `resource` | yes | Non-empty resource name that owns this image. |
| `repository` | yes | Non-empty lowercase OCI repository with no tag suffix, URI scheme/query/fragment, whitespace, backslash, empty path segment, or `@`. It excludes a registry prefix only when `registry` is `<late-bound>`. |
| `digest` | yes | `sha256:` followed by exactly 64 hexadecimal characters. Readers canonicalize the hexadecimal characters to lowercase. This is the only pull identity. |
| `tag` | no | Omitted or JSON `null` when unavailable; otherwise non-empty human-readable metadata. It is never used to pull or select an image. |
| `archivePath` | no | Omitted or JSON `null` when unavailable; otherwise a non-empty relative path. `/` and `\` are interpreted as path separators on every host. The path is resolved against the directory containing the index document and must not escape that directory. |
| `aot` | yes | JSON boolean recording whether the published entry point is NativeAOT. |
| `baseImage` | yes | Non-empty publisher-recorded base-image identity. |
| `registry` | yes | Either JSON `null` (the repository needs no target binding) or the exact string `<late-bound>` (the target may prefix a registry authority). |

`cohesion/plan/v1` has exactly one artifact reference: `ArtifactRef.Self` serialized as `"self"`.
A gateway resolving a plan must select the unique index entry whose `resource` equals that plan's
resource. It must reject a missing entry, a duplicate entry, another artifact reference, a
tag-only manifest image, or a manifest repository/digest that differs from the selected entry.
The per-resource index is never searched for another resource's artifact.

When `registry` is `<late-bound>`, a target-supplied authority such as
`registry.example.test:5000` prefixes `repository`; the resulting pull reference is always
`registry.example.test:5000/example/appa-api@sha256:…`. An unresolved late-bound registry is valid
only for a verified local archive path that loads by immutable image ID (Docker) or into Kind.
It must not fall through to a tag or an implicit public registry.

The SDK container-publish item owns writing `image.json` and gathering
`application.images.json`. This package owns strict reading, validation, archive resolution, and
target acquisition; it never builds an image.
