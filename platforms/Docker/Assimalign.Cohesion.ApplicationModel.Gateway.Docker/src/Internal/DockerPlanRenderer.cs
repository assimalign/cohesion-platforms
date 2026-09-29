using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal static class DockerPlanRenderer
{
    public static string Render(IReadOnlyList<DockerPlanCompilation> compilations)
    {
        ArgumentNullException.ThrowIfNull(compilations);
        var output = new StringBuilder();
        output.AppendLine("version: \"3.9\"");
        if (compilations.Count > 0)
        {
            output.Append("name: ").AppendLine(Quote(compilations[0].Application.ToString()));
        }

        output.AppendLine("services:");
        for (int index = 0; index < compilations.Count; index++)
        {
            WriteService(output, compilations[index]);
        }

        output.AppendLine("networks:");
        var networks = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < compilations.Count; index++)
        {
            DockerNetworkPlan network = compilations[index].Network;
            if (!networks.Add(network.Name))
            {
                continue;
            }

            output.Append("  ").Append(Quote(network.Name)).AppendLine(":");
            output.Append("    name: ").AppendLine(Quote(network.Name));
            WriteMap(output, "    labels:", "      ", network.Labels);
        }

        bool hasVolumes = false;
        for (int compilationIndex = 0; compilationIndex < compilations.Count; compilationIndex++)
        {
            if (compilations[compilationIndex].Volumes.Count > 0)
            {
                hasVolumes = true;
                break;
            }
        }

        output.AppendLine(hasVolumes ? "volumes:" : "volumes: {}");
        var volumes = new HashSet<string>(StringComparer.Ordinal);
        for (int compilationIndex = 0; compilationIndex < compilations.Count; compilationIndex++)
        {
            DockerPlanCompilation compilation = compilations[compilationIndex];
            for (int volumeIndex = 0; volumeIndex < compilation.Volumes.Count; volumeIndex++)
            {
                DockerVolumePlan volume = compilation.Volumes[volumeIndex];
                if (!volumes.Add(volume.Name))
                {
                    continue;
                }

                output.Append("  ").Append(Quote(volume.Name)).AppendLine(":");
                output.Append("    name: ").AppendLine(Quote(volume.Name));
                WriteMap(output, "    labels:", "      ", volume.Labels);
            }
        }

        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void WriteService(StringBuilder output, DockerPlanCompilation compilation)
    {
        DockerContainerPlan container = compilation.Container;
        output.Append("  ").Append(Quote(container.Name)).AppendLine(":");
        output.Append("    container_name: ").AppendLine(Quote(container.Name));
        output.Append("    image: ").AppendLine(Quote(container.ImageReference));
        output.AppendLine("    restart: \"no\"");
        WriteMap(output, "    environment:", "      ", container.Environment);
        WriteMap(output, "    labels:", "      ", container.Labels);
        output.AppendLine("    networks:");
        output.Append("      ").Append(Quote(compilation.Network.Name)).AppendLine(":");
        output.AppendLine("        aliases:");
        for (int index = 0; index < container.NetworkAliases.Count; index++)
        {
            output.Append("          - ").AppendLine(Quote(container.NetworkAliases[index]));
        }

        if (container.VolumeMounts.Count > 0)
        {
            output.AppendLine("    volumes:");
            for (int index = 0; index < container.VolumeMounts.Count; index++)
            {
                DockerVolumeMountPlan mount = container.VolumeMounts[index];
                output.Append("      - ").AppendLine(Quote($"{mount.Source}:{mount.Target}"));
            }
        }

        if (container.Tmpfs.Count > 0)
        {
            output.AppendLine("    tmpfs:");
            for (int index = 0; index < container.Tmpfs.Count; index++)
            {
                output.Append("      - ").AppendLine(
                    Quote($"{container.Tmpfs[index]}:rw,noexec,nosuid,nodev,mode=0700"));
            }
        }

        if (container.PortBindings.Count > 0)
        {
            output.AppendLine("    ports:");
            for (int index = 0; index < container.PortBindings.Count; index++)
            {
                DockerPortPublishPlan port = container.PortBindings[index];
                string hostPort = port.HostPort?.ToString(CultureInfo.InvariantCulture) ?? "0";
                string value = $"{port.HostIp}:{hostPort}:{port.ContainerPort}/{port.Protocol}";
                output.Append("      - ").AppendLine(Quote(value));
            }
        }

        output.AppendLine("    x-cohesion:");
        output.Append("      restart_policy: ").AppendLine(Quote(container.RestartPolicy));
        output.AppendLine("      restart_owner: \"gateway\"");
        output.Append("      workload: ").AppendLine(Quote(container.Workload.ToString()));
        output.Append("      run_once: ").AppendLine(container.Workload is WorkloadKind.Job ? "true" : "false");
        output.Append("      stop_grace_seconds: ")
            .AppendLine(container.StopGraceSeconds.ToString(CultureInfo.InvariantCulture));
        output.Append("      plan_hash: ").AppendLine(Quote(compilation.PlanHash));
        if (compilation.Files.Count == 0)
        {
            output.AppendLine("      inputs: []");
        }
        else
        {
            output.AppendLine("      inputs:");
            for (int index = 0; index < compilation.Files.Count; index++)
            {
                DockerFilePlan file = compilation.Files[index];
                output.Append("        - path: ").AppendLine(Quote(file.Path));
                output.Append("          sensitive: ").AppendLine(file.Sensitive ? "true" : "false");
            }
        }

        if (compilation.Probes.Count == 0)
        {
            output.AppendLine("      probes: []");
        }
        else
        {
            output.AppendLine("      probes:");
            for (int index = 0; index < compilation.Probes.Count; index++)
            {
                DockerProbePlan probe = compilation.Probes[index];
                output.Append("        - role: ").AppendLine(Quote(probe.Role));
                output.Append("          kind: ").AppendLine(Quote(probe.Kind.ToString()));
                if (probe.Endpoint is not null)
                {
                    output.Append("          endpoint: ").AppendLine(Quote(probe.Endpoint));
                }

                if (probe.Value is not null)
                {
                    output.Append("          value: ").AppendLine(Quote(probe.Value));
                }
            }
        }
    }

    private static void WriteMap(
        StringBuilder output,
        string heading,
        string indentation,
        IReadOnlyDictionary<string, string> values)
    {
        output.AppendLine(heading);
        var keys = new List<string>(values.Keys);
        keys.Sort(StringComparer.Ordinal);
        for (int index = 0; index < keys.Count; index++)
        {
            string key = keys[index];
            output.Append(indentation).Append(Quote(key)).Append(": ")
                .AppendLine(Quote(values[key]));
        }
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            switch (character)
            {
                case '\\':
                    result.Append("\\\\");
                    break;
                case '"':
                    result.Append("\\\"");
                    break;
                case '\n':
                    result.Append("\\n");
                    break;
                case '\r':
                    result.Append("\\r");
                    break;
                case '\t':
                    result.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        result.Append("\\u")
                            .Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        result.Append(character);
                    }

                    break;
            }
        }

        return result.Append('"').ToString();
    }
}
