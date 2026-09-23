using Tedd.Defrag.Visualization;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class MapRasterizerTests
{
    [Theory]
    [InlineData(1280, 276, 8192)]
    [InlineData(1280, 276, 65536)]
    [InlineData(256, 42, 256)]
    [InlineData(73, 19, 157)]
    [InlineData(5, 3, 20)]
    public void DrawingMethodsProduceTheSamePixels(int width, int height, int cellCount)
    {
        uint[] colors = Enumerable.Range(0, cellCount).Select(i => 0xFF000000u | (uint)i).ToArray();
        var (columns, rows) = MapRasterizer.Grid(cellCount, width, height);
        var scalar = new uint[width * height];
        var spans = new uint[width * height];
        var rowOriented = new uint[width * height];

        MapRasterizer.DrawScalar(scalar, width, width, height, colors, columns, rows, 0xFF172531, 1);
        MapRasterizer.DrawSpans(spans, width, width, height, colors, columns, rows, 0xFF172531, 1);
        MapRasterizer.DrawRows(rowOriented, width, width, height, colors, columns, rows, 0xFF172531, 1);

        Assert.Equal(scalar, spans);
        Assert.Equal(scalar, rowOriented);
    }

    [Theory]
    [InlineData(1280, 42)]
    [InlineData(2560, 84)]
    [InlineData(3, 2)]
    public void OverviewRowsMatchColumnFills(int width, int height)
    {
        uint[] colors = Enumerable.Range(0, 8192).Select(i => 0xFF000000u | (uint)i).ToArray();
        var columns = new uint[width * height];
        var rows = new uint[width * height];
        for (int x = 0; x < width; x++)
            MapRasterizer.FillRectangle(columns, width, width, height, x, 0, x + 1, height,
                colors[(int)((long)x * colors.Length / width)]);

        MapRasterizer.DrawOverviewRows(rows, width, width, height, colors);

        Assert.Equal(columns, rows);
    }
}
