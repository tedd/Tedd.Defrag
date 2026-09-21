using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Tedd.Defrag.Core;
using Windows.Win32;
using Windows.Win32.System.Power;

namespace Tedd.Defrag.Windows;

public readonly record struct CompressionProgress(string Status, string? Path, int FilesTotal, int FilesProcessed,
    int FilesWaiting, int FilesChanged, int FilesSkipped, int FilesFailed, long BytesTotal, long BytesProcessed,
    long BytesWaiting, long BytesSaved);
public sealed record CompressionResult(int FilesMatched, int FilesChanged, int FilesSkipped, int FilesFailed,
    long BytesSaved, string[] Warnings)
{
    public static CompressionResult Empty { get; } = new(0, 0, 0, 0, 0, []);
}
public sealed record CompressionInventoryResult(CompressedFileSummary[] Files, int TotalFiles, long TotalSize,
    long BytesSaved, string[] Warnings)
{
    public static CompressionInventoryResult Empty { get; } = new([], 0, 0, 0, []);
}

public static class NtfsCompression
{
    public static CompressionResult Run(JobRequest request, VolumeInfo volume, Action<CompressionProgress> progress,
        Action checkpoint, CancellationToken token)
        => Run(request, volume, progress, checkpoint, token, _ => new NativeCompressionPlatform(volume.Root));

    internal static CompressionResult Run(JobRequest request, VolumeInfo volume, Action<CompressionProgress> progress,
        Action checkpoint, CancellationToken token, Func<string, ICompressionPlatform> platformForPath)
    {
        if (request.CompressionTargets.Length == 0) return CompressionResult.Empty;
        if (!volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Compression targets require NTFS; {volume.Root} uses {volume.FileSystem}.");

        var warnings = new List<string>();
        string[] excludedExtensions = request.CompressionExcludedExtensions.Select(CompressionFileTypes.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        int matched = 0, changed = 0, skipped = 0, failed = 0;
        long bytesTotal = 0, bytesProcessed = 0, bytesSaved = 0;
        var active = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        object stateLock = new();
        using var awake = request.Preview ? null : new ExecutionStateScope();
        var candidates = new List<(string Path, CompressionMode Mode, long Size)>();
        Report("Finding compression candidates", null);
        foreach (var candidate in Enumerate(request.CompressionTargets, volume.Root, warnings, checkpoint, token))
        {
            if (IsFileTypeExcluded(candidate.Path, excludedExtensions)) continue;
            long size = 0;
            try { size = Math.Max(0, new FileInfo(candidate.Path).Length); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                if (warnings.Count < 50) warnings.Add($"{candidate.Path}: logical size is unavailable: {error.Message}");
            }
            candidates.Add((candidate.Path, candidate.Mode, size));
            bytesTotal = checked(bytesTotal + size);
            Report("Finding compression candidates", candidate.Path);
        }
        matched = candidates.Count;
        Report(request.Preview ? "Compression preview ready" : "Compression queue ready", null);
        int nextCandidate = -1;
        int workerCount = Math.Min(candidates.Count, WorkerPolicy.CompressionWorkers(request.Resources));
        Task[] workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(ProcessCandidates)).ToArray();
        Task.WhenAll(workers).GetAwaiter().GetResult();
        Report(candidates.Count == 0 ? "No matching compression targets" :
            request.Preview ? "Compression preview complete" : "Compression complete", null);
        return new(matched, changed, skipped, failed, bytesSaved, warnings.ToArray());

        void ProcessCandidates()
        {
            while (true)
            {
                int index = Interlocked.Increment(ref nextCandidate);
                if (index >= candidates.Count) return;
                var candidate = candidates[index];
                lock (stateLock)
                {
                    token.ThrowIfCancellationRequested();
                    checkpoint();
                    active.Add(candidate.Path, candidate.Size);
                }
                try
                {
                    if (request.Preview)
                    {
                        Report("Inspecting compression target", candidate.Path);
                        lock (stateLock) skipped++;
                    }
                    else
                    {
                        var outcome = CompressionFileProcessor.Apply(candidate.Path, candidate.Mode, platformForPath(candidate.Path),
                            status => Report(status, candidate.Path));
                        lock (stateLock)
                        {
                            if (outcome.Changed) { changed++; bytesSaved += outcome.BytesSaved; }
                            else skipped++;
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or COMException or NotSupportedException)
                {
                    lock (stateLock)
                    {
                        failed++;
                        if (warnings.Count < 50) warnings.Add($"{candidate.Path}: {error.Message}");
                    }
                }
                lock (stateLock)
                {
                    active.Remove(candidate.Path);
                    bytesProcessed = checked(bytesProcessed + candidate.Size);
                }
                Report(request.Preview ? "Compression target inspected" : "Compression target processed", null);
            }
        }

        void Report(string status, string? path)
        {
            lock (stateLock)
            {
                int filesProcessed = changed + skipped + failed;
                int waiting = Math.Max(0, candidates.Count - filesProcessed - active.Count);
                long activeBytes = active.Values.Sum();
                long waitingBytes = Math.Max(0, bytesTotal - bytesProcessed - activeBytes);
                progress(new(status, path, candidates.Count, filesProcessed, waiting, changed, skipped, failed,
                    bytesTotal, bytesProcessed, waitingBytes, bytesSaved));
            }
        }
    }

    internal static bool IsFileTypeExcluded(string path, IEnumerable<string> extensions) =>
        extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string Path, CompressionMode Mode)> Enumerate(CompressionTarget[] targets, string root,
        List<string> warnings, Action checkpoint, CancellationToken token)
    {
        var compiled = targets.Select(target => (target.Mode, Rules: new PathRules([target.Rule], []))).ToArray();
        var timedOut = new HashSet<int>();
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool broad = targets.Any(target => target.Rule.Kind != PathRuleKind.Path);
        if (broad)
        {
            foreach (string path in EnumerateFiles(root, warnings))
            {
                token.ThrowIfCancellationRequested(); checkpoint();
                if (ModeFor(path) is { } mode && yielded.Add(path)) yield return (path, mode);
            }
            yield break;
        }

        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested(); checkpoint();
            string path;
            try { path = Path.GetFullPath(target.Rule.Pattern); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                if (warnings.Count < 50) warnings.Add($"{target.Rule.Pattern}: {error.Message}");
                continue;
            }
            if (!IsWithinRoot(path, root))
            {
                if (warnings.Count < 50) warnings.Add($"{path}: compression target is outside {root}.");
                continue;
            }
            if (File.Exists(path))
            {
                if (ModeFor(path) is { } mode && yielded.Add(path)) yield return (path, mode);
                continue;
            }
            if (Directory.Exists(path))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (warnings.Count < 50) warnings.Add($"{path}: {error.Message}");
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (warnings.Count < 50) warnings.Add($"{path}: reparse-point directories are not traversed for compression.");
                    continue;
                }
                foreach (string file in EnumerateFiles(path, warnings))
                {
                    token.ThrowIfCancellationRequested(); checkpoint();
                    if (ModeFor(file) is { } mode && yielded.Add(file)) yield return (file, mode);
                }
                continue;
            }
            if (warnings.Count < 50) warnings.Add($"{path}: compression target was not found.");
        }

        CompressionMode? ModeFor(string path)
        {
            CompressionMode? result = null;
            for (int i = 0; i < compiled.Length; i++)
            {
                try { if (compiled[i].Rules.IsSelected(path)) result = compiled[i].Mode; }
                catch (RegexMatchTimeoutException)
                {
                    if (timedOut.Add(i) && warnings.Count < 50)
                        warnings.Add($"{targets[i].Rule.Pattern}: matching timed out; affected paths were omitted.");
                }
            }
            return result;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, List<string> warnings)
    {
        var pending = new Stack<IEnumerator<FileSystemInfo>>();
        if (!Open(new DirectoryInfo(root))) yield break;
        try
        {
            while (pending.Count > 0)
            {
                FileSystemInfo entry;
                try
                {
                    if (!pending.Peek().MoveNext()) { pending.Pop().Dispose(); continue; }
                    entry = pending.Peek().Current;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    pending.Pop().Dispose();
                    if (warnings.Count < 50) warnings.Add($"{root}: a compression directory could not be enumerated: {error.Message}");
                    continue;
                }
                FileAttributes attributes;
                try { attributes = entry.Attributes; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (warnings.Count < 50) warnings.Add($"{entry.FullName}: {error.Message}");
                    continue;
                }
                if (entry is DirectoryInfo directory)
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0) _ = Open(directory);
                    continue;
                }
                yield return entry.FullName;
            }
        }
        finally { while (pending.TryPop(out var enumerator)) enumerator.Dispose(); }

        bool Open(DirectoryInfo directory)
        {
            try
            {
                pending.Push(directory.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    AttributesToSkip = 0,
                    ReturnSpecialDirectories = false
                }).GetEnumerator());
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (warnings.Count < 50) warnings.Add($"{directory.FullName}: compression directory could not be opened: {error.Message}");
                return false;
            }
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}

public static class CompressionInventory
{
    internal const int MaximumFiles = 500;

    public static CompressionInventoryResult Read(VolumeLayout layout, Action checkpoint, CancellationToken token)
    {
        if (!layout.Volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || !layout.Files.Any(IsCandidate))
            return CompressionInventoryResult.Empty;
        var platform = new NativeCompressionPlatform(layout.Volume.Root);
        return Read(layout, platform.InspectState, platform.GetAllocatedSize, checkpoint, token);
    }

    internal static CompressionInventoryResult Read(VolumeLayout layout, Func<string, CompressionFileState> getState,
        Func<string, long> getAllocatedSize, Action checkpoint, CancellationToken token)
    {
        var files = new List<CompressedFileSummary>();
        var warnings = new List<string>();
        long totalSize = 0, totalSaved = 0;
        foreach (var file in layout.Files.Where(IsCandidate).DistinctBy(file => file.FileId))
        {
            token.ThrowIfCancellationRequested(); checkpoint();
            try
            {
                CompressionFileState state = getState(file.Path);
                if (!state.IsCompressed) continue;
                long allocated = getAllocatedSize(file.Path);
                long saved = Math.Max(0, file.Size - allocated);
                files.Add(new(file.Path, TypeName(state), file.Size, saved));
                totalSize = checked(totalSize + file.Size);
                totalSaved = checked(totalSaved + saved);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or COMException or NotSupportedException)
            {
                if (warnings.Count < 50) warnings.Add($"{file.Path}: compressed-size inspection failed: {error.Message}");
            }
        }
        return new(files.OrderByDescending(file => file.BytesSaved).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumFiles).ToArray(), files.Count, totalSize, totalSaved, warnings.ToArray());
    }

    private static bool IsCandidate(FileLayout file) => file.StreamName.Length == 0 &&
        (file.Flags & (StreamFlags.Compressed | StreamFlags.ReparsePoint)) != 0 &&
        (file.Flags & (StreamFlags.Directory | StreamFlags.Incomplete)) == 0 &&
        !file.Path.Contains("<unresolved>", StringComparison.Ordinal);

    private static string TypeName(CompressionFileState state)
    {
        var types = new List<string>(2);
        if (state.IsExternal)
        {
            types.Add(state.Provider switch
            {
                NativeCompressionPlatform.WofProviderFile => state.Algorithm switch
                {
                    0 => "XPRESS 4K",
                    1 => "LZX",
                    2 => "XPRESS 8K",
                    3 => "XPRESS 16K",
                    _ => $"WOF algorithm {state.Algorithm}"
                },
                NativeCompressionPlatform.WofProviderWim => "WIM",
                _ => $"WOF provider {state.Provider}"
            });
        }
        if (state.IsNtfsCompressed) types.Add("NTFS");
        return string.Join(" + ", types);
    }
}

internal readonly record struct CompressionFileState(bool IsExternal, uint Provider, uint Algorithm, bool IsNtfsCompressed)
{
    public bool IsCompressed => IsExternal || IsNtfsCompressed;
    public bool IsFileProvider => IsExternal && Provider == NativeCompressionPlatform.WofProviderFile;
}

internal readonly record struct CompressionFileOutcome(bool Changed, long BytesSaved);

internal interface ICompressionPlatform
{
    CompressionFileState GetState(string path);
    long GetAllocatedSize(string path);
    bool ApplyWof(string path, CompressionMode mode);
    void Decompress(string path, CompressionFileState state);
}

internal static class CompressionFileProcessor
{
    private static readonly CompressionMode[] Algorithms =
        [CompressionMode.Xpress4K, CompressionMode.Xpress8K, CompressionMode.Xpress16K, CompressionMode.Lzx];

    public static CompressionFileOutcome Apply(string path, CompressionMode target, ICompressionPlatform platform,
        Action<string>? progress = null)
    {
        var state = platform.GetState(path);
        long before = platform.GetAllocatedSize(path);
        if (target == CompressionMode.Smallest)
        {
            if (state.IsCompressed) return new(false, 0);
            return TryAll(path, before, platform, progress);
        }
        if (target == CompressionMode.None)
        {
            if (!state.IsCompressed) return new(false, 0);
            progress?.Invoke("Removing compression");
            platform.Decompress(path, state);
            return new(true, before - platform.GetAllocatedSize(path));
        }

        uint algorithm = NativeCompressionPlatform.Algorithm(target);
        if (state.IsFileProvider && state.Algorithm == algorithm && !state.IsNtfsCompressed) return new(false, 0);
        progress?.Invoke($"Compressing with {CompressionModes.DisplayName(target)}");
        if (state.IsCompressed) platform.Decompress(path, state);
        if (!platform.ApplyWof(path, target)) return new(false, 0);
        Verify(path, target, platform);
        return new(true, before - platform.GetAllocatedSize(path));
    }

    private static CompressionFileOutcome TryAll(string path, long before, ICompressionPlatform platform,
        Action<string>? progress)
    {
        CompressionMode best = CompressionMode.None;
        long bestSize = before;
        CompressionMode current = CompressionMode.None;
        try
        {
            foreach (var algorithm in Algorithms)
            {
                progress?.Invoke($"Finding best compression · testing {CompressionModes.DisplayName(algorithm)}");
                if (current != CompressionMode.None)
                {
                    platform.Decompress(path, platform.GetState(path));
                    current = CompressionMode.None;
                }
                if (!platform.ApplyWof(path, algorithm)) continue;
                current = algorithm;
                Verify(path, algorithm, platform);
                long size = platform.GetAllocatedSize(path);
                if (size < bestSize) { best = algorithm; bestSize = size; }
            }
            if (current != best)
            {
                if (current != CompressionMode.None) platform.Decompress(path, platform.GetState(path));
                if (best != CompressionMode.None)
                {
                    progress?.Invoke($"Applying smallest result · {CompressionModes.DisplayName(best)}");
                    if (!platform.ApplyWof(path, best)) throw new IOException($"{CompressionModes.DisplayName(best)} was no longer beneficial when reapplied.");
                    Verify(path, best, platform);
                }
            }
            return new(best != CompressionMode.None, before - platform.GetAllocatedSize(path));
        }
        catch
        {
            try
            {
                var state = platform.GetState(path);
                if (state.IsFileProvider) platform.Decompress(path, state);
            }
            catch { }
            throw;
        }
    }

    private static void Verify(string path, CompressionMode expected, ICompressionPlatform platform)
    {
        var state = platform.GetState(path);
        if (!state.IsFileProvider || state.Algorithm != NativeCompressionPlatform.Algorithm(expected))
            throw new IOException($"The requested {CompressionModes.DisplayName(expected)} state could not be verified.");
    }
}

internal sealed unsafe class NativeCompressionPlatform : ICompressionPlatform
{
    internal const uint WofProviderWim = 1;
    internal const uint WofProviderFile = 2;
    private const int CompressionNotBeneficialHResult = unchecked((int)0x80070158);
    private readonly long _clusterSize;

    public NativeCompressionPlatform(string root)
    {
        if (!PInvoke.GetDiskFreeSpace(root, out uint sectors, out uint bytesPerSector, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot query allocation geometry for {root}.");
        _clusterSize = checked((long)sectors * bytesPerSector);
        if (_clusterSize <= 0) throw new IOException($"Invalid allocation geometry for {root}.");
    }

    public CompressionFileState GetState(string path)
    {
        var (state, attributes) = QueryState(path);
        bool isExternal = state.IsExternal;
        uint provider = state.Provider;
        if ((attributes & (FileAttributes.Directory | FileAttributes.Encrypted | FileAttributes.SparseFile)) != 0)
            throw new NotSupportedException("Directories, encrypted files, and sparse files cannot use this compression target.");
        if ((attributes & FileAttributes.ReparsePoint) != 0 && (!isExternal || provider != WofProviderFile))
            throw new NotSupportedException("Non-WOF reparse points are not compressed.");
        return state;
    }

    internal CompressionFileState InspectState(string path) => QueryState(path).State;

    private static (CompressionFileState State, FileAttributes Attributes) QueryState(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        WofCompressionInfo info = default;
        uint length = (uint)sizeof(WofCompressionInfo);
        var result = PInvoke.WofIsExternalFile(path, out var external, out uint provider, &info, ref length);
        if (result.Failed && TryShortPath(path) is { } shortPath)
        {
            length = (uint)sizeof(WofCompressionInfo);
            result = PInvoke.WofIsExternalFile(shortPath, out external, out provider, &info, ref length);
        }
        result.ThrowOnFailure();
        return (new(external, provider, info.Algorithm, (attributes & FileAttributes.Compressed) != 0), attributes);
    }

    public long GetAllocatedSize(string path)
    {
        Marshal.SetLastPInvokeError(0);
        uint low = PInvoke.GetCompressedFileSize(path, out uint high);
        int error = Marshal.GetLastPInvokeError();
        if (low == uint.MaxValue && error != 0 && TryShortPath(path) is { } shortPath)
        {
            Marshal.SetLastPInvokeError(0);
            low = PInvoke.GetCompressedFileSize(shortPath, out high);
            error = Marshal.GetLastPInvokeError();
        }
        if (low == uint.MaxValue && error != 0) throw new Win32Exception(error, $"Cannot query allocated size for {path}.");
        long bytes = checked((long)(((ulong)high << 32) | low));
        return bytes == 0 ? 0 : checked((bytes + _clusterSize - 1) / _clusterSize * _clusterSize);
    }

    public bool ApplyWof(string path, CompressionMode mode)
    {
        using SafeFileHandle handle = OpenCompressionHandle(path, write: true);
        var info = new WofCompressionInfo { Algorithm = Algorithm(mode) };
        var result = PInvoke.WofSetFileDataLocation(handle, WofProviderFile, &info, (uint)sizeof(WofCompressionInfo));
        if ((int)result == CompressionNotBeneficialHResult) return false;
        result.ThrowOnFailure();
        return true;
    }

    public void Decompress(string path, CompressionFileState state)
    {
        if (state.IsExternal)
        {
            if (!state.IsFileProvider) throw new NotSupportedException("Externally backed files not owned by the WOF file provider are not modified.");
            using SafeFileHandle externalHandle = OpenCompressionHandle(path, write: false);
            _ = NativeIo.Control(externalHandle, PInvoke.FSCTL_DELETE_EXTERNAL_BACKING, [], [], out int error);
            if (error != 0) throw new Win32Exception(error, $"Cannot remove WOF compression from {path} (Win32 error {error}: {new Win32Exception(error).Message}).");
        }
        if (state.IsNtfsCompressed)
        {
            using SafeFileHandle ntfsHandle = OpenCompressionHandle(path, write: true);
            Span<byte> format = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(format, 0);
            _ = NativeIo.Control(ntfsHandle, PInvoke.FSCTL_SET_COMPRESSION, format, [], out int error);
            if (error != 0) throw new Win32Exception(error, $"Cannot remove NTFS compression from {path}.");
        }
    }

    internal static uint Algorithm(CompressionMode mode) => mode switch
    {
        CompressionMode.Xpress4K => 0,
        CompressionMode.Lzx => 1,
        CompressionMode.Xpress8K => 2,
        CompressionMode.Xpress16K => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static string? TryShortPath(string path)
    {
        char[] buffer = new char[32768];
        uint length = PInvoke.GetShortPathName(path, buffer);
        return length is > 0 and < 32768 ? new string(buffer, 0, (int)length) : null;
    }

    private static SafeFileHandle OpenCompressionHandle(string path, bool write)
    {
        SafeFileHandle handle = NativeIo.Open(path, write, openReparsePoint: false);
        try
        {
            string requested = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            string resolved = Path.GetFullPath(FileSystemQueries.FinalPath(handle)).TrimEnd(Path.DirectorySeparatorChar);
            if (!requested.Equals(resolved, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Compression target resolved to a different path: {resolved}.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WofCompressionInfo
    {
        public uint Algorithm;
        public uint Flags;
    }
}

internal sealed class ExecutionStateScope : IDisposable
{
    private readonly bool _active;
    public ExecutionStateScope()
    {
        _active = PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED) != 0;
    }
    public void Dispose()
    {
        if (_active) _ = PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
    }
}
