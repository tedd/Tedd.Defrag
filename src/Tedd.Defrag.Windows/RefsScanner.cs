using System.ComponentModel;
using System.Diagnostics;
using Tedd.Defrag.Core;

namespace Tedd.Defrag.Windows;

public sealed class RefsScanner
{
    public VolumeLayout Scan(VolumeInfo volume, JobRequest request, Action<double, long, string> progress,
        Action checkpoint, CancellationToken token, Action<WorkProgress>? diagnostics = null)
    {
        if (!FileSystemCapabilities.IsRefs(volume.FileSystem)) throw new NotSupportedException("This scanner requires ReFS.");
        var watch = Stopwatch.StartNew();
        using var handle = NativeIo.Open(VolumeDiscovery.Device(volume.Root));
        long totalClusters = VolumeBitmap.QueryTotalClusters(handle);
        byte[] bitmap = VolumeBitmap.Read(handle, totalClusters, request.Resources.MemoryMiB, p =>
        {
            checkpoint();
            progress(p * .1, 0, "Reading ReFS allocation bitmap");
        }, token);
        var files = new List<FileLayout>();
        var warnings = new List<string>
        {
            "ReFS file coverage includes accessible unnamed data streams. Filesystem metadata, named streams and reparse targets are not enumerated; fragmentation totals describe observed streams only.",
            "ReFS observations are live. Custom relocation is unavailable; use WindowsDefrag or Automatic for Windows-managed maintenance."
        };
        var rules = new PathRules(request.SelectedPaths, request.Exclusions);
        var pending = new Stack<IEnumerator<FileSystemInfo>>();
        long scanned = 0, skipped = 0, lastReport = -1000;
        long heapBudget = request.Resources.MemoryMiB == 0 ? long.MaxValue : request.Resources.MemoryMiB * 1024L * 1024 / 4;
        // IDs are snapshot-local keys. ReFS has 128-bit native identities; never truncate
        // one to fit the NTFS relocation model, or use these keys with OpenFileById.
        ulong nextId = 1;
        try
        {
            OpenDirectory(new DirectoryInfo(volume.Root));
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested(); checkpoint();
                if (watch.ElapsedMilliseconds - lastReport >= 200)
                {
                    diagnostics?.Invoke(new("Enumerating ReFS files", scanned, 0, "entries", ActiveWorkers: 1, PeakWorkers: 1,
                        ElapsedMilliseconds: watch.ElapsedMilliseconds, Detail: $"Directory traversal and filesystem extent queries; {skipped:N0} entries skipped. Total entry count is unknown."));
                    progress(.1, scanned, "Enumerating ReFS files; total count unknown");
                    lastReport = watch.ElapsedMilliseconds;
                }
                if (MemoryLimitReached())
                {
                    warnings.Add($"File coverage is limited by the {request.Resources.MemoryMiB:N0} MiB memory cap; the allocation bitmap covers the entire volume.");
                    break;
                }
                FileSystemInfo entry;
                try
                {
                    if (!pending.Peek().MoveNext()) { pending.Pop().Dispose(); continue; }
                    entry = pending.Peek().Current;
                }
                catch (Exception e) when (IsScanError(e))
                {
                    pending.Pop().Dispose(); Skip(e); continue;
                }
                scanned++;
                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    if (entry is DirectoryInfo directory) { OpenDirectory(directory); continue; }
                    using var file = NativeIo.Open(entry.FullName);
                    // The handle is opened with OPEN_REPARSE_POINT; re-check the final
                    // attributes after opening in case the directory entry changed.
                    var identity = NtfsVolume.Identity(file);
                    if ((identity.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    { skipped++; continue; }
                    // GET_RETRIEVAL_POINTERS supports ReFS as well as NTFS.
                    Extent[] extents = NormalizeExtents(NtfsVolume.RetrievalPointers(file, token, checkpoint), totalClusters);
                    var flags = StreamFlags.AnalysisOnly;
                    if (rules.IsExcluded(entry.FullName)) flags |= StreamFlags.Excluded;
                    if (extents.Any(e => e.IsSparse)) flags |= StreamFlags.Sparse;
                    files.Add(new(nextId++, entry.FullName, "", flags, ((FileInfo)entry).Length,
                        entry.CreationTimeUtc.Ticks, entry.LastWriteTimeUtc.Ticks, extents));
                }
                catch (Exception e) when (IsScanError(e)) { Skip(e); }
            }
        }
        finally { while (pending.TryPop(out var enumerator)) enumerator.Dispose(); }
        if (skipped > 0) warnings.Add($"{skipped:N0} entries or directory enumerations were skipped; their allocation remains represented in the volume bitmap.");
        diagnostics?.Invoke(new("ReFS analysis complete", scanned, scanned, "entries", PeakWorkers: 1,
            ElapsedMilliseconds: watch.ElapsedMilliseconds, Detail: $"{files.Count:N0} observed streams; {skipped:N0} skipped entries. File coverage is partial."));
        progress(1, scanned, "ReFS analysis complete; partial file coverage");
        return new(volume, totalClusters, bitmap, files.ToArray(), DateTimeOffset.UtcNow, scanned, skipped, false, warnings.ToArray());

        void OpenDirectory(DirectoryInfo directory)
        {
            pending.Push(directory.EnumerateFileSystemInfos("*", new EnumerationOptions
            { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }).GetEnumerator());
        }
        void Skip(Exception error)
        {
            skipped++;
            if (warnings.Count < 20) warnings.Add(error.Message);
        }
        bool MemoryLimitReached()
        {
            if (request.Resources.MemoryMiB == 0) return false;
            if (GC.GetTotalMemory(false) >= heapBudget) return true;
            if (scanned % 256 != 0) return false;
            using var process = Process.GetCurrentProcess();
            return process.PrivateMemorySize64 > request.Resources.MemoryMiB * 1024L * 1024 * .65;
        }
    }

    private static bool IsScanError(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception;

    internal static Extent[] NormalizeExtents(Extent[] extents, long totalClusters)
    {
        var result = new List<Extent>();
        long nextVcn = 0;
        foreach (var extent in extents)
        {
            if (extent.Vcn != nextVcn || extent.Length <= 0 || extent.Lcn < -1 ||
                extent.Vcn > long.MaxValue - extent.Length || !extent.IsSparse && extent.Lcn > totalClusters - extent.Length)
                throw new IOException("Invalid ReFS retrieval extent.");
            nextVcn = extent.Vcn + extent.Length;
            if (result.Count > 0 && !extent.IsSparse && !result[^1].IsSparse && result[^1].End == extent.Lcn)
                result[^1] = result[^1] with { Length = checked(result[^1].Length + extent.Length) };
            else result.Add(extent);
        }
        return result.ToArray();
    }
}
