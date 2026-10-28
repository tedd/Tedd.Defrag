using Tedd.Defrag.Core;
using Tedd.Defrag.Engine;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.TestFixtures;
using Tedd.Defrag.Visualization;
using Tedd.Defrag.Windows;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class ScanMapTests
{
    [Theory]
    [InlineData(17, 4)]
    [InlineData(17, 256)]
    [InlineData(262144, 8192)]
    public void PagedAllocationPreservesGeometryAndUnreadCells(long clusters, int count)
    {
        var cells = new MapCell[count];
        var builder = new ScanMapBuilder(clusters, cells);
        byte[] bitmap = new byte[(int)((clusters + 7) / 8)];
        BitmapOperations.SetRange(bitmap, 0, clusters, true);
        Assert.Equal(clusters, cells.Sum(c => c.Clusters));
        Assert.Equal(clusters, cells.Sum(c => c.Unknown));
        foreach (long end in new[] { 3L, 8L, clusters })
        {
            builder.Apply(new(clusters, bitmap, end));
            Assert.Equal(clusters, cells.Sum(c => c.Clusters));
            Assert.Equal(clusters - end, cells.Sum(c => c.Unknown));
            Assert.Equal(end, cells.Sum(c => c.Allocated));
            foreach (var cell in cells.Where(c => c.Unknown > 0))
                Assert.Equal(MapPalette.Dark.Canvas, MapAggregator.Color(cell));
        }
    }

    [Theory]
    [InlineData(256)]
    [InlineData(8192)]
    [InlineData(65536)]
    public void IncrementalClassificationMatchesFinalAggregation(int count)
    {
        var layout = SyntheticVolume.Create();
        var cells = new MapCell[count];
        var builder = new ScanMapBuilder(layout.TotalClusters, cells);
        for (long end = 511; end < layout.TotalClusters; end += 511)
            builder.Apply(new(layout.TotalClusters, layout.Bitmap, end));
        builder.Apply(new(layout.TotalClusters, layout.Bitmap, layout.TotalClusters));
        foreach (var file in layout.Files) builder.Apply(new(layout.TotalClusters, File: file));
        var expected = new MapCell[count];
        MapAggregator.Build(layout, expected);
        Assert.Equal(expected, cells);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerPublishesMapBeforeScanReturnsAndPreservesItOnCancellation(bool cancel)
    {
        var layout = SyntheticVolume.Create();
        var request = new JobRequest { Volume = layout.Volume.Root, Resources = new() { AcOnly = false, MapCells = 256 } };
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JobStore(root);
            store.SaveRequest(request);
            JobSnapshot? first = null;
            var volume = new ScanVolume(layout, report =>
            {
                report(new(layout.TotalClusters, layout.Bitmap, layout.TotalClusters / 2));
                first = Assert.IsType<JobSnapshot>(store.ReadSnapshot(request.Id));
                Assert.Equal(JobState.Scanning, first.State);
                Assert.Null(first.ObservedAt);
                Assert.Equal(layout.TotalClusters, first.TotalClusters);
                Assert.Equal(layout.TotalClusters / 2, first.Map!.Sum(c => c.Unknown));
                report(new(layout.TotalClusters, layout.Bitmap, layout.TotalClusters));
                foreach (var file in layout.Files) report(new(layout.TotalClusters, File: file));
                if (cancel) throw new OperationCanceledException();
            });
            new JobExecutor(store, _ => volume, readCompressionInventory: (_, _, _) =>
            {
                var current = Assert.IsType<JobSnapshot>(store.ReadSnapshot(request.Id));
                Assert.Equal(JobState.Scanning, current.State);
                Assert.Null(current.ObservedAt);
                Assert.All(current.Map!, c => Assert.Equal(0, c.Unknown));
                return CompressionInventoryResult.Empty;
            }).Run(request, default);

            var final = Assert.IsType<JobSnapshot>(store.ReadSnapshot(request.Id));
            Assert.Equal(cancel ? JobState.Cancelled : JobState.Completed, final.State);
            var expected = new MapCell[256];
            MapAggregator.Build(layout, expected);
            Assert.Equal(expected, final.Map);
            Assert.Equal(layout.TotalClusters / 2, first!.Map!.Sum(c => c.Unknown));
            Assert.Equal(!cancel, final.ObservedAt.HasValue);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PostMaintenanceScanReplacesPreviousClassifications()
    {
        var layout = SyntheticVolume.Create();
        var request = new JobRequest
        {
            Volume = layout.Volume.Root, Operation = Operation.WindowsDefrag, Preview = false,
            Resources = new() { AcOnly = false, MapCells = 256 }
        };
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JobStore(root);
            store.SaveRequest(request);
            int scans = 0;
            var volume = new ScanVolume(layout, report =>
            {
                scans++;
                report(new(layout.TotalClusters, layout.Bitmap, layout.TotalClusters / 2));
                var snapshot = Assert.IsType<JobSnapshot>(store.ReadSnapshot(request.Id));
                Assert.Null(snapshot.ObservedAt);
                Assert.Equal(layout.TotalClusters / 2, snapshot.Map!.Sum(c => c.Unknown));
                Assert.All(snapshot.Map!, c => Assert.Equal(0, c.Fragmented));
                report(new(layout.TotalClusters, layout.Bitmap, layout.TotalClusters));
                foreach (var file in layout.Files) report(new(layout.TotalClusters, File: file));
            });
            new JobExecutor(store, _ => volume, runMaintenance: (_, _, _, _, _) => { },
                readCompressionInventory: (_, _, _) => CompressionInventoryResult.Empty).Run(request, default);
            var final = Assert.IsType<JobSnapshot>(store.ReadSnapshot(request.Id));
            Assert.Equal(JobState.Completed, final.State);
            Assert.Equal(2, scans);
            var expected = new MapCell[256];
            MapAggregator.Build(layout, expected);
            Assert.Equal(expected, final.Map);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class ScanVolume(VolumeLayout layout, Action<Action<ScanMapUpdate>> scan) : IJobVolume
    {
        public VolumeInfo Info => layout.Volume;
        public bool IsDirty() => false;
        public VolumeLayout Scan(JobRequest request, Action<double, long, string> progress, Action checkpoint,
            CancellationToken token, Action<WorkProgress>? diagnostics = null, Action<ScanMapUpdate>? mapProgress = null)
        {
            checkpoint();
            scan(mapProgress!);
            return layout;
        }
        public byte[] ReadBitmap(JobRequest request, Action checkpoint, CancellationToken token) => throw new NotSupportedException();
        public void ExecuteMove(FileLayout file, PlannedMove move, PathRules rules, bool verifyResult) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
