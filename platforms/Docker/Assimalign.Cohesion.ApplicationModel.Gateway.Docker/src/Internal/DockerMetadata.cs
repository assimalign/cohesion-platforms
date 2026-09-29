using System;
using System.Text;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal static class DockerMetadata
{
    public const string ApplicationLabel = "cohesion.io/application";
    public const string OwnerLabel = "cohesion.io/owner";
    public const string PlanHashLabel = "cohesion.io/plan-hash";
    public const string ResourceLabel = "cohesion.io/resource";
    public const string RuntimeHashLabel = "cohesion.io/runtime-hash";

    public static string ApplicationNetwork(ApplicationName application) =>
        ChildName("cohesion", application.ToString());

    public static string ContainerName(ApplicationName application, ResourceName resource) =>
        ChildName(application.ToString(), resource.ToString());

    public static string VolumeName(
        ApplicationName application,
        ResourceName resource,
        string claim) =>
        ChildName(ContainerName(application, resource), claim);

    public static string ChildName(string parent, string child)
    {
        string value = $"{Normalize(parent)}-{Normalize(child)}";
        if (value.Length > 63)
        {
            throw new InvalidOperationException(
                $"Docker object name '{value}' exceeds the Cohesion 63-character portability limit.");
        }

        return value;
    }

    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var builder = new StringBuilder(value.Length);
        bool separator = false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = char.ToLowerInvariant(value[index]);
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                separator = false;
            }
            else if (!separator && builder.Length > 0)
            {
                builder.Append('-');
                separator = true;
            }
        }

        while (builder.Length > 0 && builder[^1] == '-')
        {
            builder.Length--;
        }

        if (builder.Length == 0)
        {
            throw new InvalidOperationException(
                $"Docker object name source '{value}' contains no portable name characters.");
        }

        return builder.ToString();
    }
}
