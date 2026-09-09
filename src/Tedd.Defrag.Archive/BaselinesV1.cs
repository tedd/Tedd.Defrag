using Tedd.Defrag.Core;

namespace Tedd.Defrag.Archive;

/// <summary>Frozen v1 scalar baselines. Keep for comparative benchmarks; do not optimize these methods.</summary>
public static class BaselinesV1
{
    public static long BitByBitPopcount(byte[] data)
    {
        long n = 0; foreach (byte b in data) for (int i = 0; i < 8; i++) n += (b >> i) & 1; return n;
    }
    public static long LinearFirstFit(ClusterRange[] ranges, long length)
    { foreach (var range in ranges) if (range.Length >= length) return range.Start; return -1; }
}
