using System;
using System.Collections.Generic;
using System.Text;

using k8s;
using k8s.Models;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal static class KubernetesPlanRenderer
{
    public static string Render(KubernetesPlanCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        return Render(compilation.Objects);
    }

    public static string Render(IReadOnlyList<IKubernetesObject<V1ObjectMeta>> objects)
    {
        var output = new StringBuilder();
        for (int index = 0; index < objects.Count; index++)
        {
            if (index > 0)
            {
                output.Append('\n');
                output.Append("---\n");
            }

            // JSON is a YAML 1.2 document and, unlike KubernetesYaml in client 17.0.4,
            // correctly base64-encodes Secret.Data byte arrays.
            output.Append(KubernetesJson.Serialize(objects[index]));
        }

        output.Append('\n');
        return output.ToString();
    }
}
