using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Xunit;

namespace Tedd.Defrag.Tests;

public class PersistenceTests
{
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
}
