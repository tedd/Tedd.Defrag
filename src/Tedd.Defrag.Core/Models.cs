using System.Text.Json.Serialization;

namespace Tedd.Defrag.Core;

[JsonConverter(typeof(JsonStringEnumConverter<Operation>))]
public enum Operation { Analyze, MinimumWrite, FilesOnly, Pack, PackAndDefrag, Alphabetical, Size, Created, Modified, Extension, DirectoryLocality, PrepareShrink, ReTrim, SlabConsolidate, Automatic, OptimizeMft, DirectoryIndexes, ZeroFreeSpace, WindowsDefrag }
[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState { Queued, Scanning, Planning, Running, Paused, WaitingForIdle, Completed, Partial, Cancelled, Failed, Interrupted }
public enum PathRuleKind { Path, Wildcard, Glob, Regex }

[JsonConverter(typeof(JsonStringEnumConverter<CompressionMode>))]
public enum CompressionMode { None, Xpress4K, Xpress8K, Xpress16K, Lzx, Smallest }

[JsonConverter(typeof(PathRuleJsonConverter))]
public sealed record PathRule(string Pattern, PathRuleKind Kind = PathRuleKind.Path)
{
    public static implicit operator PathRule(string pattern) => new(pattern);
}
public sealed record CompressionTarget(PathRule Rule, CompressionMode Mode = CompressionMode.Xpress4K);

public static class CompressionModes
{
    public static IReadOnlyList<CompressionMode> Supported { get; } = Array.AsReadOnly(Enum.GetValues<CompressionMode>());
    public static string DisplayName(CompressionMode mode) => mode switch
    {
        CompressionMode.None => "NONE",
        CompressionMode.Xpress4K => "XPRESS 4K",
        CompressionMode.Xpress8K => "XPRESS 8K",
        CompressionMode.Xpress16K => "XPRESS 16K",
        CompressionMode.Lzx => "LZX",
        CompressionMode.Smallest => "Try all, pick smallest",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

[Flags]
public enum StreamFlags { None = 0, Directory = 1, Metadata = 2, Sparse = 4, Compressed = 8, Encrypted = 16, ReparsePoint = 32, Incomplete = 64, HardLinked = 128, Excluded = 256, Resident = 512, AnalysisOnly = 1024 }

public readonly record struct ClusterRange(long Start, long Length)
{
    public long End => checked(Start + Length);
    public bool Contains(long start, long length) => start >= Start && length > 0 && start <= End - length;
}
public readonly record struct Extent(long Vcn, long Lcn, long Length)
{
    public long End => checked(Lcn + Length);
    public bool IsSparse => Lcn < 0;
}
public sealed record FileLayout(ulong FileId, string Path, string StreamName, StreamFlags Flags,
    long Size, long CreatedUtcTicks, long ModifiedUtcTicks, Extent[] Extents)
{
    public bool Fragmented => Extents.Length > 1;
    public bool Movable => Extents.Length > 0 && (Flags & (StreamFlags.Sparse | StreamFlags.Compressed | StreamFlags.Encrypted |
        StreamFlags.ReparsePoint | StreamFlags.Incomplete | StreamFlags.HardLinked | StreamFlags.Excluded | StreamFlags.Resident | StreamFlags.AnalysisOnly)) == 0;
}
public sealed record VolumeInfo(string Id, string Root, string Label, string FileSystem, long SizeBytes, long FreeBytes,
    int BytesPerCluster, bool? SeekPenalty, bool? TrimEnabled, string[] Resources, string TopologyConfidence);

public sealed record VolumeLayout(VolumeInfo Volume, long TotalClusters, byte[] Bitmap, FileLayout[] Files,
    DateTimeOffset ObservedAt, long RecordsScanned, long RecordsSkipped, bool Complete, string[] Warnings,
    ClusterRange MftZone = default);

public sealed record ResourcePolicy
{
    public int MemoryMiB { get; init; }
    public int CpuPercent { get; init; } = 25;
    public int IoMiBPerSecond { get; init; }
    public ulong AffinityMask { get; init; }
    public bool Background { get; init; } = true;
    public bool IdleOnly { get; init; }
    public int IdleSeconds { get; init; } = 600;
    public bool AcOnly { get; init; } = true;
    public int MapCells { get; init; } = 8192;
    public int ScanWorkers { get; init; }
    public int PlanningWorkers { get; init; }
    public int MoveQueueDepth { get; init; } = 1;
    public static ResourcePolicy Quiet => new() { MemoryMiB = 512, CpuPercent = 10, IoMiBPerSecond = 8, IdleOnly = true, ScanWorkers = 1, PlanningWorkers = 1 };
    public static ResourcePolicy Balanced => new();
    public static ResourcePolicy Performance => new() { CpuPercent = 100, Background = false, AcOnly = false, MoveQueueDepth = 16 };
    public void Validate()
    {
        if ((MemoryMiB != 0 && MemoryMiB < 256) || CpuPercent is < 1 or > 100 || IoMiBPerSecond < 0 ||
            IdleSeconds is < 1 or > 86400 || MapCells is < 256 or > 65536 ||
            ScanWorkers is < 0 or > 32 || PlanningWorkers is < 0 or > 32 || MoveQueueDepth is < 1 or > 16)
            throw new ArgumentException("Resource policy is out of range (memory 0 or at least 256 MiB, CPU 1–100%, I/O 0 or greater, scan/planning workers 0–32, move queue 1–16).");
    }
}
public sealed record JobRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Volume { get; init; } = "";
    public Operation Operation { get; init; } = Operation.Analyze;
    public bool Preview { get; init; } = true;
    public PathRule[] SelectedPaths { get; init; } = [];
    public PathRule[] Exclusions { get; init; } = [];
    public CompressionTarget[] CompressionTargets { get; init; } = [];
    public ResourcePolicy Resources { get; init; } = ResourcePolicy.Performance;
    public long MaxMoveBytes { get; init; }
    public int MaxMinutes { get; init; }
    public int MinimumFragments { get; init; } = 20;
    public long MinimumFileBytes { get; init; }
    public long MaximumFileBytes { get; init; }
    public long ShrinkBoundaryBytes { get; init; }
    public bool AllowSsdRelocation { get; init; }
    public bool ConfirmVirtualDiskZeroing { get; init; }
    public long FreeSpaceReserveBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    // Preserve the old JSON flag only to reject saved simulation requests. Ignoring it
    // could reinterpret a previously harmless request as a real disk operation.
    [JsonPropertyName("Demo"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacySimulation { get; init; }
    public void Validate()
    {
        if (LegacySimulation) throw new NotSupportedException("Simulation jobs are no longer supported. Create a new job for a real volume.");
        Resources.Validate();
        if (string.IsNullOrWhiteSpace(Volume) || Id == Guid.Empty || !Enum.IsDefined(Operation) || MaxMoveBytes < 0 || MaxMinutes < 0 ||
            MinimumFragments < 2 || MinimumFileBytes < 0 || MaximumFileBytes < 0 || (MaximumFileBytes > 0 && MaximumFileBytes < MinimumFileBytes) ||
            SelectedPaths.Length > 10000 || Exclusions.Length > 10000 || CompressionTargets.Length > 10000 || FreeSpaceReserveBytes < 256L * 1024 * 1024)
            throw new ArgumentException("Invalid job parameters.");
        if (Operation == Operation.PrepareShrink && ShrinkBoundaryBytes <= 0) throw new ArgumentException("Supply a positive shrink boundary.");
        if (Operation == Operation.ZeroFreeSpace && !Preview && !ConfirmVirtualDiskZeroing)
            throw new ArgumentException("Virtual-disk pre-zeroing requires explicit acknowledgement.");
        foreach (var rule in SelectedPaths.Concat(Exclusions)) PathRules.Validate(rule);
        foreach (var target in CompressionTargets)
        {
            if (target is null || target.Rule is null || !Enum.IsDefined(target.Mode)) throw new ArgumentException("Invalid compression target.");
            PathRules.Validate(target.Rule);
        }
    }
}
public sealed record ConcurrencySettings
{
    public int MaxConcurrentVolumes { get; init; } = 2;
    public bool AllowParallelOnSharedStorage { get; init; }
    public int MaxConcurrentJobsPerSharedResource { get; init; } = 2;
    public Dictionary<string, string[]> ManualResourceGroups { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
public readonly record struct PlannedMove(ulong FileId, int FileIndex, long Vcn, long SourceLcn, long DestinationLcn, long Clusters);
public sealed record MovePlan(PlannedMove[] Moves, long ClustersToMove, int FilesConsidered, int FilesBlocked, string Explanation);
public sealed record JobSnapshot(Guid Id, string Volume, Operation Operation, JobState State, string Message,
    double Progress, long BytesMoved, long FilesScanned, int FragmentedFiles, int TotalFiles, DateTimeOffset UpdatedAt,
    DateTimeOffset? ObservedAt = null, MapCell[]? Map = null, FileSummary[]? Files = null,
    string[]? Warnings = null, long PlannedBytes = 0, long TotalBytes = 0, long FreeBytes = 0,
    int CpuPercent = 0, int MemoryMiB = 0, int IoMiBPerSecond = 0,
    int PlannedMoves = 0, int AttemptedMoves = 0, int VerifiedMoves = 0, int FailedMoves = 0,
    int FilesConsidered = 0, int FilesBlocked = 0, int InitialFragmentedFiles = 0, long ElapsedMilliseconds = 0,
    string? WorkerBuild = null, JobDiagnostics? Diagnostics = null, int FragmentationThreshold = 20,
    int StreamsAtOrAboveThreshold = 0, int EligibleStreamsAtOrAboveThreshold = 0,
    int MftExtents = 0, int FragmentedDirectoryIndexes = 0, int DirectoryIndexesAtOrAboveThreshold = 0,
    long TotalClusters = 0, int CompressionFilesMatched = 0, int CompressionFilesChanged = 0,
    int CompressionFilesSkipped = 0, int CompressionFilesFailed = 0, long CompressionBytesSaved = 0)
{
    public bool IsTerminal => State is JobState.Completed or JobState.Partial or JobState.Cancelled or JobState.Failed or JobState.Interrupted;
    public JobSnapshot Transition(JobState state, string message)
    {
        var now = DateTimeOffset.UtcNow;
        return this with { State = state, Message = message, UpdatedAt = now > UpdatedAt ? now : UpdatedAt.AddTicks(1) };
    }
}
public readonly record struct MapCell(long Clusters, long Allocated, long Fragmented, long Metadata, long Excluded, long Moving, long Verified);
public sealed record FileSummary(string Path, string Stream, int Extents, long Bytes, string Status);
public sealed record ClusterFile(ulong FileId, string Path, string Stream, long Bytes, long Clusters,
    long ClustersInRegion, int Extents, string Status);
public sealed record MapFileSelection(ulong FileId, string Path, string Stream, long Bytes, long Clusters,
    int Extents, string Status, ClusterRange[] Ranges);
public sealed record MapRegion(long StartCluster, long ClusterCount, long TotalClusters, MapCell[] Cells,
    ClusterFile[] Files, int FileCount, MapFileSelection? Selection = null);
public sealed record MoveJournalEntry(DateTimeOffset At, string Phase, PlannedMove Move, string? Detail = null);
public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double n = bytes; int i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.#} {units[i]}";
    }
    public static string StorageDelta(long bytes) => bytes >= 0 ? $"{Bytes(bytes)} saved" : $"{Bytes(-bytes)} added";
}
