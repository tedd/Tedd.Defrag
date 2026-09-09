using Tedd.Defrag.Core;

namespace Tedd.Defrag.Archive.V1;

public sealed class LayoutPlanner
{
    public MovePlan Plan(VolumeLayout layout, JobRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        var rules = new PathRules(request.SelectedPaths, request.Exclusions);
        var free = new FreeSpaceIndex(BitmapOperations.FreeRanges(layout.Bitmap, layout.TotalClusters));
        // Never consume MFT growth reservation, including its currently unallocated portion.
        if (layout.MftZone.Length > 0)
        {
            foreach (var range in BitmapOperations.FreeRanges(layout.Bitmap, layout.TotalClusters))
            {
                long start = Math.Max(range.Start, layout.MftZone.Start), end = Math.Min(range.End, layout.MftZone.End);
                if (end > start) free.Reserve(start, end - start);
            }
        }
        int[] order = Enumerable.Range(0, layout.Files.Length).ToArray();
        Array.Sort(order, (a, b) => Compare(layout.Files[a], layout.Files[b], request.Operation));
        var moves = new List<PlannedMove>(1024);
        long remaining = request.MaxMoveBytes / layout.Volume.BytesPerCluster, planned = 0, cursor = 0;
        long chunk = Math.Max(1, 16L * 1024 * 1024 / layout.Volume.BytesPerCluster);
        int blocked = 0, considered = 0;
        foreach (int index in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = layout.Files[index];
            if (!rules.IsSelected(file.Path)) continue;
            bool metadata = (file.Flags & StreamFlags.Metadata) != 0, directory = (file.Flags & StreamFlags.Directory) != 0;
            if (request.Operation == Operation.OptimizeMft ? (file.FileId & 0xFFFFFFFFFFFF) != 0 : metadata) continue;
            if (request.Operation == Operation.DirectoryIndexes ? !directory : directory && request.Operation != Operation.DirectoryLocality) continue;
            considered++;
            if (!file.Movable || rules.IsExcluded(file.Path)) { blocked++; continue; }
            var extents = file.Extents;
            if (extents.Length == 0) continue;
            bool pack = request.Operation is Operation.Pack or Operation.PrepareShrink;
            bool ordered = request.Operation is Operation.Alphabetical or Operation.Size or Operation.Created or Operation.Modified or Operation.Extension or Operation.DirectoryLocality;
            long total = 0;
            foreach (var e in extents) total = checked(total + e.Length);
            if (pack)
            {
                long boundary = request.Operation == Operation.PrepareShrink ? request.ShrinkBoundaryBytes / layout.Volume.BytesPerCluster : long.MaxValue;
                foreach (var e in extents.Reverse())
                {
                    long offset = boundary < e.End ? Math.Max(0, boundary - e.Lcn) : e.Length;
                    if (request.Operation == Operation.Pack) offset = 0;
                    while (offset < e.Length && remaining > 0 && moves.Count < 1024)
                    {
                        long size = Math.Min(Math.Min(chunk, e.Length - offset), remaining);
                        size = Math.Min(size, free.Largest);
                        if (size == 0) break;
                        long target = free.FindFirstFit(size, Math.Min(e.Lcn + offset, boundary));
                        if (target < 0) break;
                        Add(index, file.FileId, e.Vcn + offset, e.Lcn + offset, target, size);
                        offset += size;
                    }
                }
            }
            else
            {
                if (!ordered && request.Operation != Operation.PackAndDefrag && extents.Length < 2) continue;
                // Preserve the first extent when the entire remaining tail fits immediately after it.
                long tail = total - extents[0].Length;
                if (!ordered && extents.Length > 1 && tail <= remaining && free.Contains(extents[0].End, tail) && (tail + chunk - 1) / chunk + extents.Length <= 1024 - moves.Count)
                {
                    long destination = extents[0].End;
                    for (int i = 1; i < extents.Length; i++) MoveExtent(extents[i], ref destination);
                }
                else if (total <= remaining)
                {
                    long before = request.Operation == Operation.PackAndDefrag ? extents.Min(e => e.Lcn) : long.MaxValue;
                    long target = free.FindFirstFit(total, before, ordered ? cursor : 0);
                    long requiredMoves = total / chunk + extents.Length;
                    if (target < 0 || requiredMoves > 1024 - moves.Count) { blocked++; continue; }
                    foreach (var extent in extents) MoveExtent(extent, ref target);
                    if (ordered) cursor = target;
                }
                else blocked++;

                void MoveExtent(Extent e, ref long destination)
                {
                    for (long offset = 0; offset < e.Length;)
                    {
                        long size = Math.Min(chunk, e.Length - offset);
                        Add(index, file.FileId, e.Vcn + offset, e.Lcn + offset, destination, size);
                        offset += size; destination += size;
                    }
                }
            }
            if (moves.Count >= 1024 || remaining <= 0) break;
        }
        return new(moves.ToArray(), planned, considered, blocked,
            "Bounded free-destination plan. Existing anchors are preserved where possible; excluded objects are never moved. Constraints may prevent full packing or ordering.");

        void Add(int index, ulong id, long vcn, long source, long destination, long count)
        {
            if (!free.Reserve(destination, count)) throw new InvalidOperationException("Planner attempted an overlapping destination.");
            moves.Add(new(id, index, vcn, source, destination, count));
            planned += count; remaining -= count;
            // Source space is deliberately not recycled in this batch: failed moves cannot invalidate downstream destinations.
        }
    }
    private static int Compare(FileLayout a, FileLayout b, Operation operation) => operation switch
    {
        Operation.Alphabetical => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path),
        Operation.Extension => CompareThenPath(Path.GetExtension(a.Path), Path.GetExtension(b.Path), a, b),
        Operation.DirectoryLocality => CompareThenPath(Path.GetDirectoryName(a.Path) ?? "", Path.GetDirectoryName(b.Path) ?? "", a, b),
        Operation.Size => a.Size.CompareTo(b.Size),
        Operation.Created => a.CreatedUtcTicks.CompareTo(b.CreatedUtcTicks),
        Operation.Modified => a.ModifiedUtcTicks.CompareTo(b.ModifiedUtcTicks),
        Operation.Pack or Operation.PrepareShrink => Last(b).CompareTo(Last(a)),
        _ => Score(b).CompareTo(Score(a))
    };
    private static long Last(FileLayout f) => f.Extents.Length == 0 ? 0 : f.Extents[^1].End;
    private static double Score(FileLayout f) => (f.Extents.Length - 1d) / Math.Max(1, f.Size);
    private static int CompareThenPath(string a, string b, FileLayout fa, FileLayout fb) { int c = StringComparer.OrdinalIgnoreCase.Compare(a, b); return c == 0 ? StringComparer.OrdinalIgnoreCase.Compare(fa.Path, fb.Path) : c; }
}
