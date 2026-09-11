using System.Globalization;
using System.Text.Json;
using Tedd.Defrag.Client;
using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Update;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--apply-update", var requestPayload]) return await ReleaseUpdater.ApplyUpdateAsync(requestPayload);
        if (args is ["--apply-installer-update", var installerRequestPayload]) return await ReleaseUpdater.ApplyInstallerUpdateAsync(installerRequestPayload);
        bool json = args.Contains("--json");
        try
        {
            var parsed = new Arguments(args);
            string command = parsed.Positionals.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "tui";
            if (command is "help" or "--help" or "-h" || parsed.Has("help")) { Console.WriteLine(Help); return 0; }
            if (command is "version" or "--version" || parsed.Has("version")) { Console.WriteLine(ReleaseUpdater.DisplayVersion); return 0; }
            if (command == "update") { await OfferUpdate(args, true); return 0; }
            if (!json && !parsed.Has("no-update-check") && await OfferUpdate(args, false)) return 0;
            var client = new DefragClient();
            if (command == "volumes")
            {
                var volumes = VolumeDiscovery.List();
                if (json) PrintJson(volumes);
                else foreach (var v in volumes) Console.WriteLine($"{v.Root,-5} {v.Label,-22} {v.FileSystem,-8} {Format.Bytes(v.SizeBytes),12}  {Format.Bytes(v.FreeBytes),12} free  trim={v.TrimEnabled?.ToString() ?? "unknown"}  {string.Join(',', v.Resources)}");
                return 0;
            }
            if (command == "jobs")
            {
                string action = parsed.Positionals.ElementAtOrDefault(1) ?? "list";
                if (action == "list")
                {
                    var reply = await client.Send(new("list"));
                    if (json) PrintJson(reply.Jobs); else foreach (var job in reply.Jobs ?? []) Console.WriteLine($"{job.Id}  {job.Volume,-4} {job.Operation,-16} {job.State,-16} {job.Message}");
                    return 0;
                }
                Guid id = Guid.Parse(parsed.Positionals.ElementAtOrDefault(2) ?? throw new ArgumentException("Supply a job ID."));
                if (action == "watch") return await Watch(client, id, json, parsed.Has("events"));
                await client.Send(new(action, Id: id)); return 0;
            }
            if (command == "worker")
            {
                string action = parsed.Positionals.ElementAtOrDefault(1) ?? "status";
                var reply = await client.Send(new(action == "stop" ? "stop" : "ping"), startBroker: action == "start");
                if (json) PrintJson(reply);
                else if (reply.Worker is { } worker) Console.WriteLine($"Worker {worker.Build} · PID {worker.ProcessId} · {worker.ExecutablePath}");
                else if (action != "stop") Console.WriteLine("Legacy worker: build information unavailable. Submit with the current client or use 'worker start' to replace it when idle.");
                return 0;
            }
            if (command == "settings")
            {
                var current = (await client.Send(new("settings"))).Settings!;
                var next = current with { MaxConcurrentVolumes = parsed.Int("parallel", current.MaxConcurrentVolumes),
                    AllowParallelOnSharedStorage = parsed.Has("shared") ? true : parsed.Has("no-shared") ? false : current.AllowParallelOnSharedStorage,
                    MaxConcurrentJobsPerSharedResource = parsed.Int("per-device", current.MaxConcurrentJobsPerSharedResource) };
                PrintJson((await client.Send(new("settings", Settings: next))).Settings); return 0;
            }
            if (command is "boot" or "registry" or "offline") throw new NotSupportedException("Early-boot execution and offline hive replacement are not implemented. Ordinary startup jobs remain online; this application does not modify BootExecute or registry hives.");
            string? selected = parsed.All("path").Concat(parsed.All("file")).Concat(parsed.All("folder")).FirstOrDefault();
            string defaultVolume = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            string volume = parsed.Positionals.ElementAtOrDefault(1) ?? parsed.Get("volume", selected == null ? defaultVolume : Path.GetPathRoot(selected) ?? defaultVolume);
            if (command == "tui")
            {
                if (Console.IsInputRedirected || Console.IsOutputRedirected) throw new ArgumentException("Interactive TUI requires a terminal. Use --json for redirected output.");
                TerminalDashboard.Run(volume); return 0;
            }
            if (command is not ("analyze" or "optimize" or "defrag" or "trim" or "zero")) throw new ArgumentException("Unknown command. Use help.");
            var volumeInfo = VolumeDiscovery.Get(volume);
            var jobRequest = MakeRequest(parsed, command, volume, volumeInfo.FileSystem);
            FileSystemCapabilities.Validate(volumeInfo.FileSystem, jobRequest.Operation);
            var submitted = await client.Send(new("submit", Job: jobRequest), startBroker: true);
            if (parsed.Has("wait") || command == "analyze") return await Watch(client, submitted.Id, json, parsed.Has("events"));
            if (json) PrintJson(new { jobId = submitted.Id }); else Console.WriteLine(submitted.Id);
            return 0;
        }
        catch (NotSupportedException e) { Error(e, json); return 4; }
        catch (ArgumentException e) { Error(e, json); return 2; }
        catch (FormatException e) { Error(e, json); return 2; }
        catch (OverflowException e) { Error(e, json); return 2; }
        catch (Exception e) { Error(e, json); return 1; }
    }
    private static async Task<bool> OfferUpdate(string[] launchArguments, bool explicitRequest)
    {
        if (!ReleaseUpdater.IsPackaged)
        {
            if (explicitRequest) Console.WriteLine($"Tedd.Defrag {ReleaseUpdater.DisplayVersion}. Update checks are enabled in release distributions.");
            return false;
        }
        AvailableRelease? release;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(explicitRequest ? 15 : 3));
            release = await ReleaseUpdater.CheckForUpdateAsync(timeout.Token);
        }
        catch when (!explicitRequest) { return false; }
        if (release is null)
        {
            if (explicitRequest) Console.WriteLine($"Tedd.Defrag {ReleaseUpdater.DisplayVersion} is current.");
            return false;
        }
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            if (explicitRequest) Console.WriteLine($"Tedd.Defrag {release.DisplayVersion} is available: {release.ReleasePageUri}");
            return false;
        }
        string action = release.PackageKind == ReleasePackageKind.Installer ? "run the verified installer" : "replace this portable copy";
        Console.Error.Write($"Tedd.Defrag {release.DisplayVersion} is available. Download, {action}, and restart? [y/N] ");
        string? answer = Console.ReadLine();
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            try { await new DefragClient().Send(new("stop")); }
            catch (TimeoutException) { }
            Console.Error.WriteLine("Downloading and verifying the update…");
            await ReleaseUpdater.LaunchUpdateAsync(release, "Tedd.Defrag.Cli.exe", launchArguments);
            return true;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"Update postponed: {exception.Message}");
            return false;
        }
    }
    private static JobRequest MakeRequest(Arguments args, string command, string volume, string fileSystem)
    {
        var support = FileSystemCapabilities.Get(fileSystem);
        string policy = args.Get("policy", (command == "defrag" ? support.DefaultDefrag : support.DefaultOptimization).ToString());
        if (!Enum.TryParse<Operation>(policy.Replace("-", ""), true, out var operation)) throw new ArgumentException("Unknown layout policy.");
        operation = command switch { "analyze" => Operation.Analyze, "trim" => Operation.ReTrim, "zero" => Operation.ZeroFreeSpace, _ => operation };
        ResourcePolicy resources = args.Get("preset", "performance").ToLowerInvariant() switch { "quiet" => ResourcePolicy.Quiet, "performance" => ResourcePolicy.Performance, "balanced" => ResourcePolicy.Balanced, _ => throw new ArgumentException("Unknown resource preset.") };
        resources = resources with { CpuPercent = args.Int("cpu", resources.CpuPercent), MemoryMiB = args.Int("memory", resources.MemoryMiB),
            ScanWorkers = args.Int("scan-workers", resources.ScanWorkers), PlanningWorkers = args.Int("planning-workers", resources.PlanningWorkers),
            MoveQueueDepth = args.Int("move-queue", resources.MoveQueueDepth),
            IoMiBPerSecond = args.Int("io", resources.IoMiBPerSecond), AffinityMask = Convert.ToUInt64(args.Get("affinity", "0").Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16),
            IdleOnly = args.Has("idle-only") || resources.IdleOnly, AcOnly = !args.Has("allow-battery") && resources.AcOnly, Background = !args.Has("foreground") && resources.Background };
        return new() { Volume = volume, Operation = operation, Preview = !args.Has("execute"), Resources = resources,
            SelectedPaths = [.. args.All("path"), .. args.All("file"), .. args.All("folder")], Exclusions = args.All("exclude"),
            MaxMoveBytes = checked(args.Long("budget-mib", 0) * 1024 * 1024), MaxMinutes = args.Int("minutes", 0),
            MinimumFragments = args.Int("min-fragments", 20),
            MinimumFileBytes = checked(args.Long("min-file-mib", 0) * 1024 * 1024), MaximumFileBytes = checked(args.Long("max-file-mib", 0) * 1024 * 1024),
            ShrinkBoundaryBytes = checked(args.Long("boundary-mib", 0) * 1024 * 1024), AllowSsdRelocation = args.Has("allow-ssd"),
            ConfirmVirtualDiskZeroing = args.Has("confirm-virtual-zero"), FreeSpaceReserveBytes = checked(args.Long("reserve-mib", 2048) * 1024 * 1024) };
    }
    private static async Task<int> Watch(DefragClient client, Guid id, bool json, bool events)
    {
        // Ctrl+C detaches the viewer. Cancellation is an explicit jobs cancel command.
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            DateTimeOffset last = default;
            while (!cts.IsCancellationRequested)
            {
                var snapshot = (await client.Send(new("get", Id: id), token: cts.Token)).Snapshot ?? throw new IOException("Unknown job.");
                if (snapshot.UpdatedAt != last)
                {
                    if (events) PrintJson(snapshot);
                    else if (!json) Console.WriteLine($"{snapshot.State,-16} {snapshot.Progress,6:P0}  {snapshot.Message}");
                    last = snapshot.UpdatedAt;
                }
                if (snapshot.IsTerminal)
                {
                    if (json && !events) PrintJson(snapshot);
                    return snapshot.State switch { JobState.Failed or JobState.Interrupted => 1, JobState.Cancelled => 130, JobState.Partial => 3, _ => 0 };
                }
                await Task.Delay(300, cts.Token);
            }
        }
        catch (OperationCanceledException) { return 130; }
        finally { Console.CancelKeyPress -= handler; }
        return 0;
    }
    private static void PrintJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JobStore.Json));
    private static void Error(Exception e, bool json) { if (json) PrintJson(new { error = e.Message }); else Console.Error.WriteLine(e.Message); }
    private const string Help = """
        TEDD / DEFRAG · .NET 11

        version                                  Print the compiled application version
        update                                   Check GitHub Releases and offer an in-place update
        volumes [--json]                       List local volumes and capabilities
        tui [C:]                               Interactive Tedd.TUI dashboard
        analyze C: [--json]                     Analyze allocation and file fragmentation
        optimize C: --policy MinimumWrite       Preview a bounded layout plan
        defrag C: --path C:\Data\file.bin        Preview selected-file optimization
        trim C: --execute                       Submit Windows ReTRIM
        zero V: --execute --confirm-virtual-zero Prepare a virtual disk using allocated zero files

        --execute              Perform changes (default: preview)
        --wait --json          Wait and emit structured result; --events emits NDJSON
        --exclude <path/glob>  Repeat for recursive paths or patterns; exclusions always win
        --preset quiet|balanced|performance       Default: performance
        --cpu 100 --memory 0 --io 0       CPU %, process commit MiB, relocation MiB/s; 0 means unlimited
        --affinity 0xF0        Advanced logical CPU mask (single processor group)
        --scan-workers 0 --planning-workers 0   0 = automatic, 1–32 = explicit worker limit
        --move-queue 16       1–16 independent file moves in flight; Performance preset uses 16
        --idle-only --allow-battery --foreground
        --budget-mib 0 --minutes 0 --allow-ssd   0 means unlimited
        --min-fragments 20 --min-file-mib 0 --max-file-mib 0
        --no-update-check       Skip the automatic GitHub release check for this run
        --boundary-mib <n>     PrepareShrink boundary

        Policies: MinimumWrite, FilesOnly, Pack, PackAndDefrag, Alphabetical,
        Size, Created, Modified, Extension, DirectoryLocality, PrepareShrink,
        OptimizeMft, DirectoryIndexes, Automatic, ReTrim, SlabConsolidate, WindowsDefrag

        ReFS: analyze, trim, WindowsDefrag, Automatic and SlabConsolidate.
        FAT12/16/32: analyze, trim, WindowsDefrag and Automatic. exFAT is not supported.
        On ReFS/FAT, defrag defaults to whole-volume WindowsDefrag; optimize defaults to Automatic.
        Windows decides maintenance availability. Custom placement/file filters require NTFS.

        jobs list|pause|resume|cancel|watch [id] [--json]
        settings --parallel 2 [--shared|--no-shared] [--per-device 2]
        worker status | start | stop          Inspect, start/refresh, or stop an idle worker

        Exit codes: 0 complete; 1 failed; 2 arguments; 3 partial; 4 unsupported; 130 detached/cancelled.
        Affinity cannot guarantee cache isolation. I/O pacing covers custom relocation/zeroing,
        not all kernel traffic or OS-managed optimization. Preview is the default.
        """;
}

internal sealed class Arguments
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positionals { get; } = [];
    public Arguments(string[] args)
    {
        string[] flags = ["json", "events", "wait", "execute", "idle-only", "allow-battery", "foreground", "allow-ssd", "confirm-virtual-zero", "shared", "no-shared", "help", "version", "no-update-check"];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--demo", StringComparison.OrdinalIgnoreCase) || args[i].StartsWith("--demo=", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Simulation mode has been removed. Analyze or preview a real volume instead.");
            if (!args[i].StartsWith("--")) { Positionals.Add(args[i]); continue; }
            string key = args[i][2..]; string value = "true";
            if (!flags.Contains(key))
            {
                if (++i >= args.Length || args[i].StartsWith("--")) throw new ArgumentException($"Missing value for --{key}.");
                value = args[i];
            }
            if (!_options.TryGetValue(key, out var list)) _options[key] = list = [];
            list.Add(value);
        }
    }
    public bool Has(string key) => _options.ContainsKey(key);
    public string Get(string key, string fallback) => _options.TryGetValue(key, out var values) ? values[^1] : fallback;
    public string[] All(string key) => _options.TryGetValue(key, out var values) ? values.ToArray() : [];
    public int Int(string key, int fallback) => int.Parse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
    public long Long(string key, long fallback) => long.Parse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
}
