using System.Globalization;
using System.Text.Json;
using Tedd.Defrag.Client;
using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Visualization;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Desktop;

public partial class MainPage : ContentPage
{
    private readonly DefragClient _client = new();
    private readonly DiskMapDrawable _map = new();
    private VolumeInfo? _volume;
    private bool _polling, _refreshing, _submitting;
    private Guid _job;
    private JobSnapshot? _snapshot;
    private readonly IDispatcherTimer _timer;
    private static readonly (string Name, Operation Operation)[] Policies =
    [ ("Minimum-write defrag", Operation.MinimumWrite), ("Defragment files", Operation.FilesOnly), ("Pack toward beginning", Operation.Pack),
      ("Pack + defragment", Operation.PackAndDefrag), ("Alphabetical layout", Operation.Alphabetical), ("Order by size", Operation.Size),
      ("Order by creation time", Operation.Created), ("Order by modification time", Operation.Modified), ("Order by extension", Operation.Extension),
      ("Directory locality", Operation.DirectoryLocality), ("Prepare for shrink", Operation.PrepareShrink), ("Optimize movable MFT", Operation.OptimizeMft),
      ("Directory indexes", Operation.DirectoryIndexes), ("ReTRIM", Operation.ReTrim), ("Slab consolidation", Operation.SlabConsolidate),
      ("Windows automatic", Operation.Automatic), ("Prepare virtual disk · zero", Operation.ZeroFreeSpace) ];
    public MainPage()
    {
        InitializeComponent(); DiskMap.Drawable = _map;
        PolicyPicker.ItemsSource = Policies.Select(p => p.Name).ToArray(); PolicyPicker.SelectedIndex = 0;
        ResourcePreset.ItemsSource = new[] { "Quiet · idle maintenance", "Balanced · everyday", "Performance · dedicated" }; ResourcePreset.SelectedIndex = 1;
        _timer = Dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(250); _timer.Tick += async (_, _) => await Poll();
        Loaded += async (_, _) => { _timer.Start(); await RefreshVolumes(); };
        Unloaded += (_, _) => _timer.Stop();
    }
    private async Task RefreshVolumes()
    {
        if (_refreshing || _submitting) return;
        _refreshing = true;
        try
        {
            var volumes = await Task.Run(VolumeDiscovery.List);
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
                VolumesPanel.Children.Add(new Label { Text = "No available volumes", FontSize = 11, TextColor = Color.FromArgb("#8095A9") });
            UpdateVolumeActions();
        }
        catch (Exception e) { FooterStatus.Text = e.Message; }
        finally { _refreshing = false; }
    }
    private void AddVolume(VolumeInfo volume)
    {
        var button = new Button { Text = $"▣  {volume.Root[..2]}   {volume.Label}", FontSize = 12, Padding = new Thickness(10, 12),
            BackgroundColor = Color.FromArgb("#132131"), HorizontalOptions = LayoutOptions.Fill, BindingContext = volume.Id };
        button.Clicked += (_, _) => SelectVolume(volume); VolumesPanel.Children.Add(button);
    }
    private void SelectVolume(VolumeInfo? volume)
    {
        _volume = volume; _job = Guid.Empty; _snapshot = null; _map.Cells = []; _map.Reset();
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
        ObservationText.Text = "Awaiting analysis"; BudgetDetail.Text = "Within your write budget";
        CellDetail.Text = "Hover to inspect a cell. Click to center the next zoom."; UpdateRange();
        FooterStatus.Text = volume == null ? "●  No available volumes · refresh to try again" : $"●  {volume.Root} selected · disk access requests elevation";
        if (volume != null && !volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            JobMessage.Text = "Analysis and optimization require an NTFS volume.";
        UpdateVolumeActions();
    }
    private void UpdateVolumeActions()
    {
        AnalyzeButton.IsEnabled = PreviewButton.IsEnabled = OptimizeButton.IsEnabled = !_submitting && _volume?.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) == true;
        foreach (var button in VolumesPanel.Children.OfType<Button>())
        {
            button.IsEnabled = !_submitting;
            button.BackgroundColor = Color.FromArgb(Equals(button.BindingContext, _volume?.Id) ? "#1B3D45" : "#132131");
        }
    }
    private JobRequest Request(Operation operation, bool preview)
    {
        var volume = _volume ?? throw new InvalidOperationException("Select an available NTFS volume first.");
        if (!volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Select an NTFS volume.");
        var request = new JobRequest { Volume = volume.Root, Operation = operation, Preview = preview,
            SelectedPaths = string.IsNullOrWhiteSpace(SelectedPath.Text) ? [] : [SelectedPath.Text.Trim()],
            Exclusions = (ExclusionEntry.Text ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            Resources = new() { CpuPercent = (int)CpuSlider.Value, MemoryMiB = (int)MemorySlider.Value, IoMiBPerSecond = (int)IoSlider.Value,
                AffinityMask = string.IsNullOrWhiteSpace(AffinityEntry.Text) ? 0 : Convert.ToUInt64(AffinityEntry.Text.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16),
                IdleOnly = IdleSwitch.IsToggled, AcOnly = AcSwitch.IsToggled, Background = BackgroundSwitch.IsToggled },
            MaxMoveBytes = checked(long.Parse(BudgetEntry.Text, CultureInfo.InvariantCulture) * 1024 * 1024), MaxMinutes = int.Parse(MinutesEntry.Text, CultureInfo.InvariantCulture),
            ShrinkBoundaryBytes = string.IsNullOrWhiteSpace(BoundaryEntry.Text) ? 0 : checked(long.Parse(BoundaryEntry.Text, CultureInfo.InvariantCulture) * 1024 * 1024),
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
                    $"Run {operation} on {request.Volume}, with a {Format.Bytes(request.MaxMoveBytes)} relocation budget and {request.MaxMinutes}-minute limit?";
                if (_volume?.SeekPenalty != true && operation is not (Operation.ReTrim or Operation.SlabConsolidate or Operation.Automatic or Operation.ZeroFreeSpace))
                    details += "\n\nThis is SSD or unknown media. Custom relocation adds writes and may provide no performance benefit.";
                if (!await DisplayAlertAsync("Review disk operation", details, "Start job", "Cancel")) return;
            }
            OptimizeButton.IsEnabled = false; FooterStatus.Text = "Connecting to the persistent worker…";
            _job = (await _client.Send(new("submit", Job: request), startBroker: true)).Id;
            _map.Reset(); FooterStatus.Text = "●  Job submitted · closing this view does not stop it"; await Poll();
        }
        catch (Exception e) { await DisplayAlertAsync("Job could not start", e.Message, "Close"); FooterStatus.Text = e.Message; }
        finally { _submitting = false; UpdateVolumeActions(); }
    }
    private async Task Poll()
    {
        if (_polling || _job == Guid.Empty) return;
        _polling = true;
        try
        {
            Guid id = _job; var reply = await _client.Send(new("get", Id: id));
            if (id == _job && reply.Snapshot != null && reply.Snapshot.UpdatedAt != _snapshot?.UpdatedAt) Apply(reply.Snapshot);
        }
        catch (Exception e) { FooterStatus.Text = e.Message; }
        finally { _polling = false; }
    }
    private void Apply(JobSnapshot snapshot)
    {
        _snapshot = snapshot; _map.Cells = snapshot.Map ?? _map.Cells; DiskMap.Invalidate();
        if (snapshot.TotalBytes > 0)
        {
            FreeMetric.Text = Format.Bytes(snapshot.FreeBytes); CapacityDetail.Text = $"of {Format.Bytes(snapshot.TotalBytes)} capacity";
            FragmentMetric.Text = snapshot.FragmentedFiles.ToString("N0"); StreamDetail.Text = $"of {snapshot.TotalFiles:N0} allocated streams";
        }
        MovedMetric.Text = Format.Bytes(snapshot.BytesMoved); BudgetDetail.Text = snapshot.PlannedBytes > 0 ? $"{Format.Bytes(snapshot.PlannedBytes)} in current plan" : "Relocation payload, not NAND writes";
        JobTitle.Text = snapshot.Operation + " · " + snapshot.State; JobMessage.Text = snapshot.Message;
        JobProgress.Progress = Math.Clamp(snapshot.Progress, 0, 1); ProgressText.Text = snapshot.IsTerminal ? snapshot.State.ToString().ToUpperInvariant() : snapshot.Progress.ToString("P0");
        ObservationText.Text = snapshot.ObservedAt.HasValue ? $"Observed {snapshot.ObservedAt.Value.ToLocalTime():HH:mm:ss} · logical volume allocation" : "Reading the current volume layout";
        PauseButton.IsEnabled = CancelButton.IsEnabled = !snapshot.IsTerminal && _job != Guid.Empty; PauseButton.Text = snapshot.State == JobState.Paused ? "Resume" : "Pause";
        if (snapshot.Files != null) FileList.ItemsSource = snapshot.Files.Select(f => new FileRow(f.Path, f.Extents, Format.Bytes(f.Bytes), f.Status)).ToArray();
        WarningsText.Text = string.Join("\n", snapshot.Warnings ?? []);
        FooterStatus.Text = $"●  {snapshot.Volume} · {snapshot.State} · worker-owned job";
    }
    private async void OnAnalyze(object? sender, EventArgs e) => await Submit(Operation.Analyze, true);
    private async void OnPreview(object? sender, EventArgs e) => await Submit(Policies[PolicyPicker.SelectedIndex].Operation, true);
    private async void OnOptimize(object? sender, EventArgs e) => await Submit(Policies[PolicyPicker.SelectedIndex].Operation, false);
    private async void OnPause(object? sender, EventArgs e) => await Control(_snapshot?.State == JobState.Paused ? "resume" : "pause");
    private async void OnCancel(object? sender, EventArgs e) => await Control("cancel");
    private async Task Control(string action) { try { if (_job != Guid.Empty) await _client.Send(new(action, Id: _job)); } catch (Exception e) { await DisplayAlertAsync("Job control", e.Message, "Close"); } }
    private async void OnRefreshVolumes(object? sender, EventArgs e) => await RefreshVolumes();
    private void OnOverview(object? sender, EventArgs e) { SelectedPath.Text = ""; OnResetZoom(sender, e); }
    private void OnZoomIn(object? sender, EventArgs e) { _map.Zoom(true); DiskMap.Invalidate(); UpdateRange(); }
    private void OnZoomOut(object? sender, EventArgs e) { _map.Zoom(false); DiskMap.Invalidate(); UpdateRange(); }
    private void OnResetZoom(object? sender, EventArgs e) { _map.Reset(); DiskMap.Invalidate(); UpdateRange(); }
    private void UpdateRange() => MapRange.Text = $"MAP CELLS {_map.Start:N0} – {Math.Min(_map.Cells.Length, (long)_map.Start + _map.Count):N0} / {_map.Cells.Length:N0}";
    private void OnMapClick(object? sender, TouchEventArgs e) { if (e.Touches.Length > 0) { _map.Anchor = Math.Max(0, _map.Hit(e.Touches[0].X, e.Touches[0].Y)); OnMapHover(sender, e); } }
    private void OnMapHover(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0) return;
        int index = _map.Hit(e.Touches[0].X, e.Touches[0].Y); if (index < 0) return;
        var c = _map.Cells[index]; double n = Math.Max(1, c.Clusters);
        CellDetail.Text = $"Cell {index:N0} · {c.Clusters:N0} clusters · {c.Allocated / n:P0} allocated · {c.Fragmented / n:P0} fragmented · {c.Metadata / n:P0} metadata";
    }
    private void OnResourceChanged(object? sender, ValueChangedEventArgs e)
    { if (CpuValue == null || MemoryValue == null || IoValue == null || IoSlider == null) return; CpuValue.Text = $"{(int)CpuSlider.Value}%"; MemoryValue.Text = $"{(int)MemorySlider.Value:N0} MiB"; IoValue.Text = $"{(int)IoSlider.Value} MiB/s"; }
    private void OnPresetChanged(object? sender, EventArgs e)
    {
        if (ResourcePreset.SelectedIndex < 0 || CpuSlider == null) return;
        var p = ResourcePreset.SelectedIndex switch { 0 => ResourcePolicy.Quiet, 2 => ResourcePolicy.Performance, _ => ResourcePolicy.Balanced };
        CpuSlider.Value = p.CpuPercent; MemorySlider.Value = p.MemoryMiB; IoSlider.Value = p.IoMiBPerSecond; IdleSwitch.IsToggled = p.IdleOnly; AcSwitch.IsToggled = p.AcOnly; BackgroundSwitch.IsToggled = p.Background;
    }
    private void OnFileSelected(object? sender, SelectionChangedEventArgs e) { if (e.CurrentSelection.FirstOrDefault() is FileRow row) { SelectedPath.Text = row.Path; CellDetail.Text = row.Path; } }
    private async void OnExport(object? sender, EventArgs e)
    {
        if (_snapshot == null) return;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"Tedd-Defrag-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try { await File.WriteAllTextAsync(path, JsonSerializer.Serialize(_snapshot, new JsonSerializerOptions(JobStore.Json) { WriteIndented = true })); await DisplayAlertAsync("Report saved", path, "Close"); }
        catch (Exception ex) { await DisplayAlertAsync("Export failed", ex.Message, "Close"); }
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
            int index = Array.IndexOf(titles, choice); if (index >= 0) { _job = jobs[index].Id; await Poll(); }
        }
        catch (Exception ex) { await DisplayAlertAsync("Activity", ex.Message, "Close"); }
    }
    private async void OnSchedules(object? sender, EventArgs e)
    {
        try
        {
            var schedules = (await _client.Send(new("schedules"))).Schedules ?? [];
            string? action = await DisplayActionSheetAsync("Schedules · broker must be running", "Close", null, ["Add weekly schedule", .. schedules.Select(s => $"Remove: {s.Name} ({s.Time:HH:mm})")]);
            if (action == "Add weekly schedule")
            {
                string? name = await DisplayPromptAsync("Weekly schedule", "Schedule name", initialValue: "Weekly maintenance"); if (string.IsNullOrWhiteSpace(name)) return;
                string? day = await DisplayActionSheetAsync("Day", "Cancel", null, Enum.GetNames<DayOfWeek>()); if (!Enum.TryParse<DayOfWeek>(day, out var weekday)) return;
                string? at = await DisplayPromptAsync("Local start time", "24-hour time (HH:mm)", initialValue: "02:00"); if (!TimeOnly.TryParse(at, out var time)) return;
                var request = Request(Policies[PolicyPicker.SelectedIndex].Operation, false);
                if (!await DisplayAlertAsync("Save execution schedule", $"Run {request.Operation} on {request.Volume} every {weekday} at {time:HH:mm}? Resource limits and exclusions are copied from this view.", "Save schedule", "Cancel")) return;
                await _client.Send(new("schedule-add", Schedule: new(name, request, [weekday], time)), startBroker: true);
                await DisplayAlertAsync("Schedule saved", "The persistent broker runs this schedule. Use Install-ScheduledWorker.ps1 to start it automatically at logon.", "Close");
            }
            else if (action?.StartsWith("Remove: ") == true)
            {
                var target = schedules.First(s => action == $"Remove: {s.Name} ({s.Time:HH:mm})"); await _client.Send(new("schedule-remove", Name: target.Name));
            }
        }
        catch (Exception ex) { await DisplayAlertAsync("Schedules", ex.Message, "Close"); }
    }
    private sealed record FileRow(string Path, int Extents, string BytesLabel, string Status);
}
