using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tedd.Defrag.Core;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace Tedd.Defrag.Windows;

public sealed unsafe class NtfsVolume : IDisposable
{
    public SafeFileHandle Handle { get; }
    public VolumeInfo Info { get; }
    public long TotalClusters { get; }
    public int RecordSize { get; }
    public int SectorSize { get; }
    public long MftLength { get; }
    public ClusterRange MftZone { get; }
    public NtfsVolume(string root, bool write = false)
    {
        Info = VolumeDiscovery.Get(root);
        if (!Info.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Custom scanning and relocation require NTFS.");
        Handle = NativeIo.Open(VolumeDiscovery.Device(root), write);
        try
        {
            Span<byte> data = stackalloc byte[128];
            int n = NativeIo.Control(Handle, PInvoke.FSCTL_GET_NTFS_VOLUME_DATA, [], data);
            var geometry = NtfsGeometry.Parse(data[..n]);
            TotalClusters = geometry.TotalClusters;
            SectorSize = geometry.SectorSize;
            RecordSize = geometry.RecordSize;
            MftLength = geometry.MftLength;
            Info = Info with { BytesPerCluster = geometry.ClusterSize };
            MftZone = geometry.MftZone;
        }
        catch { Handle.Dispose(); throw; }
    }
    public bool IsDirty()
    {
        Span<byte> output = stackalloc byte[4];
        int n = NativeIo.Control(Handle, PInvoke.FSCTL_IS_VOLUME_DIRTY, [], output);
        if (n < 4) throw new IOException("Cannot determine volume dirty state.");
        return (BinaryPrimitives.ReadUInt32LittleEndian(output) & 1) != 0;
    }
    public byte[] ReadBitmap(int memoryMiB, Action<double>? progress, CancellationToken token)
        => VolumeBitmap.Read(Handle, TotalClusters, memoryMiB, progress, token);
    /// <summary>Read a filesystem-restored record. Use TryParseFileSystemRecord, never raw-record fixups.</summary>
    public byte[] ReadRecord(long number)
    {
        Span<byte> input = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(input, number);
        byte[] output = new byte[RecordSize + 16];
        int n = NativeIo.Control(Handle, PInvoke.FSCTL_GET_NTFS_FILE_RECORD, input, output);
        if (n < 12 + RecordSize || (BinaryPrimitives.ReadInt64LittleEndian(output) & 0xFFFFFFFFFFFF) != number || BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8)) != RecordSize)
            throw new IOException($"NTFS record {number} was unavailable.");
        return output.AsSpan(12, RecordSize).ToArray();
    }
    public SafeFileHandle OpenById(ulong id)
    {
        FILE_ID_DESCRIPTOR descriptor = new() { dwSize = (uint)sizeof(FILE_ID_DESCRIPTOR), Type = FILE_ID_TYPE.FileIdType };
        descriptor.FileId = unchecked((long)id);
        var file = PInvoke.OpenFileById(Handle, in descriptor, 0x80000000,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT);
        if (file.IsInvalid) { int error = Marshal.GetLastWin32Error(); file.Dispose(); throw new Win32Exception(error); }
        return file;
    }
    public static (ulong Id, uint Attributes, uint Links, string Path) Identity(SafeFileHandle file)
    {
        if (!PInvoke.GetFileInformationByHandle(file, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow, info.dwFileAttributes, info.nNumberOfLinks, FileSystemQueries.FinalPath(file));
    }
    public void Move(SafeFileHandle file, PlannedMove move, SafeFileHandle? volumeHandle = null)
    {
        if (move.Clusters is <= 0 or > uint.MaxValue || move.DestinationLcn < 0 || move.DestinationLcn > TotalClusters - move.Clusters)
            throw new ArgumentOutOfRangeException(nameof(move));
        MOVE_FILE_DATA data = new() { FileHandle = new(file.DangerousGetHandle()), StartingVcn = move.Vcn, StartingLcn = move.DestinationLcn, ClusterCount = (uint)move.Clusters };
        var input = new ReadOnlySpan<byte>(&data, sizeof(MOVE_FILE_DATA));
        NativeIo.Control(volumeHandle ?? Handle, PInvoke.FSCTL_MOVE_FILE, input, [], out int error);
        GC.KeepAlive(file);
        if (error != 0) throw new Win32Exception(error);
    }
    public void Dispose() => Handle.Dispose();
}
