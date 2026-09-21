# ContainerImagePublishing

Shared Local image preparation. RuntimeIdentifier(string) maps arm64/aarch64 and amd64/x86_64/x64
to linux-arm64 and linux-x64. CreatePublisher() supplies IContainerImagePublisher.

PrepareAsync(IApplicationModel, string?, Func<CancellationToken, Task<string>>, IContainerImagePublisher,
CancellationToken) returns the validated index path, or null for non-Local/package models.
Explicit indexes skip target inspection and publishing. Source apphosts are identified through
their manifest; the current model omission uses the matching entry assembly's embedded manifest.
ReloadAsync(string, ApplicationName, CancellationToken) reads and validates application identity.

Unsupported architectures, unavailable apphost identity, failed SDK execution, invalid indexes,
and I/O errors propagate. Cancellation terminates SDK child processes. See the area README for
staging paths and the -restore/-t:CohesionPublishImages invocation contract.
