using System.ComponentModel;
using Tedd.Defrag.Core;
using Tedd.Defrag.Engine;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Planning;
using Tedd.Defrag.Windows;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class ExecutionContinuationTests
{
    [Fact]
    public void FinishedSelectedFilesDoNotImplyThatTheWholeVolumeIsOrdered()
    {
        var volume = Volume(6, [File(32, [new(0, 0, 1)]), File(33, [new(0, 2, 1)]), File(34, [new(0, 1, 1)])]);
        var selected = Request(Operation.Alphabetical) with
        {
            SelectedPaths = [new(volume.Layout.Files[0].Path)], MinimumFragments = int.MaxValue
        };
        var limited = Run(volume, selected);
        Assert.Equal(0, limited.BytesMoved);
        Assert.Equal(1, limited.FilesConsidered);
        Assert.Contains("Requested layout already satisfied within the selected scope", limited.Message);
        Assert.Contains("1 inclusion rule; other files are outside this job", limited.Message);
        Assert.Equal(2, volume.Layout.Files[1].Extents[0].Lcn);

        var wholeVolume = Run(volume, selected with { Id = Guid.NewGuid(), SelectedPaths = [] });
        Assert.Equal(JobState.Completed, wholeVolume.State);
        Assert.Equal(3, wholeVolume.FilesConsidered);
        Assert.True(wholeVolume.BytesMoved > 0);
        Assert.Contains("Optimization scope: entire volume", wholeVolume.Message);
        Assert.Equal(1, volume.Layout.Files[1].Extents[0].Lcn);
        Assert.Equal(2, volume.Layout.Files[2].Extents[0].Lcn);
    }

    [Fact]
    public void WholeVolumeOrderingStillPreservesExplicitExclusions()
    {
        var volume = Volume(6, [File(32, [new(0, 2, 1)]), File(33, [new(0, 0, 1)]), File(34, [new(0, 1, 1)])]);
        var request = Request(Operation.Alphabetical) with { Exclusions = [new(volume.Layout.Files[1].Path)] };
        var result = Run(volume, request);
        Assert.True(result.BytesMoved > 0);
        Assert.DoesNotContain(volume.Attempts, move => move.FileId == 33);
        Assert.Equal(0, volume.Layout.Files[1].Extents[0].Lcn);
    }

    [Fact]
    public void AnalysisReportIdentifiesTheScopeOfSubsequentOptimization()
    {
        var volume = ManyFiles(3);
        var request = Request(Operation.Analyze) with { SelectedPaths = [new(volume.Layout.Files[0].Path)] };
        var result = Run(volume, request);
        Assert.Equal(3, result.TotalFiles);
        Assert.Contains("Optimization scope: 1 inclusion rule", result.Message);
    }

    [Fact]
    public void NoMoveReportStillDisclosesIncompleteFileCoverage()
    {
        var observed = Volume(3, [File(32, [new(0, 0, 1)])]).Layout with { Complete = false };
        var result = Run(new TestVolume(observed), Request(Operation.Alphabetical));
        Assert.Equal(JobState.Partial, result.State);
        Assert.Equal(0, result.BytesMoved);
        Assert.Contains("File coverage is partial", result.Message);
    }

    [Fact]
    public void OrderedJobCompletesAfterClearingOccupiedDestinations()
    {
        var volume = Volume(3, [File(32, [new(0, 1, 1)]), File(33, [new(0, 0, 1)])]);
        var request = Request(Operation.Alphabetical) with { MinimumFragments = 1, Resources = Request(Operation.Alphabetical).Resources with { MoveQueueDepth = 4 } };
        var result = Run(volume, request);
        Assert.Equal(JobState.Completed, result.State);
        Assert.Equal(3, result.VerifiedMoves);
        Assert.Equal(2, volume.Scans);
        Assert.Equal(0, volume.Layout.Files[0].Extents[0].Lcn);
        Assert.Equal(1, volume.Layout.Files[1].Extents[0].Lcn);
    }

    [Fact]
    public void FinalReconciliationDetectsAChangedOrderedPlacementWithoutRestarting()
    {
        var volume = Volume(5, [File(32, [new(0, 2, 1)]), File(33, [new(0, 3, 1)])]);
        volume.BeforeScan = () =>
        {
            if (volume.Scans == 1) LayoutMutation.Apply(volume.Layout, new(32, 0, 0, 0, 4, 1));
        };
        var result = Run(volume, Request(Operation.Alphabetical));
        Assert.Equal(JobState.Partial, result.State);
        Assert.Equal(2, volume.Scans);
        Assert.Equal(2, result.VerifiedMoves);
        Assert.Contains("1 planned placement was not confirmed", result.Message);
    }

    [Theory]
    [InlineData(Operation.MinimumWrite)]
    [InlineData(Operation.FilesOnly)]
    [InlineData(Operation.PackAndDefrag)]
    public void DefragJobCompletesAfterRelocatingAContiguousBlocker(Operation operation)
    {
        var volume = Volume(10, [File(32, [new(0, 0, 4), new(4, 8, 1)]), File(33, [new(0, 4, 4)])]);
        var result = Run(volume, Request(operation));
        Assert.Equal(JobState.Completed, result.State);
        Assert.All(volume.Layout.Files, file => Assert.Single(file.Extents));
        Assert.Equal(6 * 4096, result.BytesMoved);
    }

    [Fact]
    public void RejectedOrderedMoveKeepsLaterTargetsAndReleasesSkippedReservations()
    {
        var volume = ManyFiles(3);
        volume.BeforeMove = (file, _) =>
        {
            if (file.FileId == 32) throw new MovePreconditionException("File changed");
        };
        var result = Run(volume, Request(Operation.Alphabetical));
        Assert.Equal(JobState.Partial, result.State);
        Assert.Equal(1, result.FailedMoves);
        Assert.Equal(0, volume.BitmapRefreshes);
        Assert.Equal(2, volume.Layout.Files[1].Extents[0].Lcn);
        Assert.Equal(4, volume.Layout.Files[2].Extents[0].Lcn);
        Assert.Single(volume.Attempts, move => move.FileId == 32);
    }

    [Fact]
    public void RelocationJobsDoNotInspectCompressionInventory()
    {
        var volume = ManyFiles(3);
        var request = Request(Operation.Alphabetical);
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JobStore(root); store.SaveRequest(request);
            new JobExecutor(store, _ => volume, readCompressionInventory: (_, _, _) =>
                throw new InvalidOperationException("Unexpected compression inspection")).Run(request, default);
            var report = store.ReadReport(request.Id)!;
            Assert.Equal(JobState.Completed, report.State);
            Assert.Null(report.CompressionObservedAt);
            Assert.Null(report.CompressedFiles);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void TemporaryPlacementConstraintsDoNotKeepAResolvedJobPartial()
    {
        var volume = Volume(13, [File(32, [new(0, 0, 4), new(4, 8, 1)]), File(33, [new(0, 4, 4)]),
            File(34, [new(0, 10, 1), new(1, 12, 1)])]);
        var result = Run(volume, Request(Operation.MinimumWrite));
        Assert.Equal(JobState.Completed, result.State);
        Assert.Equal(0, result.FilesBlocked);
        Assert.All(volume.Layout.Files, file => Assert.Single(file.Extents));
    }

    [Fact]
    public void MetadataCompletionIgnoresUnrelatedFragmentedData()
    {
        var mft = File(0, [new(0, 0, 4), new(4, 8, 1)]) with { Flags = StreamFlags.Metadata };
        var volume = Volume(13, [mft, File(33, [new(0, 4, 4)]), File(34, [new(0, 10, 1), new(1, 12, 1)])]);
        var result = Run(volume, Request(Operation.OptimizeMft));
        Assert.Equal(JobState.Completed, result.State);
        Assert.Single(volume.Layout.Files[0].Extents);
        Assert.True(volume.Layout.Files[2].Fragmented);
    }

    [Theory]
    [InlineData(Operation.Pack)]
    [InlineData(Operation.PackAndDefrag)]
    [InlineData(Operation.MinimumWrite)]
    [InlineData(Operation.Alphabetical)]
    public void TotalProgressPersistsAcrossBatchesAndWaitsForFinalReconciliation(Operation operation)
    {
        var volume = ManyFiles(1100);
        var request = Request(operation);
        var batches = new List<JobSnapshot>();
        JobSnapshot? reconciliation = null;
        var result = Run(volume, request, observe: store =>
        {
            volume.BeforeMove = (_, _) =>
            {
                var snapshot = store.ReadSnapshot(request.Id)!;
                if (batches.Count == 0 || batches[^1].PlannedMoves != snapshot.PlannedMoves) batches.Add(snapshot);
            };
            volume.BeforeScan = () =>
            {
                if (volume.Scans > 0) reconciliation = store.ReadSnapshot(request.Id);
            };
        });

        Assert.True(batches.Count > 1);
        Assert.Equal(0d, batches[0].TotalProgress);
        for (int i = 1; i < batches.Count; i++)
        {
            Assert.Equal(0, batches[i].Progress);
            Assert.True(batches[i].TotalProgress > batches[i - 1].TotalProgress);
            Assert.InRange(batches[i].TotalProgress!.Value, .001, .99);
        }
        Assert.NotNull(reconciliation);
        Assert.Equal(JobState.Scanning, reconciliation.State);
        Assert.Equal(.99, reconciliation.TotalProgress);
        Assert.Equal(1d, result.TotalProgress);
    }

    [Fact]
    public void CancellationRetainsIncompleteTotalProgress()
    {
        using var cancellation = new CancellationTokenSource();
        var volume = ManyFiles(1100);
        volume.BeforeMove = (_, _) =>
        {
            if (volume.Attempts.Count > 1024) cancellation.Cancel();
        };

        var result = Run(volume, Request(Operation.MinimumWrite), cancellation.Token);

        Assert.Equal(JobState.Cancelled, result.State);
        Assert.InRange(result.TotalProgress!.Value, .001, .99);
    }

    [Fact]
    public void TotalEstimateHonorsEligibilityPathsSizesAndByteBudget()
    {
        var volume = ManyFiles(6);
        volume.Layout.Files[0] = volume.Layout.Files[0] with { Flags = StreamFlags.Excluded };
        volume.Layout.Files[1] = volume.Layout.Files[1] with { Flags = StreamFlags.Metadata };
        volume.Layout.Files[2] = volume.Layout.Files[2] with { Flags = StreamFlags.Directory };
        volume.Layout.Files[3] = volume.Layout.Files[3] with { Size = 1 };
        var request = Request(Operation.MinimumWrite) with
        {
            MinimumFileBytes = 4096,
            Exclusions = [new(volume.Layout.Files[4].Path)]
        };
        var rules = new PathRules(request.SelectedPaths, request.Exclusions);

        Assert.Equal(8192, JobProgressEstimate.MoveBytes(volume.Layout, request, rules));
        Assert.Equal(4096, JobProgressEstimate.MoveBytes(volume.Layout, request with { MaxMoveBytes = 4096 }, rules));
        Assert.Equal(0, JobProgressEstimate.MoveBytes(volume.Layout, request with { MinimumFragments = 3 }, rules));
        Assert.Equal(0, JobProgressEstimate.MoveBytes(volume.Layout, request with { MaximumFileBytes = 4096 }, rules));
        Assert.Equal(0, JobProgressEstimate.MoveBytes(volume.Layout, request, new([new(@"V:\Other")], [])));
    }

    [Fact]
    public void ShrinkEstimateCountsOnlyAllocationBeyondTheBoundary()
    {
        var volume = Volume(100, [File(32, [new(0, 10, 20), new(20, 60, 20)])]);
        var request = Request(Operation.PrepareShrink) with { ShrinkBoundaryBytes = 70L * 4096 };

        Assert.Equal(10L * 4096, JobProgressEstimate.MoveBytes(volume.Layout, request, new([], [])));
    }

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
    public void PackFillsALeadingHoleDirectlyFromTheTail()
    {
        var volume = Volume(80, [File(32, [new(0, 1, 70)])]);

        var result = Run(volume, Request(Operation.Pack));

        Assert.Equal(JobState.Completed, result.State);
        Assert.Equal(1, result.VerifiedMoves);
        var move = Assert.Single(volume.Attempts);
        Assert.Equal(70, move.SourceLcn);
        Assert.Equal(0, move.DestinationLcn);
        Assert.Equal(70, BitmapOperations.CountRange(volume.Layout.Bitmap, 0, 70));
        Assert.Equal(70, BitmapOperations.CountAllocated(volume.Layout.Bitmap));
    }

    [Fact]
    public void DeferredVerificationSkipsPerMoveExtentChecksAndReconcilesWithFinalScan()
    {
        var volume = ManyFiles(2);

        var result = Run(volume, Request(Operation.MinimumWrite) with
        {
            Resources = Request(Operation.MinimumWrite).Resources with { VerifyMoves = false }
        });

        Assert.Equal(JobState.Completed, result.State);
        Assert.Equal(0, result.VerifiedMoves);
        Assert.Equal(2, volume.Attempts.Count);
        Assert.All(volume.VerifyResults, Assert.False);
        Assert.Equal(2, volume.Scans);
        Assert.Contains("final scan reconciled", result.Message);
    }

    [Fact]
    public void PackRanksFilesAndExtentsByPhysicalLocation()
    {
        var highFirstLogicalRun = File(32, [new(0, 100, 1), new(1, 10, 1)]);
        var middle = File(33, [new(0, 90, 1)]);
        var layout = Volume(128, [highFirstLogicalRun, middle]).Layout;

        var plan = new LayoutPlanner().Plan(layout, Request(Operation.Pack));

        Assert.Equal(100, plan.Moves[0].SourceLcn);
        Assert.Equal(0, plan.Moves[0].DestinationLcn);
    }

    [Fact]
    public void PackReusesPhysicalAndFreeSpaceCursorsAcrossExecutionWindows()
    {
        var layout = ManyFiles(1100).Layout;
        var planner = new LayoutPlanner();
        using var session = new PlanningSession();
        var phases = new List<string>();

        var first = planner.Plan(layout, Request(Operation.Pack), session: session, progress: p => phases.Add(p.Phase));
        Assert.Equal(1024, first.Moves.Length);
        Assert.Contains("Indexing free space", phases);
        Assert.Contains("Indexing physical extents", phases);
        Assert.Contains("Ordering physical extents", phases);
        Assert.DoesNotContain("Sorting candidates", phases);
        foreach (var move in first.Moves) LayoutMutation.Apply(layout, move);

        phases.Clear();
        var second = planner.Plan(layout, Request(Operation.Pack), session: session, progress: p => phases.Add(p.Phase));

        Assert.NotEmpty(second.Moves);
        Assert.DoesNotContain("Sorting candidates", phases);
        Assert.DoesNotContain("Indexing free space", phases);
        Assert.DoesNotContain("Indexing physical extents", phases);
        Assert.Contains("Continuing pack plan", phases);
    }

    [Fact]
    public void MinimumWriteRetainsCandidateAndFreeSpaceIndexesAcrossExecutionWindows()
    {
        var layout = ManyFiles(1100).Layout;
        var planner = new LayoutPlanner();
        using var session = new PlanningSession();
        var phases = new List<string>();

        var first = planner.Plan(layout, Request(Operation.MinimumWrite), session: session, progress: p => phases.Add(p.Phase));
        Assert.Equal(1024, first.Moves.Length);
        Assert.Contains("Indexing free space", phases);
        Assert.Contains("Indexing defrag candidates", phases);
        Assert.Contains("Sorting candidates", phases);
        foreach (var move in first.Moves)
        {
            LayoutMutation.Apply(layout, move);
            session.MoveVerified(move);
        }

        phases.Clear();
        var second = planner.Plan(layout, Request(Operation.MinimumWrite), session: session, progress: p => phases.Add(p.Phase));

        Assert.NotEmpty(second.Moves);
        Assert.DoesNotContain("Indexing free space", phases);
        Assert.DoesNotContain("Indexing defrag candidates", phases);
        Assert.DoesNotContain("Sorting candidates", phases);
        Assert.Contains("Free-space index ready", phases);
        Assert.Contains("Continuing minimum-write plan", phases);
    }

    [Fact]
    public void MinimumWriteCrossesExecutionWindowsWithoutRefreshingAllocation()
    {
        var volume = ManyFiles(1100);

        var result = Run(volume, Request(Operation.MinimumWrite));

        Assert.Equal(JobState.Completed, result.State);
        Assert.Equal(1100, result.VerifiedMoves);
        Assert.Equal(0, volume.BitmapRefreshes);
        Assert.All(volume.Layout.Files, file => Assert.Single(file.Extents));
    }

    [Fact]
    public void PreflightRejectionDoesNotInvalidateMinimumWriteAllocationState()
    {
        var volume = ManyFiles(1030);
        ulong rejectedId = volume.Layout.Files[0].FileId;
        volume.BeforeMove = (file, _) =>
        {
            if (file.FileId == rejectedId) throw new MovePreconditionException("Source changed before submission");
        };

        var result = Run(volume, Request(Operation.MinimumWrite));

        Assert.Equal(JobState.Partial, result.State);
        Assert.Equal(1, result.FailedMoves);
        Assert.Equal(0, volume.BitmapRefreshes);
        Assert.True(result.VerifiedMoves > 1024);
        Assert.Single(volume.Attempts, move => move.FileId == rejectedId);
    }

    [Fact]
    public void MinimumWriteRetainsCandidateOrderWhenAllocationMustBeRebuilt()
    {
        var layout = ManyFiles(1100).Layout;
        var planner = new LayoutPlanner();
        using var session = new PlanningSession();
        var first = planner.Plan(layout, Request(Operation.MinimumWrite), session: session);
        foreach (var move in first.Moves)
        {
            LayoutMutation.Apply(layout, move);
            session.MoveVerified(move);
        }
        session.InvalidateAllocationPlan(Operation.MinimumWrite);
        var phases = new List<string>();

        var second = planner.Plan(layout, Request(Operation.MinimumWrite), session: session, progress: p => phases.Add(p.Phase));

        Assert.NotEmpty(second.Moves);
        Assert.Contains("Indexing free space", phases);
        Assert.Contains("Candidate order ready", phases);
        Assert.DoesNotContain("Indexing defrag candidates", phases);
        Assert.DoesNotContain("Sorting candidates", phases);
    }

    [Fact]
    public void MinimumWriteSortsOnlyEligibleFragmentedCandidates()
    {
        var contiguous = Enumerable.Range(0, 5000)
            .Select(index => File((ulong)index + 32, [new(0, 10000 + index, 1)]));
        var fragmented = File(6000, [new(0, 20000, 1), new(1, 20002, 1)]);
        var layout = Volume(24000, [.. contiguous, fragmented]).Layout;
        var stages = new List<WorkProgress>();
        using var session = new PlanningSession();

        var plan = new LayoutPlanner().Plan(layout, Request(Operation.MinimumWrite), session: session, progress: stages.Add);

        Assert.NotEmpty(plan.Moves);
        Assert.All(plan.Moves, move => Assert.Equal(fragmented.FileId, move.FileId));
        Assert.Contains(stages, stage => stage.Phase == "Sorting candidates" && stage.Total == 1);
        Assert.Contains(stages, stage => stage.Phase == "Plan ready" && stage.Total == 1);
    }

    [Fact]
    public void PackRadixOrdersLargePhysicalExtentColumnsFromTheTail()
    {
        var files = Enumerable.Range(0, 5000)
            .Select(i => File((ulong)i + 32, [new(0, 10000 + i, 1)]))
            .ToArray();
        var layout = Volume(20000, files).Layout;
        var phases = new List<string>();

        var plan = new LayoutPlanner().Plan(layout, Request(Operation.Pack), session: new PlanningSession(), progress: p => phases.Add(p.Phase));

        Assert.Equal(1024, plan.Moves.Length);
        Assert.Equal(14999, plan.Moves[0].SourceLcn);
        Assert.Equal(0, plan.Moves[0].DestinationLcn);
        Assert.True(plan.Moves.Zip(plan.Moves.Skip(1)).All(pair => pair.First.SourceLcn > pair.Second.SourceLcn));
        Assert.Contains("Ordering physical extents", phases);
    }

    [Fact]
    public void PackProducesADenseAllocatedPrefixAcrossExecutionWindows()
    {
        const int totalClusters = 4000, allocatedClusters = 2000;
        var positions = Enumerable.Range(allocatedClusters, allocatedClusters).ToArray();
        var layout = Volume(totalClusters, positions.Select((lcn, index) => File((ulong)index + 32, [new(0, lcn, 1)])).ToArray()).Layout;
        var planner = new LayoutPlanner();
        using var session = new PlanningSession();
        int windows = 0;

        while (true)
        {
            var plan = planner.Plan(layout, Request(Operation.Pack), session: session);
            if (plan.Moves.Length == 0) break;
            Assert.True(++windows < 10);
            foreach (var move in plan.Moves) LayoutMutation.Apply(layout, move);
        }

        Assert.True(windows > 1);
        Assert.Equal(allocatedClusters, BitmapOperations.CountRange(layout.Bitmap, 0, allocatedClusters));
        Assert.Equal(allocatedClusters, BitmapOperations.CountAllocated(layout.Bitmap));
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
        using var session = new PlanningSession();
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
                session.MoveVerified(move);
            }
        }
        Assert.True(batches > 1);
        Assert.Equal(1100, visits.Count);
        Assert.All(visits.Values, visits => Assert.Equal(1, visits));
        Assert.All(layout.Files, file => Assert.Single(file.Extents));
    }

    [Fact]
    public void OrderedFileCanContinueAcrossAnExecutionWindowWithoutBeingDropped()
    {
        var volume = ManyFiles(513);
        var layout = volume.Layout;
        // The next file spans the boundary of the 1,024-move execution window.
        var old = layout.Files[511];
        var extra = new Extent(2, 19000, 1);
        layout.Files[511] = old with { Extents = [.. old.Extents, extra], Size = 3 * 4096 };
        BitmapOperations.SetRange(layout.Bitmap, extra.Lcn, extra.Length, true);
        var planner = new LayoutPlanner(); using var session = new PlanningSession();

        var first = planner.Plan(layout, Request(Operation.Alphabetical), session: session);
        Assert.Equal(1024, first.Moves.Length);
        foreach (var move in first.Moves) { LayoutMutation.Apply(layout, move); session.MoveVerified(move); }
        var second = planner.Plan(layout, Request(Operation.Alphabetical), session: session);

        Assert.Equal(old.FileId, second.Moves[0].FileId);
        Assert.Single(second.Moves, m => m.FileId == old.FileId);
        Assert.Equal(0, second.FilesBlocked);
        Assert.Equal(513, second.FilesConsidered);
    }

    [Fact]
    public void OrderedPassReusesItsImmutableCandidateColumnsAcrossBatches()
    {
        var layout = ManyFiles(1100).Layout;
        var planner = new LayoutPlanner();
        using var session = new PlanningSession();
        var phases = new List<string>();

        var first = planner.Plan(layout, Request(Operation.Size), session: session, progress: p => phases.Add(p.Phase));
        Assert.Equal(1024, first.Moves.Length);
        Assert.Contains("Sorting candidates", phases);
        foreach (var move in first.Moves) { LayoutMutation.Apply(layout, move); session.MoveVerified(move); }

        phases.Clear();
        var second = planner.Plan(layout, Request(Operation.Size), session: session, progress: p => phases.Add(p.Phase));

        Assert.NotEmpty(second.Moves);
        Assert.DoesNotContain("Sorting candidates", phases);
        Assert.DoesNotContain("Indexing free space", phases);
        Assert.Contains("Continuing relocation plan", phases);
    }

    [Theory]
    [InlineData(Operation.FilesOnly)]
    [InlineData(Operation.PrepareShrink)]
    public void StablePoliciesRetainCandidateAndAllocationIndexesAcrossWindows(Operation operation)
    {
        var layout = ManyFiles(1100).Layout;
        var planner = new LayoutPlanner();
        using var session = new PlanningSession();
        var request = Request(operation) with { ShrinkBoundaryBytes = 10000L * 4096 };
        var first = planner.Plan(layout, request, session: session);
        Assert.Equal(1024, first.Moves.Length);
        foreach (var move in first.Moves) { LayoutMutation.Apply(layout, move); session.MoveVerified(move); }
        var phases = new List<string>();

        var second = planner.Plan(layout, request, session: session, progress: p => phases.Add(p.Phase));

        Assert.NotEmpty(second.Moves);
        Assert.DoesNotContain("Indexing free space", phases);
        Assert.DoesNotContain("Indexing candidates", phases);
        Assert.DoesNotContain("Sorting candidates", phases);
        Assert.Contains("Candidate order ready", phases);
    }

    [Fact]
    public void DynamicPackAndDefragRetainsItsAllocationIndexAcrossWindows()
    {
        var layout = ManyFiles(1100).Layout;
        var planner = new LayoutPlanner();
        using var session = new PlanningSession();
        var first = planner.Plan(layout, Request(Operation.PackAndDefrag), session: session);
        foreach (var move in first.Moves) { LayoutMutation.Apply(layout, move); session.MoveVerified(move); }
        var phases = new List<string>();

        var second = planner.Plan(layout, Request(Operation.PackAndDefrag), session: session, progress: p => phases.Add(p.Phase));

        Assert.NotEmpty(second.Moves);
        Assert.DoesNotContain("Indexing free space", phases);
        Assert.Contains("Free-space index ready", phases);
        Assert.Contains("Sorting candidates", phases);
    }

    [Theory]
    [InlineData(Operation.MinimumWrite)]
    [InlineData(Operation.FilesOnly)]
    [InlineData(Operation.Pack)]
    [InlineData(Operation.PackAndDefrag)]
    [InlineData(Operation.Alphabetical)]
    public void ExecutionWindowScalesWithMoveQueueDepth(Operation operation)
    {
        var files = Enumerable.Range(0, 5000)
            .Select(index => File((ulong)index + 32, [new(0, 10000 + index * 4, 1), new(1, 10002 + index * 4, 1)]))
            .ToArray();
        var request = Request(operation) with { Resources = Request(operation).Resources with { MoveQueueDepth = 4 } };

        var plan = new LayoutPlanner().Plan(Volume(32000, files).Layout, request);

        Assert.Equal(4096, plan.Moves.Length);
    }

    [Fact]
    public void CompressionRunsBeforeTheFirstMftScan()
    {
        var volume = Volume(64, []);
        var request = Request(Operation.Analyze) with
        {
            CompressionTargets = [new(new PathRule(@"V:\Data"), CompressionMode.Xpress4K)]
        };
        bool compressionRan = false;
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JobStore(root);
            store.SaveRequest(request);
            new JobExecutor(store, _ => volume, runCompression: (_, _, reportProgress, _, _) =>
            {
                Assert.Equal(0, volume.Scans);
                compressionRan = true;
                reportProgress(new("Finding best compression · testing LZX", @"V:\Data\compressed.bin",
                    2, 0, 1, 0, 0, 0, 16384, 0, 8192, 0));
                var active = Assert.IsType<JobSnapshot>(store.ReadSnapshot(request.Id));
                Assert.Equal("Finding best compression · testing LZX", active.CompressionStatus);
                Assert.Equal(1, active.CompressionFilesWaiting);
                Assert.Equal(8192, active.CompressionBytesWaiting);
                Assert.Equal(@"V:\Data\compressed.bin", active.CompressionCurrentPath);
                return new(2, 1, 1, 0, 4096, []);
            }, readCompressionInventory: (_, _, _) => new(
                [new(@"V:\Data\compressed.bin", "XPRESS 4K", 8192, 4096)], 1, 8192, 4096, [])).Run(request, default);

            Assert.True(compressionRan);
            Assert.Equal(1, volume.Scans);
            var report = Assert.IsType<JobSnapshot>(store.ReadReport(request.Id));
            Assert.Equal(2, report.CompressionFilesProcessed);
            Assert.Equal(0, report.CompressionFilesWaiting);
            Assert.Equal(1, report.CompressionFilesChanged);
            Assert.Equal(4096, report.CompressionBytesSaved);
            Assert.Equal("Compression complete", report.CompressionStatus);
            Assert.Equal(1, report.CompressedFileCount);
            Assert.NotNull(report.CompressionObservedAt);
            Assert.Equal(4096, report.CompressedBytesSaved);
            Assert.Equal("XPRESS 4K", Assert.Single(report.CompressedFiles!).CompressionType);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static JobRequest Request(Operation operation) => new()
    {
        Volume = "V:\\", Operation = operation, Preview = false, MinimumFragments = 2,
        Resources = new() { AcOnly = false, MapCells = 256 }
    };

    private static JobSnapshot Run(TestVolume volume, JobRequest request, CancellationToken token = default, Action<JobStore>? observe = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JobStore(root);
            store.SaveRequest(request);
            observe?.Invoke(store);
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
        public List<bool> VerifyResults { get; } = [];
        public Action<FileLayout, PlannedMove>? BeforeMove { get; set; }
        public Action? BeforeScan { get; set; }
        public int BitmapRefreshes { get; private set; }
        public int Scans { get; private set; }
        public bool FailBitmapRefresh { get; set; }
        public bool IsDirty() => false;
        public VolumeLayout Scan(JobRequest request, Action<double, long, string> progress, Action checkpoint, CancellationToken token, Action<WorkProgress>? diagnostics = null, Action<ScanMapUpdate>? mapProgress = null)
        {
            checkpoint(); BeforeScan?.Invoke(); Scans++;
            return Layout with { Bitmap = Layout.Bitmap.ToArray(), Files = Layout.Files.ToArray() };
        }
        public byte[] ReadBitmap(JobRequest request, Action checkpoint, CancellationToken token)
        {
            checkpoint(); BitmapRefreshes++;
            if (FailBitmapRefresh) throw new IOException("Cannot refresh bitmap");
            return Layout.Bitmap.ToArray();
        }
        public void ExecuteMove(FileLayout file, PlannedMove move, PathRules rules, bool verifyResult)
        {
            lock (gate)
            {
            Attempts.Add(move);
            VerifyResults.Add(verifyResult);
            BeforeMove?.Invoke(file, move);
            // A separate volume model detects stale sources, occupied destinations,
            // and accidental reuse of allocation after an ambiguous native result.
            LayoutMutation.Apply(Layout, move);
            }
        }
        public void Dispose() { }
    }
}
