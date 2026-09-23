using Tedd.Defrag.Core;
using Tedd.Defrag.Visualization;
using Tedd.Maui;

namespace Tedd.Defrag.Desktop;

/// <summary>Map geometry, hit testing, selection and reusable pixel rendering.</summary>
public sealed class DiskMapDrawable
{
    public MapCell[] Cells { get; set; } = [];
    public string EmptyMessage { get; set; } = "Select a volume to begin.";
    public long StartCluster { get; private set; }
    public long ClusterCount { get; private set; }
    public long TotalClusters { get; private set; }
    private readonly HashSet<int> _highlightedCells = [];
    private uint[] _packedCells = [];
    private uint[] _rowTemplate = [];
    private float _width, _height;
    private int _columns, _rows;
    private int _selectionStart = -1, _selectionEnd = -1;
    public void SetRegion(long start, long count, long total, MapCell[] cells)
    {
        TotalClusters = Math.Max(0, total); Cells = cells;
        _packedCells = [];
        if (TotalClusters == 0) { StartCluster = ClusterCount = 0; return; }
        StartCluster = Math.Clamp(start, 0, TotalClusters - 1);
        ClusterCount = Math.Clamp(count, 1, TotalClusters - StartCluster);
        _highlightedCells.Clear(); ClearSelection();
    }

    public void Reset(MapCell[] cells, long total) => SetRegion(0, Math.Max(1, total), total, cells);

    public ClusterRange CellRange(int index)
    {
        if (index < 0 || index >= Cells.Length || ClusterCount <= 0) return default;
        long step = Math.Max(1, (ClusterCount + Cells.Length - 1) / Cells.Length);
        long start = StartCluster + index * step;
        return start >= StartCluster + ClusterCount ? default : new(start, Math.Min(step, StartCluster + ClusterCount - start));
    }

    public long ClusterAt(float x, float y)
    {
        int index = Hit(x, y); var range = CellRange(index);
        return range.Length == 0 ? -1 : range.Start + range.Length / 2;
    }

    public void SetHighlight(IEnumerable<ClusterRange> ranges)
    {
        _highlightedCells.Clear();
        if (Cells.Length == 0 || ClusterCount == 0) return;
        long step = Math.Max(1, (ClusterCount + Cells.Length - 1) / Cells.Length), end = StartCluster + ClusterCount;
        foreach (var range in ranges)
        {
            long a = Math.Max(StartCluster, range.Start), b = Math.Min(end, range.End);
            if (a >= b) continue;
            int first = (int)((a - StartCluster) / step), last = (int)((b - 1 - StartCluster) / step);
            for (int i = first; i <= last && i < Cells.Length; i++) _highlightedCells.Add(i);
        }
    }

    public void SetSelection(int start, int end) { _selectionStart = start; _selectionEnd = end; }
    public void ClearSelection() { _selectionStart = _selectionEnd = -1; }
    public void SetViewport(float width, float height) { _width = width; _height = height; }

    public void Rasterize(Span<uint> pixels, int stride, int width, int height, int padding)
    {
        int count = Cells.Length;
        while (count > 0 && Cells[count - 1].Clusters == 0) count--;
        (_columns, _rows) = MapRasterizer.Grid(count, width, height);
        if (_packedCells.Length != count)
        {
            _packedCells = new uint[count];
            for (int i = 0; i < count; i++) _packedCells[i] = Pack(MapAggregator.Color(Cells[i]));
        }
        if (_rows * 3 >= height)
            MapRasterizer.DrawRows(pixels, stride, width, height, _packedCells, _columns, _rows,
                Pack(0xFF172531), padding);
        else
        {
            if (_rowTemplate.Length < width) _rowTemplate = new uint[width];
            MapRasterizer.DrawRowTemplate(pixels, stride, width, height, _packedCells, _columns, _rows,
                Pack(0xFF172531), padding, _rowTemplate);
        }
        foreach (int index in _highlightedCells)
            if (index < count) MapRasterizer.Border(pixels, stride, width, height, index, _columns, _rows, Pack(0xFFFFD45A), Math.Max(1, padding));
        if (_selectionStart < 0 || count == 0) return;
        int first = Math.Max(0, Math.Min(_selectionStart, _selectionEnd));
        int last = Math.Min(count - 1, Math.Max(_selectionStart, _selectionEnd));
        for (int index = first; index <= last; index++)
            MapRasterizer.Border(pixels, stride, width, height, index, _columns, _rows, Pack(0xFFFFFFFF));
    }

    internal static uint Pack(uint argb) => WriteableBitmap.FromRgba(
        (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    public int Hit(float x, float y)
    {
        if (_columns == 0 || _rows == 0 || Cells.Length == 0 || x < 0 || y < 0 || x >= _width || y >= _height) return -1;
        int display = Math.Min(_rows - 1, (int)(y / _height * _rows)) * _columns + Math.Min(_columns - 1, (int)(x / _width * _columns));
        return display >= Cells.Length || Cells[display].Clusters == 0 ? -1 : display;
    }
}

/// <summary>A linear whole-volume navigator; horizontal position always corresponds to LCN.</summary>
public sealed class MapOverviewDrawable
{
    public MapCell[] Cells { get; set; } = [];
    public long TotalClusters { get; set; }
    public long StartCluster { get; set; }
    public long ClusterCount { get; set; }
    private float _width;
    private MapCell[]? _packedSource;
    private uint[] _packedCells = [];

    public void SetViewport(float width) => _width = width;

    public void Rasterize(Span<uint> pixels, int stride, int width, int height)
    {
        if (Cells.Length == 0 || TotalClusters <= 0)
        {
            uint background = DiskMapDrawable.Pack(0xFF172531);
            for (int y = 0; y < height; y++) pixels.Slice(y * stride, width).Fill(background);
            _packedSource = null;
            _packedCells = [];
            return;
        }
        if (!ReferenceEquals(_packedSource, Cells))
        {
            _packedSource = Cells;
            _packedCells = new uint[Cells.Length];
            for (int i = 0; i < Cells.Length; i++) _packedCells[i] = DiskMapDrawable.Pack(MapAggregator.Color(Cells[i]));
        }
        MapRasterizer.DrawOverviewRows(pixels, stride, width, height, _packedCells);
        int left = (int)((double)StartCluster / TotalClusters * width);
        int right = (int)((double)(StartCluster + ClusterCount) / TotalClusters * width);
        uint shade = DiskMapDrawable.Pack(0xFF111B24);
        MapRasterizer.FillRectangle(pixels, stride, width, height, 0, 0, left, height, shade);
        MapRasterizer.FillRectangle(pixels, stride, width, height, right, 0, width, height, shade);
        uint accent = DiskMapDrawable.Pack(0xFFFFD45A);
        MapRasterizer.FillRectangle(pixels, stride, width, height, left, 0, Math.Max(left + 2, right), 2, accent);
        MapRasterizer.FillRectangle(pixels, stride, width, height, left, height - 2, Math.Max(left + 2, right), height, accent);
        MapRasterizer.FillRectangle(pixels, stride, width, height, left, 0, left + 2, height, accent);
        MapRasterizer.FillRectangle(pixels, stride, width, height, right - 2, 0, right, height, accent);
        int center = (left + right) / 2;
        for (int y = 0; y < Math.Min(8, height); y++)
            MapRasterizer.FillRectangle(pixels, stride, width, height, center - 6 + y, y, center + 6 - y, y + 1, accent);
    }

    public long ClusterAt(float x)
    {
        if (TotalClusters <= 0 || _width <= 0) return 0;
        return Math.Clamp((long)(x / _width * TotalClusters), 0, TotalClusters - 1);
    }
}
