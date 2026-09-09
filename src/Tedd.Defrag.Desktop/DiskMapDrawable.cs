using Tedd.Defrag.Core;
using Tedd.Defrag.Visualization;

namespace Tedd.Defrag.Desktop;

/// <summary>One retained surface, bounded cells, cached palette; draw traversal creates no objects.</summary>
public sealed class DiskMapDrawable : IDrawable
{
    public MapCell[] Cells { get; set; } = [];
    public string EmptyMessage { get; set; } = "Select a volume to begin.";
    public int Start { get; private set; }
    public int Count { get; private set; } = int.MaxValue;
    private readonly Dictionary<uint, Color> _colors = [];
    private float _width, _height;
    private int _columns, _rows;
    public int Anchor { get; set; }
    public DiskMapDrawable()
    {
        // Quantized allocation mixtures plus all semantic colors, prepared once.
        for (int i = 0; i <= 256; i++)
        {
            var c = new MapCell(256, i, 0, 0, 0, 0, 0); Add(MapAggregator.Color(c));
        }
        foreach (uint c in new uint[] { 0xFF101924, 0xFF172330, 0xFFFFFFFF, 0xFF79E7AE, 0xFFA995F5, 0xFFF5AF71, 0xFF617084 }) Add(c);
        void Add(uint c) => _colors.TryAdd(c, Color.FromUint(c));
    }
    public void Reset() { Start = 0; Count = int.MaxValue; Anchor = Cells.Length / 2; }
    public void Zoom(bool inward)
    {
        int current = Math.Min(Count, Cells.Length);
        Count = Math.Clamp(inward ? current / 2 : current * 2, Math.Min(64, Cells.Length), Math.Max(1, Cells.Length));
        Start = Math.Clamp(Anchor - Count / 2, 0, Math.Max(0, Cells.Length - Count));
    }
    public int Hit(float x, float y)
    {
        if (_columns == 0 || _rows == 0 || Cells.Length == 0 || x < 0 || y < 0 || x >= _width || y >= _height) return -1;
        int display = Math.Min(_rows - 1, (int)(y / _height * _rows)) * _columns + Math.Min(_columns - 1, (int)(x / _width * _columns));
        return display >= Math.Min(Count, Cells.Length - Start) ? -1 : Start + display;
    }
    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        _width = dirtyRect.Width; _height = dirtyRect.Height;
        if (Cells.Length == 0) { canvas.FontColor = Colors.SlateGray; canvas.FontSize = 13; canvas.DrawString(EmptyMessage, dirtyRect, HorizontalAlignment.Center, VerticalAlignment.Center); return; }
        int n = Math.Min(Count, Cells.Length - Start);
        _columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n * _width / Math.Max(1, _height))));
        _rows = Math.Max(1, (n + _columns - 1) / _columns);
        float cw = _width / _columns, ch = _height / _rows;
        for (int y = 0; y < _rows; y++) for (int x = 0; x < _columns; x++)
        {
            int slot = y * _columns + x;
            if (slot >= n) continue;
            int index = Start + slot;
            var cell = Cells[Math.Min(index, Cells.Length - 1)];
            uint key = MapAggregator.Color(cell);
            // The few non-quantized colors can be cached once on arrival; normal draw passes allocate nothing.
            if (!_colors.TryGetValue(key, out var color)) _colors[key] = color = Color.FromUint(key);
            canvas.FillColor = color;
            canvas.FillRoundedRectangle(x * cw + 1, y * ch + 1, Math.Max(1, cw - 2), Math.Max(1, ch - 2), Math.Min(1.6f, cw / 4));
        }
    }
}
