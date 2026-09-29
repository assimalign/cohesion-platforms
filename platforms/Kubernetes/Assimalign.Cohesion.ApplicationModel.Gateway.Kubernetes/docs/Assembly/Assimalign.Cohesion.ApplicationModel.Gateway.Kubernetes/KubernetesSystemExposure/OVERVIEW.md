# KubernetesSystemExposure

Public enum describing control-plane access. `None` is the default and emits only the ClusterIP
Service. `LoadBalancer` adds a separate public Service. `Ingress` adds an Ingress and requires
SystemIngressHost and SystemIngressClass. The upstream server currently binds HTTP/IP only;
external TLS termination is operator/controller configuration.
