using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class JobStorageTests
{
    [Fact]
    public async Task LivePipeReturnsDetailsWhileDiskContainsOnlyCompactSummaries()
    {
        string root = TemporaryRoot();
        using var stop = new CancellationTokenSource();
        Task? server = null;
        try
        {
            var store = new JobStore(root);
            var snapshot = Snapshot() with
            {
                Map = Enumerable.Repeat(new MapCell(1, 1, 0, 0, 0, 0, 0), 65536).ToArray(),
                Files = [new("V:\\large.bin", "", 20, 123456789, "Eligible")],
                CompressedFiles = [new("V:\\compressed.bin", "LZX", 1000, 500)]
            };
            store.Save(snapshot);
            server = JobMemoryServer.Listen(store, snapshot.Id, stop.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var live = (await JobMemoryServer.Query(new("get", Id: snapshot.Id), timeout.Token)).Snapshot!;
            Assert.Equal(65536, live.Map!.Length);
            Assert.Single(live.Files!); Assert.Single(live.CompressedFiles!);
            var persisted = new JobStore(root).ReadSnapshot(snapshot.Id)!;
            Assert.Null(persisted.Map); Assert.Null(persisted.Files); Assert.Null(persisted.CompressedFiles);
            Assert.True(new FileInfo(store.ReportPath(snapshot.Id)).Length < 8192);
            Assert.True(new FileInfo(Path.Combine(store.JobDirectory(snapshot.Id), "snapshot.json")).Length < 8192);
        }
        finally
        {
            stop.Cancel(); if (server != null) await server;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PruningMigratesLegacyPayloadsBoundsHistoryAndPreservesActiveJobsAndUnknownFiles()
    {
        string root = TemporaryRoot();
        try
        {
            var store = new JobStore(root);
            var active = Snapshot() with { State = JobState.Running };
            store.Save(active);
            string activeLayout = Path.Combine(store.JobDirectory(active.Id), "layout.bin");
            File.WriteAllBytes(activeLayout, new byte[100]);
            var stale = Snapshot() with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(-8) }; store.Save(stale);
            string unrelated = Path.Combine(store.JobDirectory(stale.Id), "user-note.txt"); File.WriteAllText(unrelated, "keep");
            for (int i = 0; i < 25; i++)
            {
                var snapshot = Snapshot() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i) };
                store.Save(snapshot);
                JobStore.AtomicWrite(Path.Combine(store.JobDirectory(snapshot.Id), "snapshot.json"), snapshot with
                { Map = [new(1, 1, 0, 0, 0, 0, 0)], Files = [new("legacy", "", 2, 100, "Eligible")] });
                File.WriteAllBytes(Path.Combine(store.JobDirectory(snapshot.Id), "layout.bin"), new byte[100]);
                File.WriteAllText(Path.Combine(store.JobDirectory(snapshot.Id), "moves.jsonl"), "legacy");
                File.WriteAllText(Path.Combine(store.JobDirectory(snapshot.Id), "snapshot.json." + Guid.NewGuid().ToString("N") + ".tmp"), "orphan");
            }
            JobHistoryRetention.Prune(store, new HashSet<Guid>(), DateTimeOffset.UtcNow);
            Assert.Equal(20, store.List().Count(s => s.IsTerminal));
            Assert.True(File.Exists(activeLayout)); Assert.True(File.Exists(unrelated));
            Assert.False(File.Exists(store.ReportPath(stale.Id)));
            Assert.Single(Directory.EnumerateFiles(root, "layout.bin", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(root, "moves.jsonl", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
            Assert.All(store.List().Where(s => s.IsTerminal), s => { Assert.Null(s.Map); Assert.Null(s.Files); });
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MaintenanceOutputHasAFixedDiskBudget()
    {
        string root = TemporaryRoot();
        try
        {
            var store = new JobStore(root); var snapshot = Snapshot(); store.Save(snapshot);
            for (int i = 0; i < 100; i++) store.AppendMaintenanceLog(snapshot.Id, new string('x', 5000));
            string path = Path.Combine(store.JobDirectory(snapshot.Id), "maintenance.log");
            Assert.InRange(new FileInfo(path).Length, 1, 64 * 1024);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void HistoryByteBudgetAndWorkerProtectionAreEnforced()
    {
        string root = TemporaryRoot();
        try
        {
            var store = new JobStore(root);
            var oversized = Snapshot(); store.Save(oversized);
            File.WriteAllBytes(Path.Combine(store.JobDirectory(oversized.Id), "request.json"), new byte[9 * 1024 * 1024]);
            var protectedJob = Snapshot() with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(-10) }; store.Save(protectedJob);
            JobHistoryRetention.Prune(store, new HashSet<Guid> { protectedJob.Id }, DateTimeOffset.UtcNow);
            Assert.False(Directory.Exists(store.JobDirectory(oversized.Id)));
            Assert.True(File.Exists(store.ReportPath(protectedJob.Id)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static JobSnapshot Snapshot() => new(Guid.NewGuid(), "V:\\", Operation.Analyze, JobState.Completed,
        "Complete", 1, 0, 10, 2, 10, DateTimeOffset.UtcNow);
    private static string TemporaryRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
}
