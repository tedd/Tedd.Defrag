using Tedd.Defrag.Core;

namespace Tedd.Defrag.Persistence;

// This is intentionally separate from JobRequest: it records desktop defaults,
// while every submitted job remains a complete immutable execution request.
public sealed record DesktopSettings
{
    public Operation SelectedOperation { get; init; } = Operation.MinimumWrite;
    public int ResourcePresetIndex { get; init; } = 2;
    public bool AdvancedControlsVisible { get; init; }
    public DesktopPathRule[] SelectedPaths { get; init; } = [];
    public DesktopPathRule[] Exclusions { get; init; } = [];
    public DesktopCompressionTarget[] CompressionTargets { get; init; } = [];
    public string[] CompressionExcludedExtensions { get; init; } = [.. CompressionFileTypes.DefaultExcludedExtensions];
    public int CpuPercent { get; init; } = 100;
    public string CompressionWorkers { get; init; } = "0";
    public string ScanWorkers { get; init; } = "0";
    public string PlanningWorkers { get; init; } = "0";
    public string MoveQueueDepth { get; init; } = "16";
    public bool VerifyMoves { get; init; } = true;
    public string MemoryMiB { get; init; } = "0";
    public string IoMiBPerSecond { get; init; } = "0";
    public bool IdleOnly { get; init; }
    public bool AcOnly { get; init; }
    public bool Background { get; init; }
    public string AffinityMask { get; init; } = "";
    public string MaxMoveMiB { get; init; } = "0";
    public string MaxMinutes { get; init; } = "0";
    public string ShrinkBoundaryMiB { get; init; } = "";
    public string MinimumFragments { get; init; } = "20";
    public string MinimumFileMiB { get; init; } = "";
    public string MaximumFileMiB { get; init; } = "";
}

public sealed record DesktopPathRule(PathRule Rule, bool Directory);
public sealed record DesktopCompressionTarget(CompressionTarget Target, bool Directory);
