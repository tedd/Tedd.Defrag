using System.Buffers.Binary;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace Tedd.Defrag.Windows;

public static class VolumeBitmap
{
    public static long QueryTotalClusters(SafeFileHandle handle)
    {
        Span<byte> input = stackalloc byte[8]; input.Clear();
        Span<byte> output = stackalloc byte[24];
        int count = NativeIo.Control(handle, PInvoke.FSCTL_GET_VOLUME_BITMAP, input, output, out int error);
        if (error != 0 && error != 234) throw new Win32Exception(error);
        if (count < 16 || BinaryPrimitives.ReadInt64LittleEndian(output) != 0)
            throw new IOException("Invalid volume bitmap header.");
        long clusters = BinaryPrimitives.ReadInt64LittleEndian(output[8..]);
        if (clusters <= 0 || clusters > long.MaxValue - 7) throw new IOException("Invalid volume cluster count.");
        return clusters;
    }

    public static byte[] Read(SafeFileHandle handle, long totalClusters, int memoryMiB, Action<double>? progress, CancellationToken token)
    {
        long bytes = checked((totalClusters + 7) / 8);
        if ((memoryMiB > 0 && bytes > memoryMiB * 1024L * 1024 / 4) || bytes > Array.MaxLength)
            throw new IOException("Allocation bitmap exceeds the configured memory cap or runtime array limit.");
        byte[] bitmap = new byte[(int)bytes], buffer = new byte[1024 * 1024 + 16];
        Span<byte> input = stackalloc byte[8]; long next = 0;
        while (next < totalClusters)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke((double)next / totalClusters);
            BinaryPrimitives.WriteInt64LittleEndian(input, next);
            int n = NativeIo.Control(handle, PInvoke.FSCTL_GET_VOLUME_BITMAP, input, buffer, out int error);
            if (error != 0 && error != 234) throw new Win32Exception(error);
            if (n <= 16) throw new IOException("Volume bitmap query did not advance.");
            next = CopyPage(buffer.AsSpan(0, n), next, totalClusters, bitmap);
        }
        progress?.Invoke(1);
        return bitmap;
    }

    internal static long CopyPage(ReadOnlySpan<byte> page, long next, long totalClusters, Span<byte> bitmap)
    {
        if (page.Length <= 16) throw new IOException("Short volume bitmap page.");
        long start = BinaryPrimitives.ReadInt64LittleEndian(page), available = BinaryPrimitives.ReadInt64LittleEndian(page[8..]);
        long bits = Math.Min((page.Length - 16L) * 8, available);
        if (start < 0 || (start & 7) != 0 || start > next || bits <= 0 || start > long.MaxValue - bits || start + bits <= next)
            throw new IOException("Invalid bitmap offset.");
        // NTFS may round the final bitmap page beyond the geometry's cluster count.
        long end = Math.Min(totalClusters, start + bits);
        int copy = checked((int)((end - start + 7) / 8));
        page.Slice(16, copy).CopyTo(bitmap[(int)(start / 8)..]);
        return end;
    }
}
