using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class LayoutExplorerTests
{
    [Fact]
    public void SelectedRegionIsReaggregatedAtClusterResolutionAndListsItsFiles()
    {
        string root = TemporaryRoot();
        try
        {
            Guid id = Guid.NewGuid(); var layout = Layout(); var store = new JobStore(root);
            var explorer = new LayoutExplorerStore(store); explorer.Save(id, layout);

            var region = explorer.Explore(id, 8, 8, 8, includeFiles: true, selectedPath: @"V:\data.bin");

            Assert.Equal(8, region.Cells.Length);
            Assert.All(region.Cells, cell => Assert.Equal(1, cell.Clusters));
            Assert.Equal(4, region.Cells.Sum(cell => cell.Allocated));
            var file = Assert.Single(region.Files);
            Assert.Equal(4, file.ClustersInRegion);
            Assert.Equal(4, file.Clusters);
            Assert.Equal(new ClusterRange(10, 4), Assert.Single(region.Selection!.Ranges));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void VerifiedMovesUpdateAllocationFileHitsAndStableFileSelection()
    {
        string root = TemporaryRoot();
        try
        {
            Guid id = Guid.NewGuid(); var store = new JobStore(root); var explorer = new LayoutExplorerStore(store);
            explorer.Save(id, Layout());
            store.Journal(id, new(DateTimeOffset.UtcNow, "verified", new(42, 0, 0, 10, 30, 4)));

            var source = explorer.Explore(id, 10, 4, 4, includeFiles: true);
            var destination = explorer.Explore(id, 30, 4, 4, includeFiles: true, selectedFileId: 42, selectedStream: "");

            Assert.Equal(0, source.Cells.Sum(cell => cell.Allocated));
            Assert.Empty(source.Files);
            Assert.Equal(4, destination.Cells.Sum(cell => cell.Allocated));
            Assert.Equal(4, Assert.Single(destination.Files).ClustersInRegion);
            Assert.Equal(new ClusterRange(30, 4), Assert.Single(destination.Selection!.Ranges));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static VolumeLayout Layout()
    {
        byte[] bitmap = new byte[16]; BitmapOperations.SetRange(bitmap, 10, 4, true);
        var volume = new VolumeInfo("test", @"V:\", "Test", "NTFS", 128 * 4096L, 124 * 4096L,
            4096, true, false, [], "fixture");
        var file = new FileLayout(42, @"V:\data.bin", "", StreamFlags.None, 4 * 4096L, 0, 0, [new(0, 10, 4)]);
        return new(volume, 128, bitmap, [file], DateTimeOffset.UtcNow, 1, 0, true, []);
    }

    private static string TemporaryRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
}
