using Tedd.Defrag.Core;
using Tedd.Defrag.Engine;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.TestFixtures;
using Tedd.Defrag.Windows;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class AnalysisProgressTests
{
    [Fact]
    public void InventoryProgressCountsFailuresUncompressedCandidatesAndDuplicateIds()
    {
        var layout = SyntheticVolume.Create();
        FileLayout Candidate(ulong id) => new(id, $@"V:\file-{id}", "", StreamFlags.Compressed, 1000, 0, 0, []);
        layout = layout with { Files = [Candidate(32), Candidate(32), Candidate(33), Candidate(34)] };
        var reports = new List<WorkProgress>();
        var inspected = new List<string>();

        var result = CompressionInventory.Read(layout, path =>
        {
            inspected.Add(path);
            if (path.EndsWith("33")) throw new IOException("Unavailable");
            return new(false, 0, 0, !path.EndsWith("34"));
        }, _ => 400, () => { }, default, reports.Add);

        Assert.Equal(3, inspected.Count);
        Assert.Equal(1, result.TotalFiles);
        Assert.Single(result.Warnings);
        Assert.Equal(0, reports[0].Completed);
        Assert.Equal(3, reports[0].Total);
        Assert.Equal("Inspecting compressed files", reports[0].Phase);
        Assert.Equal("Compression inventory complete", reports[^1].Phase);
        Assert.Equal(3, reports[^1].Completed);
        Assert.Equal(0, reports[^1].ActiveWorkers);
    }

    [Fact]
    public void EmptyInventoryReportsCompletionWithoutOpeningFiles()
    {
        var reports = new List<WorkProgress>();
        var result = CompressionInventory.Read(SyntheticVolume.Create() with { Files = [] }, () => { }, default, reports.Add);
        Assert.Equal(CompressionInventoryResult.Empty, result);
        Assert.Equal("Compression inventory complete", Assert.Single(reports).Phase);
        Assert.Equal(0, reports[0].ActiveWorkers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InventoryCheckpointsPublishElapsedTimeAndHonorCancellation(bool cancel)
    {
        var layout = SyntheticVolume.Create();
        var request = new JobRequest { Volume = layout.Volume.Root, Resources = new() { AcOnly = false, MapCells = 256 } };
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        bool inspected = false;
        try
        {
            var store = new JobStore(root);
            store.SaveRequest(request);
            new JobExecutor(store, _ => new ScannedVolume(layout), readCompressionInventory: (_, checkpoint, _) =>
            {
                var before = store.ReadSnapshot(request.Id)!;
                Assert.Equal("Inspecting compressed files", before.Diagnostics!.Scan!.Phase);
                Assert.Equal(0, before.Progress);
                Thread.Sleep(1100);
                checkpoint();
                var during = store.ReadSnapshot(request.Id)!;
                Assert.True(during.ElapsedMilliseconds >= before.ElapsedMilliseconds + 1000);
                Assert.True(during.UpdatedAt > before.UpdatedAt);
                inspected = true;
                if (cancel) { cancellation.Cancel(); checkpoint(); }
                return CompressionInventoryResult.Empty;
            }).Run(request, cancellation.Token);

            var final = store.ReadSnapshot(request.Id)!;
            Assert.True(inspected, final.Message);
            Assert.Equal(cancel ? JobState.Cancelled : JobState.Completed, final.State);
            Assert.Equal(0, final.Diagnostics!.Scan!.ActiveWorkers);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class ScannedVolume(VolumeLayout layout) : IJobVolume
    {
        public VolumeInfo Info => layout.Volume;
        public bool IsDirty() => false;
        public VolumeLayout Scan(JobRequest request, Action<double, long, string> progress, Action checkpoint,
            CancellationToken token, Action<WorkProgress>? diagnostics = null, Action<ScanMapUpdate>? mapProgress = null)
        {
            diagnostics?.Invoke(new("Analysis complete", layout.RecordsScanned, layout.RecordsScanned, "records"));
            progress(1, layout.RecordsScanned, "Analysis complete");
            return layout;
        }
        public byte[] ReadBitmap(JobRequest request, Action checkpoint, CancellationToken token) => throw new NotSupportedException();
        public void ExecuteMove(FileLayout file, PlannedMove move, PathRules rules, bool verifyResult) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
