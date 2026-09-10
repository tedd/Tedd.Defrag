using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;

[assembly: InternalsVisibleTo("Tedd.Defrag.Tests")]

namespace Tedd.Defrag.Client;

public sealed class DefragClient
{
    private readonly Func<BrokerCommand, CancellationToken, Task<BrokerReply>> _connect;
    private readonly Func<Action> _prepareStart;
    private readonly Func<int, CancellationToken, Task> _delay;

    public DefragClient() : this(Connect, PrepareBrokerStart, Task.Delay) { }
    internal DefragClient(Func<BrokerCommand, CancellationToken, Task<BrokerReply>> connect,
        Func<Action> prepareStart, Func<int, CancellationToken, Task> delay)
    {
        _connect = connect; _prepareStart = prepareStart; _delay = delay;
    }

    public async Task<BrokerReply> Send(BrokerCommand command, bool startBroker = false, CancellationToken token = default)
    {
        bool submitsWork = command.Action == "submit";
        if (startBroker || submitsWork)
        {
            await EnsureMatchingBroker(startBroker, token);
            // The broker also checks this stamp, covering replacement between the
            // handshake and submission. Never retry a submitted command implicitly.
            return await _connect(submitsWork ? command with { ClientBuild = BrokerProtocol.BuildVersion } : command, token);
        }
        try { return await _connect(command, token); }
        catch (TimeoutException) when (!startBroker && command.Action is "list" or "get" or "explore" || !startBroker && command.Action == "settings" && command.Settings == null)
        {
            var store = new JobStore();
            return command.Action switch
            {
                "list" => new(true, Jobs: store.List().Select(s => s with { Map = null, Files = null }).ToArray()),
                "get" => new(true, Snapshot: store.ReadSnapshot(command.Id)),
                "explore" => new(true, Region: new LayoutExplorerStore(store).Explore(command.Id, command.StartCluster,
                    command.ClusterCount, command.MapCells, command.IncludeFiles, command.Path, command.FileId, command.Stream)),
                _ => new(true, Settings: store.Settings)
            };
        }
    }

    public async Task ShutdownWorker(CancellationToken token = default)
    {
        try { await _connect(new("shutdown"), token); }
        catch (TimeoutException) { return; }
        for (int i = 0; i < 100; i++)
        {
            await _delay(100, token);
            try { await _connect(new("ping"), token); }
            catch (TimeoutException) { return; }
        }
        throw new TimeoutException("Worker did not stop after the application requested shutdown.");
    }

    private static bool Matches(BrokerReply reply) => reply.Worker is { Stopping: false } worker && worker.Build == BrokerProtocol.BuildVersion;

    private async Task EnsureMatchingBroker(bool mayStart, CancellationToken token)
    {
        BrokerReply reply;
        try { reply = await _connect(new("ping"), token); }
        catch (TimeoutException) when (mayStart)
        {
            await StartAndWait(_prepareStart(), token);
            return;
        }
        if (Matches(reply)) return;
        string oldBuild = reply.Worker?.Build ?? "legacy (version unavailable)";
        if (!mayStart) throw new InvalidOperationException($"Worker build {oldBuild} does not match client build {BrokerProtocol.BuildVersion}. Restart the worker before submitting work.");

        // Resolve and validate the replacement before retiring a working broker.
        Action launch = _prepareStart();
        if (reply.Worker?.Stopping != true)
        {
            try { await _connect(new("stop"), token); }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"Worker build {oldBuild} must be replaced with {BrokerProtocol.BuildVersion}. {e.Message}", e);
            }
        }
        for (int i = 0; i < 60; i++)
        {
            await _delay(250, token);
            try
            {
                reply = await _connect(new("ping"), token);
                if (Matches(reply)) return; // Another client already replaced it.
            }
            catch (TimeoutException)
            {
                await StartAndWait(launch, token);
                return;
            }
        }
        throw new TimeoutException("The old worker did not stop; no job was submitted.");
    }

    private async Task StartAndWait(Action launch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        launch();
        for (int i = 0; i < 60; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var reply = await _connect(new("ping"), token);
                if (Matches(reply)) return;
                throw new InvalidOperationException($"The started worker does not match client build {BrokerProtocol.BuildVersion}. No job was submitted.");
            }
            catch (TimeoutException) { await _delay(250, token); }
        }
        throw new TimeoutException("Worker did not become available; no job was submitted.");
    }

    private static Action PrepareBrokerStart()
    {
        var start = WorkerStart("--broker");
        start.UseShellExecute = true; start.WindowStyle = ProcessWindowStyle.Hidden;
        start.Verb = "runas";
        return () => { using var process = Process.Start(start) ?? throw new IOException("Worker could not be started."); };
    }
    private static async Task<BrokerReply> Connect(BrokerCommand command, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", BrokerProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(500, token);
        await BrokerProtocol.Write(pipe, command, JobStore.Json, token);
        var reply = await BrokerProtocol.Read<BrokerReply>(pipe, JobStore.Json, token);
        if (!reply.Success) throw new InvalidOperationException(reply.Error);
        return reply;
    }
    public static ProcessStartInfo WorkerStart(params string[] args)
    {
        string? configured = Environment.GetEnvironmentVariable("TEDD_DEFRAG_WORKER");
        string? worker = LocateWorker(AppContext.BaseDirectory, configured,
            path => FileVersionInfo.GetVersionInfo(path).ProductVersion, File.GetLastWriteTimeUtc);
        if (worker == null || !File.Exists(worker)) throw new FileNotFoundException($"Build Tedd.Defrag.Worker for client build {BrokerProtocol.BuildVersion}, or place the matching published worker beside the application.", worker);
        string? workerBuild = FileVersionInfo.GetVersionInfo(worker).ProductVersion;
        if (workerBuild != BrokerProtocol.BuildVersion)
            throw new InvalidOperationException($"Worker '{worker}' is build {workerBuild ?? "unknown"}; the client is {BrokerProtocol.BuildVersion}. Use executables from the same build.");
        var info = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(worker)! };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    internal static string? LocateWorker(string baseDirectory, string? configured,
        Func<string, string?> readBuild, Func<string, DateTime> lastWrite)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        // In a checkout the worker must use its own complete build output. A
        // project reference can leave an executable and portable deps.json beside
        // MAUI's flattened runtime assets; that copy is not a runnable worker.
        if (!File.Exists(Path.Combine(baseDirectory, "release-manifest.json")))
        {
            string? configuration = baseDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .LastOrDefault(p => p.Equals("Debug", StringComparison.OrdinalIgnoreCase) || p.Equals("Release", StringComparison.OrdinalIgnoreCase));
            for (var directory = new DirectoryInfo(baseDirectory); directory != null; directory = directory.Parent)
            {
                string project = Path.Combine(directory.FullName, "src", "Tedd.Defrag.Worker", "bin");
                if (!Directory.Exists(project)) continue;
                return SelectDevelopmentWorker(Directory.EnumerateFiles(project, "Tedd.Defrag.Worker.exe", SearchOption.AllDirectories),
                    configuration, readBuild, lastWrite);
            }
        }
        return new[] { Path.Combine(baseDirectory, "Tedd.Defrag.Worker.exe"), Path.Combine(baseDirectory, "worker", "Tedd.Defrag.Worker.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "worker", "Tedd.Defrag.Worker.exe")) }.FirstOrDefault(File.Exists);
    }

    internal static string? SelectDevelopmentWorker(IEnumerable<string> candidates, string? configuration,
        Func<string, string?> readBuild, Func<string, DateTime> lastWrite)
        => candidates.Where(path => readBuild(path) == BrokerProtocol.BuildVersion)
            .OrderByDescending(path => configuration != null && path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains(configuration, StringComparer.OrdinalIgnoreCase))
            .ThenByDescending(lastWrite).FirstOrDefault();
}
