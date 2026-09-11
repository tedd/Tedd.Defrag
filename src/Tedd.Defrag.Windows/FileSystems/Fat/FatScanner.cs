using Tedd.Defrag.Core;

namespace Tedd.Defrag.Windows;

public sealed class FatScanner
{
    public VolumeLayout Scan(VolumeInfo volume, JobRequest request, Action<double, long, string> progress,
        Action checkpoint, CancellationToken token, Action<WorkProgress>? diagnostics = null)
    {
        if (!FileSystemCapabilities.IsFat(volume.FileSystem)) throw new NotSupportedException("This scanner requires FAT12, FAT16 or FAT32.");
        return new DirectoryScanner().Scan(volume, request, progress, checkpoint, token, diagnostics);
    }
}
