using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed class KubernetesExportPublicationRaceException(string message) : InvalidOperationException(message);
