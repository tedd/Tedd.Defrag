using Tedd.Defrag.Core;

namespace Tedd.Defrag.Visualization;

/// <summary>Accumulates scan deltas without revisiting previously classified files.</summary>
public sealed class ScanMapBuilder
{
    private readonly MapCell[] cells;
    private readonly long step;
    private long knownClusters;
    public long TotalClusters { get; }

    public ScanMapBuilder(long totalClusters, MapCell[] cells)
    {
        if (totalClusters <= 0 || cells.Length == 0) throw new ArgumentOutOfRangeException(nameof(totalClusters));
        TotalClusters = totalClusters;
        this.cells = cells;
        step = Math.Max(1, (totalClusters + cells.Length - 1) / cells.Length);
        for (int i = 0; i < cells.Length; i++)
        {
            long count = Math.Clamp(totalClusters - i * step, 0, step);
            cells[i] = new(count, 0, 0, 0, 0, 0, 0, count);
        }
    }

    public void Apply(ScanMapUpdate update)
    {
        if (update.TotalClusters != TotalClusters) throw new ArgumentException("Scan geometry changed.", nameof(update));
        if (update.Bitmap is { } bitmap)
        {
            long end = update.BitmapClusters;
            if (end < knownClusters || end > TotalClusters || (bitmap.LongLength * 8 < end))
                throw new ArgumentOutOfRangeException(nameof(update));
            for (long pos = knownClusters; pos < end;)
            {
                int index = (int)(pos / step);
                long count = Math.Min(end - pos, step - pos % step);
                ref var cell = ref cells[index];
                cell = cell with { Allocated = cell.Allocated + BitmapOperations.CountRange(bitmap, pos, count), Unknown = cell.Unknown - count };
                pos += count;
            }
            knownClusters = end;
        }
        if (update.File is { } file) MapAggregator.AddFile(file, cells, TotalClusters);
    }
}
