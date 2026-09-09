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
    {
        long bytes = (TotalClusters + 7) / 8;
        if ((memoryMiB > 0 && bytes > memoryMiB * 1024L * 1024 / 4) || bytes > Array.MaxLength) throw new IOException("Allocation bitmap exceeds the configured memory cap or runtime array limit.");
        byte[] bitmap = new byte[(int)bytes], buffer = new byte[1024 * 1024 + 16];
        Span<byte> input = stackalloc byte[8]; long next = 0;
        while (next < TotalClusters)
        {
            token.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt64LittleEndian(input, next);
            int n = NativeIo.Control(Handle, PInvoke.FSCTL_GET_VOLUME_BITMAP, input, buffer, out int error);
            if (error != 0 && error != 234) throw new Win32Exception(error);
            if (n <= 16) throw new IOException("Volume bitmap query did not advance.");
            long start = BinaryPrimitives.ReadInt64LittleEndian(buffer), available = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8));
            long bits = Math.Min((n - 16L) * 8, available);
            if (start < 0 || (start & 7) != 0 || start > next || start + bits <= next) throw new IOException("Invalid bitmap offset.");
            int copy = (int)Math.Min(n - 16L, bitmap.LongLength - start / 8);
            buffer.AsSpan(16, copy).CopyTo(bitmap.AsSpan((int)(start / 8)));
            next = Math.Min(TotalClusters, start + bits); progress?.Invoke((double)next / TotalClusters);
        }
        return bitmap;
    }
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
    public static Extent[] RetrievalPointers(SafeFileHandle file)
    {
        byte[] buffer = new byte[65536]; Span<byte> input = stackalloc byte[8]; long vcn = 0;
        var extents = new List<Extent>();
        while (true)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input, vcn);
            int n = NativeIo.Control(file, PInvoke.FSCTL_GET_RETRIEVAL_POINTERS, input, buffer, out int error);
            if (error == 38) return extents.ToArray(); // Resident or empty.
            if (error != 0 && error != 234) throw new Win32Exception(error);
            if (n < 16) throw new IOException("Short retrieval-pointer response.");
            int count = BinaryPrimitives.ReadInt32LittleEndian(buffer); long current = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8));
            if (count <= 0 || count > (n - 16) / 16 || current > vcn || current < 0) throw new IOException("Invalid retrieval-pointer response.");
            for (int i = 0; i < count; i++)
            {
                long next = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16 + i * 16)), lcn = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24 + i * 16));
                if (next <= current || lcn < -1) throw new IOException("Invalid retrieval extent.");
                extents.Add(new(current, lcn, next - current)); current = next;
            }
            if (current <= vcn) throw new IOException("Retrieval-pointer query did not advance.");
            vcn = current;
            if (error == 0) return extents.ToArray();
            if (extents.Count > 1000000) throw new IOException("Extent count exceeds the scan limit.");
        }
    }
    public static (ulong Id, uint Attributes, uint Links, string Path) Identity(SafeFileHandle file)
    {
        if (!PInvoke.GetFileInformationByHandle(file, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        char[] path = new char[32768]; uint length = PInvoke.GetFinalPathNameByHandle(file, path, 0);
        if (length == 0 || length >= path.Length) throw new Win32Exception(Marshal.GetLastWin32Error());
        string resolved = new(path, 0, (int)length);
        if (resolved.StartsWith(@"\\?\")) resolved = resolved[4..];
        return (((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow, info.dwFileAttributes, info.nNumberOfLinks, resolved);
    }
    public void Move(SafeFileHandle file, PlannedMove move)
    {
        if (move.Clusters is <= 0 or > uint.MaxValue || move.DestinationLcn < 0 || move.DestinationLcn > TotalClusters - move.Clusters)
            throw new ArgumentOutOfRangeException(nameof(move));
        MOVE_FILE_DATA data = new() { FileHandle = new(file.DangerousGetHandle()), StartingVcn = move.Vcn, StartingLcn = move.DestinationLcn, ClusterCount = (uint)move.Clusters };
        var input = new ReadOnlySpan<byte>(&data, sizeof(MOVE_FILE_DATA));
        NativeIo.Control(Handle, PInvoke.FSCTL_MOVE_FILE, input, [], out int error);
        GC.KeepAlive(file);
        if (error != 0) throw new Win32Exception(error);
    }
    public void Dispose() => Handle.Dispose();
}
