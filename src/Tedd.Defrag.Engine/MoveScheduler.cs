using Tedd.Defrag.Core;

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
        var ready = new Queue<Queue<PlannedMove>>(moves.GroupBy(m => m.FileId).Select(g => new Queue<PlannedMove>(g)));
        var active = new List<(Queue<PlannedMove> Sequence, PlannedMove Move, Task<Exception?> Task)>();
        int finished = 0, peak = 0;
        try
        {
            while (ready.Count > 0 || active.Count > 0)
            {
                checkpoint();
                while (active.Count < depth && ready.TryDequeue(out var sequence)) Start(sequence);
                report(finished, active.Count, peak);
                if (active.Count == 0) continue;
                int index = active.FindIndex(x => x.Task.IsCompleted);
                if (index < 0) { Task.WaitAny(active.Select(x => (Task)x.Task).ToArray(), 100); continue; }
                var next = active[index]; active.RemoveAt(index);
                complete(next.Move, next.Task.GetAwaiter().GetResult()); finished++;
                // Keep each file sequence in its lane for locality, until completed or blocked.
                checkpoint(); Start(next.Sequence);
            }
        }
        finally
        {
            // Cancellation, pause-boundary failure or journaling failure must not orphan native requests.
            // Apply every completed result before the volume can close or a terminal report is saved.
            Exception? drainError = null;
            foreach (var next in active)
            {
                var error = next.Task.GetAwaiter().GetResult();
                try { complete(next.Move, error); }
                catch (Exception e) { drainError ??= e; }
                finished++;
            }
            report(finished, 0, peak);
            if (drainError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(drainError).Throw();
        }
        void Start(Queue<PlannedMove> sequence)
        {
            if (sequence.Count == 0) return;
            checkpoint();
            var move = sequence.Dequeue();
            if (!prepare(move)) { finished += sequence.Count + 1; sequence.Clear(); return; }
            var task = Task.Run<Exception?>(() =>
            {
                try { execute(move); return null; }
                catch (Exception e) { return e; }
            });
            active.Add((sequence, move, task)); peak = Math.Max(peak, active.Count);
        }
    }
}
