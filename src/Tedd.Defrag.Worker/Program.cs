using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using Tedd.Defrag.Core;
using Tedd.Defrag.Engine;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Worker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--probe", var root])
        {
            try
            {
                // Exercise the same dependency loading and discovery as job
                // dispatch without opening a volume for writes or creating a job.
                var volume = VolumeDiscovery.Get(root);
                Console.WriteLine(JsonSerializer.Serialize(new { WorkerBuild = BrokerProtocol.BuildVersion, Volume = volume }, JobStore.Json));
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        var store = new JobStore();
        if (args.Length == 2 && args[0] == "--execute" && Guid.TryParse(args[1], out var id))
        {
            var request = store.ReadRequest(id);
            try
            {
                request.Validate();
                using var limits = new WorkerLimits(request.Resources);
                StoragePrivileges.Enable();
                var topology = VolumeDiscovery.Get(request.Volume);
                var settings = store.Settings;
                string[] resources = topology.Resources;
                if (settings.ManualResourceGroups.TryGetValue(request.Volume, out var manual)) resources = [.. resources, .. manual.Select(s => "manual:" + s)];
                var waiting = Stopwatch.StartNew();
                using var lease = new StorageLease(topology.Id, resources, settings.AllowParallelOnSharedStorage, CancellationToken.None,
                    () => store.ReadControl(id) == "cancel" || (request.MaxMinutes > 0 && waiting.Elapsed.TotalMinutes >= request.MaxMinutes));
                new JobExecutor(store).Run(request, CancellationToken.None);
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(store.JobDirectory(id), "worker-error.txt"), e.ToString());
                store.Save(new(id, request.Volume, request.Operation, e is OperationCanceledException ? JobState.Cancelled : JobState.Failed, e.Message, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow));
            }
            return store.ReadSnapshot(id)?.State == JobState.Failed ? 1 : 0;
        }
        if (args is not ["--broker"]) { Console.WriteLine("Tedd.Defrag.Worker --broker | --execute <job-id> | --probe <volume>"); return 2; }
        using var mutex = new Mutex(true, "Local\\" + BrokerProtocol.PipeName, out bool owns);
        if (!owns) return 0;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var broker = new Broker(store, cancellation.Cancel);
        var server = broker.Listen(cancellation.Token);
        await broker.Pump(cancellation.Token);
        await server; return 0;
    }
}

internal sealed class Broker
{
    private readonly JobStore _store;
    private readonly object _sync = new();
    private readonly Queue<JobRequest> _queue = new();
    private readonly Dictionary<Guid, Process> _active = [];
    private readonly Dictionary<Guid, string[]> _resources = [];
    private readonly ResourceArbiter _arbiter = new();
    private readonly Action _requestStop;
    private bool _stopping;
    public Broker(JobStore store, Action requestStop)
    {
        _store = store;
        _requestStop = requestStop;
        foreach (var snapshot in store.List().Where(s => !s.IsTerminal))
        {
            if (snapshot.State == JobState.Queued)
            {
                try { var request = store.ReadRequest(snapshot.Id); request.Validate(); _queue.Enqueue(request); }
                catch (Exception e) { store.Save(snapshot with { State = JobState.Failed, Message = e.Message, UpdatedAt = DateTimeOffset.UtcNow }); }
            }
            else store.Save(snapshot with { State = JobState.Interrupted, Message = "Worker restarted. Reanalyze before resubmitting; saved cluster addresses are never replayed.", UpdatedAt = DateTimeOffset.UtcNow });
        }
    }
    public async Task Listen(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(BrokerProtocol.PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(token); _ = Respond(pipe, token); }
            catch { pipe.Dispose(); if (!token.IsCancellationRequested) throw; }
        }
    }
    private async Task Respond(NamedPipeServerStream pipe, CancellationToken token)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var command = await BrokerProtocol.Read<BrokerCommand>(pipe, JobStore.Json, timeout.Token);
                BrokerReply reply;
                try { lock (_sync) reply = Handle(command); }
                catch (Exception e) { reply = new(false, e.Message); }
                await BrokerProtocol.Write(pipe, reply, JobStore.Json, timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
        }
    }
    private BrokerReply Handle(BrokerCommand command)
    {
        if (_stopping && command.Action is not ("ping" or "list" or "get" or "explore" or "stop" or "shutdown"))
            throw new InvalidOperationException("The worker is stopping. Retry after it has restarted.");
        if (command.Action == "submit" && command.ClientBuild != null && command.ClientBuild != BrokerProtocol.BuildVersion)
            throw new InvalidOperationException($"Worker build {BrokerProtocol.BuildVersion} differs from client build {command.ClientBuild}. Reconnect to the matching worker before submitting work.");
        switch (command.Action)
        {
            case "ping": return new(true, Worker: new(BrokerProtocol.BuildVersion, Environment.ProcessId, Environment.ProcessPath ?? "", _stopping));
            case "submit":
                var job = command.Job ?? throw new ArgumentException("Missing job.");
                job.Validate();
                job = job with { Volume = VolumeDiscovery.Root(job.Volume) };
                Submit(job); return new(true, Id: job.Id);
            case "list": return new(true, Jobs: _store.List().Take(200).Select(j => j with { Map = null, Files = null }).ToArray());
            case "get": return new(true, Snapshot: _store.ReadSnapshot(command.Id));
            case "explore":
                var region = new LayoutExplorerStore(_store).Explore(command.Id, command.StartCluster, command.ClusterCount,
                    command.MapCells, command.IncludeFiles, command.Path, command.FileId, command.Stream);
                return new(true, Region: region);
            case "pause": case "resume": case "cancel": _store.Control(command.Id, command.Action); return new(true);
            case "settings":
                if (command.Settings != null)
                {
                    if (_active.Count > 0 || _queue.Count > 0) throw new InvalidOperationException("Finish or cancel queued and active jobs before changing concurrency settings.");
                    _store.SaveSettings(command.Settings);
                }
                return new(true, Settings: _store.Settings);
            case "stop":
                if (_active.Count != 0 || _queue.Count != 0) throw new InvalidOperationException("Cancel or finish active and queued jobs before stopping the worker.");
                if (_stopping) return new(true);
                _stopping = true;
                _ = Task.Run(async () => { await Task.Delay(500); _requestStop(); }); return new(true);
            case "shutdown":
                if (_stopping) return new(true);
                BeginShutdown(); return new(true);
            default: throw new ArgumentException("Unknown broker command.");
        }
    }
    private void Submit(JobRequest job)
    {
        _store.SaveRequest(job);
        _store.Save(new(job.Id, job.Volume, job.Operation, JobState.Queued, "Queued for backing-storage access", 0, 0, 0, 0, 0, DateTimeOffset.UtcNow));
        _queue.Enqueue(job);
    }
    public async Task Pump(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            lock (_sync)
            {
                if (_stopping) return;
                foreach (var (id, process) in _active.ToArray())
                {
                    if (!process.HasExited) continue;
                    var snapshot = _store.ReadSnapshot(id);
                    if (snapshot != null && !snapshot.IsTerminal) _store.Save(snapshot with { State = JobState.Interrupted, Message = $"Execution process exited ({process.ExitCode}); reanalysis required.", UpdatedAt = DateTimeOffset.UtcNow });
                    process.Dispose(); _active.Remove(id); _arbiter.Release(id); _resources.Remove(id);
                }
                var settings = _store.Settings;
                int queued = _queue.Count;
                for (int i = 0; i < queued; i++)
                {
                    var job = _queue.Dequeue();
                    if (_store.ReadControl(job.Id) == "cancel")
                    { _store.Save(_store.ReadSnapshot(job.Id)!.Transition(JobState.Cancelled, "Cancelled before execution")); _arbiter.Release(job.Id); continue; }
                    if (!_resources.TryGetValue(job.Id, out var resources))
                    {
                        try
                        {
                            resources = VolumeDiscovery.Get(job.Volume).Resources;
                            if (settings.ManualResourceGroups.TryGetValue(job.Volume, out var manual)) resources = [.. resources, .. manual.Select(s => "manual:" + s)];
                            _resources[job.Id] = resources;
                        }
                        catch (Exception e) { FailBeforeExecution(job, e); continue; }
                    }
                    if (!_arbiter.TryAcquire(job.Id, job.Volume, resources, settings)) { _queue.Enqueue(job); continue; }
                    try
                    {
                        bool elevate = !VolumeDiscovery.IsElevated;
                        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = elevate, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                        if (elevate) info.Verb = "runas";
                        info.ArgumentList.Add("--execute"); info.ArgumentList.Add(job.Id.ToString());
                        if (!elevate && job.Resources.MemoryMiB > 0)
                        {
                            info.Environment["DOTNET_GCHeapHardLimit"] = ((long)job.Resources.MemoryMiB * 1024 * 1024 * 55 / 100).ToString("X", CultureInfo.InvariantCulture);
                            info.Environment["DOTNET_GCConserveMemory"] = "7";
                        }
                        _active.Add(job.Id, Process.Start(info) ?? throw new IOException("Unable to launch isolated worker."));
                    }
                    catch (Exception e) { _arbiter.Release(job.Id); FailBeforeExecution(job, e); }
                }
            }
            try { await Task.Delay(500, token); } catch (OperationCanceledException) { break; }
        }
    }

    private void FailBeforeExecution(JobRequest job, Exception error)
    {
        var snapshot = _store.ReadSnapshot(job.Id)!;
        _store.Save(snapshot.Transition(JobState.Failed, error.Message) with { WorkerBuild = BrokerProtocol.BuildVersion });
        File.WriteAllText(Path.Combine(_store.JobDirectory(job.Id), "worker-error.txt"), error.ToString());
        _resources.Remove(job.Id);
    }

    private void BeginShutdown()
    {
        _stopping = true;
        while (_queue.TryDequeue(out var job))
        {
            var snapshot = _store.ReadSnapshot(job.Id);
            if (snapshot is { IsTerminal: false })
            {
                try { _store.Save(snapshot with { State = JobState.Cancelled, Message = "Cancelled because the application closed.", UpdatedAt = DateTimeOffset.UtcNow }); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            _arbiter.Release(job.Id);
            _resources.Remove(job.Id);
        }
        var active = _active.ToArray();
        foreach (var (id, _) in active)
        {
            try { _store.Control(id, "cancel"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        _ = Task.Run(() => StopActiveJobs(active));
    }

    private async Task StopActiveJobs(KeyValuePair<Guid, Process>[] active)
    {
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (active.Any(item => !HasExited(item.Value)) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            foreach (var (_, process) in active)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
            deadline = DateTime.UtcNow.AddSeconds(2);
            while (active.Any(item => !HasExited(item.Value)) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            lock (_sync)
            {
                foreach (var (id, process) in active)
                {
                    var snapshot = _store.ReadSnapshot(id);
                    if (snapshot is { IsTerminal: false })
                    {
                        try { _store.Save(snapshot with { State = JobState.Cancelled, Message = "Cancelled because the application closed.", UpdatedAt = DateTimeOffset.UtcNow }); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    }
                    process.Dispose();
                    _active.Remove(id);
                    _arbiter.Release(id);
                    _resources.Remove(id);
                }
            }
        }
        finally { _requestStop(); }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return true; }
    }
}
