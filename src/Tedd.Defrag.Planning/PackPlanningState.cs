using System.Buffers;
using Tedd.Defrag.Core;

namespace Tedd.Defrag.Planning;

/// <summary>
/// Persistent data-oriented state for a pack pass. Sources are stored in ascending
/// physical order and consumed from the tail; free ranges are consumed from the head.
/// Every destination therefore comes from the original bitmap and never depends on
/// source space produced by an unverified move.
/// </summary>
internal sealed class PackPlanningState
{
    private readonly FileLayout[] files;
    private readonly PackSource[] sources;
    private readonly ClusterRange[] destinations;
    private int sourceIndex;
    private long sourceRemaining;
    private int destinationIndex;
    private long destinationOffset;

    private PackPlanningState(FileLayout[] files, PackSource[] sources, ClusterRange[] destinations,
        int filesConsidered, int filesBlocked)
    {
        this.files = files;
        this.sources = sources;
        this.destinations = destinations;
        sourceIndex = sources.Length - 1;
        FilesConsidered = filesConsidered;
        FilesBlocked = filesBlocked;
    }

    public int FilesConsidered { get; }
    public int FilesBlocked { get; }
    public int SourceCount => sources.Length;
    public int SourcesConsumed => sources.Length - Math.Max(0, sourceIndex + 1);

    public static PackPlanningState Create(VolumeLayout layout, JobRequest request, PathRules rules,
        IReadOnlySet<ulong>? blockedFiles, ClusterRange[] destinations, Action<int>? indexProgress, Action<long, long>? sortProgress,
        Action checkpoint, CancellationToken cancellationToken)
    {
        var files = layout.Files;
        long firstDestination = destinations.Length == 0 ? long.MaxValue : destinations[0].Start;
        byte[] eligible = ArrayPool<byte>.Shared.Rent(Math.Max(1, files.Length));
        int sourceCount = 0, considered = 0, blocked = 0;
        try
        {
            for (int index = 0; index < files.Length; index++)
            {
                if ((index & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checkpoint(); indexProgress?.Invoke(index);
                }
                var file = files[index];
                bool candidate = rules.IsSelected(file.Path)
                    && (file.Flags & (StreamFlags.Metadata | StreamFlags.Directory)) == 0
                    && file.Size >= request.MinimumFileBytes
                    && (request.MaximumFileBytes == 0 || file.Size <= request.MaximumFileBytes);
                if (!candidate) { eligible[index] = 0; continue; }
                considered++;
                if (!file.Movable || rules.IsExcluded(file.Path) || blockedFiles?.Contains(file.FileId) == true)
                {
                    eligible[index] = 0; blocked++; continue;
                }
                eligible[index] = 1;
                foreach (var extent in file.Extents)
                    if (!extent.IsSparse && extent.Lcn > firstDestination) sourceCount = checked(sourceCount + 1);
            }
            indexProgress?.Invoke(files.Length);

            var sources = GC.AllocateUninitializedArray<PackSource>(sourceCount);
            int position = 0;
            for (int index = 0; index < files.Length; index++)
            {
                if (eligible[index] == 0) continue;
                foreach (var extent in files[index].Extents)
                {
                    if (extent.IsSparse || extent.Lcn <= firstDestination) continue;
                    sources[position++] = new(index, extent.Vcn, extent.Lcn, extent.Length);
                }
            }
            if (position != sources.Length) Array.Resize(ref sources, position);
            PackSourceRadixSort.Sort(sources, sortProgress, checkpoint, cancellationToken);
            return new(files, sources, destinations, considered, blocked);
        }
        finally { ArrayPool<byte>.Shared.Return(eligible); }
    }

    public MovePlan NextBatch(long maximumClusters, long chunkClusters, int maximumMoves)
    {
        using var moves = new PooledBuffer<PlannedMove>(maximumMoves);
        long planned = 0;
        while (sourceIndex >= 0 && destinationIndex < destinations.Length && maximumClusters > 0 && moves.Count < maximumMoves)
        {
            ref readonly var source = ref sources[sourceIndex];
            if (sourceRemaining == 0) sourceRemaining = source.Length;
            var destination = destinations[destinationIndex];
            long target = destination.Start + destinationOffset;

            // Sources are globally descending. If the earliest original hole is not
            // below this source, it cannot be used by any remaining source either.
            if (target >= source.Lcn) break;

            long count = Math.Min(Math.Min(chunkClusters, sourceRemaining), maximumClusters);
            count = Math.Min(count, destination.Length - destinationOffset);
            if (count <= 0) throw new InvalidOperationException("Pack cursor failed to make progress.");

            long sourceLcn = source.Lcn + sourceRemaining - count;
            long sourceVcn = source.Vcn + sourceRemaining - count;
            var file = files[source.FileIndex];
            moves.Add(new(file.FileId, source.FileIndex, sourceVcn, sourceLcn, target, count));
            planned += count;
            maximumClusters -= count;
            sourceRemaining -= count;
            destinationOffset += count;

            if (sourceRemaining == 0) sourceIndex--;
            if (destinationOffset == destination.Length)
            {
                destinationIndex++;
                destinationOffset = 0;
            }
        }
        return new(moves.ToArray(), planned, FilesConsidered, FilesBlocked,
            "Tail-to-hole pack plan. Physical source and destination cursors persist across execution windows; only failed moves require reconstruction.");
    }

    private readonly record struct PackSource(int FileIndex, long Vcn, long Lcn, long Length);

    private static class PackSourceRadixSort
    {
        private const int Radix = 1 << 16;
        private const int Passes = 4;

        public static void Sort(PackSource[] values, Action<long, long>? progress, Action checkpoint, CancellationToken token)
        {
            long total = (long)values.Length * Passes;
            progress?.Invoke(0, total);
            if (values.Length < 2) { progress?.Invoke(total, total); return; }
            if (values.Length < 4096)
            {
                Array.Sort(values, static (a, b) => a.Lcn.CompareTo(b.Lcn));
                progress?.Invoke(total, total); return;
            }

            var scratch = ArrayPool<PackSource>.Shared.Rent(values.Length);
            var counts = ArrayPool<int>.Shared.Rent(Radix);
            try
            {
                PackSource[] input = values, output = scratch;
                for (int pass = 0; pass < Passes; pass++)
                {
                    token.ThrowIfCancellationRequested(); checkpoint();
                    Array.Clear(counts, 0, Radix);
                    int shift = pass * 16;
                    for (int index = 0; index < values.Length; index++)
                    {
                        counts[(int)(((ulong)input[index].Lcn >> shift) & 0xFFFF)]++;
                        if ((index & 0xFFFFF) == 0) token.ThrowIfCancellationRequested();
                    }
                    int offset = 0;
                    for (int bucket = 0; bucket < Radix; bucket++)
                    {
                        int count = counts[bucket]; counts[bucket] = offset; offset += count;
                    }
                    for (int index = 0; index < values.Length; index++)
                    {
                        var value = input[index];
                        output[counts[(int)(((ulong)value.Lcn >> shift) & 0xFFFF)]++] = value;
                        if ((index & 0xFFFFF) == 0) token.ThrowIfCancellationRequested();
                    }
                    (input, output) = (output, input);
                    progress?.Invoke((long)(pass + 1) * values.Length, total);
                }
                if (!ReferenceEquals(input, values)) input.AsSpan(0, values.Length).CopyTo(values);
            }
            finally
            {
                ArrayPool<PackSource>.Shared.Return(scratch);
                ArrayPool<int>.Shared.Return(counts);
            }
        }
    }
}
