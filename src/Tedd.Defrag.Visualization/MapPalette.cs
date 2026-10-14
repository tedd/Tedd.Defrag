namespace Tedd.Defrag.Visualization;

/// <summary>Shared ARGB colors for the map, navigator and legend.</summary>
public sealed record MapPalette(uint Canvas, uint Allocated, uint Excluded, uint Metadata,
    uint FragmentedLow, uint Fragmented, uint Moving, uint Verified, uint Selection)
{
    public uint FreeSpace => 0xFFFFFFFF;
    public static MapPalette Light { get; } = new(0xFFCED8E0, 0xFF69B5F5, 0xFF24558C,
        0xFF238443, 0xFFF4B5B5, 0xFFC62828, 0xFFF2C230, 0xFF008A8A, 0xFF7840B8);
    public static MapPalette Dark { get; } = new(0xFF172531, 0xFF79BFFF, 0xFF356BA3,
        0xFF48B96A, 0xFFF6B6B6, 0xFFEB4545, 0xFFFFD54A, 0xFF37C9BE, 0xFFD2A3FF);
}
