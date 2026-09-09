using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using Tedd.Defrag.Core;
using Tedd.Defrag.Engine;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Scheduling;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Worker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
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
                    () => store.ReadControl(id) == "cancel" || waiting.Elapsed.TotalMinutes >= request.MaxMinutes);
                new JobExecutor(store).Run(request, CancellationToken.None);
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(store.JobDirectory(id), "worker-error.txt"), e.ToString());
                store.Save(new(id, request.Volume, request.Operation, e is OperationCanceledException ? JobState.Cancelled : JobState.Failed, e.Message, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow));
            }
            return store.ReadSnapshot(id)?.State == JobState.Failed ? 1 : 0;
        }
        if (args is not ["--broker"]) { Console.WriteLine("Tedd.Defrag.Worker --broker | --execute <job-id>"); return 2; }
        using var mutex = new Mutex(true, "Local\\" + BrokerProtocol.PipeName, out bool owns);
        if (!owns) return 0;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var broker = new Broker(store);
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
    private readonly ResourceScheduler _scheduler = new();
    public Broker(JobStore store)
    {
        _store = store;
        foreach (var snapshot in store.List().Where(s => !s.IsTerminal))
        {
            if (snapshot.State == JobState.Queued)
            {
                try { var request = store.ReadRequest(snapshot.Id); request.Validate(); _queue.Enqueue(request); }
                catch (Exception e) { store.Save(snapshot with { State = JobState.Failed, Message = e.Message, UpdatedAt = DateTimeOffset.UtcNow }); }
            }
            else store.Save(snapshot with { State = JobState.Interrupted, Message = "Worker restarted. Reanalyze before resubmitting; saved cluster addresses are never replayed.", UpdatedAt = DateTimeOffset.UtcNow });
        }
        var schedules = store.Schedules;
        if (schedules.Any(s => s.Enabled && s.Template.LegacySimulation))
            store.SaveSchedules(schedules.Select(s => s.Template.LegacySimulation ? s with { Enabled = false } : s).ToArray());
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
        switch (command.Action)
        {
            case "ping": return new(true);
            case "submit":
                var job = command.Job ?? throw new ArgumentException("Missing job.");
                job.Validate();
                job = job with { Volume = VolumeDiscovery.Root(job.Volume) };
                Submit(job); return new(true, Id: job.Id);
            case "list": return new(true, Jobs: _store.List().Take(200).Select(j => j with { Map = null, Files = null }).ToArray());
            case "get": return new(true, Snapshot: _store.ReadSnapshot(command.Id));
            case "pause": case "resume": case "cancel": _store.Control(command.Id, command.Action); return new(true);
            case "settings":
                if (command.Settings != null)
                {
                    if (_active.Count > 0 || _queue.Count > 0) throw new InvalidOperationException("Finish or cancel queued and active jobs before changing concurrency settings.");
                    _store.SaveSettings(command.Settings);
                }
                return new(true, Settings: _store.Settings);
            case "schedules": return new(true, Schedules: _store.Schedules);
            case "schedule-add":
                var schedule = command.Schedule ?? throw new ArgumentException("Missing schedule."); schedule.Template.Validate();
                if (schedule.Template.Operation == Operation.ZeroFreeSpace) throw new NotSupportedException("Virtual-disk pre-zeroing is a manual workflow and cannot be scheduled.");
                if (schedule.Days.Length == 0 || string.IsNullOrWhiteSpace(schedule.Name)) throw new ArgumentException("Schedule name and days are required.");
                _store.SaveSchedules([.. _store.Schedules.Where(s => s.Name != schedule.Name), schedule]); return new(true);
            case "schedule-remove": _store.SaveSchedules(_store.Schedules.Where(s => s.Name != command.Name).ToArray()); return new(true);
            case "stop":
                if (_active.Count != 0 || _queue.Count != 0) throw new InvalidOperationException("Cancel or finish active and queued jobs before stopping the worker.");
                _ = Task.Run(async () => { await Task.Delay(500); Environment.Exit(0); }); return new(true);
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
                foreach (var (id, process) in _active.ToArray())
                {
                    if (!process.HasExited) continue;
                    var snapshot = _store.ReadSnapshot(id);
                    if (snapshot != null && !snapshot.IsTerminal) _store.Save(snapshot with { State = JobState.Interrupted, Message = $"Execution process exited ({process.ExitCode}); reanalysis required.", UpdatedAt = DateTimeOffset.UtcNow });
                    process.Dispose(); _active.Remove(id); _scheduler.Release(id); _resources.Remove(id);
                }
                var settings = _store.Settings;
                int queued = _queue.Count;
                for (int i = 0; i < queued; i++)
                {
                    var job = _queue.Dequeue();
                    if (_store.ReadControl(job.Id) == "cancel")
                    { _store.Save(_store.ReadSnapshot(job.Id)! with { State = JobState.Cancelled, Message = "Cancelled before execution" }); _scheduler.Release(job.Id); continue; }
                    if (!_resources.TryGetValue(job.Id, out var resources))
                    {
                        try
                        {
                            resources = VolumeDiscovery.Get(job.Volume).Resources;
                            if (settings.ManualResourceGroups.TryGetValue(job.Volume, out var manual)) resources = [.. resources, .. manual.Select(s => "manual:" + s)];
                            _resources[job.Id] = resources;
                        }
                        catch (Exception e) { _store.Save(_store.ReadSnapshot(job.Id)! with { State = JobState.Failed, Message = e.Message }); continue; }
                    }
                    if (!_scheduler.TryAcquire(job.Id, job.Volume, resources, settings)) { _queue.Enqueue(job); continue; }
                    try
                    {
                        bool elevate = !VolumeDiscovery.IsElevated;
                        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = elevate, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                        if (elevate) info.Verb = "runas";
                        info.ArgumentList.Add("--execute"); info.ArgumentList.Add(job.Id.ToString());
                        if (!elevate)
                        {
                            info.Environment["DOTNET_GCHeapHardLimit"] = ((long)job.Resources.MemoryMiB * 1024 * 1024 * 55 / 100).ToString("X", CultureInfo.InvariantCulture);
                            info.Environment["DOTNET_GCConserveMemory"] = "7";
                        }
                        _active.Add(job.Id, Process.Start(info) ?? throw new IOException("Unable to launch isolated worker."));
                    }
                    catch (Exception e) { _scheduler.Release(job.Id); _store.Save(_store.ReadSnapshot(job.Id)! with { State = JobState.Failed, Message = e.Message }); }
                }
                var schedules = _store.Schedules; bool changed = false;
                for (int i = 0; i < schedules.Length; i++)
                {
                    var s = schedules[i];
                    if (!ScheduleClock.IsDue(s, DateTime.Now)) continue;
                    if (_queue.Any(j => j.Volume == s.Template.Volume) || _active.Keys.Any(id => _store.ReadSnapshot(id)?.Volume == s.Template.Volume)) continue;
                    Submit(s.Template with { Id = Guid.NewGuid() }); schedules[i] = s with { LastRun = DateOnly.FromDateTime(DateTime.Now) }; changed = true;
                }
                if (changed) _store.SaveSchedules(schedules);
            }
            try { await Task.Delay(500, token); } catch (OperationCanceledException) { break; }
        }
    }
}
