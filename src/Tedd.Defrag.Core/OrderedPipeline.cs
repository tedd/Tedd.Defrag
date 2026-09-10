namespace Tedd.Defrag.Core;

/// <summary>Bounded read/compute workers with deterministic, single-threaded consumption.
/// All work drains before returning or disposing worker-owned buffers and handles.</summary>
public static class OrderedPipeline
{
    public static void Run<TInput, TOutput, TWorker>(IEnumerable<TInput> input, int workers,
        Func<TWorker> create, Func<TWorker, TInput, TOutput> process, Action<TInput, TOutput> consume,
        Action checkpoint, Func<bool>? canSchedule = null) where TWorker : IDisposable
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        using var source = input.GetEnumerator();
        var contexts = new List<TWorker>();
        var pending = new Queue<(TWorker Worker, TInput Input, Task<TOutput> Task)>();
        bool ended = false;
        try
        {
            for (int i = 0; i < workers; i++)
            {
                checkpoint();
                if (canSchedule?.Invoke() == false || !source.MoveNext()) { ended = true; break; }
                var worker = create(); contexts.Add(worker);
                Schedule(worker, source.Current);
            }
            while (pending.TryPeek(out var next))
            {
                while (!next.Task.IsCompleted) { checkpoint(); Task.WaitAny([next.Task], 100); }
                pending.Dequeue();
                consume(next.Input, next.Task.GetAwaiter().GetResult());
                checkpoint();
                if (!ended)
                {
                    if (canSchedule?.Invoke() == false || !source.MoveNext()) ended = true;
                    else Schedule(next.Worker, source.Current);
                }
            }
        }
        finally
        {
            // Do not dispose a native buffer while a raw read or parser still uses it.
            foreach (var next in pending) { try { next.Task.GetAwaiter().GetResult(); } catch { /* Preserve the original failure. */ } }
            foreach (var worker in contexts) worker.Dispose();
        }
        void Schedule(TWorker worker, TInput item) => pending.Enqueue((worker, item, Task.Run(() => process(worker, item))));
    }
}
