using System;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Containers;

internal static class ContainerImageValidation
{
    private const string digestPrefix = "sha256:";

    public static bool TryNormalizeDigest(string? digest, out string normalized)
    {
        if (digest is null || digest.Length != digestPrefix.Length + 64
            || !digest.StartsWith(digestPrefix, StringComparison.Ordinal))
        {
            normalized = string.Empty;
            return false;
        }

        for (int index = digestPrefix.Length; index < digest.Length; index++)
        {
            char character = digest[index];
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')
                and not (>= 'A' and <= 'F'))
            {
                normalized = string.Empty;
                return false;
            }
        }

        normalized = digest.ToLowerInvariant();
        return true;
    }

    public static bool IsRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)
            || repository.Contains('@', StringComparison.Ordinal)
            || repository.Contains('\\')
            || repository.Contains('?')
            || repository.Contains('#')
            || repository.StartsWith("/", StringComparison.Ordinal)
            || repository.EndsWith("/", StringComparison.Ordinal)
            || repository.Contains("//", StringComparison.Ordinal)
            || repository.Contains("://", StringComparison.Ordinal)
            || repository.LastIndexOf(':') > repository.LastIndexOf('/'))
        {
            return false;
        }

        for (int index = 0; index < repository.Length; index++)
        {
            if (char.IsWhiteSpace(repository[index]))
            {
                return false;
            }
        }

        string[] components = repository.Split('/');
        for (int index = 0; index < components.Length; index++)
        {
            string component = components[index];
            if (index == 0 && HasRegistryAuthority(repository))
            {
                if (!IsRegistryAuthority(component))
                {
                    return false;
                }

                continue;
            }

            if (!IsRepositoryComponent(component))
            {
                return false;
            }
        }

        return true;
    }

    public static bool HasRegistryAuthority(string repository)
    {
        int separator = repository.IndexOf('/');
        if (separator < 0)
        {
            return false;
        }

        string first = repository[..separator];
        return string.Equals(first, "localhost", StringComparison.OrdinalIgnoreCase)
            || first.Contains('.', StringComparison.Ordinal)
            || first.Contains(':', StringComparison.Ordinal);
    }

    public static bool IsRegistryAuthority(string? registry)
    {
        if (string.IsNullOrWhiteSpace(registry)
            || registry.Contains('@', StringComparison.Ordinal)
            || registry.Contains('/', StringComparison.Ordinal)
            || registry.Contains("\\", StringComparison.Ordinal)
            || registry.Contains("://", StringComparison.Ordinal)
            || registry.EndsWith(':')
            || registry.Contains('?')
            || registry.Contains('#')
            || !Uri.TryCreate($"http://{registry}", UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        for (int index = 0; index < registry.Length; index++)
        {
            if (char.IsWhiteSpace(registry[index]))
            {
                return false;
            }
        }

        return !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    public static bool IsPlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return false;
        }

        string[] components = platform.Split('/');
        if (components.Length is < 2 or > 3)
        {
            return false;
        }

        for (int index = 0; index < components.Length; index++)
        {
            if (!IsPlatformComponent(components[index]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsRelativeArchivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path[0] is '/' or '\\')
        {
            return false;
        }

        return path.Length < 2
            || path[1] != ':'
            || !char.IsAsciiLetter(path[0]);
    }

    public static string NormalizePathSeparators(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

    public static bool TryResolveContainedPath(
        string directory,
        string relativePath,
        out string resolved)
    {
        string physicalDirectory = ResolvePhysicalPath(directory);
        string candidate = Path.GetFullPath(relativePath, Path.GetFullPath(directory));
        resolved = ResolvePhysicalPath(candidate);
        string relative = Path.GetRelativePath(physicalDirectory, resolved);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsRepositoryComponent(string component)
    {
        if (component.Length == 0
            || !IsLowerAlphaNumeric(component[0]))
        {
            return false;
        }

        int index = 0;
        while (index < component.Length && IsLowerAlphaNumeric(component[index]))
        {
            index++;
        }

        while (index < component.Length)
        {
            char separator = component[index];
            if (separator == '-')
            {
                while (index < component.Length && component[index] == '-')
                {
                    index++;
                }
            }
            else if (separator == '.')
            {
                index++;
            }
            else if (separator == '_')
            {
                index++;
                if (index < component.Length && component[index] == '_')
                {
                    index++;
                }
            }
            else
            {
                return false;
            }

            int nameStart = index;
            while (index < component.Length && IsLowerAlphaNumeric(component[index]))
            {
                index++;
            }

            if (index == nameStart)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlatformComponent(string component)
    {
        if (component.Length == 0
            || !IsLowerAlphaNumeric(component[0])
            || !IsLowerAlphaNumeric(component[^1]))
        {
            return false;
        }

        for (int index = 1; index < component.Length - 1; index++)
        {
            char character = component[index];
            if (!IsLowerAlphaNumeric(character)
                && character is not '-' and not '_' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerAlphaNumeric(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static string ResolvePhysicalPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException($"Path '{path}' has no filesystem root.");
        string current = root;
        string[] components = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            FileSystemInfo? target = info?.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                current = Path.GetFullPath(target.FullName);
            }
        }

        return Path.GetFullPath(current);
    }
}
