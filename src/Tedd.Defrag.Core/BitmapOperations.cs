using System.Buffers.Binary;
using System.Numerics;

namespace Tedd.Defrag.Core;

public static class BitmapOperations
{
    public static bool IsSet(ReadOnlySpan<byte> bitmap, long cluster) => (bitmap[checked((int)(cluster >> 3))] & (1 << (int)(cluster & 7))) != 0;
    public static long CountAllocated(ReadOnlySpan<byte> data)
    {
        long count = 0;
        while (data.Length >= 8) { count += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(data)); data = data[8..]; }
        foreach (var value in data) count += BitOperations.PopCount((uint)value);
        return count;
    }
    public static long CountRange(ReadOnlySpan<byte> bitmap, long start, long length)
    {
        long end = checked(start + length), count = 0;
        if (start < 0 || length < 0 || end > (long)bitmap.Length * 8) throw new ArgumentOutOfRangeException(nameof(length));
        while (start < end && (start & 7) != 0) { if (IsSet(bitmap, start)) count++; start++; }
        long bytes = (end - start) >> 3;
        count += CountAllocated(bitmap.Slice((int)(start >> 3), (int)bytes)); start += bytes * 8;
        while (start < end) { if (IsSet(bitmap, start)) count++; start++; }
        return count;
    }
    public static void SetRange(Span<byte> bitmap, long start, long length, bool allocated)
    {
        long end = checked(start + length);
        if (start < 0 || length < 0 || end > (long)bitmap.Length * 8) throw new ArgumentOutOfRangeException(nameof(length));
        while (start < end && (start & 7) != 0) SetBit(bitmap, start++, allocated);
        int count = (int)((end - start) >> 3);
        bitmap.Slice((int)(start >> 3), count).Fill(allocated ? (byte)255 : (byte)0); start += (long)count * 8;
        while (start < end) SetBit(bitmap, start++, allocated);
    }
    private static void SetBit(Span<byte> bitmap, long cluster, bool value)
    {
        int i = (int)(cluster >> 3); byte mask = (byte)(1 << (int)(cluster & 7));
        if (value) bitmap[i] |= mask; else bitmap[i] &= (byte)~mask;
    }
    public static IEnumerable<ClusterRange> FreeRanges(byte[] bitmap, long clusters)
    {
        long start = -1, i = 0;
        while (i < clusters)
        {
            // Skip uniform machine words; no per-cluster work across long occupied/free spans.
            if ((i & 63) == 0 && i + 64 <= clusters)
            {
                ulong word = BinaryPrimitives.ReadUInt64LittleEndian(bitmap.AsSpan((int)(i >> 3)));
                if (word == 0) { if (start < 0) start = i; i += 64; continue; }
                if (word == ulong.MaxValue) { if (start >= 0) { yield return new(start, i - start); start = -1; } i += 64; continue; }
            }
            if (!IsSet(bitmap, i)) { if (start < 0) start = i; }
            else if (start >= 0) { yield return new(start, i - start); start = -1; }
            i++;
        }
        if (start >= 0) yield return new(start, clusters - start);
    }
}
