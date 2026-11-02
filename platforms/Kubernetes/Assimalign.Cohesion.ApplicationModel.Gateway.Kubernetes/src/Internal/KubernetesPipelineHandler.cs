using System.Net.Http;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Internal;

// A pass-through handler the Kubernetes client places ahead of its own transport, so requests the
// client does not model, such as pod proxy calls with a body, travel the same authenticated pipeline:
// the cluster's certificate authority and a kubeconfig's client certificate.
internal sealed class KubernetesPipelineHandler : DelegatingHandler
{
}
