namespace Tedd.Defrag.Core;

/// <summary>Synchronous scan delta. Bitmap storage is borrowed only for the duration of the callback.</summary>
public sealed record ScanMapUpdate(long TotalClusters, byte[]? Bitmap = null, long BitmapClusters = 0, FileLayout? File = null);
