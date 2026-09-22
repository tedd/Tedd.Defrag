using Tedd.Defrag.Core;
using Tedd.Defrag.Engine;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class SnapshotSummaryTests
{
    [Fact]
    public void SummaryKeepsOnlyTheLargestFragmentedFilesInStableOrder()
    {
        var files = Enumerable.Range(0, 150).Select(index => new FileLayout((ulong)index,
            $"file-{index}", "", StreamFlags.None, index, 0, 0,
            Enumerable.Range(0, index % 5 == 0 ? 1 : 2 + index % 7)
                .Select(extent => new Extent(extent, extent * 2, 1)).ToArray())).ToArray();

        var (fragmented, top) = JobExecutor.SummarizeFiles(files);
        var expected = files.Where(file => file.Fragmented).OrderByDescending(file => file.Extents.Length)
            .Take(100).Select(file => file.Path).ToArray();

        Assert.Equal(120, fragmented);
        Assert.Equal(expected, top.Select(file => file.Path));
    }
}
