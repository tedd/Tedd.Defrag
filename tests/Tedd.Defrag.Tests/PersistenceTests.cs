using System.Text.Json;
using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Xunit;

namespace Tedd.Defrag.Tests;

public class PersistenceTests
{
    [Fact]
    public void PathRulesPersistTheirKindsAndReadLegacyStringRules()
    {
        var modern = JsonSerializer.Deserialize<PathRule>("{\"Pattern\":\"*\\\\cache\\\\*\",\"Kind\":\"Wildcard\"}", JobStore.Json);
        var legacy = JsonSerializer.Deserialize<PathRule>("\"V:\\\\Data\"", JobStore.Json);

        Assert.Equal(new PathRule(@"*\cache\*", PathRuleKind.Wildcard), modern);
        Assert.Equal(new PathRule(@"V:\Data"), legacy);
        Assert.Contains("\"Kind\":\"Regex\"", JsonSerializer.Serialize(new PathRule("\\\\temp\\\\.*", PathRuleKind.Regex), JobStore.Json));
        Assert.Throws<ArgumentException>(() => new JobRequest { Volume = "V:", Exclusions = [new("[", PathRuleKind.Regex)] }.Validate());
    }

    [Theory]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    public void DispatchFailureOrCancellationAdvancesTheSnapshotObservedByPollingClients(JobState state)
    {
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JobStore(root);
            // Also covers wall-clock adjustments: a state transition must advance
            // the publication stamp even if the previous stamp is in the future.
            var queued = new JobSnapshot(Guid.NewGuid(), "V:", Operation.Pack, JobState.Queued,
                "Queued", 0, 0, 0, 0, 0, DateTimeOffset.UtcNow.AddMinutes(1));
            store.Save(queued);
            store.Save(queued.Transition(state, "Failed before execution"));

            var observed = Assert.IsType<JobSnapshot>(store.ReadSnapshot(queued.Id));
            Assert.Equal(state, observed.State);
            Assert.True(observed.UpdatedAt > queued.UpdatedAt);
            Assert.Equal(observed.UpdatedAt, store.ReadReport(queued.Id)!.UpdatedAt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentSnapshotReadersNeverObservePartialJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        var store = new JobStore(root); var request = new JobRequest { Volume = "V:" }; store.SaveRequest(request);
        var initial = new JobSnapshot(request.Id, "V:", Operation.Analyze, JobState.Running, "test", 0, 0, 0, 0, 0, DateTimeOffset.UtcNow);
        store.Save(initial);
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = store.ReadSnapshot(request.Id);
                Assert.NotNull(snapshot); Assert.Equal(request.Id, snapshot.Id);
            }
        })).ToArray();
        try { for (int i = 0; i < 250; i++) store.Save(initial with { FilesScanned = i, UpdatedAt = DateTimeOffset.UtcNow }); }
        finally { stop.Cancel(); await Task.WhenAll(readers); Directory.Delete(root, true); }
    }

    [Fact]
    public void TerminalSnapshotAutomaticallyCreatesCompactReport()
    {
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JobStore(root);
            var snapshot = new JobSnapshot(Guid.NewGuid(), "V:", Operation.MinimumWrite, JobState.Completed,
                "4 MiB relocated and verified in 2 moves.", 1, 4 * 1024 * 1024, 100, 3, 90, DateTimeOffset.UtcNow,
                Map: [new(1, 1, 0, 0, 0, 0, 0)], PlannedBytes: 4 * 1024 * 1024,
                PlannedMoves: 2, AttemptedMoves: 2, VerifiedMoves: 2, InitialFragmentedFiles: 5, ElapsedMilliseconds: 1250);

            store.Save(snapshot);

            var report = store.ReadReport(snapshot.Id);
            Assert.NotNull(report);
            Assert.Null(report.Map);
            Assert.Equal(2, report.VerifiedMoves);
            Assert.Equal(4 * 1024 * 1024, report.BytesMoved);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
