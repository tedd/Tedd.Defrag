using System.ComponentModel;
using Tedd.Defrag.Core;
using Tedd.Defrag.Engine;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Planning;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class ExecutionContinuationTests
{
    [Theory]
    [InlineData(Operation.Pack)]
    [InlineData(Operation.PackAndDefrag)]
    [InlineData(Operation.MinimumWrite)]
    [InlineData(Operation.FilesOnly)]
    [InlineData(Operation.Alphabetical)]
    public void FileFailureDoesNotStopWorkInLaterBatches(Operation operation)
    {
        var volume = ManyFiles(1030);
        ulong failedId = volume.Layout.Files[operation == Operation.Pack ? ^1 : 0].FileId;
        ulong laterId = volume.Layout.Files[operation == Operation.Pack ? 0 : ^1].FileId;
        volume.BeforeMove = (file, _) => { if (file.FileId == failedId) throw new Win32Exception(5); };

        var result = Run(volume, Request(operation));

        Assert.Equal(JobState.Partial, result.State);
        Assert.Equal(BrokerProtocol.BuildVersion, result.WorkerBuild);
        Assert.True(result.VerifiedMoves > 1024);
        Assert.Equal(1, result.FailedMoves);
        Assert.Single(volume.Attempts, m => m.FileId == failedId);
        Assert.Contains(volume.Attempts, m => m.FileId == laterId);
        Assert.Equal(1, volume.BitmapRefreshes);
        Assert.Contains("other eligible files were processed", result.Message);
    }

    [Fact]
    public void PackContinuesPastSixtyFourBatchesUntilTheLeadingHoleIsFilled()
    {
        var volume = Volume(80, [File(32, [new(0, 1, 70)])]);

        var result = Run(volume, Request(Operation.Pack));

        Assert.Equal(JobState.Completed, result.State);
        Assert.Equal(70, result.VerifiedMoves);
        Assert.Equal(new Extent(0, 0, 70), Assert.Single(volume.Layout.Files[0].Extents));
        Assert.Equal(70, BitmapOperations.CountAllocated(volume.Layout.Bitmap));
    }

    [Fact]
    public void AmbiguousMoveIsReconciledBeforeAnotherBatchUsesFreeSpace()
    {
        var volume = ManyFiles(520);
        ulong failedId = volume.Layout.Files[^1].FileId;
        volume.BeforeMove = (file, move) =>
        {
            if (file.FileId != failedId) return;
            LayoutMutation.Apply(volume.Layout, move); // NTFS moved it, but verification failed.
            throw new IOException("Move result could not be verified");
        };

        var result = Run(volume, Request(Operation.Pack));

        Assert.Equal(JobState.Partial, result.State);
        Assert.Equal(1, result.FailedMoves);
        Assert.True(result.VerifiedMoves > 0);
        Assert.Equal(1, volume.BitmapRefreshes);
        Assert.Single(volume.Attempts, m => m.FileId == failedId);
        Assert.Equal(1040, BitmapOperations.CountAllocated(volume.Layout.Bitmap));
        Assert.Equal(2, volume.Scans);
    }

    [Fact]
    public void AllStreamsOfFailedIdentityAreSkippedAndJobTerminates()
    {
        var volume = Volume(80, [File(32, [new(0, 20, 2)]), File(32, [new(0, 30, 2)]) with { StreamName = ":extra:$DATA" }, File(33, [new(0, 40, 2)])]);
        volume.BeforeMove = (_, _) => throw new Win32Exception(5);

        var result = Run(volume, Request(Operation.Pack));

        Assert.Equal(JobState.Partial, result.State);
        Assert.Equal(2, result.FailedMoves);
        Assert.Equal(0, result.VerifiedMoves);
        Assert.Equal(2, volume.Attempts.Count);
        Assert.Equal(1, volume.BitmapRefreshes);
    }

    [Fact]
    public void BitmapRefreshFailureStopsBeforeReplanningStaleAllocation()
    {
        var volume = Volume(80, [File(32, [new(0, 1, 70)])]);
        volume.BeforeMove = (_, _) => throw new Win32Exception(5);
        volume.FailBitmapRefresh = true;

        var result = Run(volume, Request(Operation.Pack));

        Assert.Equal(JobState.Failed, result.State);
        Assert.Single(volume.Attempts);
        Assert.Contains("Cannot refresh bitmap", result.Message);
    }

    [Fact]
    public void ExplicitByteBudgetStillBoundsExecutionToWholeClusters()
    {
        var volume = Volume(80, [File(32, [new(0, 1, 70)])]);

        var result = Run(volume, Request(Operation.Pack) with { MaxMoveBytes = 5000 });

        Assert.Equal(JobState.Partial, result.State);
        Assert.Equal(4096, result.BytesMoved);
        Assert.Single(volume.Attempts);
    }

    [Fact]
    public void AnalysisPublishesRecommendationInputsForFilesAndNtfsMetadata()
    {
        var mft = File(0, [new(0, 20, 1), new(1, 24, 1), new(2, 28, 1)]) with { Path = @"V:\$MFT", Flags = StreamFlags.Metadata };
        var directoryIndex = File(12, [new(0, 40, 1), new(1, 44, 1)]) with
        {
            Path = @"V:\Projects", StreamName = ":$I30:$INDEX_ALLOCATION", Flags = StreamFlags.Directory
        };
        var ordinary = File(32, [new(0, 60, 1), new(1, 64, 1), new(2, 68, 1), new(3, 72, 1)]);
        var volume = Volume(100, [mft, directoryIndex, ordinary]);
        var request = Request(Operation.Analyze) with { Preview = true, MinimumFragments = 3 };

        var result = Run(volume, request);

        Assert.Equal(JobState.Completed, result.State);
        Assert.Equal(3, result.FragmentationThreshold);
        Assert.Equal(2, result.StreamsAtOrAboveThreshold);
        Assert.Equal(1, result.EligibleStreamsAtOrAboveThreshold);
        Assert.Equal(3, result.MftExtents);
        Assert.Equal(1, result.FragmentedDirectoryIndexes);
        Assert.Equal(0, result.DirectoryIndexesAtOrAboveThreshold);
        Assert.Equal(volume.Layout.TotalClusters, result.TotalClusters);
    }

    [Fact]
    public void ConcurrentExecutionReconcilesFailureAndHonorsTheSharedByteBudget()
    {
        var volume = ManyFiles(60);
        ulong failedId = volume.Layout.Files[0].FileId;
        volume.BeforeMove = (file, move) =>
        {
            if (file.FileId != failedId) return;
            LayoutMutation.Apply(volume.Layout, move);
            throw new IOException("Native move succeeded but verification failed");
        };
        var request = Request(Operation.MinimumWrite) with { MaxMoveBytes = 100 * 4096 };
        request = request with { Resources = request.Resources with { MoveQueueDepth = 4 } };
        var result = Run(volume, request);
        Assert.Equal(JobState.Partial, result.State);
        Assert.Equal(1, result.FailedMoves);
        Assert.Single(volume.Attempts, m => m.FileId == failedId);
        Assert.True(result.VerifiedMoves > 0);
        Assert.True(result.BytesMoved <= request.MaxMoveBytes);
        Assert.Equal(1, volume.BitmapRefreshes);
        Assert.Equal(120, BitmapOperations.CountAllocated(volume.Layout.Bitmap));
        Assert.Equal(4, result.Diagnostics?.Execution?.WorkerLimit);
        Assert.Equal(0, result.Diagnostics?.Execution?.ActiveWorkers);
        Assert.Equal(0, result.Diagnostics?.Execution?.InFlightIo);
    }

    [Fact]
    public void CancellationStillStopsAtTheNextMoveBoundary()
    {
        using var cancellation = new CancellationTokenSource();
        var volume = Volume(80, [File(32, [new(0, 1, 70)])]);
        volume.BeforeMove = (_, _) => cancellation.Cancel();

        var result = Run(volume, Request(Operation.Pack), cancellation.Token);

        Assert.Equal(JobState.Cancelled, result.State);
        Assert.Single(volume.Attempts);
    }

    [Theory]
    [InlineData(Operation.Alphabetical)]
    [InlineData(Operation.Size)]
    [InlineData(Operation.Created)]
    [InlineData(Operation.Modified)]
    [InlineData(Operation.Extension)]
    [InlineData(Operation.DirectoryLocality)]
    public void OrderingVisitsEveryFileOnceAcrossBatchesIncludingTiedKeys(Operation operation)
    {
        var layout = ManyFiles(1100).Layout;
        var planner = new LayoutPlanner();
        var session = new PlanningSession();
        var visits = new Dictionary<ulong, int>();
        long previousDestination = -1;
        int batches = 0;
        while (true)
        {
            var plan = planner.Plan(layout, Request(operation), session: session);
            if (plan.Moves.Length == 0) break;
            Assert.True(++batches < 10, "Ordered planning must converge without reshuffling earlier files.");
            foreach (var file in plan.Moves.GroupBy(m => m.FileId)) visits[file.Key] = visits.GetValueOrDefault(file.Key) + 1;
            foreach (var move in plan.Moves)
            {
                Assert.True(move.DestinationLcn > previousDestination);
                previousDestination = move.DestinationLcn;
                LayoutMutation.Apply(layout, move);
            }
        }
        Assert.True(batches > 1);
        Assert.Equal(1100, visits.Count);
        Assert.All(visits.Values, visits => Assert.Equal(1, visits));
        Assert.All(layout.Files, file => Assert.Single(file.Extents));
    }

    [Fact]
    public void OrderedFileThatOnlyFitsAFreshBatchIsDeferredWithoutBeingDropped()
    {
        var volume = ManyFiles(513);
        var layout = volume.Layout;
        // First 511 files need 1,022 moves. The next file needs three, so it
        // must be retried at the beginning of the next batch.
        var old = layout.Files[511];
        var extra = new Extent(2, 19000, 1);
        layout.Files[511] = old with { Extents = [.. old.Extents, extra], Size = 3 * 4096 };
        BitmapOperations.SetRange(layout.Bitmap, extra.Lcn, extra.Length, true);
        var planner = new LayoutPlanner(); var session = new PlanningSession();

        var first = planner.Plan(layout, Request(Operation.Alphabetical), session: session);
        Assert.Equal(1022, first.Moves.Length);
        foreach (var move in first.Moves) LayoutMutation.Apply(layout, move);
        var second = planner.Plan(layout, Request(Operation.Alphabetical), session: session);

        Assert.Equal(old.FileId, second.Moves[0].FileId);
        Assert.Equal(3, second.Moves.Count(m => m.FileId == old.FileId));
        Assert.Equal(0, second.FilesBlocked);
        Assert.Equal(513, second.FilesConsidered);
    }

    private static JobRequest Request(Operation operation) => new()
    {
        Volume = "V:\\", Operation = operation, Preview = false, MinimumFragments = 2,
        Resources = new() { AcOnly = false, MapCells = 256 }
    };

    private static JobSnapshot Run(TestVolume volume, JobRequest request, CancellationToken token = default)
    {
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JobStore(root);
            store.SaveRequest(request);
            new JobExecutor(store, _ => volume).Run(request, token);
            return Assert.IsType<JobSnapshot>(store.ReadReport(request.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static FileLayout File(ulong id, Extent[] extents) => new(id, $"V:\\{id:00000}.bin", "", 0,
        extents.Sum(e => e.Length) * 4096, 0, 0, extents);

    private static TestVolume ManyFiles(int count) => Volume(20000, Enumerable.Range(0, count)
        .Select(i => File((ulong)i + 32, [new(0, 10000 + i * 4, 1), new(1, 10002 + i * 4, 1)])).ToArray());

    private static TestVolume Volume(int clusters, FileLayout[] files)
    {
        byte[] bitmap = new byte[(clusters + 7) / 8];
        foreach (var extent in files.SelectMany(f => f.Extents)) BitmapOperations.SetRange(bitmap, extent.Lcn, extent.Length, true);
        var info = new VolumeInfo("test", "V:\\", "fixture", "NTFS", clusters * 4096L,
            (clusters - BitmapOperations.CountAllocated(bitmap)) * 4096, 4096, true, false, [], "fixture");
        return new(new(info, clusters, bitmap, files, DateTimeOffset.UtcNow, files.Length, 0, true, []));
    }

    private sealed class TestVolume(VolumeLayout layout) : IJobVolume
    {
        private readonly object gate = new();
        public VolumeLayout Layout { get; } = layout;
        public VolumeInfo Info => Layout.Volume;
        public List<PlannedMove> Attempts { get; } = [];
        public Action<FileLayout, PlannedMove>? BeforeMove { get; set; }
        public int BitmapRefreshes { get; private set; }
        public int Scans { get; private set; }
        public bool FailBitmapRefresh { get; set; }
        public bool IsDirty() => false;
        public VolumeLayout Scan(JobRequest request, Action<double, long, string> progress, Action checkpoint, CancellationToken token, Action<WorkProgress>? diagnostics = null)
        {
            checkpoint(); Scans++;
            return Layout with { Bitmap = Layout.Bitmap.ToArray(), Files = Layout.Files.ToArray() };
        }
        public byte[] ReadBitmap(JobRequest request, Action checkpoint, CancellationToken token)
        {
            checkpoint(); BitmapRefreshes++;
            if (FailBitmapRefresh) throw new IOException("Cannot refresh bitmap");
            return Layout.Bitmap.ToArray();
        }
        public void ExecuteMove(FileLayout file, PlannedMove move, PathRules rules)
        {
            lock (gate)
            {
            Attempts.Add(move);
            BeforeMove?.Invoke(file, move);
            // A separate volume model detects stale sources, occupied destinations,
            // and accidental reuse of allocation after an ambiguous native result.
            LayoutMutation.Apply(Layout, move);
            }
        }
        public void Dispose() { }
    }
}
