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
        int failed = 0;
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
                layout = new RawMftScanner().Scan(volume!, request, ScanProgress, Checkpoint, token);
                MapAggregator.Build(layout, map); state = JobState.Completed; progress = 1; message = "Maintenance completed; allocation map refreshed"; return;
            }
            var rules = new PathRules(request.SelectedPaths, request.Exclusions);
            bool ordered = request.Operation is Operation.Alphabetical or Operation.Size or Operation.Created or Operation.Modified or Operation.Extension or Operation.DirectoryLocality;
            var blocked = new HashSet<ulong>(); int batches = 0;
            while (moved < request.MaxMoveBytes)
            {
                Checkpoint(); state = JobState.Planning; message = "Calculating minimum-cost eligible placements"; Publish(true);
                var plan = new LayoutPlanner().Plan(layout, request with { MaxMoveBytes = request.MaxMoveBytes - moved }, token);
                plannedBytes = plan.ClustersToMove * layout.Volume.BytesPerCluster;
                if (plan.Moves.Length == 0) { failed += plan.FilesBlocked; break; }
                if (request.Preview)
                {
                    foreach (var move in plan.Moves) MapAggregator.Activity(map, layout.TotalClusters, move.DestinationLcn, move.Clusters, false);
                    state = JobState.Completed; progress = 1;
                    message = $"Preview: {plan.Moves.Length:N0} moves · {Format.Bytes(plannedBytes)} · {plan.FilesBlocked:N0} constrained files. No writes performed."; return;
                }
                state = JobState.Running; int completed = 0;
                foreach (var move in plan.Moves)
                {
                    Checkpoint(); var file = layout.Files[move.FileIndex];
                    if (blocked.Contains(file.FileId)) continue;
                    message = file.Path; progress = (double)completed++ / plan.Moves.Length;
                    MapAggregator.Activity(map, layout.TotalClusters, move.SourceLcn, move.Clusters, false);
                    Publish();
                    store.Journal(request.Id, new(DateTimeOffset.UtcNow, "intent", move));
                    try
                    {
                        ExecuteMove(volume, file, move, rules);
                        // Native success has been verified by retrieval pointers before changing the display model.
                        LayoutMutation.Apply(layout, move);
                        long bytes = move.Clusters * layout.Volume.BytesPerCluster;
                        moved += bytes;
                        store.Journal(request.Id, new(DateTimeOffset.UtcNow, "verified", move));
                        if (clock.ElapsedMilliseconds - lastPublish >= 200) MapAggregator.Build(layout, map);
                        MapAggregator.Activity(map, layout.TotalClusters, move.DestinationLcn, move.Clusters, true);
                        Publish(); Pace(bytes);
                    }
                    catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
                    {
                        failed++; blocked.Add(file.FileId);
                        layout.Files[move.FileIndex] = file with { Flags = file.Flags | StreamFlags.Excluded };
                        store.Journal(request.Id, new(DateTimeOffset.UtcNow, "reconcile-required", move, e.Message));
                        if (warnings.Count < 50) warnings.Add($"{file.Path}: {e.Message}");
                    }
                }
                batches++;
                if (ordered || failed > 0 || batches >= 64) break;
            }
            state = JobState.Scanning; message = "Reconciling actual allocation after execution"; Publish(true);
            layout = new RawMftScanner().Scan(volume, request, ScanProgress, Checkpoint, token);
            MapAggregator.Build(layout, map);
            bool remainingFragmentation = layout.Files.Any(f => f.Fragmented && rules.IsSelected(f.Path));
            state = failed > 0 || !layout.Complete || moved >= request.MaxMoveBytes || ordered || remainingFragmentation ? JobState.Partial : JobState.Completed;
            progress = 1; message = $"{Format.Bytes(moved)} relocated and verified. {(state == JobState.Partial ? "Budget or placement constraints may leave work remaining." : "Eligible optimization complete.")}";
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
                request.Resources.CpuPercent, request.Resources.MemoryMiB, request.Resources.IoMiBPerSecond));
            lastPublish = clock.ElapsedMilliseconds;
        }
        void Checkpoint()
        {
            JobState previous = state;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed.TotalMinutes >= request.MaxMinutes) throw new TimeoutException();
                string? control = store.ReadControl(request.Id);
                if (control == "cancel") throw new OperationCanceledException("Cancelled at a safe operation boundary");
                bool idle = ActivityGate.MayRun(request.Resources, out _);
                if (control != "pause" && idle) { state = previous; return; }
                state = control == "pause" ? JobState.Paused : JobState.WaitingForIdle;
                message = control == "pause" ? "Paused · resume to continue" : "Waiting for user inactivity / AC power";
                Publish(); Thread.Sleep(250);
            }
        }
        void ExternalCheckpoint()
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed.TotalMinutes >= request.MaxMinutes) throw new TimeoutException();
            if (store.ReadControl(request.Id) is "pause" or "cancel") throw new OperationCanceledException("External optimization stopped. Submit a new job to resume after reanalysis.");
        }
        void Pace(long bytes)
        {
            pacedBytes += bytes;
            double due = pacedBytes / (request.Resources.IoMiBPerSecond * 1024d * 1024);
            while (pacing.Elapsed.TotalSeconds < due) { Checkpoint(); Thread.Sleep((int)Math.Clamp((due - pacing.Elapsed.TotalSeconds) * 1000, 1, 100)); }
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
