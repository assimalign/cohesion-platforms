using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using k8s.Models;

using Assimalign.Cohesion.ApplicationModel.Kubernetes;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes.Internal;

/// <summary>
/// What the Kubernetes platform runs for a resource: a Deployment of its image whose pods receive the
/// runtime contract as their environment, and a Service in front of their <c>http</c> endpoint. Every
/// instance runs under the namespace's instance service account, which may write Leases only.
/// </summary>
internal static class KubernetesWorkload
{
    /// <summary>The service account, role, and role binding every instance's pod runs under.</summary>
    public const string InstanceAccount = "cohesion-instance";

    /// <summary>The label that names the resource a workload runs.</summary>
    public const string NameLabel = "app.kubernetes.io/name";

    /// <summary>The label of a pod's deployment, which the deployment's id fills in.</summary>
    public const string DeploymentLabel = "cohesion.assimalign.com/deployment";

    /// <summary>The label that names the resource's provider.</summary>
    public const string ProviderLabel = "cohesion.assimalign.com/provider";

    /// <summary>The annotation that holds the revision a Deployment and its pods run.</summary>
    public const string RevisionAnnotation = "cohesion.assimalign.com/revision";

    /// <summary>
    /// The annotation that holds the workload a Deployment runs: its revision, replica count, and
    /// settings' values. Planning compares it, not the revision.
    /// </summary>
    public const string WorkloadAnnotation = "cohesion.assimalign.com/workload";

    /// <summary>The extension that sets how many instances the Deployment runs, as a JSON number.</summary>
    public const string ReplicasExtension = "replicas";

    /// <summary>
    /// The version of the workload this platform builds for a resource. Every revision includes it, so a
    /// change to what the platform builds updates the Deployments an earlier version created: version 2
    /// added the readiness probe, and version 3 took the replica count and the settings out of the
    /// revision into the workload.
    /// </summary>
    public const int TemplateVersion = 3;

    /// <summary>
    /// Computes the revision of what deploying a resource would run that the model does not carry: the
    /// platform's template version, the image, and both ports. It leaves out the settings' values and the
    /// resource's extensions, as the provider contract requires: the gateway fingerprints the manifest,
    /// extensions included, itself, and treats a new revision as a new definition, which starts without
    /// the state recorded for the previous one.
    /// </summary>
    public static string GetRevision(string image, int endpointPort, int controlPlanePort)
    {
        var text = new StringBuilder()
            .Append(TemplateVersion.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(image).Append('\n')
            .Append(endpointPort.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(controlPlanePort.ToString(CultureInfo.InvariantCulture)).Append('\n');

        return Hash(text);
    }

    /// <summary>
    /// Computes the workload a resource's Deployment would run: the revision, the image pull policy, the
    /// environment name the pods receive, the replica count, and the settings' values in ordinal order.
    /// A Deployment that runs another workload is updated.
    /// </summary>
    /// <remarks>
    /// The pull policy and the environment name reach the pods but define nothing, so they are here and
    /// not in the revision: changing them rolls the pods without costing the resource its recorded state.
    /// Each value is prefixed with its length, as the gateway's deployment id is, so no two sets of
    /// values read alike, whatever they contain.
    /// </remarks>
    public static string GetWorkload(string revision, ApplicationResourceManifest resource, IReadOnlyDictionary<SettingName, string> settings, string imagePullPolicy, string? environment)
    {
        var text = new StringBuilder()
            .Append(revision).Append('\n')
            .Append(GetReplicas(resource).ToString(CultureInfo.InvariantCulture)).Append('\n');
        AppendValue(text, "pull", imagePullPolicy);
        AppendValue(text, "environment", environment ?? string.Empty);
        foreach (KeyValuePair<SettingName, string> setting in settings.OrderBy(static setting => setting.Key.Value, StringComparer.Ordinal))
        {
            AppendValue(text, string.Concat("setting:", setting.Key.Value), setting.Value);
        }

        return Hash(text);
    }

    /// <summary>
    /// Gets the environment name the gateway program runs under, which its resources' pods receive.
    /// </summary>
    public static string? GetEnvironmentName()
    {
        return AppEnvironment.GetValue(AppEnvironment.Variables.Environment);
    }

    /// <summary>
    /// Decides what deploying a resource changes: no Deployment is a create, and one that runs the
    /// workload and has finished rolling it out is nothing. Anything else is an update: another
    /// workload, one an earlier version created without a workload, and a rollout that has not
    /// finished or failed, such as one whose new pods crash. Deploying the same workload again applies
    /// nothing new, and the gateway's watch then reports why the rollout does not finish.
    /// </summary>
    public static ApplicationGatewayDeploymentChange GetChange(V1Deployment? running, string workload)
    {
        if (running is null)
        {
            return ApplicationGatewayDeploymentChange.Create;
        }

        return running.Metadata?.Annotations?.TryGetValue(WorkloadAnnotation, out string? current) == true && current == workload && IsRolledOut(running)
            ? ApplicationGatewayDeploymentChange.None
            : ApplicationGatewayDeploymentChange.Update;
    }

    // As kubectl rollout status decides it: the controller has seen the latest spec, every replica runs
    // it and is available, and no pod of an earlier template remains.
    private static bool IsRolledOut(V1Deployment deployment)
    {
        if (deployment.Status is not { } status)
        {
            return false;
        }

        long generation = deployment.Metadata?.Generation ?? 0;
        int replicas = deployment.Spec?.Replicas ?? 1;
        return (status.ObservedGeneration ?? 0) >= generation
            && (status.UpdatedReplicas ?? 0) == replicas
            && (status.AvailableReplicas ?? 0) == replicas
            && (status.Replicas ?? 0) == replicas;
    }

    private static void AppendValue(StringBuilder text, string name, string value)
    {
        text.Append(name)
            .Append('=')
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
    }

    /// <summary>
    /// Builds the resource's Deployment for one deployment of it.
    /// </summary>
    public static V1Deployment CreateDeployment(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        DeploymentId deployment,
        string image,
        string revision,
        string workload,
        string ns,
        KubernetesInfrastructureOptions options)
    {
        string name = resource.Name.Value;
        Dictionary<string, string> labels = CreateLabels(resource);
        Dictionary<string, string> podLabels = new(labels) { [DeploymentLabel] = deployment.ToString() };

        return new V1Deployment
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment",
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                Labels = labels,
                Annotations = new Dictionary<string, string> { [RevisionAnnotation] = revision, [WorkloadAnnotation] = workload },
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = GetReplicas(resource),
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { [NameLabel] = name } },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = podLabels,
                        Annotations = new Dictionary<string, string> { [RevisionAnnotation] = revision },
                    },
                    Spec = new V1PodSpec
                    {
                        // The pod's pause container is its PID 1, so the program is not. A program that
                        // is PID 1 cannot end itself with a default-action signal: one whose start fails
                        // with an unhandled exception keeps running instead of exiting, and the pod never
                        // shows the failure the gateway's watch reports.
                        ShareProcessNamespace = true,
                        ServiceAccountName = InstanceAccount,
                        Containers =
                        [
                            new V1Container
                            {
                                Name = "resource",
                                Image = image,
                                ImagePullPolicy = options.ImagePullPolicy,
                                Ports =
                                [
                                    new V1ContainerPort { Name = "http", ContainerPort = options.EndpointPort },
                                    new V1ContainerPort { Name = "control-plane", ContainerPort = options.ControlPlanePort },
                                ],
                                // The program's server listens only after its instance has published and
                                // restored the gateway's state, so the Service routes to a pod only once
                                // the pod accepts connections on its http endpoint.
                                ReadinessProbe = new V1Probe
                                {
                                    TcpSocket = new V1TCPSocketAction { Port = "http" },
                                    PeriodSeconds = 2,
                                    TimeoutSeconds = 1,
                                    FailureThreshold = 3,
                                },
                                Env = CreateEnvironment(model, resource, settings, deployment, ns, options),
                            },
                        ],
                    },
                },
            },
        };
    }

    /// <summary>
    /// Builds the Service in front of the resource's <c>http</c> endpoint, on port 80.
    /// </summary>
    public static V1Service CreateService(ApplicationResourceManifest resource, string ns, KubernetesInfrastructureOptions options)
    {
        return new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = new V1ObjectMeta { Name = resource.Name.Value, NamespaceProperty = ns, Labels = CreateLabels(resource) },
            Spec = new V1ServiceSpec
            {
                Selector = new Dictionary<string, string> { [NameLabel] = resource.Name.Value },
                Ports = [new V1ServicePort { Name = "http", Port = 80, TargetPort = options.EndpointPort }],
            },
        };
    }

    /// <summary>
    /// The address the resource's dependents reach its <c>http</c> endpoint at: its Service's name in
    /// the cluster's DNS.
    /// </summary>
    public static string GetEndpoint(ApplicationResourceManifest resource, string ns)
    {
        return string.Concat("http://", resource.Name.Value, ".", ns, ".svc.cluster.local");
    }

    /// <summary>
    /// The instance service account, which may manage the Leases of the namespace and nothing else.
    /// </summary>
    public static (V1ServiceAccount Account, V1Role Role, V1RoleBinding Binding) CreateInstanceAccess(string ns)
    {
        var labels = new Dictionary<string, string> { [KubernetesProtocol.ManagedByLabel] = KubernetesProtocol.ManagedByValue };
        return (
            new V1ServiceAccount
            {
                ApiVersion = "v1",
                Kind = "ServiceAccount",
                Metadata = new V1ObjectMeta { Name = InstanceAccount, NamespaceProperty = ns, Labels = labels },
            },
            new V1Role
            {
                ApiVersion = "rbac.authorization.k8s.io/v1",
                Kind = "Role",
                Metadata = new V1ObjectMeta { Name = InstanceAccount, NamespaceProperty = ns, Labels = labels },
                Rules =
                [
                    new V1PolicyRule
                    {
                        ApiGroups = ["coordination.k8s.io"],
                        Resources = ["leases"],
                        Verbs = ["get", "list", "watch", "create", "update", "patch", "delete"],
                    },
                ],
            },
            new V1RoleBinding
            {
                ApiVersion = "rbac.authorization.k8s.io/v1",
                Kind = "RoleBinding",
                Metadata = new V1ObjectMeta { Name = InstanceAccount, NamespaceProperty = ns, Labels = labels },
                RoleRef = new V1RoleRef { ApiGroup = "rbac.authorization.k8s.io", Kind = "Role", Name = InstanceAccount },
                Subjects = [new Rbacv1Subject { Kind = "ServiceAccount", Name = InstanceAccount, NamespaceProperty = ns }],
            });
    }

    /// <summary>
    /// The number of instances the Deployment runs: the resource's <c>replicas</c> extension, or 1.
    /// </summary>
    /// <exception cref="ArgumentException">The extension is not a whole number of at least 1.</exception>
    public static int GetReplicas(ApplicationResourceManifest resource)
    {
        foreach (ApplicationResourceExtension extension in resource.Extensions)
        {
            if (extension.Name.Value != ReplicasExtension)
            {
                continue;
            }

            if (extension.Value.ValueKind != JsonValueKind.Number || !extension.Value.TryGetInt32(out int replicas) || replicas < 1)
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ReplicasInvalid, resource.Provider, resource.Name), nameof(resource));
            }

            return replicas;
        }

        return 1;
    }

    private static Dictionary<string, string> CreateLabels(ApplicationResourceManifest resource)
    {
        return new Dictionary<string, string>
        {
            [NameLabel] = resource.Name.Value,
            [KubernetesProtocol.ManagedByLabel] = KubernetesProtocol.ManagedByValue,
            [ProviderLabel] = resource.Provider.Value.ToLowerInvariant().Replace('.', '-'),
        };
    }

    // What a platform hands a workload, as its environment: who it is, the deployment that started it,
    // its platform and where its gateway is, its pod, where it listens, and its settings.
    private static List<V1EnvVar> CreateEnvironment(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        DeploymentId deployment,
        string ns,
        KubernetesInfrastructureOptions options)
    {
        var environment = new List<V1EnvVar>
        {
            new() { Name = AppEnvironment.Variables.Application, Value = model.Name.Value },
            new() { Name = AppEnvironment.Variables.Gateway, Value = KubernetesProtocol.PlatformName },
            new() { Name = AppEnvironment.Variables.Resource, Value = resource.Name.Value },
            new() { Name = AppEnvironment.Variables.ManifestEndpoint, Value = KubernetesProtocol.CreateGatewayEndpoint(ns).AbsoluteUri },
            new() { Name = AppEnvironment.Variables.Deployment, Value = deployment.ToString() },
            new() { Name = AppEnvironment.Variables.Endpoint("http", "HOST"), Value = "0.0.0.0" },
            new() { Name = AppEnvironment.Variables.Endpoint("http", "PORT"), Value = options.EndpointPort.ToString(CultureInfo.InvariantCulture) },
            new() { Name = AppEnvironment.Variables.Endpoint("http", "SCHEME"), Value = Uri.UriSchemeHttp },
            new() { Name = KubernetesProtocol.ControlPlanePortVariable, Value = options.ControlPlanePort.ToString(CultureInfo.InvariantCulture) },
            FromField(KubernetesProtocol.PodNameVariable, "metadata.name"),
            FromField(KubernetesProtocol.PodNamespaceVariable, "metadata.namespace"),
            FromField(KubernetesProtocol.PodUidVariable, "metadata.uid"),
        };

        // The gateway program's environment name is its resources' too.
        if (GetEnvironmentName() is { } name)
        {
            environment.Add(new V1EnvVar { Name = AppEnvironment.Variables.Environment, Value = name });
        }

        foreach (KeyValuePair<SettingName, string> setting in settings.OrderBy(static setting => setting.Key.Value, StringComparer.Ordinal))
        {
            environment.Add(new V1EnvVar { Name = AppEnvironment.Variables.Configuration(setting.Key.Value), Value = setting.Value });
        }

        return environment;
    }

    private static string Hash(StringBuilder text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    private static V1EnvVar FromField(string name, string path)
    {
        return new V1EnvVar { Name = name, ValueFrom = new V1EnvVarSource { FieldRef = new V1ObjectFieldSelector { FieldPath = path } } };
    }
}
