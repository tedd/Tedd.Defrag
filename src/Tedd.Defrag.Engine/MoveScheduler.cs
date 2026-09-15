using Tedd.Defrag.Core;
using System.Buffers;

namespace Tedd.Defrag.Engine;

/// <summary>One outstanding move per file identity, including all its streams.
/// Only the coordinator touches journals, layouts, control state and progress.</summary>
internal static class MoveScheduler
{
    public static void Run(PlannedMove[] moves, int depth, Func<PlannedMove, bool> prepare,
        Action<PlannedMove> execute, Action<PlannedMove, Exception?> complete,
        Action checkpoint, Action<int, int, int> report)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        if (depth == 1)
        {
            // Preserve exact plan order and avoid task/queue overhead for serial policies.
            int done = 0, serialPeak = 0;
            try
            {
                foreach (var move in moves)
                {
                    checkpoint();
                    if (prepare(move))
                    {
                        serialPeak = 1; report(done, 1, serialPeak);
                        Exception? error = null;
                        try { execute(move); } catch (Exception e) { error = e; }
                        complete(move, error);
                    }
                    report(++done, 0, serialPeak);
                }
            }
            finally { report(done, 0, serialPeak); }
            return;
        }
        int capacity = Math.Max(1, moves.Length);
        int[] next = ArrayPool<int>.Shared.Rent(capacity);
        int[] heads = ArrayPool<int>.Shared.Rent(capacity);
        int[] tails = ArrayPool<int>.Shared.Rent(capacity);
        int[] remaining = ArrayPool<int>.Shared.Rent(capacity);
        var ready = new Queue<int>(Math.Min(moves.Length, 4096));
        var active = new List<(int Lane, PlannedMove Move, Task<Exception?> Task)>(depth);
        int finished = 0, peak = 0;
        try
        {
            var lanes = new Dictionary<ulong, int>(moves.Length);
            int laneCount = 0;
            for (int moveIndex = 0; moveIndex < moves.Length; moveIndex++)
            {
                next[moveIndex] = -1;
                ulong fileId = moves[moveIndex].FileId;
                if (!lanes.TryGetValue(fileId, out int lane))
                {
                    lane = laneCount++;
                    lanes.Add(fileId, lane);
                    heads[lane] = tails[lane] = moveIndex;
                    remaining[lane] = 1;
                    ready.Enqueue(lane);
                }
                else
                {
                    next[tails[lane]] = moveIndex;
                    tails[lane] = moveIndex;
                    remaining[lane]++;
                }
            }
            while (ready.Count > 0 || active.Count > 0)
            {
                checkpoint();
                while (active.Count < depth && ready.TryDequeue(out int lane)) Start(lane);
                report(finished, active.Count, peak);
                if (active.Count == 0) continue;
                int index = active.FindIndex(x => x.Task.IsCompleted);
                if (index < 0) { Task.WaitAny(active.Select(x => (Task)x.Task).ToArray(), 100); continue; }
                var completedEntry = active[index]; active.RemoveAt(index);
                complete(completedEntry.Move, completedEntry.Task.GetAwaiter().GetResult()); finished++;
                // Keep each file sequence in its lane for locality, until completed or blocked.
                checkpoint(); Start(completedEntry.Lane);
            }
        }
        finally
        {
            // Cancellation, pause-boundary failure or journaling failure must not orphan native requests.
            // Apply every completed result before the volume can close or a terminal report is saved.
            Exception? drainError = null;
            foreach (var pending in active)
            {
                var error = pending.Task.GetAwaiter().GetResult();
                try { complete(pending.Move, error); }
                catch (Exception e) { drainError ??= e; }
                finished++;
            }
            report(finished, 0, peak);
            ArrayPool<int>.Shared.Return(next);
            ArrayPool<int>.Shared.Return(heads);
            ArrayPool<int>.Shared.Return(tails);
            ArrayPool<int>.Shared.Return(remaining);
            if (drainError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(drainError).Throw();
        }
        void Start(int lane)
        {
            int moveIndex = heads[lane];
            if (moveIndex < 0) return;
            heads[lane] = next[moveIndex];
            remaining[lane]--;
            checkpoint();
            var move = moves[moveIndex];
            if (!prepare(move)) { finished += remaining[lane] + 1; remaining[lane] = 0; heads[lane] = -1; return; }
            var task = Task.Run<Exception?>(() =>
            {
                try { execute(move); return null; }
                catch (Exception e) { return e; }
            });
            active.Add((lane, move, task)); peak = Math.Max(peak, active.Count);
        }
    }
}
