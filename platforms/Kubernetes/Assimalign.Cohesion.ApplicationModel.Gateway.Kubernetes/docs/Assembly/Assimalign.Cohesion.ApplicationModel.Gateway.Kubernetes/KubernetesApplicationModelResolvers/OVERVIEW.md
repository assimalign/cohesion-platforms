# KubernetesApplicationModelResolvers

ImportFromKubernetes(ApplicationName, KubernetesGatewayOptions) validates platform options and
returns IKubernetesApplicationModelResolver. Model and address reads use the selected kubeconfig
operator identity. The common upstream model resolver contract is preserved. A malformed export
or identity mismatch fails; authentication of subsequent remote.Gateway calls is still the common
control-plane trust contract, independent of ConfigMap read access.
