# IEmbeddedOciRegistry

Namespace: `Assimalign.Cohesion.ApplicationModel.Gateway.Containers`

```csharp
public interface IEmbeddedOciRegistry : IAsyncDisposable
```

Controls a pull-only loopback Registry HTTP API v2 endpoint. The listener serves `/v2/`, manifests
by digest, and repository-reachable blobs by digest through `GET` and `HEAD`. It does not implement
tags, mutation, authentication, or catalog APIs.

## Property

`Endpoint` returns the active loopback HTTP URI. Reading it while stopped throws
`InvalidOperationException`.

## Methods

| Method | Behavior |
| --- | --- |
| `StartAsync(cancellationToken)` | Binds the configured loopback port and begins accepting requests. Repeated calls while running are idempotent. |
| `StopAsync(cancellationToken)` | Stops acceptance, cancels active requests, and waits for their completion. Repeated calls while stopped are idempotent. |
| `DisposeAsync()` | Stops the registry. |

Startup can throw `SocketException` when the listener cannot bind. Start and stop honor
cancellation. At most 64 connections are active, request headers are limited to 32 KiB and must
complete within 10 seconds, and the operating-system accept backlog is bounded to the same
connection count.

Back to the [namespace overview](../OVERVIEW.md).
