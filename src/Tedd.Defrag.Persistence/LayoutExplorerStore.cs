using System.Text;
using Tedd.Defrag.Core;

namespace Tedd.Defrag.Persistence;

/// <summary>Holds a worker's detailed layout in memory and returns bounded region queries.</summary>
public sealed class LayoutExplorerStore(JobStore store)
{
    private const int MaximumFiles = 500;
    private readonly object _sync = new();
    private readonly Queue<ClusterRange> _activity = new();
    private VolumeLayout? _layout;

    public void Save(Guid id, VolumeLayout layout)
    {
        var target = store.Layouts.GetOrAdd(id, _ => new LayoutExplorerStore(store));
        lock (target._sync)
        {
            // Records and extent arrays are immutable. The executor replaces entries and bitmap bits.
            target._layout = layout with { Bitmap = (byte[])layout.Bitmap.Clone(), Files = (FileLayout[])layout.Files.Clone() };
            target._activity.Clear();
        }
    }

    public void Clear(Guid id) => store.Layouts.TryRemove(id, out _);

    public MapRegion Explore(Guid id, long start, long length, int cellCount, bool includeFiles,
        string? selectedPath = null, ulong? selectedFileId = null, string? selectedStream = null)
    {
        if (id == Guid.Empty || cellCount is < 0 or > 65536 || selectedPath?.Length > 32760)
            throw new ArgumentException("Invalid layout query.");
        if (!store.Layouts.TryGetValue(id, out var target))
            throw new InvalidOperationException("The detailed map is no longer in memory. Analyze the volume again.");
        lock (target._sync)
            return Query(target._layout!, target._activity, start, length, cellCount, includeFiles, selectedPath, selectedFileId, selectedStream);
    }

    private static MapRegion Query(VolumeLayout layout, IEnumerable<ClusterRange> activity, long start, long length, int cellCount, bool includeFiles,
        string? selectedPath, ulong? selectedFileId, string? selectedStream)
    {
        long totalClusters = layout.TotalClusters;
        if (totalClusters <= 0 || totalClusters > (long)layout.Bitmap.Length * 8)
            throw new InvalidDataException("The detailed layout geometry is invalid.");
        start = Math.Clamp(start, 0, totalClusters - 1);
        length = length <= 0 ? totalClusters - start : Math.Clamp(length, 1, totalClusters - start);
        long end = start + length;
        var cells = new MapCell[cellCount];
        long step = cellCount == 0 ? 1 : Math.Max(1, (length + cellCount - 1) / cellCount);
        for (int i = 0; i < cells.Length; i++)
        {
            long position = start + i * step, count = Math.Clamp(end - position, 0, step);
            if (count > 0) cells[i] = new(count, BitmapOperations.CountRange(layout.Bitmap, position, count), 0, 0, 0, 0, 0);
        }
        var files = new List<ClusterFile>();
        int matchingFiles = 0, returnedTextBytes = 0;
        MapFileSelection? selection = null;
        foreach (var file in layout.Files)
        {
            long clusters = 0, overlap = 0;
            foreach (var extent in file.Extents)
            {
                if (extent.IsSparse) continue;
                clusters += extent.Length;
                overlap += Math.Max(0, Math.Min(extent.End, end) - Math.Max(extent.Lcn, start));
                AddSemantic(cells, start, length, extent, file.Fragmented, file.Flags);
            }
            if (includeFiles && overlap > 0)
            {
                matchingFiles++;
                int textBytes = Encoding.UTF8.GetByteCount(file.Path) + Encoding.UTF8.GetByteCount(file.StreamName);
                if (files.Count < MaximumFiles && returnedTextBytes + textBytes <= 1_000_000)
                {
                    files.Add(new(file.FileId, file.Path, file.StreamName, file.Size, clusters, overlap, file.Extents.Length, Status(file)));
                    returnedTextBytes += textBytes;
                }
            }
            bool identityMatch = selectedFileId == file.FileId && string.Equals(file.StreamName, selectedStream ?? "", StringComparison.Ordinal);
            bool pathMatch = selectedPath != null && string.Equals(file.Path, selectedPath, StringComparison.OrdinalIgnoreCase);
            if (identityMatch || pathMatch)
            {
                var ranges = file.Extents.Where(e => !e.IsSparse).Take(65536).Select(e => new ClusterRange(e.Lcn, e.Length)).ToArray();
                var candidate = new MapFileSelection(file.FileId, file.Path, file.StreamName, file.Size, clusters,
                    file.Extents.Length, Status(file), ranges);
                if (identityMatch || selection == null || selection.Stream.Length > 0 && file.StreamName.Length == 0) selection = candidate;
            }
        }
        foreach (var range in activity)
        {
            if (cellCount == 0) break;
            long a = Math.Max(start, range.Start), b = Math.Min(end, range.End);
            if (a >= b) continue;
            for (int i = (int)((a - start) / step); i < cellCount && start + i * step < b; i++)
                cells[i] = cells[i] with { Verified = Math.Min(b, start + (i + 1) * step) - Math.Max(a, start + i * step) };
        }
        return new(start, length, totalClusters, cells, files.ToArray(), matchingFiles, selection);
    }

    internal void Apply(PlannedMove move)
    {
        lock (_sync)
        {
            if (_layout == null || (uint)move.FileIndex >= (uint)_layout.Files.Length) return;
            var file = _layout.Files[move.FileIndex];
            if (file.FileId != move.FileId) return;
            var updated = new List<Extent>(file.Extents.Length + 2);
            foreach (var extent in file.Extents)
            {
                long a = Math.Max(extent.Vcn, move.Vcn), b = Math.Min(extent.Vcn + extent.Length, move.Vcn + move.Clusters);
                if (a >= b) { updated.Add(extent); continue; }
                if (a > extent.Vcn) updated.Add(new(extent.Vcn, extent.Lcn, a - extent.Vcn));
                updated.Add(new(a, move.DestinationLcn + a - move.Vcn, b - a));
                if (b < extent.Vcn + extent.Length) updated.Add(new(b, extent.Lcn + b - extent.Vcn, extent.Vcn + extent.Length - b));
            }
            var merged = new List<Extent>(updated.Count);
            foreach (var extent in updated)
            {
                if (merged.Count > 0 && merged[^1] is var previous && previous.Vcn + previous.Length == extent.Vcn &&
                    previous.Lcn >= 0 && extent.Lcn == previous.Lcn + previous.Length)
                    merged[^1] = previous with { Length = previous.Length + extent.Length };
                else merged.Add(extent);
            }
            _layout.Files[move.FileIndex] = file with { Extents = merged.ToArray() };
            BitmapOperations.SetRange(_layout.Bitmap, move.SourceLcn, move.Clusters, false);
            BitmapOperations.SetRange(_layout.Bitmap, move.DestinationLcn, move.Clusters, true);
            if (_activity.Count == 256) _activity.Dequeue();
            _activity.Enqueue(new(move.DestinationLcn, move.Clusters));
        }
    }

    private static string Status(FileLayout file) => file.Movable ? "Eligible" : file.Flags.ToString();

    private static void AddSemantic(MapCell[] cells, long start, long length, Extent extent, bool fragmented, StreamFlags flags)
    {
        if (cells.Length == 0 || extent.IsSparse) return;
        long a = Math.Max(start, extent.Lcn), b = Math.Min(start + length, extent.End);
        if (a >= b) return;
        long step = Math.Max(1, (length + cells.Length - 1) / cells.Length);
        int first = (int)((a - start) / step), last = (int)((b - 1 - start) / step);
        bool metadata = (flags & (StreamFlags.Metadata | StreamFlags.Directory)) != 0;
        bool excluded = (flags & (StreamFlags.Sparse | StreamFlags.Compressed | StreamFlags.Encrypted | StreamFlags.ReparsePoint |
            StreamFlags.Incomplete | StreamFlags.HardLinked | StreamFlags.Excluded | StreamFlags.Resident | StreamFlags.AnalysisOnly)) != 0;
        for (int i = first; i <= last && i < cells.Length; i++)
        {
            long overlap = Math.Min(b, start + (i + 1) * step) - Math.Max(a, start + i * step);
            ref var cell = ref cells[i];
            cell = cell with
            {
                Fragmented = Math.Min(cell.Allocated, cell.Fragmented + (fragmented ? overlap : 0)),
                Metadata = Math.Min(cell.Allocated, cell.Metadata + (metadata ? overlap : 0)),
                Excluded = Math.Min(cell.Allocated, cell.Excluded + (excluded ? overlap : 0))
            };
        }
    }
}
