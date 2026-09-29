# KubernetesGatewayTrustKeyRepository (internal)

Implements the upstream public IGatewayTrustKeyRepository native persistence contract using the
owned Opaque system Secret. Runtime creation/rotation uses P-256 PKCS#8 entries scoped to
application and gateway. Optimistic writes preserve independent identities and reject malformed,
foreign, or conflicting state. Custom option repositories are preserved. This persistence seam
does not implement token issuance or certificate generation; those remain upstream concerns.
