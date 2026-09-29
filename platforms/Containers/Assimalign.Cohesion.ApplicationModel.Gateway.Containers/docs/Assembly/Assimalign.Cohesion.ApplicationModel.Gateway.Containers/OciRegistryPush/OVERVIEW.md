# OciRegistryPush

Task PushAsync(string storePath, string repository, string digest, Uri registry,
CancellationToken cancellationToken = default).

Pushes a verified store image to an HTTP(S) Registry API v2 endpoint. Repository has no authority;
digest is the immutable manifest digest. The client verifies content, HEADs blobs, POSTs uploads,
PUTs missing blobs, then PUTs unchanged manifest bytes by digest. Returned digest headers must match.

Malformed endpoints, missing/corrupt store content, foreign upload locations and invalid acknowledgements
fail explicitly. HTTP errors and cancellation propagate. This API does not add authentication or
start a registry. See KubernetesKindCluster for the Kind network/provisioning counterpart.
