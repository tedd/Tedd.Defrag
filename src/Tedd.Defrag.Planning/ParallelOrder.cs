using System.Buffers;

namespace Tedd.Defrag.Planning;

internal static class ParallelOrder
{
    // Disjoint sorted partitions, followed by a stable k-way merge on the coordinator.
    public static void Sort(int[] indices, int count, int workers, Comparison<int> comparison,
        Action<int, int, int> report, Action checkpoint, CancellationToken token)
    {
        if (workers <= 1)
        {
            report(0, 1, 1); checkpoint(); indices.AsSpan(0, count).Sort(comparison);
            checkpoint(); report(count, 0, 1); return;
        }
        var activity = new Tedd.Defrag.Core.WorkerActivity();
        int completed = 0;
        var tasks = new Task[workers];
        for (int worker = 0; worker < workers; worker++)
        {
            int start = (int)((long)count * worker / workers), end = (int)((long)count * (worker + 1) / workers);
            tasks[worker] = Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); activity.Enter();
                try { indices.AsSpan(start, end - start).Sort(comparison); Interlocked.Add(ref completed, end - start); }
                finally { activity.Exit(); }
            });
        }
        var all = Task.WhenAll(tasks);
        try
        {
            while (!all.IsCompleted) { checkpoint(); report(Volatile.Read(ref completed), activity.Active, activity.Peak); Task.WaitAny([all], 100); }
            all.GetAwaiter().GetResult(); checkpoint();
        }
        finally { try { all.GetAwaiter().GetResult(); } catch { /* Original failure is propagated above. */ } }
        int[] merged = ArrayPool<int>.Shared.Rent(Math.Max(1, count));
        try
        {
            var queue = new PriorityQueue<(int Position, int End), int>(Comparer<int>.Create(comparison));
            for (int w = 0; w < workers; w++)
            {
                int start = (int)((long)count * w / workers), end = (int)((long)count * (w + 1) / workers);
                if (start < end) queue.Enqueue((start, end), indices[start]);
            }
            for (int i = 0; queue.TryDequeue(out var part, out int index); i++)
            {
                if ((i & 4095) == 0) { checkpoint(); report(count + i, 1, activity.Peak); }
                merged[i] = index;
                if (++part.Position < part.End) queue.Enqueue(part, indices[part.Position]);
            }
            merged.AsSpan(0, count).CopyTo(indices);
            report(count * 2, 0, activity.Peak);
        }
        finally { ArrayPool<int>.Shared.Return(merged); }
    }
}
