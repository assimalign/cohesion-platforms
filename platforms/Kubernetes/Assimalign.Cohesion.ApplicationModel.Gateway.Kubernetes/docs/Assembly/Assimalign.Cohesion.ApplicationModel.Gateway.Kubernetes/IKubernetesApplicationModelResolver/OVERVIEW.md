# IKubernetesApplicationModelResolver

Extends upstream IApplicationModelResolver without changing its contract. ImportFromKubernetes
returns this interface. ResolveAsync continues importing the existing export model over operator
kubeconfig access. ControlPlaneAddress is the most recently imported discovery URL and is cleared
when metadata is withdrawn. ResolveControlPlaneAddressAsync reads the URL separately for use in
`remote.Gateway(url)`. Missing/malformed metadata throws InvalidDataException, including while
LoadBalancer allocation is pending. Discovery contains URL/public trust key, never bearer tokens.
