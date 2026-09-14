# KubernetesGateway

Derives from ApplicationGateway and implements IKubernetesManifestRenderer,
IApplicationGatewayRenderer, and IApplicationGatewayBootstrapper. RenderAsync emits system
installation then application namespaces/models/resource plans in declaration order; it resolves
manifest/index artifacts offline and uses empty inputs with preview mount shapes. Direct Render
remains strict about resolved inputs. No rendering path gathers images or contacts Kubernetes.

BootstrapAsync always emits system output and applies by default; BootstrapApply=false is
emit-only. SystemImage and SystemStorageSize are required for either installation output path.
Apply preflights every existing owner, preserves trust Secret bytes, and applies in dependency
order with resource-version guards. Model --adopt is required for foreign infrastructure owners.
System objects and the system namespace survive application resource pruning/teardown.

The protected control-plane bind is HTTP 0.0.0.0:8080. Multi-model control-plane routing is an
explicit unsupported topology pending upstream addressing: live observer setup rejects it, after
possible base trust initialization; bootstrap apply rejects it before API contact. Offline output
for multiple models remains a review surface. Native trust-key storage honors the public upstream
repository seam while token issuance/verification remains upstream-owned.
