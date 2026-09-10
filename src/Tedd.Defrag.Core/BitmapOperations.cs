using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics;

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
    public static IEnumerable<ClusterRange> FreeRanges(byte[] bitmap, long clusters, CancellationToken token = default, Action<long>? progress = null)
    {
        if (clusters < 0 || clusters > bitmap.LongLength * 8) throw new ArgumentOutOfRangeException(nameof(clusters));
        long start = -1, i = 0, nextCheckpoint = 0;
        while (i < clusters)
        {
            if (i >= nextCheckpoint) { token.ThrowIfCancellationRequested(); progress?.Invoke(i); nextCheckpoint = i + 1024 * 1024; }
            // Skip homogeneous SIMD blocks. Mixed blocks retain the exact scalar boundary logic.
            int uniform = (i & 127) == 0 ? UniformBytes(bitmap, (int)(i >> 3), (clusters - i) >> 3) : 0;
            if (uniform > 0)
            {
                if (bitmap[(int)(i >> 3)] == 0) { if (start < 0) start = i; }
                else if (start >= 0) { yield return new(start, i - start); start = -1; }
                i += uniform * 8; continue;
            }
            // Skip uniform machine words; no per-cluster work across long occupied/free spans.
            if ((i & 63) == 0 && i + 64 <= clusters)
            {
                ulong word = BinaryPrimitives.ReadUInt64LittleEndian(bitmap.AsSpan((int)(i >> 3)));
                if (word == 0) { if (start < 0) start = i; i += 64; continue; }
                if (word == ulong.MaxValue) { if (start >= 0) { yield return new(start, i - start); start = -1; } i += 64; continue; }
                // Walk transitions within a mixed word instead of repeating SIMD/checkpoint
                // dispatch at every cluster. TrailingZeroCount has a portable fallback.
                int remaining = 64;
                while (remaining > 0)
                {
                    bool allocated = (word & 1) != 0;
                    int run = Math.Min(remaining, BitOperations.TrailingZeroCount(allocated ? ~word : word));
                    if (!allocated) { if (start < 0) start = i; }
                    else if (start >= 0) { yield return new(start, i - start); start = -1; }
                    remaining -= run; i += run; word >>= run;
                }
                continue;
            }
            if (!IsSet(bitmap, i)) { if (start < 0) start = i; }
            else if (start >= 0) { yield return new(start, i - start); start = -1; }
            i++;
        }
        if (start >= 0) yield return new(start, clusters - start);
        token.ThrowIfCancellationRequested(); progress?.Invoke(clusters);
    }
    private static int UniformBytes(byte[] bitmap, int offset, long remaining)
    {
        if (Vector256.IsHardwareAccelerated && remaining >= 32)
        {
            var value = Vector256.LoadUnsafe(ref bitmap[offset]);
            if (Vector256.EqualsAll(value, Vector256<byte>.Zero) || Vector256.EqualsAll(value, Vector256.Create(byte.MaxValue))) return 32;
        }
        else if (Vector128.IsHardwareAccelerated && remaining >= 16)
        {
            var value = Vector128.LoadUnsafe(ref bitmap[offset]);
            if (Vector128.EqualsAll(value, Vector128<byte>.Zero) || Vector128.EqualsAll(value, Vector128.Create(byte.MaxValue))) return 16;
        }
        return 0;
    }
}
