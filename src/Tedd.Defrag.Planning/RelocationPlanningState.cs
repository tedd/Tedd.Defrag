using Tedd.Defrag.Core;

namespace Tedd.Defrag.Planning;

/// <summary>Fixed final placements and a dependency frontier over the remaining physical extents.</summary>
internal sealed class RelocationPlanningState
{
    private sealed record Piece(int File, long Vcn, long Source, long Target, long Length);
    private static readonly IComparer<Piece> ByTarget = Comparer<Piece>.Create((a, b) => a.Target.CompareTo(b.Target));
    private static readonly IComparer<Piece> BySource = Comparer<Piece>.Create((a, b) => a.Source.CompareTo(b.Source));
    private readonly SortedSet<Piece> pending = new(ByTarget), sources = new(BySource), ready = new(ByTarget);
    private readonly long[] targets, lengths;
    private readonly int[] order;
    private readonly int[] supporting;
    private readonly ClusterRange[] goalsByAddress;
    private readonly HashSet<int> constrained = [];
    private readonly FileLayout[] files;
    private readonly Action checkpoint;
    private FreeSpaceIndex free;
    private readonly long targetEnd;
    private int workSinceCheckpoint;
    public bool AllocationInvalid { get; set; }

    public RelocationPlanningState(VolumeLayout layout, int[] candidateOrder, int count, FreeSpaceIndex free,
        PathRules rules, IReadOnlySet<ulong>? blockedFiles, Action check, JobRequest? defragRequest = null)
    {
        files = layout.Files;
        order = candidateOrder.AsSpan(0, count).ToArray();
        targets = new long[files.Length]; Array.Fill(targets, -1);
        lengths = new long[files.Length];
        this.free = free;
        checkpoint = check;
        // Free clusters and eligible source clusters form the placement domain. All other
        // allocation, including unknown owners, remains an immovable barrier.
        byte[] domain = layout.Bitmap.ToArray();
        var eligible = new HashSet<int>();
        IEnumerable<int> domainFiles = defragRequest == null ? order : Enumerable.Range(0, files.Length);
        foreach (int index in domainFiles)
        {
            CheckWork();
            var file = files[index];
            if (!file.Movable || !rules.IsSelected(file.Path) || rules.IsExcluded(file.Path) || blockedFiles?.Contains(file.FileId) == true || !HasDenseVcns(file)) continue;
            if (defragRequest != null && !CanSupport(file, defragRequest)) continue;
            eligible.Add(index);
            foreach (var extent in file.Extents)
            {
                lengths[index] = checked(lengths[index] + extent.Length);
                BitmapOperations.SetRange(domain, extent.Lcn, extent.Length, false);
            }
        }
        if (layout.MftZone.Length > 0) BitmapOperations.SetRange(domain, layout.MftZone.Start, layout.MftZone.Length, true);
        using var placements = new FreeSpaceIndex(BitmapOperations.FreeRanges(domain, layout.TotalClusters, progress: _ => checkpoint()));
        long cursor = 0;
        foreach (int index in order)
        {
            CheckWork();
            if (!eligible.Contains(index)) { constrained.Add(index); continue; }
            if (lengths[index] == 0) continue;
            if (defragRequest != null && files[index].Extents.Length < 2) continue;
            long target = -1, preserved = 0;
            if (defragRequest != null)
            {
                var anchors = new Dictionary<long, long>();
                foreach (var extent in files[index].Extents)
                {
                    long anchor = extent.Lcn - extent.Vcn;
                    if (anchor >= 0) anchors[anchor] = anchors.GetValueOrDefault(anchor) + extent.Length;
                }
                foreach (var (anchor, clusters) in anchors)
                    if (clusters > preserved && placements.Contains(anchor, lengths[index]))
                    { target = anchor; preserved = clusters; }
            }
            if (target < 0) target = placements.FindFirstFit(lengths[index], after: defragRequest == null ? cursor : 0);
            if (target < 0) { constrained.Add(index); continue; }
            placements.Reserve(target, lengths[index]);
            targets[index] = target;
            cursor = Math.Max(cursor, target + lengths[index]);
        }
        if (defragRequest != null)
        {
            var displaced = new List<int>();
            foreach (int index in eligible)
            {
                CheckWork();
                if (targets[index] >= 0) continue;
                if (files[index].Extents.All(e => placements.Contains(e.Lcn, e.Length)))
                {
                    foreach (var extent in files[index].Extents) placements.Reserve(extent.Lcn, extent.Length);
                }
                else displaced.Add(index);
            }
            // Keep supporting files contiguous when a target displaces them. Unaffected
            // files retain their physical anchors and consume no relocation budget.
            foreach (int index in displaced.OrderByDescending(index => lengths[index]).ThenBy(index => index))
            {
                CheckWork();
                long target = placements.FindFirstFit(lengths[index]);
                if (target < 0) { constrained.Add(index); continue; }
                placements.Reserve(target, lengths[index]); targets[index] = target;
                cursor = Math.Max(cursor, target + lengths[index]);
            }
            order = order.Concat(displaced.Where(index => targets[index] >= 0)).Distinct().ToArray();
        }
        targetEnd = cursor;
        goalsByAddress = order.Where(index => targets[index] >= 0)
            .Select(index => new ClusterRange(targets[index], lengths[index])).OrderBy(range => range.Start).ToArray();
        supporting = defragRequest == null ? [] : eligible.Where(index => targets[index] < 0).ToArray();
        Reconcile(free, blockedFiles);
    }

    private void CheckWork()
    {
        if ((++workSinceCheckpoint & 1023) == 0) checkpoint();
    }

    private static bool CanSupport(FileLayout file, JobRequest request)
    {
        if (file.Size < request.MinimumFileBytes || request.MaximumFileBytes > 0 && file.Size > request.MaximumFileBytes) return false;
        if ((file.Flags & StreamFlags.Metadata) != 0)
            return request.Operation == Operation.OptimizeMft && (file.FileId & 0xFFFFFFFFFFFF) == 0;
        return (file.Flags & StreamFlags.Directory) == 0 || request.Operation == Operation.DirectoryIndexes;
    }

    private static bool HasDenseVcns(FileLayout file)
    {
        long vcn = 0;
        foreach (var extent in file.Extents)
        {
            if (extent.IsSparse || extent.Length <= 0 || extent.Vcn != vcn) return false;
            vcn = checked(vcn + extent.Length);
        }
        return vcn > 0;
    }

    public void Reconcile(FreeSpaceIndex allocation, IReadOnlySet<ulong>? blockedFiles)
    {
        free = allocation;
        pending.Clear(); sources.Clear(); ready.Clear();
        foreach (int index in order)
        {
            CheckWork();
            if (targets[index] < 0 || constrained.Contains(index)) continue;
            var file = files[index];
            if (!file.Movable || blockedFiles?.Contains(file.FileId) == true || !HasDenseVcns(file) ||
                file.Extents.Sum(e => e.Length) != lengths[index])
            { constrained.Add(index); continue; }
            foreach (var extent in file.Extents)
            {
                long target = targets[index] + extent.Vcn;
                if (extent.Lcn != target) Add(new(index, extent.Vcn, extent.Lcn, target, extent.Length));
            }
        }
        foreach (int index in supporting)
        {
            CheckWork();
            var file = files[index];
            if (!file.Movable || blockedFiles?.Contains(file.FileId) == true || !HasDenseVcns(file)) continue;
            foreach (var extent in file.Extents) Add(new(index, extent.Vcn, extent.Lcn, -1, extent.Length));
        }
        AllocationInvalid = false;
    }

    public MovePlan NextBatch(long budget, long chunk, int maximumMoves)
    {
        var moves = new List<PlannedMove>(Math.Min(maximumMoves, pending.Count));
        long clusters = 0;
        while (moves.Count < maximumMoves && budget > 0)
        {
            CheckWork();
            if (ready.Count == 0)
            {
                // Source reuse is allowed only after the preceding batch has completed.
                if (moves.Count > 0 || pending.Count == 0) break;
                var cycle = FindCycle();
                if (cycle == null) continue;
                var (piece, source, overlap) = cycle.Value;
                bool supportingMove = piece.Target < 0;
                long scratch = free.FindFirstFit(1, after: targetEnd);
                long scratchLength = scratch < 0 ? 0 : free.AvailableFrom(scratch);
                if (scratch < 0 && supportingMove) (scratch, scratchLength) = SupportingDestination();
                if (scratch < 0) scratch = free.FindFirstFit(1);
                if (scratch < 0 || supportingMove && scratchLength == 0)
                {
                    if (supportingMove)
                    {
                        // No permanent room for this blocker. Retire the dependent goal.
                        var dependent = Predecessor(pending, source);
                        if (dependent != null) { constrained.Add(dependent.File); Remove(dependent); }
                    }
                    else
                    {
                        foreach (var item in pending) constrained.Add(item.File);
                        pending.Clear(); sources.Clear(); ready.Clear();
                    }
                    continue;
                }
                long offset = source - piece.Source;
                long size = Math.Min(Math.Min(chunk, budget), Math.Min(piece.Length - offset,
                    supportingMove ? scratchLength : free.AvailableFrom(scratch)));
                size = Math.Min(size, overlap);
                Emit(piece, offset, scratch, size, staging: true);
                break; // The newly freed source must be verified before any dependent move.
            }
            var next = ready.Min!; ready.Remove(next);
            long target = free.FindFirstFit(1, next.Target + next.Length, next.Target);
            if (target < 0) continue;
            long offsetInPiece = target - next.Target;
            long count = Math.Min(Math.Min(chunk, budget), Math.Min(next.Length - offsetInPiece, free.AvailableFrom(target)));
            Emit(next, offsetInPiece, target, count, staging: false);
        }
        return new(moves.ToArray(), clusters, order.Length, constrained.Count,
            "Fixed final placements; direct moves precede cycle staging. Source ranges are reused after successful execution.");

        void Emit(Piece piece, long offset, long destination, long count, bool staging)
        {
            if (!free.Reserve(destination, count)) throw new InvalidOperationException("Relocation destination is not free.");
            Remove(piece);
            if (offset > 0) Add(piece with { Length = offset });
            if (offset + count < piece.Length) Add(new(piece.File, piece.Vcn + offset + count,
                piece.Source + offset + count, piece.Target < 0 ? -1 : piece.Target + offset + count, piece.Length - offset - count));
            if (staging && piece.Target >= 0) Add(new(piece.File, piece.Vcn + offset, destination, piece.Target + offset, count));
            moves.Add(new(files[piece.File].FileId, piece.File, piece.Vcn + offset, piece.Source + offset, destination, count));
            clusters += count; budget -= count;
        }
    }

    private (long Start, long Length) SupportingDestination()
    {
        // Permanent blocker destinations must not consume a pending file's final placement.
        long after = 0;
        foreach (var goal in goalsByAddress)
        {
            CheckWork();
            long start = free.FindFirstFit(1, goal.Start, after);
            if (start >= 0) return (start, Math.Min(free.AvailableFrom(start), goal.Start - start));
            after = goal.End;
        }
        long tail = free.FindFirstFit(1, after: after);
        return (tail, tail < 0 ? 0 : free.AvailableFrom(tail));
    }

    private (Piece Piece, long Source, long Length)? FindCycle()
    {
        var path = new List<(Piece Piece, long Source, long Length)>();
        var visited = new Dictionary<Piece, int>();
        var current = pending.Min!;
        while (true)
        {
            CheckWork();
            visited[current] = path.Count;
            var owner = Predecessor(sources, current.Target);
            if (owner == null || current.Target >= owner.Source + owner.Length)
            {
                // A failed or externally changed file can pin an originally movable target.
                // Retire this dependency; the remaining fixed placements stay unchanged.
                constrained.Add(current.File);
                Remove(current);
                return null;
            }
            long overlap = Math.Min(owner.Source + owner.Length, current.Target + current.Length) - current.Target;
            if (owner.Target < 0) return (owner, current.Target, overlap);
            path.Add((owner, current.Target, overlap));
            if (visited.TryGetValue(owner, out int cycleStart))
                return path.Skip(cycleStart).MinBy(item => item.Length);
            current = owner;
        }
    }

    public void SpaceReleased(long start, long length)
    {
        var predecessor = Predecessor(pending, start);
        if (predecessor != null && predecessor.Target + predecessor.Length > start) ready.Add(predecessor);
        foreach (var piece in pending.GetViewBetween(Key(start), Key(start + length - 1))) ready.Add(piece);
    }

    private static Piece Key(long address) => new(0, 0, address, address, 0);
    private static Piece? Predecessor(SortedSet<Piece> set, long address)
    {
        // The lower bound has both coordinates zero; physical and target addresses are nonnegative.
        return set.GetViewBetween(Key(0), Key(address)).Max;
    }

    private void Add(Piece piece)
    {
        if (!sources.Add(piece)) throw new InvalidOperationException("Relocation sources overlap.");
        if (piece.Target < 0) return;
        if (!pending.Add(piece)) throw new InvalidOperationException("Relocation targets overlap.");
        if (free.FindFirstFit(1, piece.Target + piece.Length, piece.Target) >= 0) ready.Add(piece);
    }
    private void Remove(Piece piece) { pending.Remove(piece); sources.Remove(piece); ready.Remove(piece); }

    public Dictionary<(ulong Id, string Stream), ClusterRange> CaptureGoals()
    {
        var goals = new Dictionary<(ulong, string), ClusterRange>();
        foreach (int index in order)
            if (targets[index] >= 0) goals.Add((files[index].FileId, files[index].StreamName), new(targets[index], lengths[index]));
        return goals;
    }
}
