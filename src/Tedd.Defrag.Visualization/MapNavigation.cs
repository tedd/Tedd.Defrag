using Tedd.Defrag.Core;

namespace Tedd.Defrag.Visualization;

public static class MapNavigation
{
    public static ClusterRange Zoom(long total, long start, long length, long anchor, bool inward)
    {
        if (total <= 0 || start < 0 || length <= 0 || length > total - start)
            throw new ArgumentOutOfRangeException(nameof(length));
        anchor = Math.Clamp(anchor, start, start + length - 1);
        long count = inward ? length / 2 + length % 2 : length > total / 2 ? total : length * 2;
        long offset = (long)((Int128)(anchor - start) * count / length);
        return new(Math.Clamp(anchor - offset, 0, total - count), count);
    }

    /// <summary>Projects whole-volume cell densities into a viewport. Subcell locations are estimates.</summary>
    public static MapCell[] Project(ReadOnlySpan<MapCell> source, long total, long start, long length, int cellCount)
    {
        if (total <= 0 || start < 0 || length <= 0 || length > total - start || cellCount is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(length));
        cellCount = (int)Math.Min(length, cellCount);
        var cells = new MapCell[cellCount];
        long sourceStep = source.IsEmpty ? total : (total - 1) / source.Length + 1;
        long step = (length - 1) / cellCount + 1, end = start + length;
        for (int i = 0; i < cells.Length; i++)
        {
            long offset = (long)i * step;
            if (offset >= length) break;
            long position = start + offset;
            long limit = position + Math.Min(step, end - position);
            long allocated = 0, fragmented = 0, metadata = 0, excluded = 0, moving = 0, verified = 0, unknown = 0;
            for (long cursor = position; cursor < limit;)
            {
                int index = (int)(cursor / sourceStep);
                long sourceStart = (long)index * sourceStep;
                long next = cursor + Math.Min(limit - cursor, sourceStep - (cursor - sourceStart));
                if (index >= source.Length || source[index].Clusters <= cursor - sourceStart)
                    unknown += next - cursor;
                else
                {
                    var cell = source[index];
                    long a = cursor - sourceStart, b = Math.Min(next - sourceStart, cell.Clusters);
                    allocated += Portion(cell.Allocated, cell.Clusters, a, b);
                    fragmented += Portion(cell.Fragmented, cell.Clusters, a, b);
                    metadata += Portion(cell.Metadata, cell.Clusters, a, b);
                    excluded += Portion(cell.Excluded, cell.Clusters, a, b);
                    moving += Portion(cell.Moving, cell.Clusters, a, b);
                    verified += Portion(cell.Verified, cell.Clusters, a, b);
                    unknown += Portion(cell.Unknown, cell.Clusters, a, b) + (next - sourceStart - b);
                }
                cursor = next;
            }
            long count = limit - position;
            allocated = Math.Min(allocated, count - unknown);
            cells[i] = new(count, allocated, Math.Min(allocated, fragmented), Math.Min(allocated, metadata),
                Math.Min(allocated, excluded), Math.Min(count, moving), Math.Min(count, verified), unknown);
        }
        return cells;
    }

    private static long Portion(long value, long clusters, long a, long b)
        => (long)((Int128)value * b / clusters - (Int128)value * a / clusters);
}
