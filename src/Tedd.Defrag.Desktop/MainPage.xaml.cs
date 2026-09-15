using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Tedd.Defrag.Client;
using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Update;
using Tedd.Defrag.Visualization;
using Tedd.Defrag.Windows;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinUIElement = Microsoft.UI.Xaml.UIElement;
using WinUIWindow = Microsoft.UI.Xaml.Window;

namespace Tedd.Defrag.Desktop;

public partial class MainPage : ContentPage
{
    private readonly DefragClient _client = new();
    private readonly DiskMapDrawable _map = new();
    private readonly MapOverviewDrawable _overview = new();
    private readonly Dictionary<string, VolumeSession> _volumeSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<RuleEditorItem> _fileRules = [];
    private readonly ObservableCollection<RuleEditorItem> _exclusionRules = [];
    private VolumeInfo[] _volumes = [];
    private VolumeInfo? _volume;
    private bool _polling, _refreshing, _submitting;
    private bool _settingTheme;
    private bool _selectingRegion, _mapDragged;
    private int _mapGestureStart = -1, _mapGestureCurrent = -1, _regionRequest;
    private long _mapGestureViewportStart, _pendingPanStart;
    private WinUIElement? _mapPlatformView;
    private ulong? _selectedMapFileId;
    private string? _selectedMapPath;
    private string _selectedMapStream = "";
    private MapFileSelection? _selectedMapFile;
    private ClusterHitRow? _selectedClusterHit;
    private readonly IDispatcherTimer _timer;
    private TaskCompletionSource<Operation?>? _policyChoice;
    private Operation[] _recommendedOperations = [];
    private static readonly PolicyOption[] Policies =
    [
        new("↯", "Minimum-write defrag", "Prioritizes heavily fragmented files and preserves their first extent when possible. Honors file scope and exclusions.", Operation.MinimumWrite),
        new("✦", "Windows automatic", "Lets Windows select supported whole-volume maintenance for the detected media and current volume state.", Operation.Automatic),
        new("↻", "Windows defrag", "Asks Windows to defragment the whole volume where supported. File scope, exclusions, custom fragment thresholds and write budgets are unsupported.", Operation.WindowsDefrag),
        new("▰", "Defragment files", "Makes eligible fragmented files contiguous without trying to reorganize the whole volume.", Operation.FilesOnly),
        new("⇤", "Pack + defragment", "Moves eligible files toward lower addresses while consolidating their extents. Higher write volume.", Operation.PackAndDefrag),
        new("≪", "Pack toward beginning", "Consolidates free space by moving allocated extents toward the start of the volume.", Operation.Pack),
        new("⌁", "ReTRIM", "Resends deallocation hints to supported SSDs and thin-provisioned storage; no file relocation.", Operation.ReTrim),
        new("⌂", "Directory locality", "Places files from the same directory near each other. Useful for directory-oriented access patterns.", Operation.DirectoryLocality),
        new("◷", "Order by modification time", "Places older and newer content in modification-time order across the volume.", Operation.Modified),
        new(".x", "Order by extension", "Groups files by extension, then by path. This rewrites layout for a specialized access pattern.", Operation.Extension),
        new("A↓", "Alphabetical layout", "Places files in path order. Predictable, but potentially write-intensive.", Operation.Alphabetical),
        new("▥", "Order by size", "Places files from smallest to largest. Specialized and potentially write-intensive.", Operation.Size),
        new("◴", "Order by creation time", "Places files in creation-time order. Specialized and potentially write-intensive.", Operation.Created),
        new("⇥", "Prepare for shrink", "Moves extents below a required boundary so the partition can be reduced.", Operation.PrepareShrink),
        new("▦", "Slab consolidation", "Asks Windows to consolidate slabs on supported thin-provisioned storage.", Operation.SlabConsolidate),
        new("M", "Optimize movable MFT", "Attempts to relocate movable MFT data. Advanced NTFS maintenance.", Operation.OptimizeMft),
        new("D", "Directory indexes", "Optimizes movable NTFS directory index streams.", Operation.DirectoryIndexes),
        new("0", "Prepare virtual disk · zero", "Fills available guest free space with zeros for a later host-side compact operation. Very write-intensive.", Operation.ZeroFreeSpace)
    ];
    private PolicyOption _selectedPolicy = Policies[0];
    public MainPage()
    {
        InitializeComponent(); DiskMap.Drawable = _map; MapOverview.Drawable = _overview;
        DiskMap.HandlerChanged += OnDiskMapHandlerChanged;
        PolicyList.ItemsSource = Policies; UpdateSelectedPolicy();
        ThemePicker.ItemsSource = new[] { "Follow Windows", "Light", "Dark" };
        _settingTheme = true;
        ThemePicker.SelectedItem = (Application.Current as App)?.ThemePreference switch
        {
            "Light" => "Light",
            "Dark" => "Dark",
            _ => "Follow Windows"
        };
        _settingTheme = false;
        ResourcePreset.ItemsSource = new[] { "Quiet · bounded maintenance", "Balanced · responsive", "Performance · full speed" }; ResourcePreset.SelectedIndex = 2;
        FileRuleKind.ItemsSource = ExclusionRuleKind.ItemsSource = new[] { "Path", "Wildcard", "Regular expression" };
        FileRuleKind.SelectedIndex = ExclusionRuleKind.SelectedIndex = 0;
        _timer = Dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(250); _timer.Tick += async (_, _) => await Poll();
        Loaded += async (_, _) => { _timer.Start(); await RefreshVolumes(); await AttachActiveJobs(); await CheckForUpdate(); };
        Unloaded += (_, _) => _timer.Stop();
    }
    internal void Shutdown()
    {
        _timer.Stop();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task.Run(() => _client.ShutdownWorker(timeout.Token)).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }
    private async Task CheckForUpdate()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            AvailableRelease? release = await ReleaseUpdater.CheckForUpdateAsync(timeout.Token);
            if (release is null) return;
            string action = release.PackageKind == ReleasePackageKind.Installer
                ? "run the verified installer, and restart"
                : "replace this portable copy and restart";
            bool install = await DisplayAlertAsync("Update available",
                $"Tedd.Defrag {release.DisplayVersion} is available. Download the release, verify its SHA-256 checksum, {action}?",
                "Download and restart", "Later");
            if (!install) return;
            try { await _client.Send(new("stop")); }
            catch (TimeoutException) { }
            catch (InvalidOperationException exception)
            {
                await DisplayAlertAsync("Update postponed", exception.Message, "Close");
                return;
            }
            FooterStatus.Text = $"Downloading and verifying Tedd.Defrag {release.DisplayVersion}…";
            await ReleaseUpdater.LaunchUpdateAsync(release, "Tedd.Defrag.Desktop.exe", []);
            Environment.Exit(0);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            FooterStatus.Text = "Update check unavailable · the application remains ready";
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }
    private async Task RefreshVolumes()
    {
        if (_refreshing || _submitting) return;
        _refreshing = true;
        try
        {
            var volumes = await Task.Run(VolumeDiscovery.List);
            _volumes = volumes;
            VolumesPanel.Children.Clear();
            foreach (var volume in volumes) AddVolume(volume);
            if (_volume != null && volumes.FirstOrDefault(v => v.Id == _volume.Id) is { } current)
                _volume = current;
            else
            {
                string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
                SelectVolume(volumes.FirstOrDefault(v => v.Root.Equals(systemRoot, StringComparison.OrdinalIgnoreCase) && FileSystemCapabilities.IsSupported(v.FileSystem))
                    ?? volumes.FirstOrDefault(v => FileSystemCapabilities.IsSupported(v.FileSystem)) ?? volumes.FirstOrDefault());
            }
            if (volumes.Length == 0)
            {
                var empty = new Label { Text = "No available volumes", FontSize = 11 };
                empty.SetDynamicResource(Label.TextColorProperty, "SubtleText");
                VolumesPanel.Children.Add(empty);
            }
            UpdateVolumeActions();
        }
        catch (Exception e) { FooterStatus.Text = e.Message; }
        finally { _refreshing = false; }
    }
    private async Task AttachActiveJobs()
    {
        try
        {
            var jobs = await _client.GetActiveJobs();
            VolumeInfo? focus = null;
            foreach (var snapshot in jobs
                .GroupBy(job => VolumeKey(job.Volume), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(job => job.State == JobState.Queued).ThenByDescending(job => job.UpdatedAt).First())
                .OrderBy(job => job.State == JobState.Queued).ThenByDescending(job => job.UpdatedAt))
            {
                var volume = _volumes.FirstOrDefault(candidate =>
                    VolumeKey(candidate.Root).Equals(VolumeKey(snapshot.Volume), StringComparison.OrdinalIgnoreCase));
                if (volume == null) continue;
                var session = SessionFor(volume);
                session.JobId = snapshot.Id;
                session.AttachedExistingJob = true;
                Apply(snapshot, session);
                focus ??= volume;
            }
            if (focus != null) SelectVolume(focus);
        }
        catch (TimeoutException)
        {
            // No broker is running, so persisted nonterminal snapshots are stale
            // until a worker explicitly resumes or terminates them.
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            FooterStatus.Text = $"Active-job synchronization unavailable · {exception.Message}";
        }
    }
    private void AddVolume(VolumeInfo volume)
    {
        var button = new Button { Text = $"▣  {volume.Root[..2]}   {volume.Label}", FontSize = 12, Padding = new Thickness(10, 12),
            HorizontalOptions = LayoutOptions.Fill, BindingContext = volume.Id };
        button.SetDynamicResource(Button.BackgroundColorProperty, "ButtonSurface");
        button.Clicked += (_, _) => SelectVolume(volume); VolumesPanel.Children.Add(button);
    }
    private void SelectVolume(VolumeInfo? volume)
    {
        _volume = volume; _map.SetRegion(0, 0, 0, []);
        PolicyList.ItemsSource = Policies.Where(p => volume != null && FileSystemCapabilities.Supports(volume.FileSystem, p.Operation)).ToArray();
        if (volume != null && !FileSystemCapabilities.Supports(volume.FileSystem, _selectedPolicy.Operation))
        {
            _selectedPolicy = Policy(Operation.Automatic); UpdateSelectedPolicy();
        }
        _overview.Cells = []; _overview.TotalClusters = _overview.StartCluster = _overview.ClusterCount = 0;
        ClearFileHighlight(); ClusterOverlay.IsVisible = false; DiagnosticsOverlay.IsVisible = false;
        _map.EmptyMessage = volume == null ? "Connect a volume and refresh to begin." :
            FileSystemCapabilities.IsSupported(volume.FileSystem) ? "Analyze this volume to reveal its allocation map." : "Select an NTFS, ReFS, FAT or FAT32 volume to analyze its allocation.";
        DiskMap.Invalidate(); MapOverview.Invalidate();
        ModeBadge.Text = volume?.FileSystem.ToUpperInvariant() ?? "NO VOLUME";
        VolumeTitle.Text = volume == null ? "No volume selected" : $"{volume.Root[..2]}  {volume.Label}";
        MapSubtitle.Text = volume == null ? "Connect a volume and refresh to begin." : $"{volume.FileSystem} · {volume.BytesPerCluster:N0}-byte clusters · awaiting analysis";
        TopologyText.Text = volume == null ? "" : string.Join(", ", volume.Resources) + "\n" + volume.TopologyConfidence;
        FreeMetric.Text = volume == null ? "—" : Format.Bytes(volume.FreeBytes);
        CapacityDetail.Text = volume == null ? "Select a volume" : $"of {Format.Bytes(volume.SizeBytes)} capacity";
        FragmentMetric.Text = "—"; StreamDetail.Text = "Awaiting analysis"; MovedMetric.Text = "0 B";
        FileList.ItemsSource = null; JobTitle.Text = "Ready when you are"; JobMessage.Text = "Analyze this volume before planning changes.";
        JobProgress.Progress = 0; ProgressText.Text = "READY"; WarningsText.Text = ""; PauseButton.IsEnabled = CancelButton.IsEnabled = false;
        ObservationText.Text = "Awaiting analysis"; BudgetDetail.Text = "No relocation limit";
        CellDetail.Text = "Hover to inspect a cluster range. Click for files; drag to pan."; UpdateRange();
        RenderRecommendation(null);
        FooterStatus.Text = volume == null ? "●  No available volumes · refresh to try again" : $"●  {volume.Root} selected · administrator access active";
        if (volume != null && !FileSystemCapabilities.IsSupported(volume.FileSystem))
            JobMessage.Text = "Analysis and optimization require an NTFS, ReFS, FAT or FAT32 volume.";
        if (volume != null && _volumeSessions.TryGetValue(volume.Id, out var session) && session.Snapshot != null)
        {
            Render(session);
            PresentCompletionIfNeeded(session);
        }
        UpdateVolumeActions();
        _ = Poll();
    }
    private void UpdateVolumeActions()
    {
        var session = CurrentSession;
        bool jobActive = HasActiveJob(session);
        AnalyzeButton.IsEnabled = !_submitting && !jobActive && _volume != null && FileSystemCapabilities.IsSupported(_volume.FileSystem);
        PreviewButton.IsEnabled = OptimizeButton.IsEnabled = AnalyzeButton.IsEnabled && FileSystemCapabilities.Supports(_volume!.FileSystem, _selectedPolicy.Operation);
        RecommendationSteps.IsEnabled = AnalyzeButton.IsEnabled;
        RecommendationButton.IsEnabled = RecommendationSteps.IsEnabled && CurrentSession?.LayoutSnapshot == null;
        foreach (var button in VolumesPanel.Children.OfType<Button>())
        {
            button.IsEnabled = !_submitting;
            button.SetDynamicResource(Button.BackgroundColorProperty, Equals(button.BindingContext, _volume?.Id) ? "NavSelected" : "ButtonSurface");
        }
    }
    private JobRequest Request(Operation operation, bool preview)
    {
        var volume = _volume ?? throw new InvalidOperationException("Select an available NTFS, ReFS, FAT or FAT32 volume first.");
        FileSystemCapabilities.Validate(volume.FileSystem, operation);
        var request = new JobRequest { Volume = volume.Root, Operation = operation, Preview = preview,
            SelectedPaths = _fileRules.Select(item => item.Rule).ToArray(),
            Exclusions = _exclusionRules.Select(item => item.Rule).ToArray(),
            Resources = new() { CpuPercent = (int)CpuSlider.Value, MemoryMiB = ParseInt(MemoryEntry.Text, "Memory cap"), IoMiBPerSecond = ParseInt(IoEntry.Text, "Relocation bandwidth"),
                ScanWorkers = ParseInt(ScanWorkersEntry.Text, "MFT workers"), PlanningWorkers = ParseInt(PlanningWorkersEntry.Text, "Planner workers"),
                MoveQueueDepth = ParseInt(MoveQueueEntry.Text, "Move queue depth", ResourcePolicy.Performance.MoveQueueDepth),
                AffinityMask = string.IsNullOrWhiteSpace(AffinityEntry.Text) ? 0 : Convert.ToUInt64(AffinityEntry.Text.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16),
                IdleOnly = IdleSwitch.IsToggled, AcOnly = AcSwitch.IsToggled, Background = BackgroundSwitch.IsToggled },
            MaxMoveBytes = ParseMiB(BudgetEntry.Text, "Write budget"), MaxMinutes = ParseInt(MinutesEntry.Text, "Time limit"),
            MinimumFragments = ParseInt(MinFragmentsEntry.Text, "Minimum fragments", 20),
            MinimumFileBytes = ParseMiB(MinFileSizeEntry.Text, "Minimum file size"), MaximumFileBytes = ParseMiB(MaxFileSizeEntry.Text, "Maximum file size"),
            ShrinkBoundaryBytes = ParseMiB(BoundaryEntry.Text, "Shrink boundary"),
            ConfirmVirtualDiskZeroing = operation == Operation.ZeroFreeSpace && !preview,
            AllowSsdRelocation = !preview // The run confirmation below explicitly discloses SSD/unknown-media relocation.
        };
        request.Validate(); return request;
    }
    private async Task Submit(Operation operation, bool preview)
    {
        if (_submitting) return;
        _submitting = true; UpdateVolumeActions();
        try
        {
            var request = Request(operation, preview);
            if (!preview)
            {
                string details = operation == Operation.ZeroFreeSpace ?
                    "Pre-zeroing is for virtual-disk workflows that require it. It can expand thin storage and consume host capacity; it does not compact the disk itself." :
                    $"Run {_selectedPolicy.Name} on {request.Volume}, with {Limit(request.MaxMoveBytes, Format.Bytes, "no relocation limit")} and {Limit(request.MaxMinutes, n => $"{n}-minute limit", "no time limit")}?";
                if (_volume?.SeekPenalty != true && operation is not (Operation.ReTrim or Operation.SlabConsolidate or Operation.Automatic or Operation.ZeroFreeSpace))
                    details += "\n\nThis is SSD or unknown media. Relocation adds writes. Greater logical contiguity can reduce host I/O requests, but the device controls internal placement. Review the previewed write estimate.";
                if (!await DisplayAlertAsync("Review disk operation", details, "Start job", "Cancel")) return;
            }
            OptimizeButton.IsEnabled = false; FooterStatus.Text = "Connecting to the worker…";
            var session = CurrentSession ?? throw new InvalidOperationException("Select an available NTFS, ReFS, FAT or FAT32 volume first.");
            session.JobId = (await _client.Send(new("submit", Job: request), startBroker: true)).Id;
            session.AttachedExistingJob = false;
            session.AwaitingReport = session.JobId;
            DiagnosticsTitle.Text = "Waiting for worker"; DiagnosticsCount.Text = "Queued";
            DiagnosticsElapsed.Text = "00:00:00"; DiagnosticsProgress.Progress = 0;
            DiagnosticsProcessors.Text = DiagnosticsThreads.Text = DiagnosticsCpu.Text = DiagnosticsMemory.Text = "—";
            DiagnosticsPlanned.Text = DiagnosticsVerified.Text = DiagnosticsFailed.Text = "0"; DiagnosticsRelocated.Text = "0 B";
            RenderPhase(null, ScanStatus, ScanProgress, ScanRate, ScanWorkers, ScanRequests);
            RenderPhase(null, PlanningStatus, PlanningProgress, PlanningRate, PlanningWorkers, PlanningRequests);
            RenderPhase(null, ExecutionStatus, ExecutionProgress, ExecutionRate, ExecutionWorkers, ExecutionRequests, ExecutionBytes);
            DiagnosticsBusy.IsRunning = true; DiagnosticsOverlay.IsVisible = true;
            ResetMapView(); FooterStatus.Text = "●  Job submitted · closing the application stops it"; await Poll();
        }
        catch (Exception e) { await DisplayAlertAsync("Job could not start", e.Message, "Close"); FooterStatus.Text = e.Message; }
        finally { _submitting = false; UpdateVolumeActions(); }
    }
    private async Task Poll()
    {
        var session = CurrentSession;
        if (_polling || session == null || session.JobId == Guid.Empty) return;
        _polling = true;
        try
        {
            Guid id = session.JobId; var reply = await _client.Send(new("get", Id: id));
            if (id == session.JobId && reply.Snapshot != null &&
                (reply.Snapshot.Id != session.Snapshot?.Id || reply.Snapshot.UpdatedAt != session.Snapshot.UpdatedAt ||
                    reply.Snapshot.State != session.Snapshot.State || reply.Snapshot.Message != session.Snapshot.Message))
                Apply(reply.Snapshot, session);
        }
        catch (Exception e) { FooterStatus.Text = e.Message; }
        finally { _polling = false; }
    }
    private void Apply(JobSnapshot snapshot, VolumeSession session)
    {
        session.Snapshot = snapshot;
        if (snapshot.ObservedAt.HasValue && snapshot.TotalBytes > 0) session.LayoutSnapshot = snapshot;
        if (snapshot.Map != null) session.Cells = snapshot.Map;
        if (snapshot.Files != null) session.Files = snapshot.Files.Select(f => new FileRow(f.Path, f.Extents, Format.Bytes(f.Bytes), f.Status)).ToArray();
        if (!ReferenceEquals(session, CurrentSession)) return;
        Render(session);
        PresentCompletionIfNeeded(session);
    }
    private void Render(VolumeSession session)
    {
        var snapshot = session.Snapshot ?? throw new InvalidOperationException("The volume session has no snapshot.");
        var layout = session.LayoutSnapshot;
        // The sampled cells describe allocation, but the worker's volume geometry defines
        // the display bounds. This preserves leading and trailing free volume space.
        long totalClusters = layout?.TotalClusters > 0 ? layout.TotalClusters : session.Cells.Sum(cell => cell.Clusters);
        _overview.Cells = session.Cells; _overview.TotalClusters = totalClusters;
        if (totalClusters > 0 && (_map.TotalClusters != totalClusters || _map.ClusterCount == 0 || _map.ClusterCount == _map.TotalClusters))
            ApplyBaseMap(session, totalClusters);
        else if (totalClusters > 0)
            _ = LoadRegion(_map.StartCluster, _map.ClusterCount);
        DiskMap.Invalidate(); MapOverview.Invalidate(); UpdateRange();
        if (_volume != null && layout?.ObservedAt.HasValue == true)
            MapSubtitle.Text = $"{_volume.FileSystem} · {_volume.BytesPerCluster:N0}-byte clusters · observed {layout.ObservedAt.Value.ToLocalTime():HH:mm:ss}";
        if (layout?.TotalBytes > 0)
        {
            FreeMetric.Text = Format.Bytes(layout.FreeBytes); CapacityDetail.Text = $"of {Format.Bytes(layout.TotalBytes)} capacity";
            FragmentMetric.Text = layout.FragmentedFiles.ToString("N0"); StreamDetail.Text = $"of {layout.TotalFiles:N0} allocated streams";
        }
        MovedMetric.Text = snapshot.BytesMoved == 0 && snapshot.State is JobState.Queued or JobState.Scanning or JobState.Planning ? "Pending" : Format.Bytes(snapshot.BytesMoved);
        BudgetDetail.Text = snapshot.PlannedBytes > 0 ? $"{Format.Bytes(snapshot.PlannedBytes)} planned" : snapshot.State switch
        {
            JobState.Queued or JobState.Scanning => "Relocation begins after analysis",
            JobState.Planning => "Calculating eligible placements",
            JobState.Paused or JobState.WaitingForIdle => snapshot.Message,
            _ => "Relocation payload, not NAND writes"
        };
        JobTitle.Text = snapshot.Operation + " · " + snapshot.State; JobMessage.Text = snapshot.Message;
        JobProgress.Progress = Math.Clamp(snapshot.Progress, 0, 1); ProgressText.Text = snapshot.IsTerminal ? snapshot.State.ToString().ToUpperInvariant() : snapshot.Progress.ToString("P0");
        ObservationText.Text = snapshot.ObservedAt.HasValue ? $"Observed {snapshot.ObservedAt.Value.ToLocalTime():HH:mm:ss} · logical volume allocation" : "Reading the current volume layout";
        PauseButton.IsEnabled = CancelButton.IsEnabled = !snapshot.IsTerminal && session.JobId != Guid.Empty; PauseButton.Text = snapshot.State == JobState.Paused ? "Resume" : "Pause";
        FileList.ItemsSource = session.Files;
        WarningsText.Text = string.Join("\n", snapshot.Warnings ?? []);
        FooterStatus.Text = $"●  {snapshot.Volume} · {snapshot.State} · {(session.AttachedExistingJob ? "attached worker job" : "application worker")}";
        RenderDiagnostics(snapshot);
        RenderRecommendation(layout);
        UpdateVolumeActions();
    }
    private void OnPerformanceDetails(object? sender, EventArgs e)
    {
        if (CurrentSession?.Snapshot is not { } snapshot) return;
        RenderDiagnostics(snapshot); DiagnosticsOverlay.IsVisible = true;
    }
    private void OnClosePerformanceDetails(object? sender, EventArgs e) => DiagnosticsOverlay.IsVisible = false;
    private void RenderDiagnostics(JobSnapshot snapshot)
    {
        var d = snapshot.Diagnostics;
        var current = snapshot.State switch
        {
            JobState.Scanning => d?.Scan,
            JobState.Planning => d?.Planning,
            JobState.Running => d?.Execution,
            _ => null
        };
        DiagnosticsTitle.Text = snapshot.IsTerminal || current == null ? $"{snapshot.Operation} · {snapshot.State}" : current.Phase;
        DiagnosticsBusy.IsRunning = !snapshot.IsTerminal && snapshot.State is not (JobState.Paused or JobState.WaitingForIdle);
        DiagnosticsProgress.Progress = current?.Total > 0 ? Math.Clamp((double)current.Completed / current.Total, 0, 1) : Math.Clamp(snapshot.Progress, 0, 1);
        DiagnosticsCount.Text = current?.Total > 0 ? $"{current.Completed:N0} / {current.Total:N0} {current.Unit} · phase progress" : snapshot.Message;
        DiagnosticsPause.Text = snapshot.State == JobState.Paused ? "Resume" : "Pause";
        DiagnosticsPause.IsEnabled = DiagnosticsCancel.IsEnabled = !snapshot.IsTerminal;
        DiagnosticsElapsed.Text = FormatElapsed(snapshot.ElapsedMilliseconds);
        DiagnosticsProcessors.Text = d?.LogicalProcessors.ToString("N0") ?? "Not reported";
        DiagnosticsThreads.Text = d?.ProcessThreads.ToString("N0") ?? "Not reported";
        DiagnosticsCpu.Text = d == null ? "Not reported" : $"{d.CpuMilliseconds / 1000:N1} s / {snapshot.CpuPercent}%";
        DiagnosticsMemory.Text = d == null ? "Not reported" : Format.Bytes(d.PrivateBytes);
        DiagnosticsPlanned.Text = snapshot.PlannedMoves.ToString("N0");
        DiagnosticsVerified.Text = snapshot.VerifiedMoves.ToString("N0");
        DiagnosticsFailed.Text = snapshot.FailedMoves.ToString("N0");
        DiagnosticsRelocated.Text = Format.Bytes(snapshot.BytesMoved);
        RenderPhase(d?.Scan, ScanStatus, ScanProgress, ScanRate, ScanWorkers, ScanRequests);
        RenderPhase(d?.Planning, PlanningStatus, PlanningProgress, PlanningRate, PlanningWorkers, PlanningRequests);
        RenderPhase(d?.Execution, ExecutionStatus, ExecutionProgress, ExecutionRate, ExecutionWorkers, ExecutionRequests, ExecutionBytes);
    }
    private static void RenderPhase(WorkProgress? progress, Label status, Label completed, Label rate, Label workers, Label requests, Label? bytes = null)
    {
        status.Text = progress?.Phase ?? "Not started";
        completed.Text = progress == null ? "—" : progress.Total > 0 ? $"{progress.Completed:N0} / {progress.Total:N0} {progress.Unit}" : $"{progress.Completed:N0} {progress.Unit}";
        rate.Text = progress == null || progress.ElapsedMilliseconds <= 0 ? "—" : progress.BytesProcessed > 0
            ? $"{Format.Bytes((long)(progress.BytesProcessed * 1000d / progress.ElapsedMilliseconds))}/s"
            : $"{progress.Completed * 1000d / progress.ElapsedMilliseconds:N0} {progress.Unit}/s";
        workers.Text = progress == null ? "—" : $"{progress.ActiveWorkers:N0} / {progress.WorkerLimit:N0} · peak {progress.PeakWorkers:N0}";
        requests.Text = progress == null ? "—" : $"{progress.InFlightIo:N0} active · peak {progress.PeakIo:N0}";
        if (bytes != null) bytes.Text = progress == null || progress.BytesProcessed <= 0 ? "—" : Format.Bytes(progress.BytesProcessed);
    }
    private static string FormatElapsed(long milliseconds)
    {
        var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return elapsed.TotalDays >= 1 ? $"{(int)elapsed.TotalDays}.{elapsed:hh\\:mm\\:ss}" : elapsed.ToString(@"hh\:mm\:ss");
    }
    private void RenderRecommendation(JobSnapshot? snapshot)
    {
        var volume = _volume;
        RecommendationMedia.Text = volume?.SeekPenalty switch
        {
            true when volume.TrimEnabled == true => "HDD · TRIM",
            true => "HDD",
            false when volume.TrimEnabled == true => "SSD · TRIM",
            false => "SSD",
            _ => "UNKNOWN MEDIA"
        };
        int threshold = int.TryParse(MinFragmentsEntry?.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int selectedThreshold) && selectedThreshold >= 2
            ? selectedThreshold : 20;
        RecommendationThreshold.Text = $"{threshold:N0}+ fragments · not measured";
        RecommendationPerformance.Text = "Not measured";
        RecommendationMetadata.Text = "Not measured";
        RecommendationCulprits.Text = "Analyze to identify them.";
        RecommendationButton.Text = "Analyze now";
        RecommendationButton.IsVisible = true;
        if (volume == null)
        {
            SetRecommendedOperations([]);
            RecommendationTitle.Text = "Select a volume";
            RecommendationSummary.Text = "Select an NTFS, ReFS, FAT or FAT32 volume before requesting an analysis.";
            RecommendationFragmentation.Text = "Not measured";
            return;
        }
        if (snapshot?.ObservedAt.HasValue != true || snapshot.TotalBytes <= 0)
        {
            SetRecommendedOperations([]);
            RecommendationTitle.Text = "Analyze this volume";
            RecommendationSummary.Text = $"A scan is required before maintenance can be recommended for this {MediaName(volume).ToLowerInvariant()}.";
            RecommendationFragmentation.Text = "Not measured";
            return;
        }

        double fragmentedRatio = snapshot.TotalFiles > 0 ? (double)snapshot.FragmentedFiles / snapshot.TotalFiles : 0;
        RecommendationFragmentation.Text = $"{fragmentedRatio:P1} · {snapshot.FragmentedFiles:N0} / {snapshot.TotalFiles:N0} streams";
        var thresholdCount = ThresholdCount(snapshot, threshold);
        RecommendationThreshold.Text = $"{threshold:N0}+ fragments · {(thresholdCount.LowerBound ? "at least " : "")}{thresholdCount.Total:N0} streams ({thresholdCount.Eligible:N0} eligible)";
        RecommendationPerformance.Text = ObservedRate(snapshot);
        int mftExtents = snapshot.MftExtents > 0 ? snapshot.MftExtents :
            (snapshot.Files ?? []).FirstOrDefault(file => file.Path.EndsWith("\\$MFT", StringComparison.OrdinalIgnoreCase) && file.Stream.Length == 0)?.Extents ?? 0;
        int fragmentedIndexes = Math.Max(snapshot.FragmentedDirectoryIndexes,
            (snapshot.Files ?? []).Count(file => file.Extents > 1 && file.Stream.EndsWith(":$INDEX_ALLOCATION", StringComparison.Ordinal)));
        string mft = mftExtents == 0 ? "MFT not reported" : $"MFT {mftExtents:N0} extent{(mftExtents == 1 ? "" : "s")}";
        RecommendationMetadata.Text = $"{mft} · {fragmentedIndexes:N0} fragmented directory indexes";
        RecommendationCulprits.Text = Culprits(snapshot.Files);

        var steps = MaintenanceRecommendation.SelectSteps(volume,
            mftExtents, fragmentedIndexes, thresholdCount.Eligible, thresholdCount.Total);
        SetRecommendedOperations(steps);
        RecommendationButton.IsVisible = false;
        if (FileSystemCapabilities.Find(volume.FileSystem) is { UsesDirectoryScan: true } support)
        {
            RecommendationTitle.Text = $"{volume.FileSystem} maintenance through Windows";
            RecommendationMetadata.Text = "Filesystem metadata is outside file coverage";
            RecommendationThreshold.Text = $"{threshold:N0}+ fragments · {thresholdCount.Total:N0} observed streams";
            RecommendationSummary.Text = support.CoverageDescription + " Windows determines which whole-volume operations are supported; custom placement and file filters require NTFS.";
        }
        else if (volume.SeekPenalty == null)
        {
            RecommendationTitle.Text = "Use Windows automatic maintenance";
            RecommendationSummary.Text = "The storage device type is unknown. Windows selects maintenance for the detected media.";
        }
        else if (steps.Any(operation => operation is Operation.OptimizeMft or Operation.DirectoryIndexes or Operation.MinimumWrite))
        {
            RecommendationTitle.Text = volume.SeekPenalty == false ? "Targeted SSD maintenance" : "Targeted HDD maintenance";
            var reasons = new List<string>();
            if (steps.Contains(Operation.OptimizeMft)) reasons.Add($"The MFT occupies {mftExtents:N0} extents.");
            if (steps.Contains(Operation.DirectoryIndexes)) reasons.Add($"{fragmentedIndexes:N0} directory indexes are fragmented.");
            if (steps.Contains(Operation.MinimumWrite)) reasons.Add($"{(thresholdCount.LowerBound ? "At least " : "")}{thresholdCount.Eligible:N0} movable file streams meet the {threshold:N0}-fragment threshold.");
            reasons.Add("Preview each step to review eligible moves and estimated writes.");
            if (steps.Contains(Operation.ReTrim)) reasons.Add("Run ReTRIM after relocation to include newly freed space.");
            RecommendationSummary.Text = string.Join(" ", reasons);
        }
        else if (steps.Contains(Operation.ReTrim))
        {
            RecommendationTitle.Text = "ReTRIM available";
            RecommendationSummary.Text = "ReTRIM sends free-space information to the storage device. No metadata fragmentation or eligible files at the selected threshold were reported.";
        }
        else if (steps.Contains(Operation.Automatic))
        {
            RecommendationTitle.Text = "Use Windows automatic maintenance";
            RecommendationSummary.Text = "TRIM support was not reported. Windows selects the available maintenance for this device.";
        }
        else
        {
            RecommendationTitle.Text = "No targeted maintenance identified";
            RecommendationSummary.Text = $"The scan reports no metadata fragmentation or eligible files with at least {threshold:N0} fragments.";
        }
    }
    private void SetRecommendedOperations(Operation[] operations)
    {
        RecommendationSteps.IsVisible = operations.Length > 0;
        RecommendationStepsLabel.IsVisible = operations.Length > 0;
        if (_recommendedOperations.SequenceEqual(operations)) return;
        _recommendedOperations = operations;
        BindableLayout.SetItemsSource(RecommendationSteps, operations.Select(Policy).ToArray());
    }
    private static (int Total, int Eligible, bool LowerBound) ThresholdCount(JobSnapshot snapshot, int threshold)
    {
        var files = snapshot.Files ?? [];
        if (snapshot.FragmentationThreshold == threshold && (snapshot.StreamsAtOrAboveThreshold > 0 || files.All(file => file.Extents < threshold)))
            return (snapshot.StreamsAtOrAboveThreshold, snapshot.EligibleStreamsAtOrAboveThreshold, false);
        int total = files.Count(file => file.Extents >= threshold);
        int eligible = files.Count(file => file.Extents >= threshold && file.Status == "Eligible" &&
            !file.Path.EndsWith("\\$MFT", StringComparison.OrdinalIgnoreCase) &&
            !file.Stream.EndsWith(":$INDEX_ALLOCATION", StringComparison.Ordinal));
        bool lowerBound = files.Length > 0 && snapshot.FragmentedFiles > files.Length && total == files.Length;
        return (total, eligible, lowerBound);
    }
    private static string ObservedRate(JobSnapshot snapshot)
    {
        WorkProgress? progress = snapshot.Diagnostics?.Execution is { BytesProcessed: > 0 } execution ? execution :
            snapshot.Diagnostics?.Scan is { BytesProcessed: > 0 } scan ? scan : null;
        if (progress == null || progress.ElapsedMilliseconds <= 0) return "Not measured";
        string phase = ReferenceEquals(progress, snapshot.Diagnostics?.Execution) ? "Relocation" : "MFT scan";
        return $"{phase} · {Format.Bytes((long)(progress.BytesProcessed * 1000d / progress.ElapsedMilliseconds))}/s";
    }
    private static string Culprits(FileSummary[]? files)
    {
        if (files == null || files.Length == 0) return "No fragmented streams reported.";
        return string.Join("\n", files.Take(3).Select(file =>
        {
            string path = file.Path + file.Stream;
            if (path.Length > 48) path = "…" + path[^47..];
            return $"{path} · {file.Extents:N0} extents · {Format.Bytes(file.Bytes)}";
        }));
    }
    private static string MediaName(VolumeInfo volume) => volume.SeekPenalty switch { true => "Hard disk", false => "Solid-state drive", _ => "Storage device" };
    private static PolicyOption Policy(Operation operation) => Policies.First(option => option.Operation == operation);
    private void PresentCompletionIfNeeded(VolumeSession session)
    {
        if (session.Snapshot is { IsTerminal: true } snapshot && snapshot.Id == session.AwaitingReport)
        {
            session.AwaitingReport = Guid.Empty;
            DiagnosticsOverlay.IsVisible = false;
            _ = ShowCompletionReport(snapshot);
        }
    }
    private async void OnAnalyze(object? sender, EventArgs e) => await Submit(Operation.Analyze, true);
    private async void OnPreview(object? sender, EventArgs e) => await Submit(_selectedPolicy.Operation, true);
    private async void OnOptimize(object? sender, EventArgs e) => await Submit(_selectedPolicy.Operation, false);
    private async void OnUseRecommendation(object? sender, EventArgs e)
    {
        if (CurrentSession?.LayoutSnapshot == null) await Submit(Operation.Analyze, true);
    }
    private void OnSelectRecommendedMethod(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: Operation operation } || !_recommendedOperations.Contains(operation)) return;
        _selectedPolicy = Policy(operation); UpdateSelectedPolicy();
        FooterStatus.Text = $"●  {_selectedPolicy.Name} selected · preview or run this step";
    }
    private async void OnChooseMethod(object? sender, EventArgs e) => await ChoosePolicy();
    private Task<Operation?> ChoosePolicy()
    {
        if (_policyChoice != null) return _policyChoice.Task;
        _policyChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AlgorithmOverlay.IsVisible = true;
        return _policyChoice.Task;
    }
    private void OnPolicySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not PolicyOption option) return;
        _selectedPolicy = option; UpdateSelectedPolicy(); CompletePolicyChoice(option.Operation);
        PolicyList.SelectedItem = null;
    }
    private void OnCancelPolicy(object? sender, EventArgs e) => CompletePolicyChoice(null);
    private void CompletePolicyChoice(Operation? operation)
    {
        AlgorithmOverlay.IsVisible = false;
        var completion = _policyChoice; _policyChoice = null; completion?.TrySetResult(operation);
    }
    private void UpdateSelectedPolicy()
    {
        SelectedMethodIcon.Text = _selectedPolicy.Icon; SelectedMethodName.Text = _selectedPolicy.Name; SelectedMethodDescription.Text = _selectedPolicy.Description;
    }
    private async void OnPause(object? sender, EventArgs e) => await Control(CurrentSession?.Snapshot?.State == JobState.Paused ? "resume" : "pause");
    private async void OnCancel(object? sender, EventArgs e) => await Control("cancel");
    private async Task Control(string action) { try { if (CurrentSession?.JobId is { } id && id != Guid.Empty) await _client.Send(new(action, Id: id)); } catch (Exception e) { await DisplayAlertAsync("Job control", e.Message, "Close"); } }
    private async void OnRefreshVolumes(object? sender, EventArgs e) => await RefreshVolumes();
    private void OnOverview(object? sender, EventArgs e) { ShowSettings(false); OnResetZoom(sender, e); }
    private void OnSettings(object? sender, EventArgs e) => ShowSettings(true);
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_settingTheme || ThemePicker.SelectedItem is not string selected || Application.Current is not App app) return;
        app.SetThemePreference(selected switch { "Light" => "Light", "Dark" => "Dark", _ => "System" });
    }
    private void ShowSettings(bool settings)
    {
        OverviewContent.IsVisible = ActionContent.IsVisible = !settings; SettingsContent.IsVisible = settings;
        if (settings)
        {
            OverviewButton.BackgroundColor = Colors.Transparent;
            OverviewButton.SetDynamicResource(Button.TextColorProperty, "Ink");
            SettingsButton.SetDynamicResource(Button.BackgroundColorProperty, "NavSelected");
            SettingsButton.SetDynamicResource(Button.TextColorProperty, "Accent");
        }
        else
        {
            OverviewButton.SetDynamicResource(Button.BackgroundColorProperty, "NavSelected");
            OverviewButton.SetDynamicResource(Button.TextColorProperty, "Accent");
            SettingsButton.BackgroundColor = Colors.Transparent;
            SettingsButton.SetDynamicResource(Button.TextColorProperty, "Ink");
        }
    }
    private async void OnZoomIn(object? sender, EventArgs e) => await ZoomAt(_map.StartCluster + _map.ClusterCount / 2, true);
    private async void OnZoomOut(object? sender, EventArgs e) => await ZoomAt(_map.StartCluster + _map.ClusterCount / 2, false);
    private void OnResetZoom(object? sender, EventArgs e) => ResetMapView();
    private void ResetMapView()
    {
        if (CurrentSession is not { } session) return;
        long total = session.LayoutSnapshot?.TotalClusters > 0
            ? session.LayoutSnapshot.TotalClusters
            : session.Cells.Sum(cell => cell.Clusters);
        if (total > 0) ApplyBaseMap(session, total);
    }
    private void ApplyBaseMap(VolumeSession session, long total)
    {
        _map.Reset(session.Cells, total);
        _overview.Cells = session.Cells; _overview.TotalClusters = total;
        _overview.StartCluster = 0; _overview.ClusterCount = total;
        if (_selectedMapFile != null) _map.SetHighlight(_selectedMapFile.Ranges);
        DiskMap.Invalidate(); MapOverview.Invalidate(); UpdateRange();
        if (_selectedMapFileId.HasValue) _ = LoadRegion(0, total);
    }
    private void UpdateRange(long? previewStart = null)
    {
        long start = previewStart ?? _map.StartCluster, count = _map.ClusterCount, total = _map.TotalClusters;
        MapRange.Text = total == 0 ? "FULL VOLUME" :
            $"LCN {start:N0} – {Math.Min(total, start + count) - 1:N0} · {count:N0} CLUSTERS · {total / (double)count:0.#}×";
    }
    private async Task ZoomAt(long anchor, bool inward)
    {
        if (_map.TotalClusters <= 0 || _map.ClusterCount <= 0) return;
        long minimum = Math.Min(_map.TotalClusters, Math.Max(1, CurrentSession?.Cells.Length ?? 256));
        long length = inward ? Math.Max(minimum, (_map.ClusterCount + 1) / 2) :
            _map.ClusterCount > _map.TotalClusters / 2 ? _map.TotalClusters : _map.ClusterCount * 2;
        if (length == _map.ClusterCount) return;
        double fraction = Math.Clamp((anchor - _map.StartCluster) / (double)_map.ClusterCount, 0, 1);
        long start = Math.Clamp(anchor - (long)(fraction * length), 0, _map.TotalClusters - length);
        await LoadRegion(start, length);
    }
    private async Task LoadRegion(long start, long length)
    {
        var session = CurrentSession;
        if (session == null || session.JobId == Guid.Empty || _map.TotalClusters <= 0) return;
        int request = ++_regionRequest;
        long total = _map.TotalClusters;
        start = Math.Clamp(start, 0, total - 1); length = Math.Clamp(length, 1, total - start);
        bool full = start == 0 && length == total;
        if (full) ApplyBaseMapWithoutRefresh(session, total);
        try
        {
            // Directory scanners use snapshot-local keys; track paths across rescans.
            bool trackPath = FileSystemCapabilities.Find(_volume?.FileSystem ?? "")?.UsesDirectoryScan == true;
            var reply = await _client.Send(new("explore", Id: session.JobId, StartCluster: start, ClusterCount: length,
                MapCells: full ? 0 : Math.Max(256, session.Cells.Length), FileId: trackPath ? null : _selectedMapFileId,
                Path: trackPath ? _selectedMapPath : null, Stream: _selectedMapStream));
            if (request != _regionRequest || !ReferenceEquals(session, CurrentSession) || reply.Region == null) return;
            var region = reply.Region;
            if (!full) _map.SetRegion(region.StartCluster, region.ClusterCount, region.TotalClusters, region.Cells);
            _overview.StartCluster = region.StartCluster; _overview.ClusterCount = region.ClusterCount;
            ApplyFileSelection(region.Selection);
            DiskMap.Invalidate(); MapOverview.Invalidate(); UpdateRange();
            if (region.Selection != null && !region.Selection.Ranges.Any(range => RangesOverlap(range.Start, range.End, region.StartCluster, region.StartCluster + region.ClusterCount)))
            {
                var firstRange = region.Selection.Ranges.FirstOrDefault(range => range.Length > 0);
                if (firstRange.Length > 0 && region.ClusterCount < region.TotalClusters)
                    await LoadRegion(Math.Clamp(firstRange.Start - region.ClusterCount / 2, 0, region.TotalClusters - region.ClusterCount), region.ClusterCount);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
        { FooterStatus.Text = exception.Message; }
    }
    private void ApplyBaseMapWithoutRefresh(VolumeSession session, long total)
    {
        _map.Reset(session.Cells, total); _overview.Cells = session.Cells; _overview.TotalClusters = total;
        _overview.StartCluster = 0; _overview.ClusterCount = total;
        if (_selectedMapFile != null) _map.SetHighlight(_selectedMapFile.Ranges);
        DiskMap.Invalidate(); MapOverview.Invalidate(); UpdateRange();
    }
    private void OnSelectRegion(object? sender, EventArgs e)
    {
        _selectingRegion = !_selectingRegion; _map.ClearSelection();
        RegionSelectButton.Text = _selectingRegion ? "Drag a region…" : "Select region";
        RegionSelectButton.SetDynamicResource(Button.BackgroundColorProperty, _selectingRegion ? "NavSelected" : "ButtonSurface");
        DiskMap.Invalidate();
    }
    private void OnMapStart(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0) return;
        _mapGestureStart = _mapGestureCurrent = _map.Hit(e.Touches[0].X, e.Touches[0].Y);
        _mapGestureViewportStart = _pendingPanStart = _map.StartCluster; _mapDragged = false;
        if (_selectingRegion && _mapGestureStart >= 0) { _map.SetSelection(_mapGestureStart, _mapGestureStart); DiskMap.Invalidate(); }
    }
    private void OnMapDrag(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0 || _mapGestureStart < 0) return;
        int current = _map.Hit(e.Touches[0].X, e.Touches[0].Y); if (current < 0) return;
        _mapGestureCurrent = current; _mapDragged |= current != _mapGestureStart;
        if (_selectingRegion)
        {
            _map.SetSelection(_mapGestureStart, current); DiskMap.Invalidate(); return;
        }
        long step = Math.Max(1, _map.CellRange(0).Length);
        _pendingPanStart = Math.Clamp(_mapGestureViewportStart + (long)(_mapGestureStart - current) * step,
            0, Math.Max(0, _map.TotalClusters - _map.ClusterCount));
        _overview.StartCluster = _pendingPanStart; MapOverview.Invalidate(); UpdateRange(_pendingPanStart);
    }
    private async void OnMapEnd(object? sender, TouchEventArgs e)
    {
        if (_mapGestureStart < 0) return;
        int start = _mapGestureStart, end = _mapGestureCurrent;
        _mapGestureStart = _mapGestureCurrent = -1;
        if (_selectingRegion)
        {
            _map.ClearSelection(); _selectingRegion = false; RegionSelectButton.Text = "Select region";
            RegionSelectButton.SetDynamicResource(Button.BackgroundColorProperty, "ButtonSurface"); DiskMap.Invalidate();
            var first = _map.CellRange(Math.Min(start, end)); var last = _map.CellRange(Math.Max(start, end));
            if (first.Length > 0 && last.Length > 0) await LoadRegion(first.Start, last.End - first.Start);
            return;
        }
        if (_mapDragged) { await LoadRegion(_pendingPanStart, _map.ClusterCount); return; }
        ClearFileHighlight();
        var range = _map.CellRange(start); if (range.Length > 0) await ShowClusterFiles(range);
    }
    private void OnMapHover(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0) return;
        int index = _map.Hit(e.Touches[0].X, e.Touches[0].Y); if (index < 0) return;
        var range = _map.CellRange(index); var c = _map.Cells[index]; double n = Math.Max(1, c.Clusters);
        CellDetail.Text = $"LCN {range.Start:N0}–{range.End - 1:N0} · {c.Clusters:N0} clusters · {c.Allocated / n:P0} allocated · {c.Fragmented / n:P0} fragmented · {c.Metadata / n:P0} metadata";
    }
    private void OnOverviewStart(object? sender, TouchEventArgs e) { if (e.Touches.Length > 0) PreviewOverview(e.Touches[0].X); }
    private void OnOverviewDrag(object? sender, TouchEventArgs e) { if (e.Touches.Length > 0) PreviewOverview(e.Touches[0].X); }
    private async void OnOverviewEnd(object? sender, TouchEventArgs e) => await LoadRegion(_pendingPanStart, _map.ClusterCount);
    private void PreviewOverview(float x)
    {
        long center = _overview.ClusterAt(x);
        _pendingPanStart = Math.Clamp(center - _map.ClusterCount / 2, 0, Math.Max(0, _map.TotalClusters - _map.ClusterCount));
        _overview.StartCluster = _pendingPanStart; MapOverview.Invalidate(); UpdateRange(_pendingPanStart);
    }
    private void OnDiskMapHandlerChanged(object? sender, EventArgs e)
    {
        if (_mapPlatformView != null) _mapPlatformView.PointerWheelChanged -= OnMapPointerWheel;
        _mapPlatformView = DiskMap.Handler?.PlatformView as WinUIElement;
        if (_mapPlatformView != null) _mapPlatformView.PointerWheelChanged += OnMapPointerWheel;
    }
    private void OnMapPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if ((e.KeyModifiers & VirtualKeyModifiers.Control) == 0 || _mapPlatformView == null) return;
        var point = e.GetCurrentPoint(_mapPlatformView); long cluster = _map.ClusterAt((float)point.Position.X, (float)point.Position.Y);
        if (cluster >= 0) _ = ZoomAt(cluster, point.Properties.MouseWheelDelta > 0);
        e.Handled = true;
    }
    private void OnResourceChanged(object? sender, ValueChangedEventArgs e)
    { if (CpuValue == null || CpuSlider == null) return; CpuValue.Text = $"{(int)CpuSlider.Value}%"; }
    private void OnPresetChanged(object? sender, EventArgs e)
    {
        if (ResourcePreset.SelectedIndex < 0 || CpuSlider == null) return;
        var p = ResourcePreset.SelectedIndex switch { 0 => ResourcePolicy.Quiet, 2 => ResourcePolicy.Performance, _ => ResourcePolicy.Balanced };
        CpuSlider.Value = p.CpuPercent; MemoryEntry.Text = p.MemoryMiB.ToString(CultureInfo.InvariantCulture); IoEntry.Text = p.IoMiBPerSecond.ToString(CultureInfo.InvariantCulture); IdleSwitch.IsToggled = p.IdleOnly; AcSwitch.IsToggled = p.AcOnly; BackgroundSwitch.IsToggled = p.Background;
        ScanWorkersEntry.Text = p.ScanWorkers.ToString(CultureInfo.InvariantCulture);
        PlanningWorkersEntry.Text = p.PlanningWorkers.ToString(CultureInfo.InvariantCulture);
        MoveQueueEntry.Text = p.MoveQueueDepth.ToString(CultureInfo.InvariantCulture);
    }
    private void OnToggleAdvanced(object? sender, EventArgs e)
    { AdvancedPanel.IsVisible = !AdvancedPanel.IsVisible; AdvancedToggle.Text = AdvancedPanel.IsVisible ? "Hide controls" : "Show controls"; }
    private void OnScopeChanged(object? sender, TextChangedEventArgs e)
        => RefreshScopeSummary();
    private void RefreshScopeSummary()
    {
        if (ScopeSummary == null) return;
        string selection = _fileRules.Count == 0 ? "All files" : $"{_fileRules.Count:N0} file rules";
        int exclusions = _exclusionRules.Count;
        string fragments = string.IsNullOrWhiteSpace(MinFragmentsEntry?.Text) ? "20" : MinFragmentsEntry.Text;
        string size = string.IsNullOrWhiteSpace(MinFileSizeEntry?.Text) && string.IsNullOrWhiteSpace(MaxFileSizeEntry?.Text) ? "all sizes" : "size-filtered";
        ScopeSummary.Text = $"{selection} · {(exclusions == 0 ? "exclusions none" : $"{exclusions:N0} exclusions")} · {fragments}-fragment defrag threshold · {size}";
        RenderRecommendation(CurrentSession?.LayoutSnapshot);
        UpdateVolumeActions();
    }
    private async void OnAddFileRule(object? sender, EventArgs e) => await AddRule(_fileRules, FileRulesList, FileRulePattern, FileRuleKind, "Files to include");
    private async void OnAddExclusionRule(object? sender, EventArgs e) => await AddRule(_exclusionRules, ExclusionRulesList, ExclusionRulePattern, ExclusionRuleKind, "Exclusions");
    private async Task AddRule(ObservableCollection<RuleEditorItem> rules, VerticalStackLayout list, Entry patternEntry, Picker kindPicker, string title)
    {
        try
        {
            var rule = new PathRule(patternEntry.Text?.Trim() ?? "", RuleKind(kindPicker));
            PathRules.Validate(rule);
            if (!rules.Any(item => item.Rule == rule)) rules.Add(new(rule));
            patternEntry.Text = "";
            RenderRuleList(list, rules);
            RefreshScopeSummary();
        }
        catch (Exception exception) { await DisplayAlertAsync(title, exception.Message, "Close"); }
    }
    private static PathRuleKind RuleKind(Picker picker) => picker.SelectedIndex switch
    {
        1 => PathRuleKind.Wildcard,
        2 => PathRuleKind.Regex,
        _ => PathRuleKind.Path
    };
    private void RenderRuleList(VerticalStackLayout list, ObservableCollection<RuleEditorItem> rules)
    {
        list.Children.Clear();
        foreach (var item in rules)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = 118 });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Add(new Label { Text = RuleKindLabel(item.Rule.Kind), FontSize = 10, TextColor = Colors.Gray, VerticalOptions = LayoutOptions.Center }, 0);
            row.Add(new Label { Text = item.Rule.Pattern, FontSize = 11, LineBreakMode = LineBreakMode.MiddleTruncation, VerticalOptions = LayoutOptions.Center }, 1);
            var remove = new Button { Text = "Remove", Padding = new Thickness(10, 5) };
            remove.Clicked += (_, _) => { rules.Remove(item); RenderRuleList(list, rules); RefreshScopeSummary(); };
            row.Add(remove, 2);
            list.Children.Add(row);
        }
    }
    private static string RuleKindLabel(PathRuleKind kind) => kind switch
    {
        PathRuleKind.Wildcard => "Wildcard",
        PathRuleKind.Regex => "Regular expression",
        _ => "Path"
    };
    private void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not FileRow row) return;
        var rule = new PathRule(row.Path);
        if (!_fileRules.Any(item => item.Rule == rule))
        {
            _fileRules.Add(new(rule));
            RenderRuleList(FileRulesList, _fileRules);
            RefreshScopeSummary();
        }
        CellDetail.Text = row.Path;
    }
    private async Task ShowClusterFiles(ClusterRange range)
    {
        var session = CurrentSession; if (session == null || session.JobId == Guid.Empty) return;
        try
        {
            var reply = await _client.Send(new("explore", Id: session.JobId, StartCluster: range.Start,
                ClusterCount: range.Length, IncludeFiles: true));
            if (reply.Region == null || !ReferenceEquals(session, CurrentSession)) return;
            ClusterTitle.Text = range.Length == 1 ? $"Cluster {range.Start:N0}" : $"Clusters {range.Start:N0}–{range.End - 1:N0}";
            ClusterSubtitle.Text = reply.Region.FileCount > reply.Region.Files.Length
                ? $"{reply.Region.FileCount:N0} streams overlap this range; showing the first {reply.Region.Files.Length:N0}."
                : $"{reply.Region.FileCount:N0} streams overlap this range.";
            ClusterFilesList.ItemsSource = reply.Region.Files.OrderByDescending(file => file.ClustersInRegion)
                .Select(file => new ClusterHitRow(file)).ToArray();
            ClusterFilesList.SelectedItem = null; _selectedClusterHit = null; HighlightClusterFileButton.IsEnabled = false;
            ClusterOverlay.IsVisible = true;
        }
        catch (Exception exception) { await DisplayAlertAsync("Cluster contents unavailable", exception.Message, "Close"); }
    }
    private void OnClusterFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        _selectedClusterHit = e.CurrentSelection.FirstOrDefault() as ClusterHitRow;
        HighlightClusterFileButton.IsEnabled = _selectedClusterHit != null;
    }
    private async void OnHighlightClusterFile(object? sender, EventArgs e)
    {
        if (_selectedClusterHit == null) return;
        _selectedMapFileId = _selectedClusterHit.File.FileId; _selectedMapPath = _selectedClusterHit.File.Path; _selectedMapStream = _selectedClusterHit.File.Stream;
        ClusterOverlay.IsVisible = false; await LoadRegion(_map.StartCluster, _map.ClusterCount);
    }
    private void OnCloseClusterFiles(object? sender, EventArgs e) => ClusterOverlay.IsVisible = false;
    private async void OnLocateFile(object? sender, EventArgs e)
    {
        var session = CurrentSession; if (session == null || session.JobId == Guid.Empty) return;
        if (!LocateFileButton.IsEnabled) return;
        LocateFileButton.IsEnabled = false;
        try
        {
            string? path = WindowsFilePicker.Pick(Window?.Handler?.PlatformView as WinUIWindow,
                "Locate a file in the allocation map");
            if (path == null) return;
            _regionRequest++;
            var reply = await _client.Send(new("explore", Id: session.JobId, StartCluster: _map.StartCluster,
                ClusterCount: _map.ClusterCount, Path: path));
            if (!ReferenceEquals(session, CurrentSession)) return;
            var selection = reply.Region?.Selection;
            if (selection == null)
            {
                await DisplayAlertAsync("File not found in map", "The selected file is not present in this volume's analyzed layout. Analyze again if it was created after the current observation.", "Close");
                return;
            }
            _selectedMapFileId = selection.FileId; _selectedMapPath = selection.Path; _selectedMapStream = selection.Stream; ApplyFileSelection(selection);
            if (!selection.Ranges.Any(range => RangesOverlap(range.Start, range.End, _map.StartCluster, _map.StartCluster + _map.ClusterCount)))
            {
                var first = selection.Ranges.FirstOrDefault(range => range.Length > 0);
                if (first.Length > 0) await LoadRegion(Math.Clamp(first.Start - _map.ClusterCount / 2, 0,
                    Math.Max(0, _map.TotalClusters - _map.ClusterCount)), _map.ClusterCount);
            }
            DiskMap.Invalidate();
        }
        catch (Exception exception) { await DisplayAlertAsync("File could not be located", exception.Message, "Close"); }
        finally { LocateFileButton.IsEnabled = true; }
    }
    private void ApplyFileSelection(MapFileSelection? selection)
    {
        _selectedMapFile = selection;
        if (selection == null)
        {
            _map.SetHighlight([]); HighlightedFile.IsVisible = _selectedMapFileId.HasValue;
            HighlightedFile.Text = "Tracked file is not present in the current layout."; return;
        }
        _map.SetHighlight(selection.Ranges); HighlightedFile.IsVisible = true;
        string stream = selection.Stream.Length == 0 ? "" : selection.Stream;
        HighlightedFile.Text = $"HIGHLIGHTED · {selection.Path}{stream} · {selection.Clusters:N0} clusters · {Format.Bytes(selection.Bytes)}";
    }
    private void ClearFileHighlight()
    {
        _regionRequest++;
        _selectedMapFileId = null; _selectedMapPath = null; _selectedMapStream = ""; _selectedMapFile = null;
        _map.SetHighlight([]); HighlightedFile.IsVisible = false; DiskMap.Invalidate();
    }
    private static bool RangesOverlap(long a, long b, long c, long d) => a < d && c < b;
    private async void OnExport(object? sender, EventArgs e)
    {
        if (CurrentSession?.Snapshot is not { } snapshot) return;
        await ExportReport(snapshot);
    }
    private async Task ExportReport(JobSnapshot snapshot)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"Tedd-Defrag-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try
        {
            var report = new JobStore().ReadReport(snapshot.Id) ?? snapshot with { Map = null };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions(JobStore.Json) { WriteIndented = true }));
            await DisplayAlertAsync("Report saved", path, "Close");
        }
        catch (Exception ex) { await DisplayAlertAsync("Export failed", ex.Message, "Close"); }
    }
    private async Task ShowCompletionReport(JobSnapshot snapshot)
    {
        string report = $"{snapshot.State}: {snapshot.Message}\n\n" +
            $"Planned moves: {snapshot.PlannedMoves:N0}\nAttempted moves: {snapshot.AttemptedMoves:N0}\n" +
            $"Verified moves: {snapshot.VerifiedMoves:N0}\nFailed moves: {snapshot.FailedMoves:N0}\n" +
            $"Relocated and verified: {Format.Bytes(snapshot.BytesMoved)}\n" +
            $"Fragmented streams: {snapshot.InitialFragmentedFiles:N0} before, {snapshot.FragmentedFiles:N0} after\n" +
            $"Elapsed: {TimeSpan.FromMilliseconds(snapshot.ElapsedMilliseconds):g}";
        if (await DisplayAlertAsync("Job report", report, "Export report", "Close")) await ExportReport(snapshot);
    }
    private async void OnSaveConcurrency(object? sender, EventArgs e)
    {
        try { await _client.Send(new("settings", Settings: new() { MaxConcurrentVolumes = int.Parse(ParallelEntry.Text), AllowParallelOnSharedStorage = SharedSwitch.IsToggled })); FooterStatus.Text = "Concurrency settings saved for subsequent jobs"; }
        catch (Exception ex) { await DisplayAlertAsync("Concurrency settings", ex.Message, "Close"); }
    }
    private async void OnHistory(object? sender, EventArgs e)
    {
        try
        {
            var jobs = (await _client.Send(new("list"))).Jobs ?? [];
            if (jobs.Length == 0) { await DisplayAlertAsync("Activity", "No jobs have been submitted.", "Close"); return; }
            string[] titles = jobs.Take(30).Select(j => $"{j.UpdatedAt.ToLocalTime():MM-dd HH:mm} · {j.Volume} · {j.Operation} · {j.State} · {j.Id.ToString()[..6]}").ToArray();
            string? choice = await DisplayActionSheetAsync("Select a job to inspect", "Close", null, titles);
            int index = Array.IndexOf(titles, choice);
            if (index >= 0)
            {
                var job = jobs[index];
                var volume = _volumes.FirstOrDefault(v => VolumeKey(v.Root).Equals(VolumeKey(job.Volume), StringComparison.OrdinalIgnoreCase));
                if (volume == null)
                {
                    await DisplayAlertAsync("Activity", $"{job.Volume} is not currently available.", "Close");
                    return;
                }
                var session = SessionFor(volume);
                if (HasActiveJob(session) && session.JobId != job.Id)
                {
                    await DisplayAlertAsync("Activity", $"{job.Volume} has an active job. Select that drive to inspect or control it.", "Close");
                    return;
                }
                session.JobId = job.Id;
                session.AttachedExistingJob = true;
                if (session.Snapshot?.Id != session.JobId) session.Snapshot = null;
                SelectVolume(volume);
            }
        }
        catch (Exception ex) { await DisplayAlertAsync("Activity", ex.Message, "Close"); }
    }
    private static int ParseInt(string? text, string name, int defaultValue = 0)
    {
        long value = ParseWholeNumber(text, name, defaultValue);
        if (value > int.MaxValue) throw new ArgumentException($"{name} is too large.");
        return (int)value;
    }
    private static long ParseMiB(string? text, string name) => checked(ParseWholeNumber(text, name) * 1024 * 1024);
    private static long ParseWholeNumber(string? text, string name, long defaultValue = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) return defaultValue;
        string normalized = text.Trim().Replace(",", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        if (!long.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out long value)) throw new ArgumentException($"{name} must be a nonnegative whole number.");
        return value;
    }
    private VolumeSession? CurrentSession => _volume == null ? null : SessionFor(_volume);
    private VolumeSession SessionFor(VolumeInfo volume)
    {
        if (!_volumeSessions.TryGetValue(volume.Id, out var session)) _volumeSessions.Add(volume.Id, session = new());
        return session;
    }
    private static bool HasActiveJob(VolumeSession? session) => session != null && session.JobId != Guid.Empty &&
        (session.Snapshot == null || session.Snapshot.Id != session.JobId || !session.Snapshot.IsTerminal);
    private static string VolumeKey(string volume) => volume.Trim().TrimEnd('\\');
    private static string Limit(long value, Func<long, string> format, string unlimited) => value == 0 ? unlimited : format(value);
    private sealed class VolumeSession
    {
        public Guid JobId { get; set; }
        public Guid AwaitingReport { get; set; }
        public bool AttachedExistingJob { get; set; }
        public JobSnapshot? Snapshot { get; set; }
        public JobSnapshot? LayoutSnapshot { get; set; }
        public MapCell[] Cells { get; set; } = [];
        public FileRow[]? Files { get; set; }
    }
    private sealed record FileRow(string Path, int Extents, string BytesLabel, string Status);
    private sealed record RuleEditorItem(PathRule Rule);
    private sealed record ClusterHitRow(ClusterFile File)
    {
        public string DisplayPath => File.Path + File.Stream;
        public string BytesLabel => Format.Bytes(File.Bytes);
        public string ClusterLabel => File.ClustersInRegion == File.Clusters
            ? $"{File.Clusters:N0} clusters" : $"{File.ClustersInRegion:N0} / {File.Clusters:N0}";
        public string Status => File.Status;
    }
    private sealed record PolicyOption(string Icon, string Name, string Description, Operation Operation);
}
