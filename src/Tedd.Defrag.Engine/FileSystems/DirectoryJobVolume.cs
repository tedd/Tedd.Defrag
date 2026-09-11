using Tedd.Defrag.Core;

namespace Tedd.Defrag.Engine.FileSystems;

internal abstract class DirectoryJobVolume(VolumeInfo info) : IJobVolume
{
    public VolumeInfo Info => info;
    // Windows maintenance validates health; these implementations never move clusters.
    public bool IsDirty() => throw new NotSupportedException($"{Info.FileSystem} health is checked by Windows maintenance.");
    public abstract VolumeLayout Scan(JobRequest request, Action<double, long, string> progress, Action checkpoint,
        CancellationToken token, Action<WorkProgress>? diagnostics = null);
    public byte[] ReadBitmap(JobRequest request, Action checkpoint, CancellationToken token)
        => throw new NotSupportedException($"{Info.FileSystem} custom relocation is unavailable.");
    public void ExecuteMove(FileLayout file, PlannedMove move, PathRules rules)
        => throw new NotSupportedException($"{Info.FileSystem} custom relocation is unavailable. Use WindowsDefrag.");
    public void Dispose() { }
}
