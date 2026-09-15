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

/// <summary>The native relocation request was rejected before it was submitted.</summary>
internal sealed class MovePreconditionException(string message, Exception? innerException = null) : IOException(message, innerException);
