using System.Buffers.Binary;
using System.Diagnostics;
using Tedd.Defrag.Core;
using Tedd.Defrag.Ntfs;

namespace Tedd.Defrag.Windows;

public sealed class RawMftScanner
{
    public VolumeLayout Scan(NtfsVolume volume, JobRequest request, Action<double, long, string>? progress, Action? checkpoint, CancellationToken token)
    {
        byte[] bitmap = volume.ReadBitmap(request.Resources.MemoryMiB, p => { checkpoint?.Invoke(); progress?.Invoke(p * .1, 0, "Reading allocation bitmap"); }, token);
        var warnings = new List<string>();
        var records = new Dictionary<ulong, NtfsRecord>();
        long skipped = 0, scanned = 0; bool complete = true;
        var mft = Parse(volume.ReadRecord(0), 0);
        var runs = GetMftRuns(volume, mft);
        using var buffer = new AlignedReadBuffer(1024 * 1024);
        // Keep space for resolved paths, the final layout and temporary allocations. Check
        // every batch rather than waiting for the UI update timer or the OS hard limit.
        long scanHeapBudget = request.Resources.MemoryMiB == 0 ? long.MaxValue : request.Resources.MemoryMiB * 1024L * 1024 / 4;
        long totalRecords = volume.MftLength / volume.RecordSize;
        var watch = Stopwatch.StartNew(); long lastUpdate = -1000;
        foreach (var run in runs)
        {
            long byteOffset = run.Vcn * volume.Info.BytesPerCluster;
            long runBytes = Math.Min(run.Length * volume.Info.BytesPerCluster, volume.MftLength - byteOffset);
            if (runBytes <= 0) continue;
            for (long offset = 0; offset < runBytes;)
            {
                token.ThrowIfCancellationRequested(); checkpoint?.Invoke();
                if (GC.GetTotalMemory(false) >= scanHeapBudget)
                {
                    warnings.Add($"File coverage is limited by the {request.Resources.MemoryMiB:N0} MiB memory cap. Increase the cap for a more complete scan; the allocation bitmap covers the entire volume.");
                    complete = false; break;
                }
                int count = (int)Math.Min(buffer.Length, runBytes - offset);
                count -= count % volume.RecordSize;
                if (count == 0) { complete = false; break; }
                NativeIo.ReadExactly(volume.Handle, buffer.AsSpan(0, count), checked(run.Lcn * volume.Info.BytesPerCluster + offset));
                for (int p = 0; p < count; p += volume.RecordSize)
                {
                    long number = (byteOffset + offset + p) / volume.RecordSize;
                    var span = buffer.AsSpan(p, volume.RecordSize); scanned++;
                    // Unused records are not scan failures.
                    if (span[..4].SequenceEqual("FILE"u8) && (BinaryPrimitives.ReadUInt16LittleEndian(span[22..]) & 1) == 0) continue;
                    if (!NtfsRecordParser.TryParse(span, number, volume.TotalClusters, out var record)) { skipped++; continue; }
                    if (record!.BaseFileId == 0) records[record.FileId] = record;
                }
                offset += count;
                if (watch.ElapsedMilliseconds - lastUpdate >= 200)
                {
                    progress?.Invoke(.1 + .8 * scanned / Math.Max(1d, totalRecords), scanned, "Scanning NTFS master file table"); lastUpdate = watch.ElapsedMilliseconds;
                    using var process = Process.GetCurrentProcess();
                    if (request.Resources.MemoryMiB > 0 && process.PrivateMemorySize64 > request.Resources.MemoryMiB * 1024L * 1024 * .65)
                    { warnings.Add("File scan stopped at the memory headroom threshold; allocation bitmap remains available."); complete = false; break; }
                }
            }
            if (!complete) break;
        }
        var cache = new Dictionary<ulong, string>(); var rules = new PathRules(request.SelectedPaths, request.Exclusions);
        var files = new List<FileLayout>(records.Count);
        foreach (var (id, record) in records)
        {
            token.ThrowIfCancellationRequested();
            string path = Resolve(id, 0); StreamFlags flags = record.Flags;
            if (path.Contains("<unresolved>", StringComparison.Ordinal)) flags |= StreamFlags.Incomplete;
            if (rules.IsExcluded(path)) flags |= StreamFlags.Excluded;
            foreach (var name in record.Names)
            {
                string alias = Resolve(name.ParentId, 0).TrimEnd('\\') + "\\" + name.Name;
                if (rules.IsExcluded(alias)) flags |= StreamFlags.Excluded;
            }
            foreach (var stream in record.Streams)
            {
                if (stream.Type == 0x20) continue;
                string streamName = stream.Type switch { 0xA0 => $":{stream.Name}:$INDEX_ALLOCATION", 0xB0 => $":{stream.Name}:$BITMAP", _ => stream.Name.Length == 0 ? "" : $":{stream.Name}:$DATA" };
                bool mftData = (id & 0xFFFFFFFFFFFF) == 0 && stream.Type == 0x80 && stream.Name == "";
                var streamFlags = mftData ? flags & ~StreamFlags.Incomplete : flags | (stream.Incomplete ? StreamFlags.Incomplete : 0);
                files.Add(new(id, path, streamName, streamFlags, stream.Size,
                    record.Created, record.Modified, MergeAdjacent(mftData ? runs : stream.Extents)));
            }
        }
        if (skipped > 0) warnings.Add($"{skipped:N0} records could not be validated; their allocation remains unknown.");
        int incomplete = files.Count(f => (f.Flags & StreamFlags.Incomplete) != 0);
        if (incomplete > 0) warnings.Add($"{incomplete:N0} streams have extension attributes or unresolved ancestry and are excluded from custom moves.");
        warnings.Add("Live NTFS observations may change during the scan. Every relocation is revalidated through the filesystem.");
        progress?.Invoke(1, scanned, "Analysis complete");
        return new(volume.Info, volume.TotalClusters, bitmap, files.ToArray(), DateTimeOffset.UtcNow, scanned, skipped,
            complete && skipped == 0, warnings.ToArray(), volume.MftZone);

        NtfsRecord Parse(byte[] data, long n) => NtfsRecordParser.TryParseFileSystemRecord(data, n, volume.TotalClusters, out var r) ? r! : throw new IOException($"Invalid MFT record {n} returned by NTFS (record size {volume.RecordSize}, total clusters {volume.TotalClusters}).");
        string Resolve(ulong id, int depth)
        {
            if ((id & 0xFFFFFFFFFFFF) == 5) return volume.Info.Root.TrimEnd('\\');
            if (depth > 128 || !records.TryGetValue(id, out var r)) return volume.Info.Root + "<unresolved>";
            if (cache.TryGetValue(id, out var cached)) return cached;
            var name = r.Names.FirstOrDefault(n => n.Namespace != 2) ?? r.Names.FirstOrDefault();
            if (name == null) return volume.Info.Root + "<unresolved>";
            string result = Resolve(name.ParentId, depth + 1) + "\\" + name.Name;
            cache[id] = result; return result;
        }
    }
    private static Extent[] GetMftRuns(NtfsVolume volume, NtfsRecord mft)
    {
        var streams = mft.Streams.Where(s => s.Type == 0x80 && s.Name == "").ToList();
        var references = mft.References.ToList();
        foreach (var list in mft.Streams.Where(s => s.Type == 0x20))
        {
            if (list.Size > 16 * 1024 * 1024 || list.Incomplete) throw new IOException("MFT attribute list exceeds supported bounds.");
            using var bytes = new AlignedReadBuffer(checked((int)((list.Size + volume.SectorSize - 1) / volume.SectorSize * volume.SectorSize)));
            foreach (var e in list.Extents)
            {
                int start = checked((int)(e.Vcn * volume.Info.BytesPerCluster));
                int count = (int)Math.Min(e.Length * volume.Info.BytesPerCluster, bytes.Length - (long)start);
                if (e.IsSparse || start < 0 || count < 0) throw new IOException("Invalid MFT attribute-list mapping.");
                NativeIo.ReadExactly(volume.Handle, bytes.AsSpan(start, count), e.Lcn * volume.Info.BytesPerCluster);
            }
            references.AddRange(NtfsRecordParser.ParseAttributeList(bytes.AsSpan(0, (int)list.Size)));
        }
        foreach (var reference in references.Where(r => r.Type == 0x80 && r.Name == "" && r.FirstVcn > 0).DistinctBy(r => r.FileId))
        {
            long number = (long)(reference.FileId & 0xFFFFFFFFFFFF);
            if (!NtfsRecordParser.TryParseFileSystemRecord(volume.ReadRecord(number), number, volume.TotalClusters, out var extension)
                || extension!.FileId != reference.FileId || extension.BaseFileId != mft.FileId)
                throw new IOException("MFT extension identity could not be validated.");
            streams.AddRange(extension.Streams.Where(s => s.Type == 0x80 && s.Name == ""));
        }
        var runs = streams.SelectMany(s => s.Extents).OrderBy(e => e.Vcn).ToArray(); long expected = 0;
        foreach (var run in runs) { if (run.IsSparse || run.Vcn != expected) throw new IOException("MFT mapping is incomplete; raw scan refused."); expected += run.Length; }
        if (expected * volume.Info.BytesPerCluster < volume.MftLength) throw new IOException("MFT mapping does not cover its valid data length.");
        return runs;
    }
    public static Extent[] MergeAdjacent(Extent[] extents)
    {
        if (extents.Length < 2) return extents;
        var result = new List<Extent>(extents.Length);
        foreach (var e in extents)
        {
            if (result.Count > 0 && !e.IsSparse && result[^1].End == e.Lcn && result[^1].Vcn + result[^1].Length == e.Vcn)
                result[^1] = result[^1] with { Length = result[^1].Length + e.Length };
            else result.Add(e);
        }
        return result.ToArray();
    }
}
