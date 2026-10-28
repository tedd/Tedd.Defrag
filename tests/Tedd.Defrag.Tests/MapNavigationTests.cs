using Tedd.Defrag.Core;
using Tedd.Defrag.Visualization;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class MapNavigationTests
{
    [Fact]
    public void ZoomReachesOneClusterAndKeepsThePointerAnchorVisible()
    {
        var range = new ClusterRange(0, 1_000_000);
        for (int i = 0; i < 20; i++)
        {
            range = MapNavigation.Zoom(1_000_000, range.Start, range.Length, 550_000, true);
            Assert.InRange(550_000, range.Start, range.End - 1);
        }
        Assert.Equal(new ClusterRange(550_000, 1), range);
        Assert.Equal(range, MapNavigation.Zoom(1_000_000, range.Start, range.Length, 550_000, true));
    }

    [Fact]
    public void ZoomOutClampsToTheWholeVolume()
    {
        var range = new ClusterRange(400, 20);
        for (int i = 0; i < 8; i++)
        {
            range = MapNavigation.Zoom(1000, range.Start, range.Length, range.Start + range.Length / 2, false);
            Assert.InRange(range.Start, 0, 1000 - range.Length);
        }
        Assert.Equal(new ClusterRange(0, 1000), range);
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(59, 40)]
    public void WheelZoomPreservesTheAnchorPosition(long anchor, long expectedStart)
        => Assert.Equal(new ClusterRange(expectedStart, 20), MapNavigation.Zoom(100, 20, 40, anchor, true));

    [Fact]
    public void FullVolumeProjectionPreservesTheOriginalSummary()
    {
        MapCell[] source = [new(4, 3, 1, 1, 1, 0, 0), new(4, 0, 0, 0, 0, 0, 0, 4), new(2, 1, 1, 0, 0, 0, 1)];
        Assert.Equal(source, MapNavigation.Project(source, 10, 0, 10, 3));
    }

    [Fact]
    public void SelectedAndPannedViewsUseAbsoluteClusterAddresses()
    {
        var source = Enumerable.Range(0, 8).Select(i => new MapCell(10, i, 0, 0, 0, 0, 0)).ToArray();
        Assert.Equal(source[2..6], MapNavigation.Project(source, 80, 20, 40, 4));
        Assert.Equal(source[3..7], MapNavigation.Project(source, 80, 30, 40, 4));
    }

    [Fact]
    public void MissingAndUnscannedCellsRemainUnknown()
    {
        Assert.All(MapNavigation.Project([], 100, 20, 40, 10), cell => Assert.Equal(cell.Clusters, cell.Unknown));
        MapCell[] source = [new(5, 0, 0, 0, 0, 0, 0, 5), new(5, 5, 0, 0, 0, 0, 0)];
        var cells = MapNavigation.Project(source, 10, 0, 10, 10);
        Assert.All(cells[..5], cell => Assert.Equal(1, cell.Unknown));
        Assert.All(cells[5..], cell => Assert.Equal(1, cell.Allocated));
    }

    [Fact]
    public void SubcellEstimatesCannotAllocateUnknownClusters()
    {
        MapCell[] source = [new(10, 5, 3, 2, 4, 0, 0, 5)];
        var cells = MapNavigation.Project(source, 10, 0, 10, 10);
        Assert.Equal(10, cells.Sum(cell => cell.Clusters));
        Assert.All(cells, cell =>
        {
            Assert.InRange(cell.Allocated + cell.Unknown, 0, cell.Clusters);
            Assert.InRange(cell.Fragmented, 0, cell.Allocated);
            Assert.InRange(cell.Metadata, 0, cell.Allocated);
            Assert.InRange(cell.Excluded, 0, cell.Allocated);
        });
    }

    [Fact]
    public void SmallRangesDoNotCreateMoreCellsThanClusters()
    {
        var cell = Assert.Single(MapNavigation.Project([new(100, 100, 0, 0, 0, 0, 0)], 100, 42, 1, 8192));
        Assert.Equal(new MapCell(1, 1, 0, 0, 0, 0, 0), cell);
    }

    [Fact]
    public void ProjectionAndZoomDoNotOverflowAtTheEndOfAVolume()
    {
        long total = long.MaxValue, half = total / 2 + 1;
        MapCell[] source = [new(half, half, 0, 0, 0, 0, 0), new(total - half, total - half, 0, 0, 0, 0, 0)];
        var cells = MapNavigation.Project(source, total, total - 5, 5, 4);
        Assert.Equal(5, cells.Sum(cell => cell.Clusters));
        Assert.Equal(5, cells.Sum(cell => cell.Allocated));
        Assert.Equal(new ClusterRange(0, total), MapNavigation.Zoom(total, 0, half, 0, false));
    }
}
