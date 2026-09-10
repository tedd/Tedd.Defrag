using Tedd.Defrag.Core;

namespace Tedd.Defrag.Visualization;

public static class MapAggregator
{
    /// <summary>Caller owns the cells. No allocation in aggregation; cells represent mixtures, not exclusive states.</summary>
    public static void Build(VolumeLayout layout, Span<MapCell> cells, long start = 0, long length = 0)
    {
        if (cells.IsEmpty) return;
        if (length == 0) length = layout.TotalClusters - start;
        if (start < 0 || length < 0 || start > layout.TotalClusters - length) throw new ArgumentOutOfRangeException(nameof(start));
        cells.Clear();
        long step = Math.Max(1, (length + cells.Length - 1) / cells.Length), end = start + length;
        for (int i = 0; i < cells.Length; i++)
        {
            long pos = start + i * step, count = Math.Clamp(end - pos, 0, step);
            if (count == 0) continue;
            long occupied = count >= 512 && (pos & 7) == 0 && (count & 7) == 0
                ? BitmapKernel.CountAllocated(layout.Bitmap.AsSpan((int)(pos >> 3), (int)(count >> 3)))
                : BitmapOperations.CountRange(layout.Bitmap, pos, count);
            cells[i] = new(count, occupied, 0, 0, 0, 0, 0);
        }
        foreach (var file in layout.Files)
        {
            bool fragmented = file.Fragmented, metadata = (file.Flags & (StreamFlags.Metadata | StreamFlags.Directory)) != 0,
                excluded = !file.Movable || (file.Flags & StreamFlags.Excluded) != 0;
            if (!fragmented && !metadata && !excluded) continue;
            foreach (var extent in file.Extents)
            {
                if (extent.IsSparse) continue;
                long a = Math.Max(start, extent.Lcn), b = Math.Min(end, extent.End);
                if (a >= b) continue;
                int first = (int)((a - start) / step), last = (int)((b - 1 - start) / step);
                for (int i = first; i <= last && i < cells.Length; i++)
                {
                    long overlap = Math.Min(b, start + (i + 1) * step) - Math.Max(a, start + i * step);
                    ref var cell = ref cells[i];
                    cell = cell with {
                        Fragmented = Math.Min(cell.Allocated, cell.Fragmented + (fragmented ? overlap : 0)),
                        Metadata = Math.Min(cell.Allocated, cell.Metadata + (metadata ? overlap : 0)),
                        Excluded = Math.Min(cell.Allocated, cell.Excluded + (excluded ? overlap : 0)) };
                }
            }
        }
    }
    public static void Activity(Span<MapCell> cells, long totalClusters, long start, long length, bool verified)
    {
        long step = Math.Max(1, (totalClusters + cells.Length - 1) / cells.Length);
        int first = (int)(start / step), last = (int)((start + length - 1) / step);
        for (int i = Math.Max(0, first); i <= last && i < cells.Length; i++)
        {
            long n = Math.Min(start + length, (i + 1) * step) - Math.Max(start, i * step);
            cells[i] = verified ? cells[i] with { Verified = n, Moving = 0 } : cells[i] with { Moving = n };
        }
    }
    public static uint Color(in MapCell c)
    {
        if (c.Clusters == 0) return 0xFF172531;
        if (c.Moving > 0) return 0xFFFFFFFF;
        if (c.Verified > 0) return 0xFF70C94A;
        if (c.Metadata > c.Clusters / 8) return 0xFF37C3DB;
        if (c.Fragmented > c.Clusters / 8) return 0xFFF3BF3E;
        if (c.Excluded > c.Clusters / 3) return 0xFF8295A3;
        if (c.Allocated == 0) return 0xFF223646;
        float fill = (float)c.Allocated / c.Clusters;
        return 0xFF000000 | (uint)(27 + 2 * fill) << 16 | (uint)(72 + 67 * fill) << 8 | (uint)(105 + 130 * fill);
    }
}
