using Tedd.Defrag.Core;
using Tedd.Defrag.Windows;
using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;

namespace Tedd.Defrag.Engine.FileSystems;

internal sealed class NtfsJobVolume(JobRequest request) : IJobVolume
{
    private readonly NtfsVolume _volume = new(request.Volume, !request.Preview && request.Operation != Operation.Analyze);
    private readonly ConcurrentBag<SafeFileHandle> _moveHandles = [];
    public VolumeInfo Info => _volume.Info;
    public bool IsDirty() => _volume.IsDirty();
    public VolumeLayout Scan(JobRequest request, Action<double, long, string> progress, Action checkpoint, CancellationToken token, Action<WorkProgress>? diagnostics = null)
        => new RawMftScanner().Scan(_volume, request, progress, checkpoint, token, diagnostics);
    public byte[] ReadBitmap(JobRequest request, Action checkpoint, CancellationToken token)
        => _volume.ReadBitmap(request.Resources.MemoryMiB, _ => checkpoint(), token);

    public void ExecuteMove(FileLayout file, PlannedMove move, PathRules rules)
    {
        SafeFileHandle? pendingHandle = null;
        try
        {
            pendingHandle = file.StreamName.Length == 0 ? _volume.OpenById(file.FileId) : NativeIo.Open(file.Path + file.StreamName);
            var identity = NtfsVolume.Identity(pendingHandle);
            string path = identity.Path;
            if (file.StreamName.Length > 0 && path.EndsWith(file.StreamName, StringComparison.OrdinalIgnoreCase)) path = path[..^file.StreamName.Length];
            if (identity.Id != file.FileId || identity.Links > 1 || !path.StartsWith(_volume.Info.Root, StringComparison.OrdinalIgnoreCase) ||
                !rules.IsSelected(path) || rules.IsExcluded(path) || (identity.Attributes & (0x400u | 0x800u | 0x200u | 0x4000u)) != 0)
                throw new IOException("Current identity, attributes, selection or exclusions prohibit this move.");
            if (!LayoutMutation.Matches(FileSystemQueries.RetrievalPointers(pendingHandle), move.Vcn, move.SourceLcn, move.Clusters))
                throw new IOException("Source extent changed since planning.");
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            pendingHandle?.Dispose();
            throw new MovePreconditionException(exception.Message, exception);
        }
        using var handle = pendingHandle!;
        // Independent file objects avoid serializing all synchronous FSCTLs on the scan handle.
        if (!_moveHandles.TryTake(out var volumeHandle)) volumeHandle = NativeIo.Open(VolumeDiscovery.Device(_volume.Info.Root), write: true);
        try { _volume.Move(handle, move, volumeHandle); }
        finally { _moveHandles.Add(volumeHandle); }
        if (!LayoutMutation.Matches(FileSystemQueries.RetrievalPointers(handle), move.Vcn, move.DestinationLcn, move.Clusters)) throw new IOException("Move result could not be verified; fresh analysis required.");
    }

    public void Dispose() { while (_moveHandles.TryTake(out var handle)) handle.Dispose(); _volume.Dispose(); }
}
