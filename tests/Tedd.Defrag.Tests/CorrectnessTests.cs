using System.Buffers.Binary;
using Tedd.Defrag.Core;
using Tedd.Defrag.Engine;
using Tedd.Defrag.Ntfs;
using Tedd.Defrag.Planning;
using Tedd.Defrag.Visualization;
using Tedd.Defrag.TestFixtures;
using Xunit;

namespace Tedd.Defrag.Tests;

public class CorrectnessTests
{
    [Fact]
    public void VectorAndScalarPopcountsMatchIncludingUnalignedTails()
    {
        var random = new Random(71); byte[] bytes = new byte[10240]; random.NextBytes(bytes);
        for (int n = 0; n < bytes.Length - 7; n += 13)
        {
            var span = bytes.AsSpan(7, n); long expected = 0;
            foreach (byte b in span) for (int bit = 0; bit < 8; bit++) expected += (b >> bit) & 1;
            Assert.Equal(expected, BitmapOperations.CountAllocated(span)); Assert.Equal(expected, BitmapKernel.CountAllocated(span));
        }
    }
    [Fact]
    public void BitmapRangeOperationsNeverTouchOutsideTheirBounds()
    {
        var random = new Random(81); byte[] bitmap = new byte[2048];
        for (int i = 0; i < 400; i++)
        {
            random.NextBytes(bitmap); var old = (byte[])bitmap.Clone(); int start = random.Next(bitmap.Length * 8), count = random.Next(bitmap.Length * 8 - start + 1); bool set = random.Next(2) == 0;
            BitmapOperations.SetRange(bitmap, start, count, set);
            for (int bit = 0; bit < bitmap.Length * 8; bit++) Assert.Equal(bit >= start && bit < start + count ? set : BitmapOperations.IsSet(old, bit), BitmapOperations.IsSet(bitmap, bit));
            Assert.Equal(set ? count : 0, BitmapOperations.CountRange(bitmap, start, count));
        }
    }
    [Fact]
    public void IndexedReservationsMatchExhaustiveSearch()
    {
        var random = new Random(98);
        for (int scenario = 0; scenario < 50; scenario++)
        {
            byte[] bitmap = new byte[1024]; random.NextBytes(bitmap);
            var index = new FreeSpaceIndex(BitmapOperations.FreeRanges(bitmap, bitmap.Length * 8));
            for (int step = 0; step < 200; step++)
            {
                int length = random.Next(1, 12), after = random.Next(0, 2048), before = random.Next(after + 1, 8193);
                long expected = -1;
                for (int pos = after; pos <= before - length; pos++) if (BitmapOperations.CountRange(bitmap, pos, length) == 0) { expected = pos; break; }
                long actual = index.FindFirstFit(length, before, after); Assert.Equal(expected, actual);
                if (actual < 0) continue;
                Assert.True(index.Reserve(actual, length)); BitmapOperations.SetRange(bitmap, actual, length, true);
                Assert.False(index.Reserve(actual, length));
            }
        }
    }
    [Fact]
    public void MinimumWriteKeepsLargeAnchor()
    {
        var bitmap = new byte[128]; BitmapOperations.SetRange(bitmap, 10, 100, true); BitmapOperations.SetRange(bitmap, 900, 4, true);
        var file = new FileLayout(32, @"V:\test.bin", "", 0, 104 * 4096, 0, 0, [new(0, 10, 100), new(100, 900, 4)]);
        var layout = Layout(bitmap, [file]);
        var plan = new LayoutPlanner().Plan(layout, Request(Operation.MinimumWrite));
        var move = Assert.Single(plan.Moves); Assert.Equal(4, move.Clusters); Assert.Equal(110, move.DestinationLcn);
        LayoutMutation.Apply(layout, move); Assert.Single(layout.Files[0].Extents); Assert.Equal(104, BitmapOperations.CountAllocated(layout.Bitmap));
    }
    [Fact]
    public void UnlimitedDefaultsAndUncappedResourcesAreValid()
    {
        var request = new JobRequest { Volume = "V:" };
        request.Validate();
        Assert.Equal(0, request.MaxMoveBytes); Assert.Equal(0, request.MaxMinutes);
        Assert.Equal(0, request.Resources.MemoryMiB); Assert.Equal(0, request.Resources.IoMiBPerSecond);
        Assert.Equal(20, request.MinimumFragments);
        (request.Resources with { MemoryMiB = int.MaxValue, IoMiBPerSecond = int.MaxValue }).Validate();
    }
    [Fact]
    public void DefragThresholdAndFileSizeLimitCandidateSelection()
    {
        var bitmap = new byte[256];
        var few = new FileLayout(1, @"V:\few.bin", "", 0, 3 * 4096, 0, 0,
            [new(0, 100, 1), new(1, 102, 1), new(2, 104, 1)]);
        var manyExtents = Enumerable.Range(0, 20).Select(i => new Extent(i, 200 + i * 2, 1)).ToArray();
        var many = new FileLayout(2, @"V:\many.bin", "", 0, 20 * 4096, 0, 0, manyExtents);
        foreach (var extent in few.Extents.Concat(many.Extents)) BitmapOperations.SetRange(bitmap, extent.Lcn, extent.Length, true);
        var request = new JobRequest { Volume = "V:", Operation = Operation.MinimumWrite, MinimumFileBytes = 10 * 4096, MaximumFileBytes = 30 * 4096 };

        var plan = new LayoutPlanner().Plan(Layout(bitmap, [few, many]), request);

        Assert.NotEmpty(plan.Moves); Assert.All(plan.Moves, move => Assert.Equal(many.FileId, move.FileId));
    }
    [Theory]
    [InlineData(Operation.MinimumWrite)] [InlineData(Operation.FilesOnly)] [InlineData(Operation.Pack)] [InlineData(Operation.PackAndDefrag)]
    [InlineData(Operation.Alphabetical)] [InlineData(Operation.Size)] [InlineData(Operation.Created)] [InlineData(Operation.Modified)] [InlineData(Operation.Extension)]
    [InlineData(Operation.PrepareShrink)]
    public void PlannerPreservesAllocationAndRespectsExclusionsAndBudgets(Operation operation)
    {
        for (int seed = 1; seed <= 8; seed++)
        {
            var layout = SyntheticVolume.Create(seed); var original = layout.Files.ToArray(); long before = BitmapOperations.CountAllocated(layout.Bitmap);
            var request = Request(operation) with { Exclusions = [@"V:\Projects"], MaxMoveBytes = 32 * 1024 * 1024, ShrinkBoundaryBytes = 512 * 1024 * 1024 };
            var plan = new LayoutPlanner().Plan(layout, request);
            Assert.True(plan.ClustersToMove * 4096 <= request.MaxMoveBytes); Assert.True(plan.Moves.Length <= 1024);
            foreach (var move in plan.Moves)
            {
                Assert.False(layout.Files[move.FileIndex].Path.StartsWith(@"V:\Projects\")); Assert.True(move.Clusters > 0);
                Assert.Equal(0, BitmapOperations.CountRange(layout.Bitmap, move.DestinationLcn, move.Clusters));
                LayoutMutation.Apply(layout, move);
            }
            Assert.Equal(before, BitmapOperations.CountAllocated(layout.Bitmap));
            for (int i = 0; i < original.Length; i++) Assert.Equal(original[i].Extents.Sum(e => e.Length), layout.Files[i].Extents.Sum(e => e.Length));
        }
    }
    [Fact]
    public void SingleFileSelectionNeverMovesSupportingFiles()
    {
        var layout = SyntheticVolume.Create(); string selected = layout.Files.First(f => f.Fragmented && f.Movable).Path;
        var plan = new LayoutPlanner().Plan(layout, Request(Operation.MinimumWrite) with { SelectedPaths = [selected] });
        Assert.NotEmpty(plan.Moves); Assert.All(plan.Moves, m => Assert.Equal(selected, layout.Files[m.FileIndex].Path));
    }
    [Fact]
    public void PathRulesAreRecursiveWithoutPrefixCollisions()
    {
        var rules = new PathRules([], [@"C:\Data", @"*\cache\*"]);
        Assert.True(rules.IsExcluded(@"c:\data\report.bin")); Assert.True(rules.IsExcluded(@"C:\Data"));
        Assert.False(rules.IsExcluded(@"C:\Database\file.bin")); Assert.True(rules.IsExcluded(@"D:\app\cache\entry"));
    }
    [Fact]
    public void NtfsRunsDecodeSignedDeltasAndSparseHoles()
    {
        Assert.True(NtfsRecordParser.TryDecodeRuns([0x11, 3, 100, 0x11, 2, 0xF6, 0x01, 4, 0x11, 1, 20, 0], 0, 1000, out var runs));
        Assert.Equal(new Extent(0, 100, 3), runs[0]); Assert.Equal(new Extent(3, 90, 2), runs[1]);
        Assert.Equal(new Extent(5, -1, 4), runs[2]); Assert.Equal(new Extent(9, 110, 1), runs[3]);
        Assert.False(NtfsRecordParser.TryDecodeRuns([0x11, 5, 0xFF, 0], 0, 100, out _));
        Assert.False(NtfsRecordParser.TryDecodeRuns([0x19, 1, 0], 0, 100, out _));
    }
    [Fact]
    public void TornMftRecordIsRejectedBeforeFixupWrites()
    {
        byte[] record = new byte[1024]; "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 48); BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(48), 0xAABB); record[50] = 7; record[52] = 9;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510), 0xAABB);
        var old = record.ToArray(); Assert.False(NtfsRecordParser.ApplyFixups(record)); Assert.Equal(old, record);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(1022), 0xAABB);
        Assert.True(NtfsRecordParser.ApplyFixups(record)); Assert.Equal(7, record[510]); Assert.Equal(9, record[1022]);
    }
    [Fact]
    public void MalformedRunlistsNeverThrow()
    {
        var random = new Random(19); byte[] bytes = new byte[256];
        for (int i = 0; i < 20000; i++) { random.NextBytes(bytes); NtfsRecordParser.TryDecodeRuns(bytes.AsSpan(0, random.Next(256)), 0, 10000, out _); }
    }
    [Fact]
    public void MapConservesAllocationAcrossZoomAndPartialCells()
    {
        var layout = SyntheticVolume.Create(); var cells = new MapCell[8191]; MapAggregator.Build(layout, cells);
        Assert.Equal(layout.TotalClusters, cells.Sum(c => c.Clusters)); Assert.Equal(BitmapOperations.CountAllocated(layout.Bitmap), cells.Sum(c => c.Allocated));
        MapAggregator.Build(layout, cells, 51, 7921); Assert.Equal(7921, cells.Sum(c => c.Clusters)); Assert.Equal(BitmapOperations.CountRange(layout.Bitmap, 51, 7921), cells.Sum(c => c.Allocated));
    }
    [Fact]
    public void MapShowsTheFullVolumeOutsideTheOccupiedRange()
    {
        byte[] bitmap = new byte[128];
        BitmapOperations.SetRange(bitmap, 320, 64, true);
        var cells = new MapCell[16];

        MapAggregator.Build(Layout(bitmap, []), cells);

        Assert.Equal(1024, cells.Sum(cell => cell.Clusters));
        Assert.Equal(0, cells[0].Allocated);
        Assert.Equal(0xFF455B6Bu, MapAggregator.Color(cells[0]));
        Assert.Equal(0, cells[^1].Allocated);
        Assert.Equal(0xFF455B6Bu, MapAggregator.Color(cells[^1]));
        Assert.Contains(cells, cell => cell.Allocated > 0);
    }
    [Fact]
    public void ArbiterQueuesConflictingDevicesButAllowsIndependentWork()
    {
        var arbiter = new ResourceArbiter(); var settings = new ConcurrencySettings { MaxConcurrentVolumes = 4 };
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid(); var d = Guid.NewGuid();
        Assert.True(arbiter.TryAcquire(a, "C:", ["disk:0"], settings)); Assert.False(arbiter.TryAcquire(b, "D:", ["disk:0", "disk:1"], settings));
        Assert.True(arbiter.TryAcquire(c, "E:", ["disk:2"], settings)); Assert.False(arbiter.TryAcquire(d, "F:", ["disk:1"], settings));
        arbiter.Release(a); Assert.True(arbiter.TryAcquire(b, "D:", ["disk:0", "disk:1"], settings));
        arbiter.Release(b); Assert.True(arbiter.TryAcquire(d, "F:", ["disk:1"], settings));
    }
    [Fact]
    public void SharedOverrideStillSerializesTheSameVolume()
    {
        var s = new ResourceArbiter(); var settings = new ConcurrencySettings { MaxConcurrentVolumes = 4, AllowParallelOnSharedStorage = true };
        Assert.True(s.TryAcquire(Guid.NewGuid(), "C:", ["disk:0"], settings));
        Assert.True(s.TryAcquire(Guid.NewGuid(), "D:", ["disk:0"], settings));
        Assert.False(s.TryAcquire(Guid.NewGuid(), "C:", ["disk:0"], settings));
    }
    private static JobRequest Request(Operation op) => new() { Volume = "V:", Operation = op, MinimumFragments = 2, Resources = new() { AcOnly = false } };
    private static VolumeLayout Layout(byte[] bitmap, FileLayout[] files) => new(SyntheticVolume.Create().Volume, bitmap.Length * 8L, bitmap, files, DateTimeOffset.UtcNow, files.Length, 0, true, []);
}
