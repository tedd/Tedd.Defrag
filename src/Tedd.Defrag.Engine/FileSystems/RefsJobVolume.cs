using Tedd.Defrag.Core;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Engine.FileSystems;

internal sealed class RefsJobVolume(VolumeInfo info) : DirectoryJobVolume(info)
{
    public override VolumeLayout Scan(JobRequest request, Action<double, long, string> progress, Action checkpoint,
        CancellationToken token, Action<WorkProgress>? diagnostics = null)
        => new RefsScanner().Scan(VolumeDiscovery.Get(Info.Root), request, progress, checkpoint, token, diagnostics);
}
