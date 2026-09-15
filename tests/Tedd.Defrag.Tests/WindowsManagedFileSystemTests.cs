using System.Buffers.Binary;
using Tedd.Defrag.Core;
using Tedd.Defrag.Engine;
using Tedd.Defrag.Maintenance;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Windows;
using Xunit;

namespace Tedd.Defrag.Tests;

public abstract class WindowsManagedFileSystemTests
{
    protected abstract string FileSystemName { get; }
    private VolumeInfo Info => new("filesystem-fixture", @"V:\", "fixture", FileSystemName, 64 * 4096, 40 * 4096, 4096, true, true, [], "fixture");
    private static JobRequest Request(Operation operation, bool preview = false) => new()
    { Volume = @"V:\", Operation = operation, Preview = preview, Resources = ResourcePolicy.Performance };

    [Fact]
    public void AnalysisPublishesPartialCoverageWithoutEligibleMovesOrNtfsMetadata()
    {
        var volume = new TestVolume();
        var result = Run(volume, Request(Operation.Analyze));
        Assert.Equal(JobState.Partial, result.State);
        Assert.Equal(1, volume.Scans);
        Assert.Equal(1, result.FragmentedFiles);
        Assert.Equal(0, result.EligibleStreamsAtOrAboveThreshold);
        Assert.Equal(0, result.MftExtents);
        Assert.Equal(0, result.PlannedMoves);
        Assert.Equal(64, result.TotalClusters);
        Assert.Contains("partial", result.Message);
    }

    [Theory]
    [InlineData(Operation.MinimumWrite)]
    [InlineData(Operation.FilesOnly)]
    [InlineData(Operation.Pack)]
    [InlineData(Operation.OptimizeMft)]
    [InlineData(Operation.DirectoryIndexes)]
    [InlineData(Operation.ZeroFreeSpace)]
    public void CustomOperationsFailBeforeScanningOrMoving(Operation operation)
    {
        var volume = new TestVolume();
        var result = Run(volume, Request(operation, true));
        Assert.Equal(JobState.Failed, result.State);
        Assert.Contains("require NTFS", result.Message);
        Assert.Equal(0, volume.Scans);
    }

    [Theory]
    [InlineData(Operation.ReTrim, "/L")]
    [InlineData(Operation.Automatic, "/O")]
    [InlineData(Operation.SlabConsolidate, "/K")]
    [InlineData(Operation.WindowsDefrag, "/D")]
    public void MaintenancePreviewNeverExecutesAndDisclosesCommand(Operation operation, string flag)
    {
        bool executed = false;
        var result = Run(new(), Request(operation, true), () => executed = true);
        if (!FileSystemCapabilities.Supports(FileSystemName, operation))
        {
            Assert.Equal(JobState.Failed, result.State); Assert.False(executed); return;
        }
        Assert.Equal(JobState.Completed, result.State);
        Assert.False(executed);
        Assert.Contains($"V: {flag} /U /V", result.Message);
        Assert.Contains("availability is determined by Windows", result.Message);
    }

    [Theory]
    [InlineData(Operation.ReTrim)]
    [InlineData(Operation.Automatic)]
    [InlineData(Operation.SlabConsolidate)]
    [InlineData(Operation.WindowsDefrag)]
    public void MaintenanceExecutesAndRescans(Operation operation)
    {
        var volume = new TestVolume();
        int executed = 0;
        var result = Run(volume, Request(operation), () => executed++);
        if (!FileSystemCapabilities.Supports(FileSystemName, operation))
        {
            Assert.Equal(JobState.Failed, result.State); Assert.Equal(0, executed); Assert.Equal(0, volume.Scans); return;
        }
        Assert.Equal(JobState.Partial, result.State); // Windows succeeded, file coverage is partial.
        Assert.Equal(1, executed);
        Assert.Equal(2, volume.Scans);
        Assert.Contains("Windows maintenance completed", result.Message);
        Assert.Equal(0, result.VerifiedMoves); // Never fabricate OS relocation counts.
    }

    [Fact]
    public void CompressionTargetsDoNotBlockSupportedNonNtfsMaintenance()
    {
        var volume = new TestVolume();
        var request = Request(Operation.Automatic, true) with
        {
            CompressionTargets = [new(new PathRule(@"V:\Data"), CompressionMode.Xpress4K)]
        };

        var result = Run(volume, request);

        Assert.Equal(JobState.Completed, result.State);
        Assert.Equal(1, volume.Scans);
        Assert.Equal("Skipped · compression requires NTFS", result.CompressionStatus);
        Assert.Contains(result.Warnings!, warning => warning.Contains($"uses {FileSystemName}") && warning.Contains("remains available"));
    }

    [Fact]
    public void TrimDoesNotDependOnAllocationScanAvailability()
    {
        var volume = new TestVolume { ScanError = new IOException("Bitmap unavailable") };
        bool executed = false;
        var result = Run(volume, Request(Operation.ReTrim), () => executed = true);
        Assert.True(executed);
        Assert.Equal(JobState.Partial, result.State);
        Assert.Equal(2, volume.Scans);
        Assert.Null(result.Map);
        Assert.Equal(Info.SizeBytes, result.TotalBytes);
        Assert.Contains(result.Warnings!, warning => warning.Contains("Bitmap unavailable"));
    }

    [Fact]
    public void AnalysisDoesNotSuppressScanFailure()
    {
        var result = Run(new() { ScanError = new IOException("Bitmap unavailable") }, Request(Operation.Analyze));
        Assert.Equal(JobState.Failed, result.State);
        Assert.Contains("Bitmap unavailable", result.Message);
    }

    [Fact]
    public void CancellationDuringScanDoesNotStartMaintenance()
    {
        bool executed = false;
        var result = Run(new() { ScanError = new OperationCanceledException("cancelled") }, Request(Operation.ReTrim), () => executed = true);
        Assert.False(executed);
        Assert.Equal(JobState.Cancelled, result.State);
    }

    [Fact]
    public void WindowsRejectionIsReportedAsFailureWithoutRescanning()
    {
        var volume = new TestVolume();
        var result = Run(volume, Request(Operation.WindowsDefrag), () => throw new IOException("ReFS defrag unavailable: 0x89000020"));
        Assert.Equal(JobState.Failed, result.State);
        Assert.Contains("0x89000020", result.Message);
        Assert.Equal(1, volume.Scans);
    }

    [Fact]
    public void WindowsDefragOnSsdRequiresExecutionOptIn()
    {
        var result = Run(new() { VolumeInfo = Info with { SeekPenalty = false } }, Request(Operation.WindowsDefrag));
        Assert.Equal(JobState.Failed, result.State);
        Assert.Contains("explicit opt-in", result.Message);
        result = Run(new() { VolumeInfo = Info with { SeekPenalty = false } }, Request(Operation.WindowsDefrag) with { AllowSsdRelocation = true });
        Assert.Equal(JobState.Partial, result.State);
    }

    [Fact]
    public void MaintenanceConstraintsAreRejectedInPreviewToo()
    {
        JobRequest[] requests =
        [
            Request(Operation.ReTrim, true) with { SelectedPaths = [new(@"V:\folder")] },
            Request(Operation.WindowsDefrag, true) with { Exclusions = [new("*.bin", PathRuleKind.Wildcard)] },
            Request(Operation.WindowsDefrag, true) with { MaxMoveBytes = 4096 },
            Request(Operation.WindowsDefrag, true) with { MinimumFragments = 2 },
            Request(Operation.WindowsDefrag, true) with { MinimumFileBytes = 4096 },
            Request(Operation.WindowsDefrag, true) with { MaximumFileBytes = 4096 }
        ];
        foreach (var request in requests)
        {
            var volume = new TestVolume();
            Assert.Equal(JobState.Failed, Run(volume, request).State);
            Assert.Equal(0, volume.Scans);
        }
        Assert.Throws<NotSupportedException>(() => WindowsMaintenance.Validate(Request(Operation.ReTrim, true), Info with { TrimEnabled = false }));
        Assert.Equal("/L", WindowsMaintenance.Validate(Request(Operation.ReTrim, true), Info with { TrimEnabled = null }));
        Assert.Throws<InvalidOperationException>(() => WindowsMaintenance.Run(Request(Operation.ReTrim, true), Info, _ => { }, () => { }, default));
    }

    [Fact]
    public void RecommendationsNeverRequestNtfsRelocation()
    {
        Assert.Equal([Operation.WindowsDefrag, Operation.ReTrim], MaintenanceRecommendation.SelectSteps(Info, 4, 8, 5, 5));
        Assert.Equal([Operation.ReTrim], MaintenanceRecommendation.SelectSteps(Info with { SeekPenalty = false }, 4, 8, 5, 5));
        Assert.Equal([Operation.Automatic], MaintenanceRecommendation.SelectSteps(Info with { SeekPenalty = null }, 4, 8, 5, 5));
    }

    [Fact]
    public void ExtentNormalizationMergesAdjacentRunsAndPreservesHoles()
    {
        Assert.Equal([new Extent(0, 10, 5), new(5, -1, 3), new(8, 15, 1)],
            FileSystemQueries.NormalizeExtents([new(0, 10, 2), new(2, 12, 3), new(5, -1, 3), new(8, 15, 1)], 20));
        Assert.Throws<IOException>(() => FileSystemQueries.NormalizeExtents([new(0, 19, 2)], 20));
        Assert.Throws<IOException>(() => FileSystemQueries.NormalizeExtents([new(1, 1, 2)], 20));
        Assert.Throws<IOException>(() => FileSystemQueries.NormalizeExtents([new(0, -2, 2)], 20));
    }

    [Fact]
    public void BitmapPagesHandleRoundedStartsAndFinalPartialBytes()
    {
        byte[] bitmap = new byte[3];
        Assert.Equal(16, VolumeBitmap.CopyPage(Page(0, 19, [0xAA, 0x55]), 0, 19, bitmap));
        Assert.Equal(19, VolumeBitmap.CopyPage(Page(8, 11, [0x55, 0x07]), 16, 19, bitmap));
        Assert.Equal(new byte[] { 0xAA, 0x55, 0x07 }, bitmap);
        Assert.Throws<IOException>(() => VolumeBitmap.CopyPage(Page(16, 3, [7]), 8, 19, bitmap));
        Assert.Throws<IOException>(() => VolumeBitmap.CopyPage(Page(0, 8, [0]), 8, 19, bitmap));
        Assert.Throws<IOException>(() => VolumeBitmap.CopyPage(Page(1, 18, [0, 0, 0]), 1, 19, bitmap));
    }

    private static byte[] Page(long start, long available, byte[] bits)
    {
        byte[] page = new byte[16 + bits.Length];
        BinaryPrimitives.WriteInt64LittleEndian(page, start);
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(8), available);
        bits.CopyTo(page, 16); return page;
    }

    private JobSnapshot Run(TestVolume volume, JobRequest request, Action? maintenance = null)
    {
        volume.VolumeInfo ??= Info;
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.FileSystemTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JobStore(root); store.SaveRequest(request);
            bool ran = false;
            new JobExecutor(store, _ => volume, (_, _, report, checkpoint, _) =>
            {
                checkpoint(); maintenance?.Invoke(); report("Windows fixture result"); ran = true;
            }).Run(request, default);
            if (ran) Assert.Contains("Windows fixture result", File.ReadAllText(Path.Combine(store.JobDirectory(request.Id), "maintenance.log")));
            return Assert.IsType<JobSnapshot>(store.ReadReport(request.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class TestVolume : IJobVolume
    {
        public VolumeInfo VolumeInfo { get; set; } = null!;
        public VolumeInfo Info => VolumeInfo;
        public int Scans { get; private set; }
        public Exception? ScanError { get; init; }
        public bool IsDirty() => throw new InvalidOperationException("Windows validates filesystem health");
        public VolumeLayout Scan(JobRequest request, Action<double, long, string> progress, Action checkpoint, CancellationToken token, Action<WorkProgress>? diagnostics = null)
        {
            Scans++; checkpoint();
            if (ScanError != null) throw ScanError;
            FileLayout file = new(0, @"V:\file.bin", "", StreamFlags.AnalysisOnly, 8192, 0, 0, [new(0, 8, 1), new(1, 10, 1)]);
            byte[] bitmap = new byte[8]; bitmap[1] = 5;
            return new(Info, 64, bitmap, [file], DateTimeOffset.UtcNow, 1, 0, false, [$"Partial {Info.FileSystem} file coverage"]);
        }
        public byte[] ReadBitmap(JobRequest request, Action checkpoint, CancellationToken token) => throw new InvalidOperationException("No custom moves through Windows maintenance");
        public void ExecuteMove(FileLayout file, PlannedMove move, PathRules rules) => throw new InvalidOperationException("No custom moves through Windows maintenance");
        public void Dispose() { }
    }
}
