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
        int stride = width + 3;
        var scalar = new uint[stride * height];
        var spans = new uint[stride * height];
        var rowOriented = new uint[stride * height];
        var rowTemplate = new uint[stride * height];

        MapRasterizer.DrawScalar(scalar, stride, width, height, colors, columns, rows, 0xFF172531, 1);
        MapRasterizer.DrawSpans(spans, stride, width, height, colors, columns, rows, 0xFF172531, 1);
        MapRasterizer.DrawRows(rowOriented, stride, width, height, colors, columns, rows, 0xFF172531, 1);
        MapRasterizer.DrawRowTemplate(rowTemplate, stride, width, height, colors, columns, rows, 0xFF172531, 1, new uint[width]);

        Assert.Equal(scalar, spans);
        Assert.Equal(scalar, rowOriented);
        Assert.Equal(scalar, rowTemplate);
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
