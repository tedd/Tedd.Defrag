using Tedd.Defrag.Core;
using Tedd.Defrag.Visualization;

namespace Tedd.Defrag.Desktop;

/// <summary>One retained surface, bounded cells, cached palette; draw traversal creates no objects.</summary>
public sealed class DiskMapDrawable : IDrawable
{
    private static readonly Color HighlightColor = Color.FromArgb("#FFD45A");
    public MapCell[] Cells { get; set; } = [];
    public string EmptyMessage { get; set; } = "Select a volume to begin.";
    public long StartCluster { get; private set; }
    public long ClusterCount { get; private set; }
    public long TotalClusters { get; private set; }
    private readonly Dictionary<uint, Color> _colors = [];
    private readonly Dictionary<uint, List<int>> _fillGroups = [];
    private readonly List<uint> _activeFillGroups = [];
    private readonly HashSet<int> _highlightedCells = [];
    private float _width, _height;
    private int _columns, _rows;
    private int _selectionStart = -1, _selectionEnd = -1;
    public DiskMapDrawable()
    {
        // Quantized allocation mixtures plus all semantic colors, prepared once.
        for (int i = 0; i <= 256; i++)
        {
            var c = new MapCell(256, i, 0, 0, 0, 0, 0); Add(MapAggregator.Color(c));
        }
        foreach (uint c in new uint[] { 0xFF172531, 0xFF455B6B, 0xFFFFFFFF, 0xFF70C94A, 0xFF37C3DB, 0xFFF3BF3E, 0xFF8295A3 }) Add(c);
        void Add(uint c) => _colors.TryAdd(c, Color.FromUint(c));
    }
    public void SetRegion(long start, long count, long total, MapCell[] cells)
    {
        TotalClusters = Math.Max(0, total); Cells = cells;
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
    public int Hit(float x, float y)
    {
        if (_columns == 0 || _rows == 0 || Cells.Length == 0 || x < 0 || y < 0 || x >= _width || y >= _height) return -1;
        int display = Math.Min(_rows - 1, (int)(y / _height * _rows)) * _columns + Math.Min(_columns - 1, (int)(x / _width * _columns));
        return display >= Cells.Length || Cells[display].Clusters == 0 ? -1 : display;
    }
    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        _width = dirtyRect.Width; _height = dirtyRect.Height;
        if (Cells.Length == 0) { canvas.FontColor = Colors.SlateGray; canvas.FontSize = 13; canvas.DrawString(EmptyMessage, dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center); return; }
        int n = Cells.Length;
        while (n > 0 && Cells[n - 1].Clusters == 0) n--;
        if (n == 0) return;
        _columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n * _width / Math.Max(1, _height))));
        _rows = Math.Max(1, (n + _columns - 1) / _columns);
        float cw = _width / _columns, ch = _height / _rows;
        ClearFillGroups();
        for (int index = 0; index < n; index++)
        {
            uint key = MapAggregator.Color(Cells[index]);
            if (!_colors.TryGetValue(key, out var color)) _colors[key] = color = Color.FromUint(key);
            if (!_fillGroups.TryGetValue(key, out var group)) _fillGroups[key] = group = [];
            if (group.Count == 0) _activeFillGroups.Add(key);
            group.Add(index);
        }
        float radius = Math.Min(1.6f, cw / 4);
        foreach (uint key in _activeFillGroups)
        {
            canvas.FillColor = _colors[key];
            foreach (int index in _fillGroups[key])
            {
                int x = index % _columns, y = index / _columns;
                canvas.FillRoundedRectangle(x * cw + 1, y * ch + 1, Math.Max(1, cw - 2), Math.Max(1, ch - 2), radius);
            }
        }
        if (_highlightedCells.Count > 0)
        {
            canvas.StrokeColor = HighlightColor; canvas.StrokeSize = Math.Min(3, Math.Max(1, cw / 4));
            foreach (int index in _highlightedCells)
            {
                if (index < 0 || index >= n) continue;
                int x = index % _columns, y = index / _columns;
                canvas.DrawRoundedRectangle(x * cw + .5f, y * ch + .5f, Math.Max(1, cw - 1), Math.Max(1, ch - 1), radius);
            }
        }
        if (_selectionStart >= 0)
        {
            canvas.StrokeColor = Colors.White; canvas.StrokeSize = 1;
            int first = Math.Max(0, Math.Min(_selectionStart, _selectionEnd));
            int last = Math.Min(n - 1, Math.Max(_selectionStart, _selectionEnd));
            for (int index = first; index <= last; index++)
            {
                int x = index % _columns, y = index / _columns;
                canvas.DrawRectangle(x * cw + .5f, y * ch + .5f, Math.Max(1, cw - 1), Math.Max(1, ch - 1));
            }
        }
    }

    private void ClearFillGroups()
    {
        foreach (uint key in _activeFillGroups) _fillGroups[key].Clear();
        _activeFillGroups.Clear();
    }
}

/// <summary>A linear whole-volume navigator; horizontal position always corresponds to LCN.</summary>
public sealed class MapOverviewDrawable : IDrawable
{
    private static readonly Color BackgroundColor = Color.FromArgb("#172531");
    private static readonly Color ViewportShadeColor = Color.FromArgb("#99111B24");
    private static readonly Color ViewportAccentColor = Color.FromArgb("#FFD45A");
    public MapCell[] Cells { get; set; } = [];
    public long TotalClusters { get; set; }
    public long StartCluster { get; set; }
    public long ClusterCount { get; set; }
    private readonly Dictionary<uint, Color> _colors = [];
    private readonly Dictionary<uint, List<int>> _fillGroups = [];
    private readonly List<uint> _activeFillGroups = [];
    private float _width;

    public long ClusterAt(float x)
    {
        if (TotalClusters <= 0 || _width <= 0) return 0;
        return Math.Clamp((long)(x / _width * TotalClusters), 0, TotalClusters - 1);
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        _width = dirtyRect.Width;
        canvas.FillColor = BackgroundColor; canvas.FillRectangle(dirtyRect);
        if (Cells.Length == 0 || TotalClusters <= 0) return;
        int columns = Math.Max(1, (int)Math.Ceiling(dirtyRect.Width));
        float width = dirtyRect.Width / columns;
        ClearFillGroups();
        for (int x = 0; x < columns; x++)
        {
            int index = Math.Min(Cells.Length - 1, (int)((long)x * Cells.Length / columns));
            uint key = MapAggregator.Color(Cells[index]);
            if (!_colors.TryGetValue(key, out var color)) _colors[key] = color = Color.FromUint(key);
            if (!_fillGroups.TryGetValue(key, out var group)) _fillGroups[key] = group = [];
            if (group.Count == 0) _activeFillGroups.Add(key);
            group.Add(x);
        }
        foreach (uint key in _activeFillGroups)
        {
            canvas.FillColor = _colors[key];
            foreach (int x in _fillGroups[key])
                canvas.FillRectangle(x * width, 0, Math.Max(1, width), dirtyRect.Height);
        }
        float left = (float)StartCluster / TotalClusters * dirtyRect.Width;
        float right = (float)(StartCluster + ClusterCount) / TotalClusters * dirtyRect.Width;
        canvas.FillColor = ViewportShadeColor;
        if (left > 0) canvas.FillRectangle(0, 0, left, dirtyRect.Height);
        if (right < dirtyRect.Width) canvas.FillRectangle(right, 0, dirtyRect.Width - right, dirtyRect.Height);
        canvas.StrokeColor = ViewportAccentColor; canvas.StrokeSize = 2;
        canvas.DrawRectangle(left, 1, Math.Max(2, right - left), Math.Max(1, dirtyRect.Height - 2));
        float center = (left + right) / 2;
        var marker = new PathF(); marker.MoveTo(center - 6, 0); marker.LineTo(center + 6, 0); marker.LineTo(center, 8); marker.Close();
        canvas.FillColor = ViewportAccentColor; canvas.FillPath(marker);
    }

    private void ClearFillGroups()
    {
        foreach (uint key in _activeFillGroups) _fillGroups[key].Clear();
        _activeFillGroups.Clear();
    }
}
