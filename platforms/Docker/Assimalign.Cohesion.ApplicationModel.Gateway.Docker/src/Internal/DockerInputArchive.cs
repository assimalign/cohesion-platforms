using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Docker;

internal static class DockerInputArchive
{
    public static MemoryStream Create(IReadOnlyList<DockerFilePlan> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            var directories = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < files.Count; index++)
            {
                AddDirectories(writer, directories, files[index].Path);
                string entryName = files[index].Path.TrimStart('/');
                var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
                {
                    DataStream = new MemoryStream(files[index].Content.ToArray(), writable: false),
                    Mode = files[index].Sensitive
                        ? UnixFileMode.UserRead
                        : UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                };
                writer.WriteEntry(entry);
                entry.DataStream.Dispose();
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static void AddDirectories(
        TarWriter writer,
        ISet<string> directories,
        string path)
    {
        string relative = path.TrimStart('/');
        int separator = relative.IndexOf('/');
        while (separator > 0)
        {
            string directory = relative[..separator];
            if (directories.Add(directory))
            {
                var entry = new PaxTarEntry(TarEntryType.Directory, directory)
                {
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                };
                writer.WriteEntry(entry);
            }

            separator = relative.IndexOf('/', separator + 1);
        }
    }
}
