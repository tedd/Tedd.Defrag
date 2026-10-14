using Tedd.Defrag.Core;
using Tedd.Defrag.Visualization;
using Xunit;

namespace Tedd.Defrag.Tests;

public class MapPaletteTests
{
    [Fact]
    public void FragmentationDensityHasMonotonicRedIntensityAtEveryZoom()
    {
        foreach (var palette in new[] { MapPalette.Light, MapPalette.Dark })
        {
            uint low = MapAggregator.Color(new MapCell(100, 100, 1, 0, 0, 0, 0), palette);
            uint middle = MapAggregator.Color(new MapCell(100, 100, 50, 0, 0, 0, 0), palette);
            uint high = MapAggregator.Color(new MapCell(100, 100, 100, 0, 0, 0, 0), palette);
            Assert.True((low >> 8 & 255) > (middle >> 8 & 255));
            Assert.True((middle >> 8 & 255) > (high >> 8 & 255));
            Assert.Equal(palette.Fragmented, high);
            Assert.Equal(middle, MapAggregator.Color(new MapCell(1000, 1000, 500, 0, 0, 0, 0), palette));
            Assert.Equal(middle, MapAggregator.Color(new MapCell(100, 50, 50, 0, 0, 0, 0), palette));
        }
    }

    [Fact]
    public void SemanticAndActivityColorsRemainDistinctInBothThemes()
    {
        foreach (var palette in new[] { MapPalette.Light, MapPalette.Dark })
        {
            Assert.Equal(0xFFFFFFFFu, MapAggregator.Color(new MapCell(100, 0, 0, 0, 0, 0, 0), palette));
            Assert.Equal(palette.Canvas, MapAggregator.Color(default, palette));
            Assert.Equal(palette.Allocated, MapAggregator.Color(new MapCell(100, 100, 0, 0, 0, 0, 0), palette));
            var mixed = new MapCell(100, 100, 100, 100, 100, 1, 1);
            Assert.Equal(palette.Moving, MapAggregator.Color(mixed, palette));
            mixed = mixed with { Moving = 0 };
            Assert.Equal(palette.Verified, MapAggregator.Color(mixed, palette));
            mixed = mixed with { Verified = 0 };
            Assert.Equal(palette.Metadata, MapAggregator.Color(mixed, palette));
            mixed = mixed with { Metadata = 0 };
            Assert.Equal(palette.Excluded, MapAggregator.Color(mixed, palette));
            uint[] states = [palette.Canvas, palette.FreeSpace, palette.Allocated, palette.Excluded,
                palette.Metadata, palette.FragmentedLow, palette.Fragmented, palette.Moving, palette.Verified, palette.Selection];
            Assert.Equal(states.Length, states.Distinct().Count());
        }
    }
}
