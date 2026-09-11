using Tedd.Defrag.Core;

namespace Tedd.Defrag.Windows;

public sealed class RefsScanner
{
    public VolumeLayout Scan(VolumeInfo volume, JobRequest request, Action<double, long, string> progress,
        Action checkpoint, CancellationToken token, Action<WorkProgress>? diagnostics = null)
    {
        if (!FileSystemCapabilities.IsRefs(volume.FileSystem)) throw new NotSupportedException("This scanner requires ReFS.");
        return new DirectoryScanner().Scan(volume, request, progress, checkpoint, token, diagnostics);
    }
}
