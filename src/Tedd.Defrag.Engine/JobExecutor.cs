using System.Diagnostics;
using System.ComponentModel;
using Tedd.Defrag.Core;
using Tedd.Defrag.Maintenance;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Planning;
using Tedd.Defrag.Visualization;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Engine;

public sealed class JobExecutor
{
    private readonly JobStore store;
    private readonly Func<JobRequest, IJobVolume> openVolume;
    private readonly Action<JobRequest, VolumeInfo, Action<string>, Action, CancellationToken> runMaintenance;

    public JobExecutor(JobStore store) : this(store, JobVolume.Open) { }
    internal JobExecutor(JobStore store, Func<JobRequest, IJobVolume> openVolume,
        Action<JobRequest, VolumeInfo, Action<string>, Action, CancellationToken>? runMaintenance = null)
    {
        this.store = store;
        this.openVolume = openVolume;
        this.runMaintenance = runMaintenance ?? WindowsMaintenance.Run;
    }

    public void Run(JobRequest request, CancellationToken token)
    {
        request.Validate();
        var clock = Stopwatch.StartNew(); long moved = 0, plannedBytes = 0, lastPublish = -1000;
        long pacedBytes = 0; var pacing = Stopwatch.StartNew();
        VolumeLayout? layout = null; MapCell[] map = new MapCell[request.Resources.MapCells];
        VolumeInfo? volumeInfo = null;
        var warnings = new List<string>();
        JobState state = JobState.Scanning; double progress = 0; string message = "Preparing analysis";
        int plannedMoves = 0, attemptedMoves = 0, verifiedMoves = 0, failedMoves = 0;
        int filesConsidered = 0, filesBlocked = 0, initialFragmentedFiles = 0;
        int streamsAtThreshold = 0, eligibleStreamsAtThreshold = 0, mftExtents = 0, fragmentedDirectoryIndexes = 0, directoryIndexesAtThreshold = 0;
        bool noMovesPlanned = false;
        bool layoutIndexDirty = false;
        long scannedRecords = 0;
        WorkProgress? scanWork = null, planningWork = null, executionWork = null;
        var executionWatch = new Stopwatch(); var moveActivity = new WorkerActivity();
        var explorer = new LayoutExplorerStore(store);
        try
        {
            Checkpoint();
            using var volume = openVolume(request);
            volumeInfo = volume.Info;
            FileSystemCapabilities.Validate(volume.Info.FileSystem, request.Operation);
            bool external = FileSystemCapabilities.IsWindowsMaintenance(request.Operation);
            string? maintenanceFlag = external ? WindowsMaintenance.Validate(request, volume.Info) : null;
            if (!request.Preview && request.Operation != Operation.Analyze && !volume.FileSystem.UsesDirectoryScan && volume.IsDirty())
                throw new IOException($"The {volume.Info.FileSystem} volume is dirty. Resolve filesystem errors before optimization.");
            if (!request.Preview && request.Operation is not (Operation.Analyze or Operation.ReTrim or Operation.SlabConsolidate or Operation.Automatic or Operation.ZeroFreeSpace)
                && volume!.Info.SeekPenalty != true && !request.AllowSsdRelocation)
                throw new InvalidOperationException("Defragmentation on SSD or unknown media requires explicit opt-in.");
            layout = ScanVolume();
            UpdateConditionMetrics();
            layoutIndexDirty = true;
            if (layout != null)
            {
                warnings.AddRange(layout.Warnings);
                initialFragmentedFiles = layout.Files.Count(f => f.Fragmented);
                MapAggregator.Build(layout, map); SaveLayoutIndex();
            }
            Publish(true);
            if (request.Operation == Operation.Analyze) { state = layout!.Complete ? JobState.Completed : JobState.Partial; message = layout.Complete ? "Analysis complete" : "Analysis complete; file coverage is partial"; progress = 1; return; }
            if (external || request.Operation == Operation.ZeroFreeSpace)
            {
                if (request.Preview) { state = JobState.Completed; progress = 1; message = external
                    ? $"Maintenance preview: defrag.exe {volume.Info.Root[..2]} {maintenanceFlag} /U /V. Whole volume; availability is determined by Windows at execution. No storage changes submitted."
                    : "Maintenance preview ready; no storage changes submitted"; return; }
                state = JobState.Running; progress = 0; message = "Windows maintenance; progress is indeterminate"; Publish(true);
                if (request.Operation == Operation.ZeroFreeSpace)
                    moved = VirtualDiskPreparation.Zero(request, volume!.Info, b => { Pace(b - moved); moved = b; Publish(); }, Checkpoint, token);
                else runMaintenance(request, volume!.Info, line =>
                {
                    File.AppendAllText(Path.Combine(store.JobDirectory(request.Id), "maintenance.log"), line + Environment.NewLine);
                    message = line; Publish();
                }, ExternalCheckpoint, token);
                state = JobState.Scanning; progress = 0; scanWork = null;
                ReleaseLayoutForRescan();
                layout = ScanVolume();
                UpdateConditionMetrics();
                layoutIndexDirty = true;
                if (layout != null) { AddWarnings(layout.Warnings); MapAggregator.Build(layout, map); }
                state = layout?.Complete == true ? JobState.Completed : JobState.Partial; progress = 1;
                message = layout == null ? "Windows maintenance completed; allocation map unavailable (see warnings)"
                    : layout.Complete ? "Maintenance completed; allocation map refreshed" : "Windows maintenance completed; allocation map refreshed, file coverage is partial";
                return;
            }
            if (layout == null) throw new IOException("The allocation layout is unavailable.");
            var rules = new PathRules(request.SelectedPaths, request.Exclusions);
            bool ordered = request.Operation is Operation.Alphabetical or Operation.Size or Operation.Created or Operation.Modified or Operation.Extension or Operation.DirectoryLocality;
            var session = new PlanningSession();
            var planner = new LayoutPlanner();
            while (request.MaxMoveBytes == 0 || moved < request.MaxMoveBytes)
            {
                Checkpoint(); state = JobState.Planning; progress = 0; message = "Calculating eligible placements"; Publish(true);
                var plan = planner.Plan(layout, request with { MaxMoveBytes = request.MaxMoveBytes == 0 ? 0 : request.MaxMoveBytes - moved }, token, session,
                    p => { planningWork = p; message = p.Phase; progress = p.Total > 0 ? (double)p.Completed / p.Total : 0; Publish(); }, Checkpoint);
                long batchPlannedBytes = plan.ClustersToMove * layout.Volume.BytesPerCluster;
                filesConsidered = Math.Max(filesConsidered, plan.FilesConsidered);
                filesBlocked = Math.Max(filesBlocked, plan.FilesBlocked);
                if (plan.Moves.Length == 0) { noMovesPlanned = plannedMoves == 0; break; }
                plannedBytes += batchPlannedBytes; plannedMoves += plan.Moves.Length;
                if (request.Preview)
                {
                    foreach (var move in plan.Moves) MapAggregator.Activity(map, layout.TotalClusters, move.DestinationLcn, move.Clusters, false);
                    state = JobState.Completed; progress = 1;
                    message = $"Preview of first batch: {plan.Moves.Length:N0} moves · {Format.Bytes(batchPlannedBytes)} · {plan.FilesBlocked:N0} constrained files. Execution continues through further batches. No writes performed."; return;
                }
                state = JobState.Running; int failuresBeforeBatch = failedMoves;
                // Metadata moves remain serial. User queue depth applies to independent ordinary files.
                int depth = request.Operation is Operation.OptimizeMft or Operation.DirectoryIndexes or Operation.DirectoryLocality
                    ? 1 : request.Resources.MoveQueueDepth;
                if (!executionWatch.IsRunning) { executionWatch.Start(); pacing.Restart(); }
                var batchWatch = Stopwatch.StartNew(); long bytesBeforeBatch = moved;
                MoveScheduler.Run(plan.Moves, depth, move =>
                {
                    Checkpoint(); var file = layout.Files[move.FileIndex];
                    if (session.BlockedFiles.Contains(file.FileId)) return false;
                    Pace(move.Clusters * layout.Volume.BytesPerCluster);
                    Checkpoint(); message = file.Path;
                    MapAggregator.Activity(map, layout.TotalClusters, move.SourceLcn, move.Clusters, false);
                    Publish();
                    store.Journal(request.Id, new(DateTimeOffset.UtcNow, "intent", move));
                    attemptedMoves++;
                    return true;
                }, move =>
                {
                    moveActivity.Enter();
                    try { volume.ExecuteMove(layout.Files[move.FileIndex], move, rules); }
                    finally { moveActivity.Exit(); }
                }, (move, error) =>
                {
                    var file = layout.Files[move.FileIndex];
                    try
                    {
                        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                        // Native success has been verified by retrieval pointers before changing the display model.
                        LayoutMutation.Apply(layout, move);
                    }
                    catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
                    {
                        failedMoves++; session.BlockFile(file.FileId);
                        layout.Files[move.FileIndex] = file with { Flags = file.Flags | StreamFlags.Excluded };
                        store.Journal(request.Id, new(DateTimeOffset.UtcNow, "reconcile-required", move, e.Message));
                        if (warnings.Count < 50) warnings.Add($"{file.Path}: {e.Message}");
                        return;
                    }
                    layoutIndexDirty = true;
                    moved += move.Clusters * layout.Volume.BytesPerCluster;
                    verifiedMoves++;
                    // Publication failures must stop dispatch, not count a verified native move as failed too.
                    store.Journal(request.Id, new(DateTimeOffset.UtcNow, "verified", move));
                    if (clock.ElapsedMilliseconds - lastPublish >= 200) MapAggregator.Build(layout, map);
                    MapAggregator.Activity(map, layout.TotalClusters, move.DestinationLcn, move.Clusters, true);
                    Publish();
                }, Checkpoint, (completed, pending, peak) =>
                {
                    progress = (double)completed / plan.Moves.Length;
                    executionWork = new("Relocating and verifying", completed, plan.Moves.Length, "batch moves", depth,
                        moveActivity.Active, moveActivity.Peak, moveActivity.Active, moveActivity.Peak, moved - bytesBeforeBatch, batchWatch.ElapsedMilliseconds,
                        $"{volume.Info.FileSystem} filesystem I/O; scalar validation",
                        $"{pending:N0} submitted requests pending (peak {peak:N0}); {verifiedMoves:N0} verified, {failedMoves:N0} failed. Per-file sequences stay serial; metadata queue depth is 1. Device scheduling remains under Windows control.");
                    Publish();
                });
                if (failedMoves > failuresBeforeBatch)
                {
                    // A failed request may have changed allocation before verification
                    // failed. Refresh before recycling source space in another batch.
                    state = JobState.Scanning; progress = 0;
                    message = $"Refreshing allocation after {failedMoves - failuresBeforeBatch:N0} failed moves; continuing with other files";
                    scanWork = new("Refreshing allocation", 0, 0, "clusters", ActiveWorkers: 1, PeakWorkers: 1, Detail: "Indeterminate bitmap refresh before source space can be reused.");
                    Publish(true);
                    layout = layout with { Bitmap = volume.ReadBitmap(request, Checkpoint, token) };
                    layoutIndexDirty = true; MapAggregator.Build(layout, map); SaveLayoutIndex();
                }
            }
            state = JobState.Scanning; progress = 0; scanWork = null;
            message = "Reconciling actual allocation after execution"; Publish(true);
            ReleaseLayoutForRescan();
            layout = volume.Scan(request, ScanProgress, Checkpoint, token, p => scanWork = p);
            UpdateConditionMetrics();
            layoutIndexDirty = true;
            AddWarnings(layout.Warnings);
            MapAggregator.Build(layout, map);
            bool remainingFragmentation = request.Operation is not (Operation.Pack or Operation.PrepareShrink) && layout.Files.Any(f => f.Fragmented && f.Movable && rules.IsSelected(f.Path) && !rules.IsExcluded(f.Path)
                && f.Size >= request.MinimumFileBytes && (request.MaximumFileBytes == 0 || f.Size <= request.MaximumFileBytes)
                && (request.Operation is not (Operation.MinimumWrite or Operation.FilesOnly) || f.Extents.Length >= request.MinimumFragments));
            bool budgetReached = request.MaxMoveBytes > 0 && request.MaxMoveBytes - moved < layout.Volume.BytesPerCluster;
            state = failedMoves > 0 || filesBlocked > 0 || !layout.Complete || budgetReached || ordered || remainingFragmentation ? JobState.Partial : JobState.Completed;
            progress = 1;
            message = noMovesPlanned
                ? filesConsidered == 0
                    ? "No files met the selected path, size, and fragmentation thresholds; no disk changes were required."
                    : filesBlocked > 0
                    ? $"No relocations were possible: {filesBlocked:N0} of {filesConsidered:N0} considered files were constrained or ineligible."
                    : $"No eligible extents required relocation among {filesConsidered:N0} considered files."
                : $"{Format.Bytes(moved)} relocated and verified in {verifiedMoves:N0} moves. "
                    + (failedMoves > 0 ? $"{failedMoves:N0} moves failed across {session.BlockedFiles.Count:N0} files; other eligible files were processed. " : "")
                    + (state == JobState.Partial ? "Budget, eligibility, scan, or placement constraints leave a partial result." : "Eligible optimization complete.");

            VolumeLayout? ScanVolume()
            {
                try { return volume.Scan(request, ScanProgress, Checkpoint, token, p => scanWork = p); }
                catch (Exception e) when (external && volume.FileSystem.UsesDirectoryScan &&
                    e is IOException or Win32Exception or UnauthorizedAccessException)
                {
                    AddWarnings([$"{volume.Info.FileSystem} allocation analysis unavailable: {e.Message}. Windows maintenance can run independently of the allocation scan."]);
                    return null;
                }
            }
        }
        catch (OperationCanceledException e) { state = JobState.Cancelled; message = string.IsNullOrEmpty(e.Message) ? "Cancelled at a safe boundary" : e.Message; }
        catch (TimeoutException) { state = JobState.Partial; message = "Execution time budget reached"; }
        catch (Exception e)
        {
            state = JobState.Failed; message = e.Message;
            try { File.WriteAllText(Path.Combine(store.JobDirectory(request.Id), "error.txt"), e.ToString()); } catch (IOException) { }
        }
        finally
        {
            // Pipelines drain before returning, including cancellation and failure paths.
            if (scanWork != null) scanWork = scanWork with { ActiveWorkers = 0, InFlightIo = 0 };
            if (planningWork != null) planningWork = planningWork with { ActiveWorkers = 0, InFlightIo = 0 };
            if (executionWork != null) executionWork = executionWork with { ActiveWorkers = 0, InFlightIo = 0 };
            SaveLayoutIndex(); Publish(true);
        }

        void ScanProgress(double p, long count, string text) { progress = p; scannedRecords = count; message = $"{text} · {count:N0} records"; Publish(); }
        void Publish(bool force = false)
        {
            if (!force && clock.ElapsedMilliseconds - lastPublish < 200) return;
            var files = layout?.Files;
            using var process = Process.GetCurrentProcess();
            var diagnostics = new JobDiagnostics(scanWork, planningWork, executionWork, Environment.ProcessorCount,
                process.Threads.Count, process.PrivateMemorySize64, process.TotalProcessorTime.TotalMilliseconds,
                System.Runtime.Intrinsics.X86.Avx2.IsSupported ? "AVX2 for aligned map ranges of at least 64 bytes; scalar tails" : "POPCNT / scalar bitmap counting");
            store.Save(new(request.Id, request.Volume, request.Operation, state, message, progress, moved,
                state == JobState.Scanning ? scannedRecords : layout?.RecordsScanned ?? scannedRecords, files?.Count(f => f.Fragmented) ?? 0, files?.Length ?? 0, DateTimeOffset.UtcNow,
                layout?.ObservedAt, layout == null ? null : map, files?.Where(f => f.Fragmented).OrderByDescending(f => f.Extents.Length).Take(100)
                    .Select(f => new FileSummary(f.Path, f.StreamName, f.Extents.Length, f.Size, f.Movable ? "Eligible" : f.Flags.ToString())).ToArray(),
                warnings.ToArray(), plannedBytes, layout?.Volume.SizeBytes ?? volumeInfo?.SizeBytes ?? 0, layout?.Volume.FreeBytes ?? volumeInfo?.FreeBytes ?? 0,
                request.Resources.CpuPercent, request.Resources.MemoryMiB, request.Resources.IoMiBPerSecond,
                plannedMoves, attemptedMoves, verifiedMoves, failedMoves, filesConsidered, filesBlocked,
                initialFragmentedFiles, clock.ElapsedMilliseconds, BrokerProtocol.BuildVersion, diagnostics,
                request.MinimumFragments, streamsAtThreshold, eligibleStreamsAtThreshold, mftExtents, fragmentedDirectoryIndexes,
                directoryIndexesAtThreshold, layout?.TotalClusters ?? 0));
            lastPublish = clock.ElapsedMilliseconds;
        }
        void UpdateConditionMetrics()
        {
            var files = layout?.Files;
            streamsAtThreshold = files?.Count(f => f.Fragmented && f.Extents.Length >= request.MinimumFragments) ?? 0;
            eligibleStreamsAtThreshold = files?.Count(f => f.Fragmented && f.Movable && f.Extents.Length >= request.MinimumFragments &&
                (f.Flags & (StreamFlags.Metadata | StreamFlags.Directory)) == 0) ?? 0;
            mftExtents = FileSystemCapabilities.Supports(volumeInfo?.FileSystem ?? "", Operation.OptimizeMft)
                ? files?.FirstOrDefault(f => (f.FileId & 0xFFFFFFFFFFFF) == 0 && f.StreamName.Length == 0)?.Extents.Length ?? 0 : 0;
            fragmentedDirectoryIndexes = files?.Count(f => f.Fragmented && f.StreamName.EndsWith(":$INDEX_ALLOCATION", StringComparison.Ordinal)) ?? 0;
            directoryIndexesAtThreshold = files?.Count(f => f.Extents.Length >= request.MinimumFragments && f.StreamName.EndsWith(":$INDEX_ALLOCATION", StringComparison.Ordinal)) ?? 0;
        }
        void Checkpoint()
        {
            JobState previous = state;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (request.MaxMinutes > 0 && clock.Elapsed.TotalMinutes >= request.MaxMinutes) throw new TimeoutException();
                string? control = store.ReadControl(request.Id);
                if (control == "cancel") throw new OperationCanceledException("Cancelled at a safe operation boundary");
                bool idle = ActivityGate.MayRun(request.Resources, out string gateReason);
                if (control != "pause" && idle) { state = previous; return; }
                state = control == "pause" ? JobState.Paused : JobState.WaitingForIdle;
                message = control == "pause" ? "Paused · resume to continue" : gateReason;
                Publish(); Thread.Sleep(250);
            }
        }
        void ExternalCheckpoint()
        {
            token.ThrowIfCancellationRequested();
            if (request.MaxMinutes > 0 && clock.Elapsed.TotalMinutes >= request.MaxMinutes) throw new TimeoutException();
            if (store.ReadControl(request.Id) is "pause" or "cancel") throw new OperationCanceledException("External optimization stopped. Submit a new job to resume after reanalysis.");
        }
        void Pace(long bytes)
        {
            if (request.Resources.IoMiBPerSecond == 0) return;
            pacedBytes += bytes;
            double due = pacedBytes / (request.Resources.IoMiBPerSecond * 1024d * 1024);
            while (pacing.Elapsed.TotalSeconds < due) { Checkpoint(); Thread.Sleep((int)Math.Clamp((due - pacing.Elapsed.TotalSeconds) * 1000, 1, 100)); }
        }
        void ReleaseLayoutForRescan()
        {
            // A full layout may consume most of the configured process budget. It must not
            // remain live while the post-operation scan constructs its replacement.
            layout = null;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        void AddWarnings(IEnumerable<string> additional)
        {
            foreach (string warning in additional)
                if (!warnings.Contains(warning, StringComparer.Ordinal)) warnings.Add(warning);
        }
        void SaveLayoutIndex()
        {
            if (!layoutIndexDirty || layout == null) return;
            try { explorer.Save(request.Id, layout); layoutIndexDirty = false; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                string warning = $"Detailed map index unavailable: {e.Message}";
                if (!warnings.Contains(warning, StringComparer.Ordinal)) warnings.Add(warning);
            }
        }
    }
}
