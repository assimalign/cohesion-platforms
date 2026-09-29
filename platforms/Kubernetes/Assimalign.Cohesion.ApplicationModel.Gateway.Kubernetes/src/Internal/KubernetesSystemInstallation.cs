using System;
using System.Collections.Generic;
using System.Linq;

using k8s;
using k8s.Models;

using Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

/// <summary>Builds gateway infrastructure separately from application resource plans.</summary>
internal static class KubernetesSystemInstallation
{
    internal const string ControlPlaneName = "cohesion-control-plane";
    internal const string PublicServiceName = "cohesion-control-plane-public";
    internal const string StateName = "cohesion-gateway-state";
    internal const string TrustName = "cohesion-gateway-trust";
    internal const string StateDirectory = "/var/lib/cohesion";
    internal const int ControlPort = 8080;
    internal const string FieldManagerVariable = "COHESION_KUBERNETES_FIELD_MANAGER";
    internal const string StateDirectoryVariable = "COHESION_KUBERNETES_STATE_DIRECTORY";
    internal const string IngressClassVariable = "COHESION_KUBERNETES_INGRESS_CLASS";

    internal static IReadOnlyList<IKubernetesObject<V1ObjectMeta>> Create(
        KubernetesGatewayOptions options, IReadOnlyList<IApplicationModel> models)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(models);
        options.ValidateKubernetes();
        if (string.IsNullOrWhiteSpace(options.SystemImage))
        {
            throw new ArgumentException("SystemImage (--cohesion-system-image) is required for Kubernetes bootstrap and render.", nameof(options.SystemImage));
        }
        if (string.IsNullOrWhiteSpace(options.SystemStorageSize))
        {
            throw new ArgumentException("SystemStorageSize (--cohesion-system-storage) is required for persistent gateway export and trust storage.", nameof(options.SystemStorageSize));
        }
        IContainerImageArtifact image = ContainerImageArtifacts.Create(default, options.SystemImage);
        string ns = options.SystemNamespace;
        string account = options.SystemServiceAccount;
        V1ObjectMeta Meta(string name, bool cluster = false)
        {
            V1ObjectMeta metadata = KubernetesMetadata.CreateObjectMeta(name, ns, "cohesion-gateway", "system/v1", options.FieldManager);
            metadata.Labels.Remove(KubernetesMetadata.ResourceLabel);
            metadata.Labels[KubernetesMetadata.SystemLabel] = "gateway";
            if (cluster)
            {
                metadata.NamespaceProperty = null;
            }

            return metadata;
        }
        var rules = new List<V1PolicyRule>
        {
            new() { ApiGroups = [""], Resources = ["configmaps", "secrets", "services", "persistentvolumeclaims", "pods", "endpoints"], Verbs = ["get", "list", "watch", "create", "patch", "update", "delete"] },
            new() { ApiGroups = ["apps"], Resources = ["deployments", "statefulsets", "daemonsets"], Verbs = ["get", "list", "watch", "create", "patch", "update", "delete"] },
            new() { ApiGroups = ["batch"], Resources = ["jobs"], Verbs = ["get", "list", "watch", "create", "patch", "update", "delete"] },
        };
        var subjects = new List<Rbacv1Subject> { new() { Kind = "ServiceAccount", Name = account, NamespaceProperty = ns } };
        V1ObjectMeta namespaceMetadata = Meta(ns, true);
        IApplicationModel? sharingModel = models.FirstOrDefault(model => KubernetesMetadata.NamespaceName(model.Name) == ns);
        if (sharingModel is not null)
        {
            namespaceMetadata.Annotations[KubernetesMetadata.OwnerAnnotation] = sharingModel.Owner;
        }

        var objects = new List<IKubernetesObject<V1ObjectMeta>>
        {
            new V1Namespace { ApiVersion = "v1", Kind = "Namespace", Metadata = namespaceMetadata },
            new V1ServiceAccount { ApiVersion = "v1", Kind = "ServiceAccount", Metadata = Meta(account) },
            new V1Role { ApiVersion = "rbac.authorization.k8s.io/v1", Kind = "Role", Metadata = Meta(account), Rules = rules },
            new V1RoleBinding { ApiVersion = "rbac.authorization.k8s.io/v1", Kind = "RoleBinding", Metadata = Meta(account), RoleRef = new("rbac.authorization.k8s.io", "Role", account), Subjects = subjects },
        };
        bool otherNamespaces = models.Any(model => KubernetesMetadata.NamespaceName(model.Name) != ns);
        {
            objects.Add(new V1ClusterRole
            {
                ApiVersion = "rbac.authorization.k8s.io/v1",
                Kind = "ClusterRole",
                Metadata = Meta(account, true),
                Rules = otherNamespaces
                    ? [.. rules, new V1PolicyRule { ApiGroups = [""], Resources = ["namespaces"], Verbs = ["get", "list", "watch", "create", "patch", "update", "delete"] }]
                    : [new V1PolicyRule { ApiGroups = [""], Resources = ["namespaces"], ResourceNames = [ns], Verbs = ["get", "patch"] },
                       new V1PolicyRule { ApiGroups = [""], Resources = ["pods"], Verbs = ["list", "watch"] }],
            });
            objects.Add(new V1ClusterRoleBinding
            {
                ApiVersion = "rbac.authorization.k8s.io/v1",
                Kind = "ClusterRoleBinding",
                Metadata = Meta(account, true),
                RoleRef = new("rbac.authorization.k8s.io", "ClusterRole", account),
                Subjects = subjects,
            });
        }
        objects.Add(new V1PersistentVolumeClaim
        {
            ApiVersion = "v1",
            Kind = "PersistentVolumeClaim",
            Metadata = Meta(StateName),
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = ["ReadWriteOnce"],
                StorageClassName = options.SystemStorageClass,
                Resources = new V1VolumeResourceRequirements { Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(options.SystemStorageSize) } },
            },
        });
        // This offline installation renderer generates no signing keys or certificates. The upstream base owns
        // credentials and certificates; its native trust repository persists keys into this Secret.
        objects.Add(new V1Secret { ApiVersion = "v1", Kind = "Secret", Metadata = Meta(TrustName), Type = "Opaque" });
        var labels = new Dictionary<string, string> { [KubernetesMetadata.SystemLabel] = "gateway" };
        var arguments = new List<string>
        {
            "--gateway", "kubernetes", "--mode", "run", "--cohesion-system-namespace", ns,
            "--cohesion-system-service-account", account,
            "--control-plane-expose", options.SystemExposure.ToString().ToLowerInvariant(),
        };
        if (options.SystemIngressHost is not null)
        {
            arguments.Add("--control-plane-host");
            arguments.Add(options.SystemIngressHost);
        }
        var environment = new List<V1EnvVar>
        {
            new() { Name = FieldManagerVariable, Value = options.FieldManager },
            new() { Name = StateDirectoryVariable, Value = options.ExportDirectory ?? StateDirectory },
        };
        if (options.SystemIngressClass is not null)
        {
            environment.Add(new V1EnvVar { Name = IngressClassVariable, Value = options.SystemIngressClass });
        }

        objects.Add(new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = Meta("cohesion-gateway"),
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Strategy = new V1DeploymentStrategy { Type = "Recreate" },
                Selector = new V1LabelSelector { MatchLabels = labels },
                Template = new V1PodTemplateSpec
                {
                    Metadata = Meta("cohesion-gateway"),
                    Spec = new V1PodSpec
                    {
                        ServiceAccountName = account,
                        Containers = [new V1Container
                        {
                            Name = "gateway", Image = $"{image.Repository}@{image.Digest}",
                            Args = arguments,
                            Env = environment,
                            Ports = [new V1ContainerPort { Name = "control", ContainerPort = ControlPort, Protocol = "TCP" }],
                            VolumeMounts = [new V1VolumeMount { Name = "state", MountPath = options.ExportDirectory ?? StateDirectory }, new V1VolumeMount { Name = "trust", MountPath = "/var/run/cohesion/trust", ReadOnlyProperty = true }],
                        }],
                        Volumes = [new V1Volume { Name = "state", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = StateName } }, new V1Volume { Name = "trust", Secret = new V1SecretVolumeSource { SecretName = TrustName, Optional = true, DefaultMode = 256 } }],
                    },
                },
            },
        });
        V1Service Service(string name, string type) => new()
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = Meta(name),
            Spec = new V1ServiceSpec { Type = type, Selector = labels, Ports = [new V1ServicePort { Name = "control", Port = ControlPort, TargetPort = ControlPort, Protocol = "TCP" }] },
        };
        objects.Add(Service(ControlPlaneName, "ClusterIP"));
        if (options.SystemExposure == KubernetesSystemExposure.LoadBalancer)
        {
            objects.Add(Service(PublicServiceName, "LoadBalancer"));
        }
        else if (options.SystemExposure == KubernetesSystemExposure.Ingress)
        {
            objects.Add(new V1Ingress
            {
                ApiVersion = "networking.k8s.io/v1",
                Kind = "Ingress",
                Metadata = Meta(ControlPlaneName),
                Spec = new V1IngressSpec
                {
                    IngressClassName = options.SystemIngressClass,
                    Rules = [new V1IngressRule { Host = options.SystemIngressHost, Http = new V1HTTPIngressRuleValue { Paths = [new V1HTTPIngressPath { Path = "/", PathType = "Prefix", Backend = new V1IngressBackend { Service = new V1IngressServiceBackend { Name = ControlPlaneName, Port = new V1ServiceBackendPort { Number = ControlPort } } } }] } }],
                },
            });
        }
        return objects;
    }
}
