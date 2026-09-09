using Tedd.Defrag.Core;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Engine;

public static class LayoutMutation
{
    public static bool Matches(ReadOnlySpan<Extent> extents, long vcn, long lcn, long count)
    {
        foreach (var e in extents)
            if (e.Vcn <= vcn && count > 0 && vcn - e.Vcn <= e.Length - count && e.Lcn >= 0 && e.Lcn + vcn - e.Vcn == lcn) return true;
        return false;
    }
    public static void Apply(VolumeLayout layout, PlannedMove move)
    {
        var file = layout.Files[move.FileIndex];
        if (!Matches(file.Extents, move.Vcn, move.SourceLcn, move.Clusters) || BitmapOperations.CountRange(layout.Bitmap, move.DestinationLcn, move.Clusters) != 0)
            throw new InvalidOperationException("Layout update precondition failed.");
        var updated = new List<Extent>(file.Extents.Length + 2);
        foreach (var e in file.Extents)
        {
            long a = Math.Max(e.Vcn, move.Vcn), b = Math.Min(e.Vcn + e.Length, move.Vcn + move.Clusters);
            if (a >= b) { updated.Add(e); continue; }
            if (a > e.Vcn) updated.Add(new(e.Vcn, e.Lcn, a - e.Vcn));
            updated.Add(new(a, move.DestinationLcn + a - move.Vcn, b - a));
            if (b < e.Vcn + e.Length) updated.Add(new(b, e.Lcn + b - e.Vcn, e.Vcn + e.Length - b));
        }
        layout.Files[move.FileIndex] = file with { Extents = RawMftScanner.MergeAdjacent(updated.ToArray()) };
        BitmapOperations.SetRange(layout.Bitmap, move.SourceLcn, move.Clusters, false);
        BitmapOperations.SetRange(layout.Bitmap, move.DestinationLcn, move.Clusters, true);
    }
}
