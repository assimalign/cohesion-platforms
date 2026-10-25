using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal sealed partial class KubernetesPlanCompiler
{
    private static void RestoreManagementBindings(IKubernetesObject<V1ObjectMeta> workload, ResourcePlan plan)
    {
        const string volumeName = "cohesion-state";
        const string mountPath = "/var/run/cohesion-state";
        V1PodTemplateSpec? template = workload switch
        {
            V1Deployment deployment => deployment.Spec?.Template,
            V1StatefulSet stateful => stateful.Spec?.Template,
            V1DaemonSet daemon => daemon.Spec?.Template,
            V1Job job => job.Spec?.Template,
            _ => null
        };
        V1Container? container = template?.Spec?.Containers?.SingleOrDefault(candidate => candidate.Name == plan.Container.Name);
        if (container is null) { throw new InvalidDataException("A managed workload must retain its declared resource container."); }
        template!.Spec.Volumes = (template.Spec.Volumes ?? new List<V1Volume>()).Where(volume => volume.Name != volumeName).Append(new V1Volume
        {
            Name = volumeName,
            Secret = new V1SecretVolumeSource
            {
                SecretName = KubernetesMetadata.ChildName(KubernetesMetadata.ResourceName(plan.Resource), "management"), DefaultMode = 288,
                Items = [new V1KeyToPath { Key = KubernetesResourceManagementStore.ProjectionKey, Path = "runtime-state.json" }]
            }
        }).ToList();
        container.VolumeMounts = (container.VolumeMounts ?? new List<V1VolumeMount>()).Where(mount => mount.Name != volumeName &&
            mount.MountPath != mountPath && !mount.MountPath.StartsWith(mountPath + "/", StringComparison.Ordinal)).Append(new V1VolumeMount
        {
            Name = volumeName, MountPath = mountPath, ReadOnlyProperty = true
        }).ToList();
        container.Env = (container.Env ?? new List<V1EnvVar>()).Where(variable =>
            variable.Name != AppEnvironment.Variables.ResourceInstance && variable.Name != AppEnvironment.Variables.ResourceStatePath)
            .Append(new V1EnvVar
            {
                Name = AppEnvironment.Variables.ResourceInstance,
                ValueFrom = new V1EnvVarSource { FieldRef = new V1ObjectFieldSelector { FieldPath = "metadata.uid" } }
            }).Append(new V1EnvVar { Name = AppEnvironment.Variables.ResourceStatePath, Value = mountPath + "/runtime-state.json" }).ToList();
    }
}
