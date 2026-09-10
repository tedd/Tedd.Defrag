using Tedd.Defrag.Client;
using Tedd.Defrag.Core;
using Tedd.Defrag.Visualization;
using Tedd.Defrag.Windows;
using Tedd.TUI;
using Tedd.TUI.Media;
using Tedd.TUI.Platform.Console;
using Tedd.TUI.Platform.WindowsTerminal;

namespace Tedd.Defrag.Cli;

internal sealed class TerminalDashboard : UIElement
{
    private readonly DefragClient _client = new();
    private readonly string _volume;
    private readonly VolumeInfo _volumeInfo;
    private JobSnapshot? _snapshot;
    private string _status = "A analyze   D preview defrag   P pause/resume   X cancel   Q detach";
    private Guid _job;
    private TuiApp? _app;
    private readonly CancellationTokenSource _stop = new();
    private int _busy;
    private static readonly TuiColor BackgroundColor = new(10, 17, 27), Muted = new(123, 144, 163), Accent = new(75, 217, 208), White = new(228, 239, 245);
    private TerminalDashboard(VolumeInfo volume) { _volume = volume.Root; _volumeInfo = volume; Focusable = true; }
    public static void Run(string volume)
    {
        var info = VolumeDiscovery.Get(volume);
        if (!info.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Select an NTFS volume.");
        var dashboard = new TerminalDashboard(info);
        var window = new TuiWindow { Content = dashboard };
        var app = new TuiApp(window, new WindowsTerminalPlatform()); dashboard._app = app;
        _ = dashboard.Poll();
        try { app.Run(); } finally { dashboard._stop.Cancel(); }
    }
    protected override Size MeasureOverride(Size availableSize) => availableSize;
    public override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is ConsoleKey.Q or ConsoleKey.Escape) { _app?.Stop(); return; }
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (e.Key is ConsoleKey.A or ConsoleKey.D)
                {
                    var request = new JobRequest { Volume = _volume, Operation = e.Key == ConsoleKey.A ? Operation.Analyze : Operation.MinimumWrite,
                        Preview = true, Resources = ResourcePolicy.Performance };
                    _job = (await _client.Send(new("submit", Job: request), startBroker: true)).Id;
                }
                else if (_job != Guid.Empty && e.Key == ConsoleKey.P) await _client.Send(new(_snapshot?.State == JobState.Paused ? "resume" : "pause", Id: _job));
                else if (_job != Guid.Empty && e.Key == ConsoleKey.X) await _client.Send(new("cancel", Id: _job));
                _status = "A analyze   D preview defrag   P pause/resume   X cancel   Q detach";
            }
            catch (Exception ex) { _status = ex.Message; }
            finally { Volatile.Write(ref _busy, 0); Invalidate(); }
        });
        e.Handled = true;
    }
    private async Task Poll()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (_job != Guid.Empty) { var reply = await _client.Send(new("get", Id: _job), token: _stop.Token); Volatile.Write(ref _snapshot, reply.Snapshot); Invalidate(); }
                await Task.Delay(200, _stop.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { _status = e.Message; await Task.Delay(500); }
        }
    }
    public override void Render(VirtualBuffer buffer, int offsetX, int offsetY)
    {
        int w = buffer.Width, h = buffer.Height; var snapshot = Volatile.Read(ref _snapshot);
        buffer.FillRect(0, 0, w, h, ' ', White, BackgroundColor);
        Text(3, 1, "▦  TEDD / DEFRAG", Accent); Text(Math.Max(25, w - 31), 1, ".NET 11 / NTFS", Muted);
        Text(3, 3, "Storage, in perspective.", White);
        Text(3, 5, $"{_volume}   {snapshot?.State.ToString() ?? "Ready"}   {snapshot?.Message ?? "Press A to analyze this volume"}", Muted);
        Text(3, 7, snapshot?.ObservedAt == null ? $"Awaiting analysis     {Format.Bytes(_volumeInfo.FreeBytes)} free of {Format.Bytes(_volumeInfo.SizeBytes)}" :
            $"{snapshot.TotalFiles:N0} streams     {snapshot.FragmentedFiles:N0} fragmented     {Format.Bytes(snapshot.BytesMoved)} moved     {Format.Bytes(snapshot.FreeBytes)} free", Accent);
        Text(3, 9, "VOLUME ALLOCATION MAP                                      Logical clusters →", Muted);
        int columns = Math.Max(1, (w - 6) / 2), rows = Math.Max(1, h - 17);
        var map = snapshot?.Map;
        if (map != null)
        {
            for (int y = 0; y < rows; y++) for (int x = 0; x < columns; x++)
            {
                int index = (int)((long)(y * columns + x) * map.Length / (columns * rows));
                int end = (int)((long)(y * columns + x + 1) * map.Length / (columns * rows));
                MapCell aggregate = default;
                for (int i = index; i < end; i++)
                {
                    var c0 = map[i]; aggregate = new(aggregate.Clusters + c0.Clusters, aggregate.Allocated + c0.Allocated,
                        aggregate.Fragmented + c0.Fragmented, aggregate.Metadata + c0.Metadata, aggregate.Excluded + c0.Excluded,
                        aggregate.Moving + c0.Moving, aggregate.Verified + c0.Verified);
                }
                uint color = MapAggregator.Color(aggregate);
                var c = new TuiColor((byte)(color >> 16), (byte)(color >> 8), (byte)color);
                buffer.SetPixel(3 + x * 2, 11 + y, '▪', c, BackgroundColor);
            }
        }
        else Text(3, 12, "Press A to reveal the volume allocation map.", Muted);
        Text(3, h - 5, "● allocated   ◼ fragmented   ◆ metadata   · free   □ planned / moving", Muted);
        Text(3, h - 3, _status, Accent);
        Text(3, h - 1, "Q closes this view. Submitted jobs continue in the worker.", Muted);
        void Text(int x, int y, string value, TuiColor color)
        { if (y >= 0 && y < h && x < w) buffer.DrawString(x, y, value.AsSpan(0, Math.Min(value.Length, w - x)), color, BackgroundColor); }
    }
}
