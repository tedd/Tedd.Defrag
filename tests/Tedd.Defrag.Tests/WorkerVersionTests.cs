using System.Text.Json;
using Tedd.Defrag.Client;
using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class WorkerVersionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("0.1.0+old-commit")]
    public async Task OldIdleBrokerIsReplacedBeforeSubmittingWork(string? oldBuild)
    {
        var broker = new BrokerFixture(oldBuild);
        var request = new JobRequest { Volume = "V:" };

        await broker.Client.Send(new("submit", Job: request), startBroker: true);

        Assert.Equal(["ping", "prepare", "stop", "ping", "start", "ping", "submit"], broker.Events);
        Assert.Equal(request.Id, Assert.Single(broker.Submissions).Job!.Id);
        Assert.Equal(BrokerProtocol.BuildVersion, broker.Submissions[0].ClientBuild);
    }

    [Fact]
    public async Task CurrentBrokerIsReusedWithoutInterruption()
    {
        var broker = new BrokerFixture(BrokerProtocol.BuildVersion);

        await broker.Client.Send(new("submit", Job: new() { Volume = "V:" }), startBroker: true);

        Assert.Equal(["ping", "submit"], broker.Events);
        Assert.Single(broker.Submissions);
    }

    [Fact]
    public async Task BusyOldBrokerDoesNotAcceptNewWorkOrGetReplaced()
    {
        var broker = new BrokerFixture("old") { Busy = true };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => broker.Client.Send(new("submit", Job: new() { Volume = "V:" }), startBroker: true));

        Assert.Contains("Finish active jobs", error.Message);
        Assert.Equal(["ping", "prepare", "stop"], broker.Events);
        Assert.Empty(broker.Submissions);
        Assert.True(broker.Running);
    }

    [Fact]
    public async Task MissingReplacementDoesNotStopExistingWorker()
    {
        var broker = new BrokerFixture("old") { ReplacementAvailable = false };

        await Assert.ThrowsAsync<FileNotFoundException>(() => broker.Client.Send(new("submit", Job: new() { Volume = "V:" }), startBroker: true));

        Assert.Equal(["ping", "prepare"], broker.Events);
        Assert.True(broker.Running);
        Assert.Empty(broker.Submissions);
    }

    [Fact]
    public async Task ReadOnlyStatusAndCancellationRemainAvailableOnOldWorkers()
    {
        var broker = new BrokerFixture(null);

        await broker.Client.Send(new("get", Id: Guid.NewGuid()));
        await broker.Client.Send(new("cancel", Id: Guid.NewGuid()));

        Assert.Equal(["get", "cancel"], broker.Events);
    }

    [Fact]
    public async Task SubmissionWithoutStartupPermissionRejectsStaleWorker()
    {
        var broker = new BrokerFixture(null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.Client.Send(new("submit", Job: new() { Volume = "V:" })));

        Assert.Equal(["ping"], broker.Events);
        Assert.Empty(broker.Submissions);
    }

    [Fact]
    public async Task WrongWorkerAfterLaunchCannotReceiveTheJob()
    {
        var broker = new BrokerFixture(null) { StartedBuild = "wrong" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.Client.Send(new("submit", Job: new() { Volume = "V:" }), startBroker: true));

        Assert.Empty(broker.Submissions);
    }

    [Fact]
    public async Task SubmissionFailureIsNotAutomaticallyRetried()
    {
        var broker = new BrokerFixture(BrokerProtocol.BuildVersion) { FailSubmission = true };

        await Assert.ThrowsAsync<TimeoutException>(() => broker.Client.Send(new("submit", Job: new() { Volume = "V:" }), startBroker: true));

        Assert.Single(broker.Submissions);
        Assert.Equal(["ping", "submit"], broker.Events);
    }

    [Fact]
    public async Task WorkerStartPerformsVersionReplacementWithoutSubmittingADiskJob()
    {
        var broker = new BrokerFixture(null);

        var reply = await broker.Client.Send(new("ping"), startBroker: true);

        Assert.Equal(BrokerProtocol.BuildVersion, reply.Worker!.Build);
        Assert.Empty(broker.Submissions);
        Assert.Contains("start", broker.Events);
    }

    [Fact]
    public async Task ApplicationShutdownStopsTheWorkerAndWaitsForItToExit()
    {
        var broker = new BrokerFixture(BrokerProtocol.BuildVersion);

        await broker.Client.ShutdownWorker();

        Assert.False(broker.Running);
        Assert.Equal(["shutdown", "ping"], broker.Events);
    }

    [Fact]
    public void DevelopmentLookupRejectsNewerTimestampFromAnOlderBuild()
    {
        string old = @"D:\repo\src\worker\bin\Debug\worker.exe", current = @"D:\repo\src\worker\bin\Release\worker.exe";

        var selected = DefragClient.SelectDevelopmentWorker([old, current], "Release",
            path => path == current ? BrokerProtocol.BuildVersion : "old", path => path == old ? DateTime.MaxValue : DateTime.MinValue);

        Assert.Equal(current, selected);
        Assert.Null(DefragClient.SelectDevelopmentWorker([old], "Release", _ => "old", _ => DateTime.MaxValue));
    }

    [Fact]
    public void DevelopmentLookupPrefersTheClientsConfigurationAmongMatchingBuilds()
    {
        string debug = @"D:\repo\src\worker\bin\Debug\worker.exe", release = @"D:\repo\src\worker\bin\Release\worker.exe";

        var selected = DefragClient.SelectDevelopmentWorker([debug, release], "Release",
            _ => BrokerProtocol.BuildVersion, path => path == debug ? DateTime.MaxValue : DateTime.MinValue);

        Assert.Equal(release, selected);
    }

    [Fact]
    public void LegacyPingReplyIsRecognizedAsMissingBuildInformation()
    {
        var reply = JsonSerializer.Deserialize<BrokerReply>("""{"Success":true}""", JobStore.Json);

        Assert.NotNull(reply);
        Assert.Null(reply.Worker);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void CheckoutUsesCompleteWorkerOutputWhilePackagesUseBundledWorker(bool packaged, bool matchingBuild)
    {
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string desktop = Path.Combine(root, "src", "Tedd.Defrag.Desktop", "bin", "Release", "win-x64");
            string worker = Path.Combine(root, "src", "Tedd.Defrag.Worker", "bin", "Release", "Tedd.Defrag.Worker.exe");
            Directory.CreateDirectory(desktop); Directory.CreateDirectory(Path.GetDirectoryName(worker)!);
            string adjacent = Path.Combine(desktop, "Tedd.Defrag.Worker.exe");
            File.WriteAllText(adjacent, "incomplete project-reference copy");
            File.WriteAllText(worker, "complete worker output");
            if (packaged) File.WriteAllText(Path.Combine(desktop, "release-manifest.json"), "{}");

            string? selected = DefragClient.LocateWorker(desktop, null,
                path => path == adjacent || matchingBuild ? BrokerProtocol.BuildVersion : "old", _ => DateTime.UtcNow);

            Assert.Equal(packaged ? adjacent : matchingBuild ? worker : null, selected);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class BrokerFixture
    {
        private string? _build;
        public bool Running { get; private set; } = true;
        public bool Busy { get; init; }
        public bool ReplacementAvailable { get; init; } = true;
        public bool FailSubmission { get; init; }
        public string StartedBuild { get; init; } = BrokerProtocol.BuildVersion;
        public List<string> Events { get; } = [];
        public List<BrokerCommand> Submissions { get; } = [];
        public DefragClient Client { get; }
        public BrokerFixture(string? build)
        {
            _build = build;
            Client = new(Connect, Prepare, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        }
        private Task<BrokerReply> Connect(BrokerCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Events.Add(command.Action);
            if (!Running) throw new TimeoutException();
            switch (command.Action)
            {
                case "ping": return Task.FromResult(new BrokerReply(true, Worker: _build == null ? null : new(_build, 123, "worker.exe")));
                case "stop":
                    if (Busy) throw new InvalidOperationException("Finish active jobs before stopping the worker.");
                    Running = false; break;
                case "shutdown": Running = false; break;
                case "submit":
                    Submissions.Add(command);
                    if (FailSubmission) throw new TimeoutException("Submission response unavailable");
                    break;
            }
            return Task.FromResult(new BrokerReply(true));
        }
        private Action Prepare()
        {
            Events.Add("prepare");
            if (!ReplacementAvailable) throw new FileNotFoundException("No matching worker");
            return () => { Events.Add("start"); Running = true; _build = StartedBuild; };
        }
    }
}
