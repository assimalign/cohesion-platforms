# KubernetesKindCluster

Task ProvisionAsync(string name = "cohesion", bool recreate = false,
CancellationToken cancellationToken = default).

Runs the bundled New-CohesionKindCluster.ps1 with PowerShell and the Podman Kind provider.
Creates or reuses the registry and cluster, configures containerd routing, and verifies the result.
Recreation is opt-in; an incompatible existing cluster fails with an actionable instruction.
Provisioning leaves the registry running for later pod pulls. Its lifecycle is owned by the local
environment, not a gateway session. See the Kubernetes area README for exact provisioning steps.

Invalid cluster names and nonzero script exits fail. Cancellation kills the provisioning process tree;
partially created infrastructure remains available for inspection and a later idempotent invocation.
