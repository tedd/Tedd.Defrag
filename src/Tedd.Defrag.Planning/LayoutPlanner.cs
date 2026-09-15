using Tedd.Defrag.Core;
using System.Buffers;
using System.Diagnostics;

namespace Tedd.Defrag.Planning;

public sealed class LayoutPlanner
{
    public MovePlan Plan(VolumeLayout layout, JobRequest request, CancellationToken cancellationToken = default, PlanningSession? session = null,
        Action<WorkProgress>? progress = null, Action? checkpoint = null)
    {
        request.Validate();
        var watch = Stopwatch.StartNew(); long lastReport = -1000;
        int peakWorkers = 0;
        int workers = WorkerPolicy.PlanningWorkers(request.Resources, layout.Files.Length);
        void Checkpoint() { cancellationToken.ThrowIfCancellationRequested(); checkpoint?.Invoke(); }
        void Report(string phase, long done, long total, string unit, int active = 1, int peak = 1, bool force = false)
        {
            peakWorkers = Math.Max(peakWorkers, peak);
            if (!force && watch.ElapsedMilliseconds - lastReport < 200) return;
            progress?.Invoke(new(phase, done, total, unit, phase == "Sorting candidates" ? workers : 1,
                active, peak, ElapsedMilliseconds: watch.ElapsedMilliseconds,
                Acceleration: phase == "Indexing free space" ? WorkerPolicy.BitmapAcceleration :
                    phase == "Ordering physical extents" ? "Stable 16-bit radix sort over contiguous extent records" : "Scalar CPU",
                Detail: phase == "Sorting candidates" ? "Parallel partition sort; deterministic merge. Small inventories use one worker." :
                    phase is "Ordering physical extents" or "Continuing pack plan"
                        ? "Tail sources and leading free ranges are retained as monotonic cursors across execution windows."
                        : "Destination reservations are serial; each destination is unique and source space is retained until the batch finishes."));
            lastReport = watch.ElapsedMilliseconds;
        }
        var rules = new PathRules(request.SelectedPaths, request.Exclusions);
        IEnumerable<ClusterRange> Ranges()
        {
            foreach (var range in BitmapOperations.FreeRanges(layout.Bitmap, layout.TotalClusters, cancellationToken,
                n => { Checkpoint(); Report("Indexing free space", n, layout.TotalClusters, "clusters"); }))
            {
                // Exclude the growth reservation while indexing, avoiding a second bitmap scan.
                if (layout.MftZone.Length == 0 || range.End <= layout.MftZone.Start || range.Start >= layout.MftZone.End) yield return range;
                else
                {
                    if (range.Start < layout.MftZone.Start) yield return new(range.Start, layout.MftZone.Start - range.Start);
                    if (range.End > layout.MftZone.End) yield return new(layout.MftZone.End, range.End - layout.MftZone.End);
                }
            }
        }

        if (request.Operation == Operation.Pack)
        {
            const int maximumMoves = 1024;
            var pack = session?.PackPlan(layout.Files);
            if (pack == null)
            {
                Report("Indexing free space", 0, layout.TotalClusters, "clusters", force: true);
                var destinations = Ranges().ToArray();
                Report("Indexing physical extents", 0, layout.Files.Length, "files", force: true);
                pack = PackPlanningState.Create(layout, request, rules, session?.BlockedFiles, destinations,
                    done => Report("Indexing physical extents", done, layout.Files.Length, "files"),
                    (done, total) => Report("Ordering physical extents", done, total, "radix entries", force: done == 0 || done == total),
                    Checkpoint, cancellationToken);
                session?.RememberPackPlan(layout.Files, pack);
            }
            else Report("Continuing pack plan", pack.SourcesConsumed, pack.SourceCount, "physical extents", active: 0, peak: 0, force: true);

            long packRemaining = request.MaxMoveBytes == 0 ? long.MaxValue : request.MaxMoveBytes / layout.Volume.BytesPerCluster;
            long packChunk = Math.Max(1, 16L * 1024 * 1024 / layout.Volume.BytesPerCluster);
            var plan = pack.NextBatch(packRemaining, packChunk, maximumMoves);
            progress?.Invoke(new("Plan ready", pack.SourcesConsumed, pack.SourceCount, "physical extents", 1, 0, peakWorkers,
                ElapsedMilliseconds: watch.ElapsedMilliseconds, Acceleration: WorkerPolicy.BitmapAcceleration + "; contiguous radix-ordered extents",
                Detail: $"{plan.Moves.Length:N0} moves, {plan.ClustersToMove:N0} clusters, {plan.FilesBlocked:N0} constrained files. Physical cursors persist across 1,024-move execution windows."));
            return plan;
        }

        Report("Indexing free space", 0, layout.TotalClusters, "clusters", force: true);
        using var free = new FreeSpaceIndex(Ranges());
        bool ordered = request.Operation is Operation.Alphabetical or Operation.Size or Operation.Created or Operation.Modified or Operation.Extension or Operation.DirectoryLocality;
        bool reusableOrder = request.Operation == Operation.PrepareShrink || ordered;
        int[]? rememberedOrder = reusableOrder && session != null ? session.CandidateOrder(layout.Files, request.Operation) : null;
        using var orderOwner = rememberedOrder == null ? new PooledBuffer<int>(layout.Files.Length) : null;
        int[] order;
        if (rememberedOrder != null)
        {
            order = rememberedOrder;
            Report("Candidate order ready", layout.Files.Length, layout.Files.Length, "candidate entries", active: 0, peak: 0, force: true);
        }
        else
        {
            for (int i = 0; i < layout.Files.Length; i++) orderOwner!.Add(i);
            Report("Sorting candidates", 0, (long)layout.Files.Length * (workers > 1 ? 2 : 1), "sort / merge entries", force: true);
            long[]? integerKeys = null;
            double[]? scoreKeys = null;
            string[]? textKeys = null;
            string[]? pathKeys = null;
            bool descendingInteger = false;
            try
            {
                if (request.Operation is Operation.Size or Operation.Created or Operation.Modified or Operation.PrepareShrink)
                {
                    integerKeys = ArrayPool<long>.Shared.Rent(Math.Max(1, layout.Files.Length));
                    descendingInteger = request.Operation == Operation.PrepareShrink;
                    for (int index = 0; index < layout.Files.Length; index++) integerKeys[index] = request.Operation switch
                    {
                        Operation.Size => layout.Files[index].Size,
                        Operation.Created => layout.Files[index].CreatedUtcTicks,
                        Operation.Modified => layout.Files[index].ModifiedUtcTicks,
                        _ => HighestLcn(layout.Files[index])
                    };
                }
                else if (request.Operation is Operation.Alphabetical or Operation.Extension or Operation.DirectoryLocality)
                {
                    pathKeys = ArrayPool<string>.Shared.Rent(Math.Max(1, layout.Files.Length));
                    if (request.Operation != Operation.Alphabetical) textKeys = ArrayPool<string>.Shared.Rent(Math.Max(1, layout.Files.Length));
                    else textKeys = pathKeys;
                    for (int index = 0; index < layout.Files.Length; index++)
                    {
                        string path = layout.Files[index].Path;
                        pathKeys[index] = path;
                        if (!ReferenceEquals(textKeys, pathKeys)) textKeys[index] = request.Operation == Operation.Extension
                            ? Path.GetExtension(path) : Path.GetDirectoryName(path) ?? "";
                    }
                }
                else
                {
                    scoreKeys = ArrayPool<double>.Shared.Rent(Math.Max(1, layout.Files.Length));
                    for (int index = 0; index < layout.Files.Length; index++) scoreKeys[index] = Score(layout.Files[index]);
                }

                ParallelOrder.Sort(orderOwner!.Array, orderOwner.Count, workers, (a, b) =>
                {
                    int comparison = integerKeys != null
                        ? descendingInteger ? integerKeys[b].CompareTo(integerKeys[a]) : integerKeys[a].CompareTo(integerKeys[b])
                        : scoreKeys != null ? scoreKeys[b].CompareTo(scoreKeys[a])
                        : StringComparer.OrdinalIgnoreCase.Compare(textKeys![a], textKeys[b]);
                    if (comparison == 0 && pathKeys != null && !ReferenceEquals(textKeys, pathKeys))
                        comparison = StringComparer.OrdinalIgnoreCase.Compare(pathKeys[a], pathKeys[b]);
                    // Stable ties are required when a pass resumes in a later batch.
                    return comparison != 0 ? comparison : a.CompareTo(b);
                }, (done, active, peak) => Report("Sorting candidates", done, (long)layout.Files.Length * (workers > 1 ? 2 : 1), "sort / merge entries", active, peak), Checkpoint, cancellationToken);
            }
            finally
            {
                if (integerKeys != null) ArrayPool<long>.Shared.Return(integerKeys);
                if (scoreKeys != null) ArrayPool<double>.Shared.Return(scoreKeys);
                if (textKeys != null && !ReferenceEquals(textKeys, pathKeys)) ArrayPool<string>.Shared.Return(textKeys, clearArray: true);
                if (pathKeys != null) ArrayPool<string>.Shared.Return(pathKeys, clearArray: true);
            }
            if (reusableOrder && session != null)
            {
                session.RememberCandidateOrder(layout.Files, request.Operation, orderOwner.Span);
                order = session.CandidateOrder(layout.Files, request.Operation)!;
            }
            else order = orderOwner.Array;
        }
        int orderCount = layout.Files.Length;
        using var moves = new PooledBuffer<PlannedMove>(1024);
        long remaining = request.MaxMoveBytes == 0 ? long.MaxValue : request.MaxMoveBytes / layout.Volume.BytesPerCluster;
        long planned = 0, cursor = ordered ? session?.DestinationCursor ?? 0 : 0;
        long chunk = Math.Max(1, 16L * 1024 * 1024 / layout.Volume.BytesPerCluster);
        int blocked = ordered ? session?.FilesBlocked ?? 0 : 0, considered = ordered ? session?.FilesConsidered ?? 0 : 0;
        Report("Reserving destinations", ordered ? session?.OrderedPosition ?? 0 : 0, orderCount, "files", force: true);
        for (int position = ordered ? session?.OrderedPosition ?? 0 : 0; position < orderCount; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((position & 1023) == 0) { Checkpoint(); Report("Reserving destinations", position, orderCount, "files"); }
            if (ordered && session != null) session.OrderedPosition = position + 1;
            int index = order[position];
            var file = layout.Files[index];
            if (!rules.IsSelected(file.Path)) continue;
            bool metadata = (file.Flags & StreamFlags.Metadata) != 0, directory = (file.Flags & StreamFlags.Directory) != 0;
            if (request.Operation == Operation.OptimizeMft ? (file.FileId & 0xFFFFFFFFFFFF) != 0 : metadata) continue;
            if (request.Operation == Operation.DirectoryIndexes ? !directory : directory && request.Operation != Operation.DirectoryLocality) continue;
            if (file.Size < request.MinimumFileBytes || (request.MaximumFileBytes > 0 && file.Size > request.MaximumFileBytes)) continue;
            if (request.Operation is Operation.MinimumWrite or Operation.FilesOnly && file.Extents.Length < request.MinimumFragments) continue;
            considered++;
            if (!file.Movable || rules.IsExcluded(file.Path) || session?.BlockedFiles.Contains(file.FileId) == true) { blocked++; continue; }
            var extents = file.Extents;
            if (extents.Length == 0) continue;
            bool pack = request.Operation == Operation.PrepareShrink;
            long total = 0;
            foreach (var e in extents) total = checked(total + e.Length);
            if (pack)
            {
                long boundary = request.ShrinkBoundaryBytes / layout.Volume.BytesPerCluster;
                void PlanExtent(Extent e)
                {
                    long offset = boundary < e.End ? Math.Max(0, boundary - e.Lcn) : e.Length;
                    while (offset < e.Length && remaining > 0 && moves.Count < 1024)
                    {
                        long size = Math.Min(Math.Min(chunk, e.Length - offset), remaining);
                        long target = free.FindFirstFit(1, Math.Min(e.Lcn + offset, boundary));
                        if (target < 0) break;
                        size = Math.Min(size, Math.Min(free.AvailableFrom(target), Math.Min(e.Lcn + offset, boundary) - target));
                        if (size == 0) break;
                        Add(index, file.FileId, e.Vcn + offset, e.Lcn + offset, target, size);
                        offset += size;
                    }
                }
                if (extents.Length == 1) PlanExtent(extents[0]);
                else
                {
                    var physicalOrder = ArrayPool<Extent>.Shared.Rent(extents.Length);
                    try
                    {
                        extents.CopyTo(physicalOrder, 0);
                        Array.Sort(physicalOrder, 0, extents.Length, DescendingPhysicalExtentComparer.Instance);
                        foreach (var extent in physicalOrder.AsSpan(0, extents.Length)) PlanExtent(extent);
                    }
                    finally { ArrayPool<Extent>.Shared.Return(physicalOrder); }
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
                    long requiredMoves = 0;
                    foreach (var extent in extents) requiredMoves += (extent.Length - 1) / chunk + 1;
                    if (ordered && session != null && target >= 0 && requiredMoves <= 1024 && requiredMoves > 1024 - moves.Count)
                    {
                        // This file fits a fresh batch. Resume here instead of dropping it
                        // merely because preceding files consumed this batch's capacity.
                        session.OrderedPosition = position;
                        considered--;
                        break;
                    }
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
        if (ordered && session != null)
        {
            session.DestinationCursor = cursor;
            session.FilesConsidered = considered;
            session.FilesBlocked = blocked;
        }
        progress?.Invoke(new("Plan ready", considered, layout.Files.Length, "considered files", workers, 0, peakWorkers,
            ElapsedMilliseconds: watch.ElapsedMilliseconds, Acceleration: WorkerPolicy.BitmapAcceleration + "; scalar sort / placement",
            Detail: $"{moves.Count:N0} moves, {planned:N0} clusters, {blocked:N0} constrained files. At most 1,024 moves per batch."));
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
    private static long HighestLcn(FileLayout file)
    {
        long highest = 0;
        foreach (var extent in file.Extents)
            if (!extent.IsSparse) highest = Math.Max(highest, extent.End);
        return highest;
    }
    private static double Score(FileLayout f) => (f.Extents.Length - 1d) / Math.Max(1, f.Size);

    private sealed class DescendingPhysicalExtentComparer : IComparer<Extent>
    {
        public static DescendingPhysicalExtentComparer Instance { get; } = new();
        public int Compare(Extent x, Extent y)
        {
            int comparison = y.Lcn.CompareTo(x.Lcn);
            return comparison != 0 ? comparison : y.Vcn.CompareTo(x.Vcn);
        }
    }
}

internal sealed class PooledBuffer<T>(int capacity) : IDisposable
{
    private T[] _data = ArrayPool<T>.Shared.Rent(Math.Max(1, capacity));
    public int Count { get; private set; }
    public T[] Array => _data;
    public Span<T> Span => _data.AsSpan(0, Count);
    public void Add(T value) => _data[Count++] = value;
    public T[] ToArray() => Span.ToArray();
    public void Dispose() { ArrayPool<T>.Shared.Return(_data, System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>()); _data = []; Count = 0; }
}
