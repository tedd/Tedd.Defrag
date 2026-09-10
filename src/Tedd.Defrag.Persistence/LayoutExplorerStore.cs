using System.Buffers;
using System.Text;
using System.Text.Json;
using Tedd.Defrag.Core;

namespace Tedd.Defrag.Persistence;

/// <summary>
/// Persists the physical layout separately from the bounded job snapshot. Queries stream the
/// file table and return only one fixed-size map region and a bounded set of matching files.
/// </summary>
public sealed class LayoutExplorerStore
{
    private const uint Magic = 0x50414D54; // TMAP
    private const int Version = 1;
    private const int MaximumFiles = 500;
    private readonly JobStore _store;

    public LayoutExplorerStore(JobStore store) => _store = store;

    public void Save(Guid id, VolumeLayout layout)
    {
        Directory.CreateDirectory(_store.JobDirectory(id));
        string path = Path.Combine(_store.JobDirectory(id), "layout.bin");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string journal = Path.Combine(_store.JobDirectory(id), "moves.jsonl");
        long journalLength = File.Exists(journal) ? new FileInfo(journal).Length : 0;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic); writer.Write(Version); writer.Write(layout.TotalClusters);
                writer.Write(layout.Volume.BytesPerCluster); writer.Write(journalLength);
                writer.Write(layout.Bitmap.Length); writer.Write(layout.Bitmap);
                writer.Write(layout.Files.Length);
                foreach (var file in layout.Files)
                {
                    writer.Write(file.FileId); writer.Write(file.Size); writer.Write((int)file.Flags);
                    WriteText(writer, file.Path); WriteText(writer, file.StreamName);
                    writer.Write(file.Extents.Length);
                    foreach (var extent in file.Extents)
                    { writer.Write(extent.Vcn); writer.Write(extent.Lcn); writer.Write(extent.Length); }
                }
                writer.Flush(); stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null, ignoreMetadataErrors: true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public MapRegion Explore(Guid id, long start, long length, int cellCount, bool includeFiles,
        string? selectedPath = null, ulong? selectedFileId = null, string? selectedStream = null)
    {
        if (id == Guid.Empty || cellCount is < 0 or > 65536 || selectedPath?.Length > 32760)
            throw new ArgumentException("Invalid layout query.");
        string path = Path.Combine(_store.JobDirectory(id), "layout.bin");
        if (!File.Exists(path)) throw new InvalidOperationException("The detailed layout is not available until analysis has produced a map.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version) throw new InvalidDataException("The detailed layout index is incompatible.");
        long totalClusters = reader.ReadInt64();
        _ = reader.ReadInt32(); // Bytes per cluster is retained for forward-compatible indexes.
        long journalOffset = reader.ReadInt64();
        int bitmapLength = ReadCount(reader, 1024 * 1024 * 1024);
        long bitmapOffset = stream.Position;
        if (totalClusters <= 0 || totalClusters > (long)bitmapLength * 8) throw new InvalidDataException("The detailed layout geometry is invalid.");
        start = Math.Clamp(start, 0, totalClusters - 1);
        length = length <= 0 ? totalClusters - start : Math.Clamp(length, 1, totalClusters - start);
        long end = start + length;

        var journalMoves = ReadVerifiedMoves(id, journalOffset);
        MapCell[] cells = BuildAllocationCells(stream, bitmapOffset, bitmapLength, start, length, cellCount, journalMoves.All);
        stream.Position = bitmapOffset + bitmapLength;
        int fileCount = ReadCount(reader, 10_000_000);
        var files = includeFiles ? new List<ClusterFile>(Math.Min(MaximumFiles, 32)) : null;
        int matchingFiles = 0, returnedTextBytes = 0;
        MapFileSelection? selection = null;

        for (int fileIndex = 0; fileIndex < fileCount; fileIndex++)
        {
            ulong fileId = reader.ReadUInt64(); long bytes = reader.ReadInt64(); var flags = (StreamFlags)reader.ReadInt32();
            int pathLength = ReadCount(reader, 1024 * 1024); long pathOffset = stream.Position; stream.Position += pathLength;
            int streamLength = ReadCount(reader, 1024 * 1024); long streamOffset = stream.Position; stream.Position += streamLength;
            int extentCount = ReadCount(reader, 1_000_000);
            bool possibleIdentity = selectedFileId == fileId;
            bool readForPath = selectedPath != null;
            string? filePath = readForPath ? ReadTextAt(stream, pathOffset, pathLength) : null;
            string? streamName = readForPath || possibleIdentity ? ReadTextAt(stream, streamOffset, streamLength) : null;
            bool pathMatch = selectedPath != null && string.Equals(filePath, selectedPath, StringComparison.OrdinalIgnoreCase);
            bool identityMatch = possibleIdentity && string.Equals(streamName, selectedStream ?? "", StringComparison.Ordinal);
            bool retainExtents = pathMatch || identityMatch || journalMoves.ByFileIndex.ContainsKey(fileIndex);
            List<Extent>? extents = retainExtents ? new(extentCount + 2) : null;
            long clusters = 0, overlap = 0;

            for (int extentIndex = 0; extentIndex < extentCount; extentIndex++)
            {
                var extent = new Extent(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
                extents?.Add(extent);
                if (extent.IsSparse) continue;
                clusters += extent.Length;
                overlap += Overlap(extent.Lcn, extent.End, start, end);
                if (extents == null) AddSemantic(cells, start, length, extent, extentCount > 1, flags);
            }

            if (journalMoves.ByFileIndex.TryGetValue(fileIndex, out var moves))
            {
                foreach (var move in moves) Apply(extents!, move);
                clusters = 0; overlap = 0;
                foreach (var extent in extents!)
                {
                    if (extent.IsSparse) continue;
                    clusters += extent.Length; overlap += Overlap(extent.Lcn, extent.End, start, end);
                    AddSemantic(cells, start, length, extent, extents.Count > 1, flags);
                }
                extentCount = extents.Count;
            }

            if (overlap > 0 && includeFiles)
            {
                matchingFiles++;
                if (files!.Count < MaximumFiles && returnedTextBytes + pathLength + streamLength <= 1_000_000)
                {
                    filePath ??= ReadTextAt(stream, pathOffset, pathLength);
                    streamName ??= ReadTextAt(stream, streamOffset, streamLength);
                    files.Add(new(fileId, filePath, streamName, bytes, clusters, overlap, extentCount, Status(flags, extentCount)));
                    returnedTextBytes += pathLength + streamLength;
                }
            }

            if (pathMatch || identityMatch)
            {
                filePath ??= ReadTextAt(stream, pathOffset, pathLength);
                streamName ??= ReadTextAt(stream, streamOffset, streamLength);
                var ranges = (extents ?? []).Where(e => !e.IsSparse).Take(65536).Select(e => new ClusterRange(e.Lcn, e.Length)).ToArray();
                var candidate = new MapFileSelection(fileId, filePath, streamName, bytes, clusters, extentCount, Status(flags, extentCount), ranges);
                if (identityMatch || selection == null || selection.Stream.Length > 0 && streamName.Length == 0) selection = candidate;
            }
        }

        foreach (var move in journalMoves.All) AddActivity(cells, start, length, move.DestinationLcn, move.Clusters);
        return new(start, length, totalClusters, cells, files?.ToArray() ?? [], matchingFiles, selection);
    }

    private static MapCell[] BuildAllocationCells(FileStream stream, long bitmapOffset, int bitmapLength,
        long start, long length, int cellCount, IReadOnlyList<PlannedMove> moves)
    {
        if (cellCount == 0) return [];
        long firstByte = start >> 3, lastByte = (start + length + 7) >> 3;
        int byteCount = checked((int)(lastByte - firstByte));
        byte[] bitmap = new byte[byteCount];
        stream.Position = bitmapOffset + firstByte; stream.ReadExactly(bitmap);
        long bitmapBase = firstByte * 8;
        foreach (var move in moves)
        {
            SetClipped(bitmap, bitmapBase, start, start + length, move.SourceLcn, move.Clusters, false);
            SetClipped(bitmap, bitmapBase, start, start + length, move.DestinationLcn, move.Clusters, true);
        }
        var cells = new MapCell[cellCount];
        long step = Math.Max(1, (length + cellCount - 1) / cellCount), end = start + length;
        for (int i = 0; i < cells.Length; i++)
        {
            long position = start + i * step, count = Math.Clamp(end - position, 0, step);
            if (count == 0) continue;
            long allocated = BitmapOperations.CountRange(bitmap, position - bitmapBase, count);
            cells[i] = new(count, allocated, 0, 0, 0, 0, 0);
        }
        return cells;
    }

    private static void SetClipped(byte[] bitmap, long bitmapBase, long windowStart, long windowEnd,
        long rangeStart, long rangeLength, bool allocated)
    {
        long a = Math.Max(windowStart, rangeStart), b = Math.Min(windowEnd, rangeStart + rangeLength);
        if (a < b) BitmapOperations.SetRange(bitmap, a - bitmapBase, b - a, allocated);
    }

    private static void AddSemantic(MapCell[] cells, long start, long length, Extent extent, bool fragmented, StreamFlags flags)
    {
        if (cells.Length == 0 || extent.IsSparse) return;
        long end = start + length, a = Math.Max(start, extent.Lcn), b = Math.Min(end, extent.End);
        if (a >= b) return;
        long step = Math.Max(1, (length + cells.Length - 1) / cells.Length);
        int first = (int)((a - start) / step), last = (int)((b - 1 - start) / step);
        bool metadata = (flags & (StreamFlags.Metadata | StreamFlags.Directory)) != 0;
        bool excluded = !Movable(flags, 1) || (flags & StreamFlags.Excluded) != 0;
        for (int i = first; i <= last && i < cells.Length; i++)
        {
            long overlap = Math.Min(b, start + (i + 1) * step) - Math.Max(a, start + i * step);
            ref var cell = ref cells[i];
            cell = cell with
            {
                Fragmented = Math.Min(cell.Allocated, cell.Fragmented + (fragmented ? overlap : 0)),
                Metadata = Math.Min(cell.Allocated, cell.Metadata + (metadata ? overlap : 0)),
                Excluded = Math.Min(cell.Allocated, cell.Excluded + (excluded ? overlap : 0))
            };
        }
    }

    private static void AddActivity(MapCell[] cells, long start, long length, long activityStart, long activityLength)
    {
        if (cells.Length == 0) return;
        long end = start + length, a = Math.Max(start, activityStart), b = Math.Min(end, activityStart + activityLength);
        if (a >= b) return;
        long step = Math.Max(1, (length + cells.Length - 1) / cells.Length);
        int first = (int)((a - start) / step), last = (int)((b - 1 - start) / step);
        for (int i = first; i <= last && i < cells.Length; i++)
        {
            long overlap = Math.Min(b, start + (i + 1) * step) - Math.Max(a, start + i * step);
            cells[i] = cells[i] with { Verified = overlap };
        }
    }

    private static void Apply(List<Extent> extents, PlannedMove move)
    {
        var updated = new List<Extent>(extents.Count + 2);
        foreach (var extent in extents)
        {
            long a = Math.Max(extent.Vcn, move.Vcn), b = Math.Min(extent.Vcn + extent.Length, move.Vcn + move.Clusters);
            if (a >= b) { updated.Add(extent); continue; }
            if (a > extent.Vcn) updated.Add(new(extent.Vcn, extent.Lcn, a - extent.Vcn));
            updated.Add(new(a, move.DestinationLcn + a - move.Vcn, b - a));
            if (b < extent.Vcn + extent.Length) updated.Add(new(b, extent.Lcn + b - extent.Vcn, extent.Vcn + extent.Length - b));
        }
        extents.Clear();
        foreach (var extent in updated)
        {
            if (extents.Count > 0)
            {
                var previous = extents[^1];
                if (previous.Vcn + previous.Length == extent.Vcn && previous.Lcn >= 0 && extent.Lcn == previous.Lcn + previous.Length)
                { extents[^1] = previous with { Length = previous.Length + extent.Length }; continue; }
            }
            extents.Add(extent);
        }
    }

    private (List<PlannedMove> All, Dictionary<int, List<PlannedMove>> ByFileIndex) ReadVerifiedMoves(Guid id, long offset)
    {
        var all = new List<PlannedMove>(); var byFile = new Dictionary<int, List<PlannedMove>>();
        string path = Path.Combine(_store.JobDirectory(id), "moves.jsonl");
        if (!File.Exists(path)) return (all, byFile);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset < 0 || offset > stream.Length) return (all, byFile);
        stream.Position = offset;
        using var text = new StreamReader(stream, Encoding.UTF8);
        while (text.ReadLine() is { } line)
        {
            MoveJournalEntry? entry;
            try { entry = JsonSerializer.Deserialize<MoveJournalEntry>(line, JobStore.Json); }
            catch (JsonException) { continue; }
            if (entry?.Phase != "verified") continue;
            all.Add(entry.Move);
            if (!byFile.TryGetValue(entry.Move.FileIndex, out var list)) byFile.Add(entry.Move.FileIndex, list = []);
            list.Add(entry.Move);
        }
        return (all, byFile);
    }

    private static string Status(StreamFlags flags, int extents) => Movable(flags, extents) ? "Eligible" : flags.ToString();
    private static bool Movable(StreamFlags flags, int extents) => extents > 0 && (flags & (StreamFlags.Sparse | StreamFlags.Compressed |
        StreamFlags.Encrypted | StreamFlags.ReparsePoint | StreamFlags.Incomplete | StreamFlags.HardLinked |
        StreamFlags.Excluded | StreamFlags.Resident)) == 0;
    private static long Overlap(long a, long b, long c, long d) => Math.Max(0, Math.Min(b, d) - Math.Max(a, c));
    private static int ReadCount(BinaryReader reader, int maximum)
    {
        int value = reader.ReadInt32();
        if (value < 0 || value > maximum) throw new InvalidDataException("The detailed layout index contains an invalid count.");
        return value;
    }
    private static void WriteText(BinaryWriter writer, string text)
    {
        int length = Encoding.UTF8.GetByteCount(text); writer.Write(length);
        if (length == 0) return;
        byte[] value = ArrayPool<byte>.Shared.Rent(length);
        try { Encoding.UTF8.GetBytes(text, value); writer.Write(value, 0, length); }
        finally { ArrayPool<byte>.Shared.Return(value); }
    }
    private static string ReadTextAt(FileStream stream, long offset, int length)
    {
        if (length == 0) return "";
        long resume = stream.Position; stream.Position = offset;
        byte[] value = ArrayPool<byte>.Shared.Rent(length);
        try { stream.ReadExactly(value.AsSpan(0, length)); return Encoding.UTF8.GetString(value, 0, length); }
        finally { stream.Position = resume; ArrayPool<byte>.Shared.Return(value); }
    }
}
