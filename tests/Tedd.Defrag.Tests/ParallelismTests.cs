using System.Collections.Concurrent;
using Tedd.Defrag.Core;
using Tedd.Defrag.Engine;
using Tedd.Defrag.Planning;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class ParallelismTests
{
    [Fact]
    public void ScanPipelineOverlapsWorkersButConsumesInOrderOnCoordinator()
    {
        using var barrier = new Barrier(3);
        var contexts = new List<Context>(); var results = new List<int>();
        int coordinator = Environment.CurrentManagedThreadId;
        var activity = new WorkerActivity();
        OrderedPipeline.Run(Enumerable.Range(0, 30), 3, () =>
        {
            var context = new Context(); contexts.Add(context); return context;
        }, (context, input) =>
        {
            Assert.False(context.Disposed); activity.Enter();
            try
            {
                if (input < 3) Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                Thread.Sleep(input % 3); Assert.False(context.Disposed); return input * 2;
            }
            finally { activity.Exit(); }
        }, (input, result) =>
        {
            Assert.Equal(coordinator, Environment.CurrentManagedThreadId);
            Assert.Equal(input * 2, result); results.Add(input);
        }, () => { });
        Assert.Equal(Enumerable.Range(0, 30), results);
        Assert.Equal(3, activity.Peak);
        Assert.Equal(3, contexts.Count);
        Assert.All(contexts, c => Assert.True(c.Disposed));
    }

    [Fact]
    public void PipelineFailureDrainsReadsBeforeDisposingBuffers()
    {
        using var barrier = new Barrier(2);
        var contexts = new List<Context>(); int finished = 0;
        Assert.Throws<IOException>(() => OrderedPipeline.Run(Enumerable.Range(0, 10), 2, () =>
        {
            var c = new Context(); contexts.Add(c); return c;
        }, (context, input) =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            if (input == 0) throw new IOException("read failed");
            Thread.Sleep(30); Assert.False(context.Disposed); Interlocked.Increment(ref finished); return input;
        }, (_, _) => { }, () => { }));
        Assert.Equal(1, finished);
        Assert.All(contexts, c => Assert.True(c.Disposed));
    }

    [Fact]
    public void MemoryStopConsumesOnlyTheBoundedWorkAlreadyScheduled()
    {
        int consumed = 0; var results = new List<int>();
        OrderedPipeline.Run(Enumerable.Range(0, 100), 4, () => new Context(), (_, i) => i,
            (_, i) => { results.Add(i); consumed++; }, () => { }, () => consumed == 0);
        Assert.Equal(new[] { 0, 1, 2, 3 }, results);
    }

    [Fact]
    public void RelocationOverlapsIdentitiesAndKeepsEveryFileSequenceSerial()
    {
        using var barrier = new Barrier(3);
        var execution = new ConcurrentDictionary<ulong, List<long>>(); var active = new ConcurrentDictionary<ulong, bool>();
        var completed = new List<PlannedMove>(); var activity = new WorkerActivity();
        var moves = Moves(); int coordinator = Environment.CurrentManagedThreadId;
        MoveScheduler.Run(moves, 3, _ => true, move =>
        {
            Assert.True(active.TryAdd(move.FileId, true)); activity.Enter();
            try
            {
                if (move.Vcn == 0) Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                execution.GetOrAdd(move.FileId, _ => []).Add(move.Vcn);
            }
            finally { activity.Exit(); active.TryRemove(move.FileId, out _); }
        }, (move, error) =>
        {
            Assert.Equal(coordinator, Environment.CurrentManagedThreadId); Assert.Null(error); completed.Add(move);
        }, () => { }, (_, _, _) => { });
        Assert.Equal(3, activity.Peak); Assert.Equal(moves.Length, completed.Count);
        Assert.All(execution.Values, list => Assert.Equal(new long[] { 0, 1, 2 }, list));
    }

    [Fact]
    public void SerialQueueKeepsOriginalInterleavedPlanOrderOnCallerThread()
    {
        var moves = Moves().OrderBy(m => m.Vcn).ThenBy(m => m.FileId).ToArray();
        var executed = new List<PlannedMove>(); int caller = Environment.CurrentManagedThreadId;
        MoveScheduler.Run(moves, 1, _ => true, move =>
        {
            Assert.Equal(caller, Environment.CurrentManagedThreadId); executed.Add(move);
        }, (_, error) => Assert.Null(error), () => { }, (_, _, _) => { });
        Assert.Equal(moves, executed);
    }

    [Fact]
    public void CancellationStopsDispatchAndAccountsForEveryInFlightResult()
    {
        using var cts = new CancellationTokenSource(); using var barrier = new Barrier(3);
        int attempted = 0, completed = 0, executing = 0;
        Assert.Throws<OperationCanceledException>(() => MoveScheduler.Run(Moves(), 3,
            _ => { attempted++; return true; }, move =>
            {
                Interlocked.Increment(ref executing);
                try
                {
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                    if (move.FileId == 1) cts.Cancel();
                    Assert.True(cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
                }
                finally { Interlocked.Decrement(ref executing); }
            }, (_, error) => { Assert.Null(error); completed++; }, cts.Token.ThrowIfCancellationRequested, (_, _, _) => { }));
        Assert.Equal(3, attempted); Assert.Equal(attempted, completed); Assert.Equal(0, executing);
    }

    [Fact]
    public void FailedFileSkipsItsOtherStreamsAndIndependentFilesContinue()
    {
        var blocked = new HashSet<ulong>(); var attempts = new List<PlannedMove>(); int completed = 0;
        MoveScheduler.Run(Moves(), 3, move =>
        {
            if (blocked.Contains(move.FileId)) return false;
            attempts.Add(move); return true;
        }, move => { if (move.FileId == 1) throw new IOException("ambiguous result"); },
            (move, error) => { if (error != null) blocked.Add(move.FileId); else completed++; }, () => { }, (_, _, _) => { });
        Assert.Single(attempts, m => m.FileId == 1); Assert.Equal(6, completed);
    }

    [Theory]
    [InlineData(Operation.MinimumWrite)]
    [InlineData(Operation.Pack)]
    [InlineData(Operation.PackAndDefrag)]
    [InlineData(Operation.Alphabetical)]
    [InlineData(Operation.Extension)]
    [InlineData(Operation.DirectoryLocality)]
    [InlineData(Operation.Size)]
    [InlineData(Operation.Created)]
    [InlineData(Operation.Modified)]
    public void ParallelPlanningProducesExactlyTheSerialPlan(Operation operation)
    {
        const int count = 20000;
        var files = Enumerable.Range(0, count).Select(i => new FileLayout((ulong)i + 32,
            $"V:\\folder{i % 7}\\{(i * 7919) % count:00000}.{i % 5}", "", i % 97 == 0 ? StreamFlags.Excluded : 0,
            8192, i % 3, i % 2, [new(0, count * 4 + i * 4, 1), new(1, count * 4 + i * 4 + 2, 1)])).ToArray();
        long clusters = count * 8L; byte[] bitmap = new byte[(clusters + 7) / 8];
        foreach (var extent in files.SelectMany(f => f.Extents)) BitmapOperations.SetRange(bitmap, extent.Lcn, extent.Length, true);
        var info = new VolumeInfo("test", "V:\\", "test", "NTFS", clusters * 4096, 0, 4096, true, false, [], "test");
        var layout = new VolumeLayout(info, clusters, bitmap, files, DateTimeOffset.UtcNow, count, 0, true, [], new(0, 256));
        var request = new JobRequest { Volume = "V:\\", Operation = operation, MinimumFragments = 2, Resources = new() { PlanningWorkers = 1 } };
        var planner = new LayoutPlanner(); var stages = new List<WorkProgress>();
        var serial = planner.Plan(layout, request);
        var parallel = planner.Plan(layout, request with { Resources = request.Resources with { PlanningWorkers = 4 } }, progress: stages.Add);
        Assert.Equal(serial.Moves, parallel.Moves); Assert.Equal(serial.FilesConsidered, parallel.FilesConsidered);
        Assert.Equal(serial.FilesBlocked, parallel.FilesBlocked); Assert.Equal(serial.ClustersToMove, parallel.ClustersToMove);
        if (operation == Operation.Pack)
        {
            Assert.DoesNotContain(stages, p => p.Phase == "Sorting candidates");
            Assert.Contains(stages, p => p.Phase == "Ordering physical extents" && p.Acceleration.Contains("radix sort"));
        }
        else Assert.Contains(stages, p => p.Phase == "Sorting candidates" && p.WorkerLimit == 4);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(7)] [InlineData(63)] [InlineData(127)]
    [InlineData(128)] [InlineData(255)] [InlineData(256)] [InlineData(257)] [InlineData(4099)]
    public void SimdFreeRangesMatchBitReferenceIncludingPartialVectorsAndPadding(int clusters)
    {
        var random = new Random(19);
        for (int pattern = 0; pattern < 4; pattern++)
        {
            byte[] bitmap = new byte[(clusters + 7) / 8 + 32];
            if (pattern == 1) bitmap.AsSpan().Fill(255);
            if (pattern == 2) random.NextBytes(bitmap);
            if (pattern == 3)
                for (int i = 0; i < bitmap.Length; i++) bitmap[i] = i / 32 % 2 == 0 ? (byte)0 : (byte)255;
            var expected = Enumerable.Range(0, clusters).Where(i => !BitmapOperations.IsSet(bitmap, i)).Select(i => (long)i);
            var ranges = BitmapOperations.FreeRanges(bitmap, clusters).ToArray();
            Assert.Equal(expected, ranges.SelectMany(r => Enumerable.Range(0, (int)r.Length).Select(i => r.Start + i)));
            for (int i = 1; i < ranges.Length; i++) Assert.True(ranges[i - 1].End < ranges[i].Start);
        }
    }

    [Fact]
    public void ApplicationDefaultsUseFullSpeedAndCapsBoundScanBuffers()
    {
        var defaults = new JobRequest().Resources;
        Assert.Equal(100, defaults.CpuPercent); Assert.Equal(16, defaults.MoveQueueDepth);
        Assert.False(defaults.Background); Assert.False(defaults.AcOnly);
        Assert.Equal(1, ResourcePolicy.Balanced.MoveQueueDepth); Assert.Equal(16, ResourcePolicy.Performance.MoveQueueDepth);
        Assert.Equal(2, WorkerPolicy.ScanWorkers(ResourcePolicy.Performance with { MemoryMiB = 256, ScanWorkers = 32 }));
        Assert.Equal(1, WorkerPolicy.CpuWorkers(new() { CpuPercent = 100, AffinityMask = 1 }));
        Assert.Throws<ArgumentException>(() => new ResourcePolicy { MoveQueueDepth = 17 }.Validate());
        Assert.Throws<ArgumentException>(() => new ResourcePolicy { ScanWorkers = -1 }.Validate());
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ResourcePolicy>("{\"CpuPercent\":25}");
        Assert.Equal(1, legacy!.MoveQueueDepth); Assert.Equal(0, legacy.ScanWorkers);
    }

    private static PlannedMove[] Moves() => Enumerable.Range(1, 3).SelectMany(id => Enumerable.Range(0, 3)
        .Select(i => new PlannedMove((ulong)id, id * 10 + i, i, id * 100 + i, id * 10 + i, 1))).ToArray();
    private sealed class Context : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
