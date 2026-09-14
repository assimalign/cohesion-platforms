# KubernetesGatewayCommandLine

Public static `Apply(KubernetesGatewayOptions, string[])` is the SDK's buildTransitive hook. It
runs before upstream GatewayControlPlane.Configure and reads the deployment-provided state directory,
normally `/var/lib/cohesion` inside the pod. Developer hosts retain upstream `.cohesion` defaults. The direct Kubernetes extension delegates the same moved parser.
This static hook is a narrow documented interface-first exception required by generated code.

Valued switches accept separated and equals forms: `--context`, `--kubeconfig`,
`--cohesion-system-namespace`, `--cohesion-system-image`, `--cohesion-system-service-account`,
`--cohesion-system-storage`, `--control-plane-expose` (none/loadbalancer/ingress), and
`--control-plane-host`. `--bootstrap-apply` accepts bare, `=true|false`, or a following boolean.
Unknown switches are ignored; missing/empty/invalid recognized values throw ArgumentException
naming the switch. Null options/arguments throw ArgumentNullException.

Private installation environment values preserve FieldManager, state directory, and Ingress class
inside the emitted gateway pod. They are gateway deployment plumbing, not resource-runtime keys
or credentials. Explicit CLI values are then applied normally.
