# KubernetesSystemInstallation (internal)

Builds ordered native system infrastructure independently of KubernetesPlanCompiler. It requires a
digest-pinned image and explicit persistent capacity, uses KubernetesMetadata for every metadata
object, and excludes application resource labels. It emits Namespace, ServiceAccount, Role and
RoleBinding, ClusterRole/Binding, PVC, initially empty trust Secret, one-replica Deployment,
ClusterIP Service, and optional public Service/Ingress. Namespace permissions are minimal when
only the system namespace is managed; the shared observer still requires cluster pod list/watch.
No offline signing keys, certificates, or tokens are fabricated.
