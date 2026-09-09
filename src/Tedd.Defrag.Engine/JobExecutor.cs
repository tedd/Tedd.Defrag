using System.Buffers.Binary;
using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Tedd.Defrag.Core;
using Tedd.Defrag.Maintenance;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Planning;
using Tedd.Defrag.Visualization;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Engine;

public sealed class JobExecutor(JobStore store)
{
    public void Run(JobRequest request, CancellationToken token)
    {
        request.Validate();
        var clock = Stopwatch.StartNew(); long moved = 0, plannedBytes = 0, lastPublish = -1000;
        long pacedBytes = 0; var pacing = Stopwatch.StartNew();
        VolumeLayout? layout = null; MapCell[] map = new MapCell[request.Resources.MapCells];
        var warnings = new List<string>();
        JobState state = JobState.Scanning; double progress = 0; string message = "Preparing analysis";
        int plannedMoves = 0, attemptedMoves = 0, verifiedMoves = 0, failedMoves = 0;
        int filesConsidered = 0, filesBlocked = 0, initialFragmentedFiles = 0;
        bool noMovesPlanned = false;
        try
        {
            Checkpoint();
            using var volume = new NtfsVolume(request.Volume, !request.Preview && request.Operation != Operation.Analyze);
            if (!request.Preview && request.Operation != Operation.Analyze && volume.IsDirty()) throw new IOException("The NTFS volume is dirty. Resolve filesystem errors before optimization.");
            if (!request.Preview && request.Operation is not (Operation.Analyze or Operation.ReTrim or Operation.SlabConsolidate or Operation.Automatic or Operation.ZeroFreeSpace)
                && volume!.Info.SeekPenalty != true && !request.AllowSsdRelocation)
                throw new InvalidOperationException("Custom relocation on SSD or unknown media requires explicit opt-in.");
            layout = new RawMftScanner().Scan(volume, request, ScanProgress, Checkpoint, token);
            warnings.AddRange(layout.Warnings);
            initialFragmentedFiles = layout.Files.Count(f => f.Fragmented);
            MapAggregator.Build(layout, map); Publish(true);
            if (request.Operation == Operation.Analyze) { state = layout.Complete ? JobState.Completed : JobState.Partial; message = "Analysis complete"; progress = 1; return; }
            if (request.Operation is Operation.Automatic or Operation.ReTrim or Operation.SlabConsolidate or Operation.ZeroFreeSpace)
            {
                if (request.Exclusions.Length != 0 && request.Operation is Operation.Automatic or Operation.SlabConsolidate)
                    throw new NotSupportedException("Volume-wide Windows optimization does not support file exclusions.");
                if (request.Preview) { state = JobState.Completed; progress = 1; message = "Maintenance preview ready; no storage changes submitted"; return; }
                state = JobState.Running; message = "Windows maintenance; progress is indeterminate"; Publish(true);
                if (request.Operation == Operation.ZeroFreeSpace)
                    moved = VirtualDiskPreparation.Zero(request, volume!.Info, b => { Pace(b - moved); moved = b; Publish(); }, Checkpoint, token);
                else WindowsMaintenance.Run(request, volume!.Info, line => { message = line; Publish(); }, ExternalCheckpoint, token);
                ReleaseLayoutForRescan();
                layout = new RawMftScanner().Scan(volume!, request, ScanProgress, Checkpoint, token);
                AddWarnings(layout.Warnings);
                MapAggregator.Build(layout, map); state = JobState.Completed; progress = 1; message = "Maintenance completed; allocation map refreshed"; return;
            }
            var rules = new PathRules(request.SelectedPaths, request.Exclusions);
            bool ordered = request.Operation is Operation.Alphabetical or Operation.Size or Operation.Created or Operation.Modified or Operation.Extension or Operation.DirectoryLocality;
            var blocked = new HashSet<ulong>(); int batches = 0;
            while (request.MaxMoveBytes == 0 || moved < request.MaxMoveBytes)
            {
                Checkpoint(); state = JobState.Planning; message = "Calculating minimum-cost eligible placements"; Publish(true);
                var plan = new LayoutPlanner().Plan(layout, request with { MaxMoveBytes = request.MaxMoveBytes == 0 ? 0 : request.MaxMoveBytes - moved }, token);
                long batchPlannedBytes = plan.ClustersToMove * layout.Volume.BytesPerCluster;
                filesConsidered = Math.Max(filesConsidered, plan.FilesConsidered);
                filesBlocked = Math.Max(filesBlocked, plan.FilesBlocked);
                if (plan.Moves.Length == 0) { noMovesPlanned = plannedMoves == 0; break; }
                plannedBytes += batchPlannedBytes; plannedMoves += plan.Moves.Length;
                if (request.Preview)
                {
                    foreach (var move in plan.Moves) MapAggregator.Activity(map, layout.TotalClusters, move.DestinationLcn, move.Clusters, false);
                    state = JobState.Completed; progress = 1;
                    message = $"Preview: {plan.Moves.Length:N0} moves · {Format.Bytes(batchPlannedBytes)} · {plan.FilesBlocked:N0} constrained files. No writes performed."; return;
                }
                state = JobState.Running; int completed = 0;
                foreach (var move in plan.Moves)
                {
                    Checkpoint(); var file = layout.Files[move.FileIndex];
                    if (blocked.Contains(file.FileId)) continue;
                    message = file.Path; progress = (double)completed++ / plan.Moves.Length;
                    MapAggregator.Activity(map, layout.TotalClusters, move.SourceLcn, move.Clusters, false);
                    Publish();
                    attemptedMoves++;
                    store.Journal(request.Id, new(DateTimeOffset.UtcNow, "intent", move));
                    try
                    {
                        ExecuteMove(volume, file, move, rules);
                        // Native success has been verified by retrieval pointers before changing the display model.
                        LayoutMutation.Apply(layout, move);
                        long bytes = move.Clusters * layout.Volume.BytesPerCluster;
                        moved += bytes;
                        verifiedMoves++;
                        store.Journal(request.Id, new(DateTimeOffset.UtcNow, "verified", move));
                        if (clock.ElapsedMilliseconds - lastPublish >= 200) MapAggregator.Build(layout, map);
                        MapAggregator.Activity(map, layout.TotalClusters, move.DestinationLcn, move.Clusters, true);
                        Publish(); Pace(bytes);
                    }
                    catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
                    {
                        failedMoves++; blocked.Add(file.FileId);
                        layout.Files[move.FileIndex] = file with { Flags = file.Flags | StreamFlags.Excluded };
                        store.Journal(request.Id, new(DateTimeOffset.UtcNow, "reconcile-required", move, e.Message));
                        if (warnings.Count < 50) warnings.Add($"{file.Path}: {e.Message}");
                    }
                }
                batches++;
                if (ordered || failedMoves > 0 || batches >= 64) break;
            }
            state = JobState.Scanning; message = "Reconciling actual allocation after execution"; Publish(true);
            ReleaseLayoutForRescan();
            layout = new RawMftScanner().Scan(volume, request, ScanProgress, Checkpoint, token);
            AddWarnings(layout.Warnings);
            MapAggregator.Build(layout, map);
            bool remainingFragmentation = layout.Files.Any(f => f.Fragmented && f.Movable && rules.IsSelected(f.Path) && !rules.IsExcluded(f.Path)
                && f.Size >= request.MinimumFileBytes && (request.MaximumFileBytes == 0 || f.Size <= request.MaximumFileBytes)
                && (request.Operation is not (Operation.MinimumWrite or Operation.FilesOnly) || f.Extents.Length >= request.MinimumFragments));
            bool budgetReached = request.MaxMoveBytes > 0 && moved >= request.MaxMoveBytes;
            state = failedMoves > 0 || filesBlocked > 0 || !layout.Complete || budgetReached || ordered || remainingFragmentation ? JobState.Partial : JobState.Completed;
            progress = 1;
            message = noMovesPlanned
                ? filesConsidered == 0
                    ? "No files met the selected path, size, and fragmentation thresholds; no disk changes were required."
                    : filesBlocked > 0
                    ? $"No relocations were possible: {filesBlocked:N0} of {filesConsidered:N0} considered files were constrained or ineligible."
                    : $"No eligible extents required relocation among {filesConsidered:N0} considered files."
                : $"{Format.Bytes(moved)} relocated and verified in {verifiedMoves:N0} moves. {(state == JobState.Partial ? "Budget, eligibility, scan, or placement constraints may leave work remaining." : "Eligible optimization complete.")}";
        }
        catch (OperationCanceledException e) { state = JobState.Cancelled; message = string.IsNullOrEmpty(e.Message) ? "Cancelled at a safe boundary" : e.Message; }
        catch (TimeoutException) { state = JobState.Partial; message = "Execution time budget reached"; }
        catch (Exception e)
        {
            state = JobState.Failed; message = e.Message;
            try { File.WriteAllText(Path.Combine(store.JobDirectory(request.Id), "error.txt"), e.ToString()); } catch (IOException) { }
        }
        finally { Publish(true); }

        void ScanProgress(double p, long count, string text) { progress = p; message = $"{text} · {count:N0} records"; Publish(); }
        void Publish(bool force = false)
        {
            if (!force && clock.ElapsedMilliseconds - lastPublish < 200) return;
            var files = layout?.Files;
            store.Save(new(request.Id, request.Volume, request.Operation, state, message, progress, moved,
                layout?.RecordsScanned ?? 0, files?.Count(f => f.Fragmented) ?? 0, files?.Length ?? 0, DateTimeOffset.UtcNow,
                layout?.ObservedAt, layout == null ? null : map, files?.Where(f => f.Fragmented).OrderByDescending(f => f.Extents.Length).Take(100)
                    .Select(f => new FileSummary(f.Path, f.StreamName, f.Extents.Length, f.Size, f.Movable ? "Eligible" : f.Flags.ToString())).ToArray(),
                warnings.ToArray(), plannedBytes, layout?.Volume.SizeBytes ?? 0, layout?.Volume.FreeBytes ?? 0,
                request.Resources.CpuPercent, request.Resources.MemoryMiB, request.Resources.IoMiBPerSecond,
                plannedMoves, attemptedMoves, verifiedMoves, failedMoves, filesConsidered, filesBlocked,
                initialFragmentedFiles, clock.ElapsedMilliseconds));
            lastPublish = clock.ElapsedMilliseconds;
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
    }
    private static void ExecuteMove(NtfsVolume volume, FileLayout file, PlannedMove move, PathRules rules)
    {
        using var handle = file.StreamName.Length == 0 ? volume.OpenById(file.FileId) : NativeIo.Open(file.Path + file.StreamName);
        var identity = NtfsVolume.Identity(handle);
        string path = identity.Path;
        if (file.StreamName.Length > 0 && path.EndsWith(file.StreamName, StringComparison.OrdinalIgnoreCase)) path = path[..^file.StreamName.Length];
        if (identity.Id != file.FileId || identity.Links > 1 || !path.StartsWith(volume.Info.Root, StringComparison.OrdinalIgnoreCase) ||
            !rules.IsSelected(path) || rules.IsExcluded(path) || (identity.Attributes & (0x400u | 0x800u | 0x200u | 0x4000u)) != 0)
            throw new IOException("Current identity, attributes, selection or exclusions prohibit this move.");
        if (!LayoutMutation.Matches(NtfsVolume.RetrievalPointers(handle), move.Vcn, move.SourceLcn, move.Clusters)) throw new IOException("Source extent changed since planning.");
        volume.Move(handle, move);
        if (!LayoutMutation.Matches(NtfsVolume.RetrievalPointers(handle), move.Vcn, move.DestinationLcn, move.Clusters)) throw new IOException("Move result could not be verified; fresh analysis required.");
    }
}
