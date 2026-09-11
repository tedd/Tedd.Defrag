using System.Runtime.CompilerServices;
using Tedd.Defrag.Core;
using Tedd.Defrag.Windows;
using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;

[assembly: InternalsVisibleTo("Tedd.Defrag.Tests")]

namespace Tedd.Defrag.Engine;

internal interface IJobVolume : IDisposable
{
    VolumeInfo Info { get; }
    bool IsDirty();
    VolumeLayout Scan(JobRequest request, Action<double, long, string> progress, Action checkpoint, CancellationToken token, Action<WorkProgress>? diagnostics = null);
    byte[] ReadBitmap(JobRequest request, Action checkpoint, CancellationToken token);
    void ExecuteMove(FileLayout file, PlannedMove move, PathRules rules);
}

internal static class JobVolume
{
    public static IJobVolume Open(JobRequest request)
    {
        var info = VolumeDiscovery.Get(request.Volume);
        FileSystemCapabilities.Validate(info.FileSystem, request.Operation);
        return FileSystemCapabilities.IsRefs(info.FileSystem) ? new RefsJobVolume(info) : new NativeJobVolume(request);
    }
}

internal sealed class RefsJobVolume(VolumeInfo info) : IJobVolume
{
    public VolumeInfo Info => info;
    // Windows maintenance checks volume health itself; no NTFS dirty-state query.
    public bool IsDirty() => throw new NotSupportedException("ReFS health is checked by Windows maintenance.");
    public VolumeLayout Scan(JobRequest request, Action<double, long, string> progress, Action checkpoint, CancellationToken token, Action<WorkProgress>? diagnostics = null)
        => new RefsScanner().Scan(VolumeDiscovery.Get(info.Root), request, progress, checkpoint, token, diagnostics);
    public byte[] ReadBitmap(JobRequest request, Action checkpoint, CancellationToken token)
        => throw new NotSupportedException("ReFS custom relocation is unavailable.");
    public void ExecuteMove(FileLayout file, PlannedMove move, PathRules rules)
        => throw new NotSupportedException("ReFS custom relocation is unavailable. Use WindowsDefrag.");
    public void Dispose() { }
}

internal sealed class NativeJobVolume(JobRequest request) : IJobVolume
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
        using var handle = file.StreamName.Length == 0 ? _volume.OpenById(file.FileId) : NativeIo.Open(file.Path + file.StreamName);
        var identity = NtfsVolume.Identity(handle);
        string path = identity.Path;
        if (file.StreamName.Length > 0 && path.EndsWith(file.StreamName, StringComparison.OrdinalIgnoreCase)) path = path[..^file.StreamName.Length];
        if (identity.Id != file.FileId || identity.Links > 1 || !path.StartsWith(_volume.Info.Root, StringComparison.OrdinalIgnoreCase) ||
            !rules.IsSelected(path) || rules.IsExcluded(path) || (identity.Attributes & (0x400u | 0x800u | 0x200u | 0x4000u)) != 0)
            throw new IOException("Current identity, attributes, selection or exclusions prohibit this move.");
        if (!LayoutMutation.Matches(NtfsVolume.RetrievalPointers(handle), move.Vcn, move.SourceLcn, move.Clusters)) throw new IOException("Source extent changed since planning.");
        // Independent file objects avoid serializing all synchronous FSCTLs on the scan handle.
        if (!_moveHandles.TryTake(out var volumeHandle)) volumeHandle = NativeIo.Open(VolumeDiscovery.Device(_volume.Info.Root), write: true);
        try { _volume.Move(handle, move, volumeHandle); }
        finally { _moveHandles.Add(volumeHandle); }
        if (!LayoutMutation.Matches(NtfsVolume.RetrievalPointers(handle), move.Vcn, move.DestinationLcn, move.Clusters)) throw new IOException("Move result could not be verified; fresh analysis required.");
    }

    public void Dispose() { while (_moveHandles.TryTake(out var handle)) handle.Dispose(); _volume.Dispose(); }
}
