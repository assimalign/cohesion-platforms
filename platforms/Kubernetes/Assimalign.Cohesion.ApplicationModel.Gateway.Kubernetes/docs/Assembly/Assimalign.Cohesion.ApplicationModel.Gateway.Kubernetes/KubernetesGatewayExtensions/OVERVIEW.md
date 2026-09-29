# KubernetesGatewayExtensions

UseKubernetesGateway selects Kubernetes on IApplicationBuilder. The arguments overload applies
common gateway arguments, delegates KubernetesGatewayCommandLine.Apply, then invokes the optional
configuration callback. The no-argument and callback-only overloads preserve their existing role.
The platform parser recognizes context/kubeconfig and system installation/exposure options. The
SDK-generated provider hook calls the same public method before common control-plane configuration;
the shipped platform assembly does not reference Gateway.ControlPlane.