using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tedd.Defrag.Core;
using Windows.Win32;

namespace Tedd.Defrag.Windows;

public static class FileSystemQueries
{
    public static uint Attributes(SafeFileHandle file)
    {
        if (!PInvoke.GetFileInformationByHandle(file, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.dwFileAttributes;
    }

    public static string FinalPath(SafeFileHandle file)
    {
        char[] path = new char[32768]; uint length = PInvoke.GetFinalPathNameByHandle(file, path, 0);
        if (length == 0 || length >= path.Length) throw new Win32Exception(Marshal.GetLastWin32Error());
        string resolved = new(path, 0, (int)length);
        return resolved.StartsWith(@"\\?\") ? resolved[4..] : resolved;
    }

    public static Extent[] RetrievalPointers(SafeFileHandle file, CancellationToken token = default, Action? checkpoint = null)
    {
        byte[] buffer = new byte[65536]; Span<byte> input = stackalloc byte[8]; long vcn = 0;
        var extents = new List<Extent>();
        while (true)
        {
            token.ThrowIfCancellationRequested(); checkpoint?.Invoke();
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
    internal static Extent[] NormalizeExtents(Extent[] extents, long totalClusters)
    {
        var result = new List<Extent>();
        long nextVcn = 0;
        foreach (var extent in extents)
        {
            if (extent.Vcn != nextVcn || extent.Length <= 0 || extent.Lcn < -1 ||
                extent.Vcn > long.MaxValue - extent.Length || !extent.IsSparse && extent.Lcn > totalClusters - extent.Length)
                throw new IOException("Invalid retrieval extent.");
            nextVcn = extent.Vcn + extent.Length;
            if (result.Count > 0 && !extent.IsSparse && !result[^1].IsSparse && result[^1].End == extent.Lcn)
                result[^1] = result[^1] with { Length = checked(result[^1].Length + extent.Length) };
            else result.Add(extent);
        }
        return result.ToArray();
    }
}
