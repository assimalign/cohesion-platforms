using System;
using System.Text;

using k8s;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes;

internal static class KubernetesPlanRenderer
{
    public static string Render(KubernetesPlanCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var output = new StringBuilder();
        for (int index = 0; index < compilation.Objects.Count; index++)
        {
            if (index > 0)
            {
                output.Append('\n');
                output.Append("---\n");
            }

            // JSON is a YAML 1.2 document and, unlike KubernetesYaml in client 17.0.4,
            // correctly base64-encodes Secret.Data byte arrays.
            output.Append(KubernetesJson.Serialize(compilation.Objects[index]));
        }

        output.Append('\n');
        return output.ToString();
    }
}
