using Tedd.Defrag.Core;
using Tedd.Defrag.Engine;
using Tedd.Defrag.Planning;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class RelocationPlanningTests
{
    [Fact]
    public void OrderedSwapUsesOneScratchClusterAndThreeMoves()
    {
        var layout = Layout(3, File(32, new Extent(0, 1, 1)), File(33, new Extent(0, 0, 1)));
        var moves = Execute(layout, Operation.Alphabetical);
        Assert.Equal(3, moves.Count);
        Assert.Equal(3, moves.Sum(move => move.Clusters));
        Assert.Equal(0, layout.Files[0].Extents[0].Lcn);
        Assert.Equal(1, layout.Files[1].Extents[0].Lcn);
    }

    [Fact]
    public void DirectDependenciesNeedNoStagingAndExistingPlacementsNeedNoWrites()
    {
        var layout = Layout(5, File(32, new Extent(0, 0, 1)), File(33, new Extent(0, 2, 1)), File(34, new Extent(0, 3, 1)));
        var moves = Execute(layout, Operation.Alphabetical);
        Assert.Equal(2, moves.Count);
        Assert.DoesNotContain(moves, move => move.FileIndex == 0);
        Assert.Empty(Execute(layout, Operation.Alphabetical));
    }

    [Fact]
    public void LargeOverlappingFileContinuesThroughBoundedWindows()
    {
        var layout = Layout(2000, File(32, new Extent(0, 1, 1500)));
        var moves = Execute(layout, Operation.Alphabetical);
        Assert.Equal(1500, moves.Sum(move => move.Clusters));
        Assert.Equal(new Extent(0, 0, 1500), Assert.Single(layout.Files[0].Extents));
    }

    [Fact]
    public void DensePermutationsConvergeWithoutOverwritingAllocatedClusters()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var random = new Random(seed);
            const int count = 30;
            int[] permutation = Enumerable.Range(0, count).OrderBy(_ => random.Next()).ToArray();
            var layout = Layout(count + 1, permutation.Select((source, index) => File((ulong)index + 32, new Extent(0, source, 1))).ToArray());
            var moves = Execute(layout, Operation.Alphabetical);
            Assert.True(moves.Count <= count * 2);
            for (int index = 0; index < count; index++) Assert.Equal(index, layout.Files[index].Extents[0].Lcn);
        }
    }

    [Fact]
    public void FragmentedVariableSizeFilesConvergeWithOnlyOneFreeCluster()
    {
        for (int seed = 0; seed < 30; seed++)
        {
            var random = new Random(seed);
            int[] positions = Enumerable.Range(0, 60).OrderBy(_ => random.Next()).ToArray();
            int offset = 0;
            var files = Enumerable.Range(0, 20).Select(index =>
            {
                int size = 1 + index % 5;
                return File((ulong)index + 32, Enumerable.Range(0, size).Select(vcn => new Extent(vcn, positions[offset++], 1)).ToArray());
            }).ToArray();
            var layout = Layout(61, files);
            Execute(layout, Operation.Alphabetical);
            long target = 0;
            foreach (var file in layout.Files)
            {
                Assert.Equal(new Extent(0, target, file.Size / 4096), Assert.Single(file.Extents));
                target += file.Size / 4096;
            }
        }
    }

    [Fact]
    public void UnknownExcludedAndCompressionAllocationRemainBarriers()
    {
        var excluded = File(34, new Extent(0, 2, 1)) with { Flags = StreamFlags.Excluded };
        var compressed = File(35, new Extent(0, 4, 1)) with { Flags = StreamFlags.Compressed };
        var layout = Layout(8, File(32, new Extent(0, 6, 1)), File(33, new Extent(0, 7, 1)), excluded, compressed);
        BitmapOperations.SetRange(layout.Bitmap, 0, 1, true);
        var moves = Execute(layout, Operation.Alphabetical);
        Assert.All(moves, move => Assert.DoesNotContain(move.DestinationLcn, new long[] { 0, 2, 4 }));
        Assert.Equal(excluded, layout.Files[2]); Assert.Equal(compressed, layout.Files[3]);
        Assert.Equal(1, layout.Files[0].Extents[0].Lcn);
        Assert.Equal(3, layout.Files[1].Extents[0].Lcn);
    }

    [Theory]
    [InlineData(Operation.MinimumWrite)]
    [InlineData(Operation.FilesOnly)]
    [InlineData(Operation.PackAndDefrag)]
    public void DefragClearsBlockersWhenNoWholeFileFreeRangeExists(Operation operation)
    {
        var layout = Layout(10, File(32, new Extent(0, 0, 4), new Extent(4, 8, 1)), File(33, new Extent(0, 4, 4)));
        var moves = Execute(layout, operation);
        Assert.Contains(moves, move => move.FileIndex == 1);
        Assert.DoesNotContain(moves, move => move.FileIndex == 0 && move.Vcn < 4);
        Assert.Equal(new Extent(0, 0, 5), Assert.Single(layout.Files[0].Extents));
        Assert.Equal(new Extent(0, 5, 4), Assert.Single(layout.Files[1].Extents));
        Assert.Equal(6, moves.Sum(move => move.Clusters));
    }

    [Fact]
    public void FullyAllocatedOrderingTerminatesWithAnExplicitConstraint()
    {
        var layout = Layout(2, File(32, new Extent(0, 1, 1)), File(33, new Extent(0, 0, 1)));
        using var session = new PlanningSession();
        var plan = new LayoutPlanner().Plan(layout, Request(Operation.Alphabetical), session: session);
        Assert.Empty(plan.Moves);
        Assert.Equal(2, plan.FilesBlocked);
    }

    [Theory]
    [InlineData(Operation.MinimumWrite)]
    [InlineData(Operation.FilesOnly)]
    [InlineData(Operation.PackAndDefrag)]
    [InlineData(Operation.OptimizeMft)]
    [InlineData(Operation.DirectoryIndexes)]
    public void DefragPreservesALargerLaterAnchorInsteadOfRewritingIt(Operation operation)
    {
        var file = File(operation == Operation.OptimizeMft ? 0UL : 32UL, new Extent(0, 20, 1), new Extent(1, 5, 4));
        if (operation == Operation.OptimizeMft) file = file with { Flags = StreamFlags.Metadata };
        if (operation == Operation.DirectoryIndexes) file = file with { Flags = StreamFlags.Directory };
        var layout = Layout(24, file);
        var moves = Execute(layout, operation);
        var move = Assert.Single(moves);
        Assert.Equal(1, move.Clusters); Assert.Equal(4, move.DestinationLcn);
        Assert.Equal(new Extent(0, 4, 5), Assert.Single(layout.Files[0].Extents));
    }

    [Theory]
    [InlineData(Operation.OptimizeMft)]
    [InlineData(Operation.DirectoryIndexes)]
    public void MetadataDefragCanRelocateOrdinaryBlockers(Operation operation)
    {
        var file = File(operation == Operation.OptimizeMft ? 0UL : 32UL, new Extent(0, 0, 4), new Extent(4, 8, 1)) with
        { Flags = operation == Operation.OptimizeMft ? StreamFlags.Metadata : StreamFlags.Directory };
        var layout = Layout(10, file, File(33, new Extent(0, 4, 4)));
        var moves = Execute(layout, operation);
        Assert.Contains(moves, move => move.FileIndex == 1);
        Assert.Equal(6, moves.Sum(move => move.Clusters));
        Assert.All(layout.Files, candidate => Assert.Single(candidate.Extents));
    }

    [Fact]
    public void SingleFileScopeDoesNotRelocateAnUnselectedBlocker()
    {
        var layout = Layout(10, File(32, new Extent(0, 0, 4), new Extent(4, 8, 1)), File(33, new Extent(0, 4, 4)));
        var request = Request(Operation.MinimumWrite) with { SelectedPaths = [new(layout.Files[0].Path)] };
        using var session = new PlanningSession();
        var plan = new LayoutPlanner().Plan(layout, request, session: session);
        Assert.Empty(plan.Moves);
        Assert.Equal(1, plan.FilesBlocked);
    }

    [Fact]
    public void ActiveFallbackRetainsItsPlanWithoutSortingOrReindexing()
    {
        var layout = Layout(10, File(32, new Extent(0, 0, 4), new Extent(4, 8, 1)), File(33, new Extent(0, 4, 4)));
        var planner = new LayoutPlanner(); using var session = new PlanningSession();
        var first = planner.Plan(layout, Request(Operation.PackAndDefrag), session: session);
        foreach (var move in first.Moves) { LayoutMutation.Apply(layout, move); session.MoveVerified(move); }
        var phases = new List<string>();
        var next = planner.Plan(layout, Request(Operation.PackAndDefrag), session: session, progress: item => phases.Add(item.Phase));
        Assert.NotEmpty(next.Moves);
        Assert.Contains("Continuing relocation plan", phases);
        Assert.DoesNotContain("Sorting candidates", phases);
        Assert.DoesNotContain("Indexing candidates", phases);
        Assert.DoesNotContain("Indexing free space", phases);
    }

    [Fact]
    public void VacatedMftReservationNeverBecomesScratchSpace()
    {
        var layout = Layout(5, File(32, new Extent(0, 0, 1)), File(33, new Extent(0, 3, 1)), File(34, new Extent(0, 2, 1))) with
        { MftZone = new(0, 1) };
        var moves = Execute(layout, Operation.Alphabetical);
        Assert.All(moves, move => Assert.NotEqual(0, move.DestinationLcn));
    }

    private static List<PlannedMove> Execute(VolumeLayout layout, Operation operation)
    {
        var planner = new LayoutPlanner();
        using var session = new PlanningSession();
        var moves = new List<PlannedMove>();
        long allocated = BitmapOperations.CountAllocated(layout.Bitmap);
        for (int window = 0; window < 10000; window++)
        {
            var plan = planner.Plan(layout, Request(operation), session: session);
            if (plan.Moves.Length == 0) return moves;
            Assert.InRange(plan.Moves.Length, 1, 1024);
            // Every destination is free before the batch starts, including concurrent batches.
            foreach (var move in plan.Moves) Assert.Equal(0, BitmapOperations.CountRange(layout.Bitmap, move.DestinationLcn, move.Clusters));
            foreach (var move in plan.Moves)
            {
                LayoutMutation.Apply(layout, move);
                session.MoveVerified(move);
                Assert.Equal(allocated, BitmapOperations.CountAllocated(layout.Bitmap));
                moves.Add(move);
            }
        }
        Assert.Fail("Relocation failed to converge.");
        return moves;
    }

    private static JobRequest Request(Operation operation) => new()
    {
        Volume = "V:\\", Operation = operation, MinimumFragments = 2,
        Resources = new() { AcOnly = false, MapCells = 256, MoveQueueDepth = 1 }
    };
    private static FileLayout File(ulong id, params Extent[] extents) => new(id, $"V:\\{id:00000}.bin", "", 0,
        extents.Sum(extent => extent.Length) * 4096, 0, 0, extents);
    private static VolumeLayout Layout(int clusters, params FileLayout[] files)
    {
        byte[] bitmap = new byte[(clusters + 7) / 8];
        foreach (var extent in files.SelectMany(file => file.Extents)) BitmapOperations.SetRange(bitmap, extent.Lcn, extent.Length, true);
        var info = new VolumeInfo("fixture", "V:\\", "fixture", "NTFS", clusters * 4096L,
            (clusters - BitmapOperations.CountAllocated(bitmap)) * 4096, 4096, true, false, [], "fixture");
        return new(info, clusters, bitmap, files, DateTimeOffset.UtcNow, files.Length, 0, true, []);
    }
}
