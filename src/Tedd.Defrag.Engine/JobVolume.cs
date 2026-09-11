using Tedd.Defrag.Core;
using Tedd.Defrag.Core.FileSystems;
using Tedd.Defrag.Engine.FileSystems;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Engine;

internal static class JobVolume
{
    public static IJobVolume Open(JobRequest request) => Open(request, VolumeDiscovery.Get(request.Volume));

    internal static IJobVolume Open(JobRequest request, VolumeInfo info)
    {
        FileSystemCapabilities.Validate(info.FileSystem, request.Operation);
        return FileSystemCapabilities.Get(info.FileSystem) switch
        {
            NtfsFileSystem => new NtfsJobVolume(request),
            RefsFileSystem => new RefsJobVolume(info),
            FatFileSystem => new FatJobVolume(info),
            _ => throw new NotSupportedException($"No volume implementation for {info.FileSystem}.")
        };
    }
}
