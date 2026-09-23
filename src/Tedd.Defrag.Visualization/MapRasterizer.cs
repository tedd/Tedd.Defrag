namespace Tedd.Defrag.Visualization;

/// <summary>Writes opaque map cells into a reusable 32-bit pixel buffer.</summary>
public static class MapRasterizer
{
    public static (int Columns, int Rows) Grid(int cells, int width, int height)
    {
        if (cells <= 0 || width <= 0 || height <= 0) return (0, 0);
        int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)cells * width / height)));
        return (columns, (cells + columns - 1) / columns);
    }

    public static void DrawScalar(Span<uint> pixels, int stride, int width, int height,
        ReadOnlySpan<uint> colors, int columns, int rows, uint background, int padding)
    {
        Validate(pixels, stride, width, height, colors.Length, columns, rows);
        Clear(pixels, stride, width, height, background);
        if (columns == 0) return;
        for (int index = 0; index < colors.Length; index++)
        {
            Bounds(index, columns, rows, width, height, padding, out int left, out int top, out int right, out int bottom);
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++) pixels[y * stride + x] = colors[index];
        }
    }

    public static void DrawSpans(Span<uint> pixels, int stride, int width, int height,
        ReadOnlySpan<uint> colors, int columns, int rows, uint background, int padding)
    {
        Validate(pixels, stride, width, height, colors.Length, columns, rows);
        Clear(pixels, stride, width, height, background);
        if (columns == 0) return;
        for (int index = 0; index < colors.Length; index++)
        {
            Bounds(index, columns, rows, width, height, padding, out int left, out int top, out int right, out int bottom);
            for (int y = top; y < bottom; y++)
                pixels.Slice(y * stride + left, right - left).Fill(colors[index]);
        }
    }

    public static void DrawRows(Span<uint> pixels, int stride, int width, int height,
        ReadOnlySpan<uint> colors, int columns, int rows, uint background, int padding)
    {
        Validate(pixels, stride, width, height, colors.Length, columns, rows);
        Clear(pixels, stride, width, height, background);
        if (columns == 0) return;
        for (int row = 0; row < rows; row++)
        {
            int top = row * height / rows + padding;
            int bottom = Math.Max(top + 1, (row + 1) * height / rows - padding);
            bottom = Math.Min(bottom, height);
            for (int y = top; y < bottom; y++)
            {
                Span<uint> line = pixels.Slice(y * stride, width);
                for (int column = 0; column < columns; column++)
                {
                    int index = row * columns + column;
                    if (index >= colors.Length) break;
                    int left = column * width / columns + padding;
                    int right = Math.Min(width, Math.Max(left + 1, (column + 1) * width / columns - padding));
                    if (left < width) line.Slice(left, right - left).Fill(colors[index]);
                }
            }
        }
    }

    public static void Border(Span<uint> pixels, int stride, int width, int height,
        int index, int columns, int rows, uint color, int thickness = 1)
    {
        if (index < 0 || index >= columns * rows || width <= 0 || height <= 0) return;
        Bounds(index, columns, rows, width, height, 0, out int left, out int top, out int right, out int bottom);
        FillRectangle(pixels, stride, width, height, left, top, right, top + thickness, color);
        FillRectangle(pixels, stride, width, height, left, bottom - thickness, right, bottom, color);
        FillRectangle(pixels, stride, width, height, left, top, left + thickness, bottom, color);
        FillRectangle(pixels, stride, width, height, right - thickness, top, right, bottom, color);
    }

    public static void FillRectangle(Span<uint> pixels, int stride, int width, int height,
        int left, int top, int right, int bottom, uint color)
    {
        left = Math.Clamp(left, 0, width); right = Math.Clamp(right, left, width);
        top = Math.Clamp(top, 0, height); bottom = Math.Clamp(bottom, top, height);
        for (int y = top; y < bottom; y++) pixels.Slice(y * stride + left, right - left).Fill(color);
    }

    public static void DrawOverviewRows(Span<uint> pixels, int stride, int width, int height, ReadOnlySpan<uint> colors)
    {
        if (colors.IsEmpty) throw new ArgumentException("The overview requires at least one color.", nameof(colors));
        Validate(pixels, stride, width, height, 0, 0, 0);
        for (int y = 0; y < height; y++)
        {
            Span<uint> row = pixels.Slice(y * stride, width);
            for (int x = 0; x < width; x++) row[x] = colors[(int)((long)x * colors.Length / width)];
        }
    }

    private static void Bounds(int index, int columns, int rows, int width, int height, int padding,
        out int left, out int top, out int right, out int bottom)
    {
        int column = index % columns, row = index / columns;
        left = column * width / columns + padding;
        top = row * height / rows + padding;
        right = Math.Min(width, Math.Max(left + 1, (column + 1) * width / columns - padding));
        bottom = Math.Min(height, Math.Max(top + 1, (row + 1) * height / rows - padding));
        left = Math.Min(left, width); top = Math.Min(top, height);
    }

    private static void Clear(Span<uint> pixels, int stride, int width, int height, uint background)
    {
        for (int y = 0; y < height; y++) pixels.Slice(y * stride, width).Fill(background);
    }

    private static void Validate(Span<uint> pixels, int stride, int width, int height, int count, int columns, int rows)
    {
        if (width < 0 || height < 0 || stride < width || (long)stride * height > pixels.Length ||
            count < 0 || columns < 0 || rows < 0 || count > (long)columns * rows)
            throw new ArgumentOutOfRangeException(nameof(pixels));
    }
}
