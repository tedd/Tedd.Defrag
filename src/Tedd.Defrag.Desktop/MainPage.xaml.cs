using System.Globalization;
using System.Text.Json;
using Tedd.Defrag.Client;
using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Update;
using Tedd.Defrag.Visualization;
using Tedd.Defrag.Windows;
using Microsoft.UI.Xaml.Input;
using Microsoft.Maui.Storage;
using Windows.System;
using WinUIElement = Microsoft.UI.Xaml.UIElement;

namespace Tedd.Defrag.Desktop;

public partial class MainPage : ContentPage
{
    private readonly DefragClient _client = new();
    private readonly DiskMapDrawable _map = new();
    private readonly MapOverviewDrawable _overview = new();
    private readonly Dictionary<string, VolumeSession> _volumeSessions = new(StringComparer.OrdinalIgnoreCase);
    private VolumeInfo[] _volumes = [];
    private VolumeInfo? _volume;
    private bool _polling, _refreshing, _submitting;
    private bool _settingTheme;
    private bool _selectingRegion, _mapDragged;
    private int _mapGestureStart = -1, _mapGestureCurrent = -1, _regionRequest;
    private long _mapGestureViewportStart, _pendingPanStart;
    private WinUIElement? _mapPlatformView;
    private ulong? _selectedMapFileId;
    private string _selectedMapStream = "";
    private MapFileSelection? _selectedMapFile;
    private ClusterHitRow? _selectedClusterHit;
    private readonly IDispatcherTimer _timer;
    private TaskCompletionSource<Operation?>? _policyChoice;
    private static readonly PolicyOption[] Policies =
    [
        new("↯", "Minimum-write defrag", "Prioritizes heavily fragmented files and preserves their first extent when possible. Honors file scope and exclusions.", Operation.MinimumWrite),
        new("✦", "Windows automatic", "Lets Windows select the appropriate whole-volume action for the media, such as HDD defrag or SSD retrim.", Operation.Automatic),
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
        ResourcePreset.ItemsSource = new[] { "Quiet · bounded maintenance", "Balanced · unlimited I/O", "Performance · dedicated" }; ResourcePreset.SelectedIndex = 1;
        _timer = Dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(250); _timer.Tick += async (_, _) => await Poll();
        Loaded += async (_, _) => { _timer.Start(); await RefreshVolumes(); await CheckForUpdate(); };
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
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException)
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
                SelectVolume(volumes.FirstOrDefault(v => v.Root.Equals(systemRoot, StringComparison.OrdinalIgnoreCase) && v.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                    ?? volumes.FirstOrDefault(v => v.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) ?? volumes.FirstOrDefault());
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
        _overview.Cells = []; _overview.TotalClusters = _overview.StartCluster = _overview.ClusterCount = 0;
        ClearFileHighlight(); ClusterOverlay.IsVisible = false; DiagnosticsOverlay.IsVisible = false;
        _map.EmptyMessage = volume == null ? "Connect a volume and refresh to begin." :
            volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ? "Analyze this volume to reveal its allocation map." : "Select an NTFS volume to analyze its allocation.";
        DiskMap.Invalidate();
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
        FooterStatus.Text = volume == null ? "●  No available volumes · refresh to try again" : $"●  {volume.Root} selected · administrator access active";
        if (volume != null && !volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            JobMessage.Text = "Analysis and optimization require an NTFS volume.";
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
        AnalyzeButton.IsEnabled = PreviewButton.IsEnabled = OptimizeButton.IsEnabled = !_submitting && !jobActive && _volume?.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) == true;
        foreach (var button in VolumesPanel.Children.OfType<Button>())
        {
            button.IsEnabled = !_submitting;
            button.SetDynamicResource(Button.BackgroundColorProperty, Equals(button.BindingContext, _volume?.Id) ? "NavSelected" : "ButtonSurface");
        }
    }
    private JobRequest Request(Operation operation, bool preview)
    {
        var volume = _volume ?? throw new InvalidOperationException("Select an available NTFS volume first.");
        if (!volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Select an NTFS volume.");
        var request = new JobRequest { Volume = volume.Root, Operation = operation, Preview = preview,
            SelectedPaths = string.IsNullOrWhiteSpace(SelectedPath.Text) ? [] : [SelectedPath.Text.Trim()],
            Exclusions = (ExclusionEntry.Text ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            Resources = new() { CpuPercent = (int)CpuSlider.Value, MemoryMiB = ParseInt(MemoryEntry.Text, "Memory cap"), IoMiBPerSecond = ParseInt(IoEntry.Text, "Relocation bandwidth"),
                ScanWorkers = ParseInt(ScanWorkersEntry.Text, "MFT workers"), PlanningWorkers = ParseInt(PlanningWorkersEntry.Text, "Planner workers"),
                MoveQueueDepth = ParseInt(MoveQueueEntry.Text, "Move queue depth", 1),
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
                    details += "\n\nThis is SSD or unknown media. Custom relocation adds writes and may provide no performance benefit.";
                if (!await DisplayAlertAsync("Review disk operation", details, "Start job", "Cancel")) return;
            }
            OptimizeButton.IsEnabled = false; FooterStatus.Text = "Connecting to the worker…";
            var session = CurrentSession ?? throw new InvalidOperationException("Select an available NTFS volume first.");
            session.JobId = (await _client.Send(new("submit", Job: request), startBroker: true)).Id;
            session.AwaitingReport = session.JobId;
            DiagnosticsTitle.Text = "Waiting for worker"; DiagnosticsCount.Text = "Queued";
            DiagnosticsProcess.Text = DiagnosticsAcceleration.Text = ""; DiagnosticsProgress.Progress = 0;
            ScanDiagnosticsText.Text = PlanningDiagnosticsText.Text = ExecutionDiagnosticsText.Text = "Pending";
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
        long totalClusters = session.Cells.Sum(cell => cell.Clusters);
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
        FooterStatus.Text = $"●  {snapshot.Volume} · {snapshot.State} · application worker";
        RenderDiagnostics(snapshot);
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
        DiagnosticsProcess.Text = d == null ? "This worker has not published performance telemetry." :
            $"Elapsed {TimeSpan.FromMilliseconds(snapshot.ElapsedMilliseconds):g} · {d.LogicalProcessors:N0} logical processors · {d.ProcessThreads:N0} process threads\n" +
            $"CPU time {d.CpuMilliseconds / 1000:N1} s · CPU ceiling {snapshot.CpuPercent}% · committed memory {Format.Bytes(d.PrivateBytes)}\n" +
            $"{snapshot.PlannedMoves:N0} planned · {snapshot.VerifiedMoves:N0} verified · {snapshot.FailedMoves:N0} failed moves · {Format.Bytes(snapshot.BytesMoved)} relocated";
        ScanDiagnosticsText.Text = Describe(d?.Scan);
        PlanningDiagnosticsText.Text = Describe(d?.Planning);
        ExecutionDiagnosticsText.Text = Describe(d?.Execution);
        DiagnosticsAcceleration.Text = d == null ? "" : $"Map: {d.MapAcceleration}";
        static string Describe(WorkProgress? p)
        {
            if (p == null) return "Pending / not applicable";
            string rate = p.ElapsedMilliseconds > 0 ? $" · {p.Completed * 1000d / p.ElapsedMilliseconds:N0} {p.Unit}/s" : "";
            string bytes = p.BytesProcessed > 0 ? $"\n{Format.Bytes(p.BytesProcessed)} processed · {Format.Bytes((long)(p.BytesProcessed * 1000d / Math.Max(1, p.ElapsedMilliseconds)))}/s" : "";
            return $"{p.Phase}: {p.Completed:N0} / {p.Total:N0} {p.Unit}{rate}\n" +
                $"Workers {p.ActiveWorkers:N0} active / {p.WorkerLimit:N0} limit · peak {p.PeakWorkers:N0} · requests in flight {p.InFlightIo:N0} / peak {p.PeakIo:N0}" +
                bytes + $"\n{p.Acceleration}\n{p.Detail}";
        }
    }
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
        long total = session.Cells.Sum(cell => cell.Clusters);
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
            var reply = await _client.Send(new("explore", Id: session.JobId, StartCluster: start, ClusterCount: length,
                MapCells: full ? 0 : Math.Max(256, session.Cells.Length), FileId: _selectedMapFileId, Stream: _selectedMapStream));
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
    { AdvancedPanel.IsVisible = !AdvancedPanel.IsVisible; AdvancedToggle.Text = AdvancedPanel.IsVisible ? "Hide advanced" : "Show advanced"; }
    private void OnScopeChanged(object? sender, TextChangedEventArgs e)
    {
        if (ScopeSummary == null) return;
        string selection = string.IsNullOrWhiteSpace(SelectedPath?.Text) ? "All files" : "Selected path";
        int exclusions = (ExclusionEntry?.Text ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length;
        string fragments = string.IsNullOrWhiteSpace(MinFragmentsEntry?.Text) ? "20" : MinFragmentsEntry.Text;
        string size = string.IsNullOrWhiteSpace(MinFileSizeEntry?.Text) && string.IsNullOrWhiteSpace(MaxFileSizeEntry?.Text) ? "all sizes" : "size-filtered";
        ScopeSummary.Text = $"{selection} · {(exclusions == 0 ? "exclusions none" : $"{exclusions:N0} exclusions")} · {fragments}-fragment defrag threshold · {size}";
    }
    private void OnFileSelected(object? sender, SelectionChangedEventArgs e) { if (e.CurrentSelection.FirstOrDefault() is FileRow row) { SelectedPath.Text = row.Path; CellDetail.Text = row.Path; } }
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
        _selectedMapFileId = _selectedClusterHit.File.FileId; _selectedMapStream = _selectedClusterHit.File.Stream;
        ClusterOverlay.IsVisible = false; await LoadRegion(_map.StartCluster, _map.ClusterCount);
    }
    private void OnCloseClusterFiles(object? sender, EventArgs e) => ClusterOverlay.IsVisible = false;
    private async void OnLocateFile(object? sender, EventArgs e)
    {
        var session = CurrentSession; if (session == null || session.JobId == Guid.Empty) return;
        FileResult? result = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Locate a file in the allocation map" });
        if (result == null) return;
        _regionRequest++;
        try
        {
            var reply = await _client.Send(new("explore", Id: session.JobId, StartCluster: _map.StartCluster,
                ClusterCount: _map.ClusterCount, Path: result.FullPath));
            if (!ReferenceEquals(session, CurrentSession)) return;
            var selection = reply.Region?.Selection;
            if (selection == null)
            {
                await DisplayAlertAsync("File not found in map", "The selected file is not present in this volume's analyzed layout. Analyze again if it was created after the current observation.", "Close");
                return;
            }
            _selectedMapFileId = selection.FileId; _selectedMapStream = selection.Stream; ApplyFileSelection(selection);
            if (!selection.Ranges.Any(range => RangesOverlap(range.Start, range.End, _map.StartCluster, _map.StartCluster + _map.ClusterCount)))
            {
                var first = selection.Ranges.FirstOrDefault(range => range.Length > 0);
                if (first.Length > 0) await LoadRegion(Math.Clamp(first.Start - _map.ClusterCount / 2, 0,
                    Math.Max(0, _map.TotalClusters - _map.ClusterCount)), _map.ClusterCount);
            }
            DiskMap.Invalidate();
        }
        catch (Exception exception) { await DisplayAlertAsync("File could not be located", exception.Message, "Close"); }
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
        _selectedMapFileId = null; _selectedMapStream = ""; _selectedMapFile = null;
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
        public JobSnapshot? Snapshot { get; set; }
        public JobSnapshot? LayoutSnapshot { get; set; }
        public MapCell[] Cells { get; set; } = [];
        public FileRow[]? Files { get; set; }
    }
    private sealed record FileRow(string Path, int Extents, string BytesLabel, string Status);
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
