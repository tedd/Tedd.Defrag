using System.Runtime.CompilerServices;
using Tedd.Defrag.Core;
using Tedd.Defrag.Core.FileSystems;

[assembly: InternalsVisibleTo("Tedd.Defrag.Tests")]

namespace Tedd.Defrag.Engine;

internal interface IJobVolume : IDisposable
{
    VolumeInfo Info { get; }
    FileSystemSupport FileSystem => FileSystemCapabilities.Get(Info.FileSystem);
    bool IsDirty();
    VolumeLayout Scan(JobRequest request, Action<double, long, string> progress, Action checkpoint, CancellationToken token, Action<WorkProgress>? diagnostics = null);
    byte[] ReadBitmap(JobRequest request, Action checkpoint, CancellationToken token);
    void ExecuteMove(FileLayout file, PlannedMove move, PathRules rules);
}
