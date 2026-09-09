using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Tedd.Defrag.Core;

namespace Tedd.Defrag.Visualization;

public static class BitmapKernel
{
    private static readonly Vector256<byte> Lookup = Vector256.Create(
        (byte)0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4,
        0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4);
    public static unsafe long CountAllocated(ReadOnlySpan<byte> data)
    {
        if (!Avx2.IsSupported || data.Length < 64) return BitmapOperations.CountAllocated(data);
        var mask = Vector256.Create((byte)15);
        var sums = Vector256<ulong>.Zero;
        int i = 0;
        fixed (byte* p = data)
        {
            for (; i <= data.Length - 32; i += 32)
            {
                var bytes = Avx.LoadVector256(p + i);
                var lo = Avx2.And(bytes, mask);
                var hi = Avx2.And(Avx2.ShiftRightLogical(bytes.AsUInt16(), 4).AsByte(), mask);
                var counts = Avx2.Add(Avx2.Shuffle(Lookup, lo), Avx2.Shuffle(Lookup, hi));
                sums = Avx2.Add(sums, Avx2.SumAbsoluteDifferences(counts, Vector256<byte>.Zero).AsUInt64());
            }
        }
        return checked((long)(sums.GetElement(0) + sums.GetElement(1) + sums.GetElement(2) + sums.GetElement(3))) + BitmapOperations.CountAllocated(data[i..]);
    }
}
