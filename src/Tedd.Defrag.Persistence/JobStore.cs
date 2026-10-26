using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Tedd.Defrag.Core;

namespace Tedd.Defrag.Persistence;

public sealed class JobStore
{
    private readonly ConcurrentDictionary<Guid, JobSnapshot> _snapshots = new();
    private readonly bool _retainDetails;
    internal ConcurrentDictionary<Guid, LayoutExplorerStore> Layouts { get; } = new();
    public void Forget(Guid id) { _snapshots.TryRemove(id, out _); Layouts.TryRemove(id, out _); }
    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = false, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    public string Root { get; }
    public JobStore(string? root = null, bool retainDetails = true)
    {
        _retainDetails = retainDetails;
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tedd.Defrag");
        Directory.CreateDirectory(Path.Combine(Root, "jobs"));
    }
    public string JobDirectory(Guid id) => Path.Combine(Root, "jobs", id.ToString("N"));
    public void SaveRequest(JobRequest request)
    {
        request.Validate(); Directory.CreateDirectory(JobDirectory(request.Id));
        string path = Path.Combine(JobDirectory(request.Id), "request.json");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, request, Json); stream.Flush(true);
    }
    public JobRequest ReadRequest(Guid id) => Read<JobRequest>(Path.Combine(JobDirectory(id), "request.json")) ?? throw new FileNotFoundException("Job request was not found.");
    public string ReportPath(Guid id) => Path.Combine(JobDirectory(id), "report.json");
    public void Save(JobSnapshot snapshot)
    {
        if (_retainDetails) _snapshots[snapshot.Id] = snapshot with { Map = snapshot.Map?.ToArray() };
        var compact = Compact(snapshot);
        AtomicWrite(Path.Combine(JobDirectory(snapshot.Id), "snapshot.json"), compact);
        if (snapshot.IsTerminal) AtomicWrite(ReportPath(snapshot.Id), compact);
    }
    public JobSnapshot? ReadReport(Guid id) => _snapshots.TryGetValue(id, out var snapshot) && snapshot.IsTerminal
        ? snapshot with { Map = null } : Read<JobSnapshot>(ReportPath(id));
    public JobSnapshot? ReadSnapshot(Guid id)
    {
        if (_snapshots.TryGetValue(id, out var current)) return current;
        string path = Path.Combine(JobDirectory(id), "snapshot.json");
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = Read<JobSnapshot>(path);
            if (snapshot != null) return snapshot;
            // A delete-pending name can briefly disappear to new Windows opens during replacement.
            // Retry the observation instead of treating an existing job as missing.
            if (!Directory.Exists(JobDirectory(id))) return null;
            Thread.Sleep(5 << Math.Min(attempt, 4));
        }
        return null;
    }
    public JobSnapshot[] List()
    {
        var result = new List<JobSnapshot>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Root, "jobs"), "snapshot.json", SearchOption.AllDirectories))
        { var snapshot = Read<JobSnapshot>(path); if (snapshot != null) result.Add(snapshot); }
        return result.OrderByDescending(s => s.UpdatedAt).ToArray();
    }
    public void Control(Guid id, string command)
    {
        if (command is not ("pause" or "resume" or "cancel")) throw new ArgumentException("Unknown job control.");
        if (!Directory.Exists(JobDirectory(id))) throw new FileNotFoundException("Unknown job.");
        AtomicWrite(Path.Combine(JobDirectory(id), "control.json"), command);
    }
    public string? ReadControl(Guid id) => Read<string>(Path.Combine(JobDirectory(id), "control.json"));
    public void Journal(Guid id, MoveJournalEntry entry)
    {
        if (entry.Phase == "verified" && Layouts.TryGetValue(id, out var layout)) layout.Apply(entry.Move);
    }
    internal static JobSnapshot Compact(JobSnapshot snapshot) => snapshot with
    {
        Map = null, Files = null, CompressedFiles = null,
        Message = Clip(snapshot.Message), CompressionCurrentPath = null,
        Warnings = snapshot.Warnings?.Take(16).Select(Clip).ToArray()
    };
    private static string Clip(string value) => value.Length <= 512 ? value : value[..512];
    public void AppendMaintenanceLog(Guid id, string line)
    {
        string path = Path.Combine(JobDirectory(id), "maintenance.log");
        string text = (line.Length <= 4096 ? line : line[..4096]) + Environment.NewLine;
        if (File.Exists(path) && new FileInfo(path).Length + System.Text.Encoding.UTF8.GetByteCount(text) > 64 * 1024)
            File.WriteAllText(path, text);
        else File.AppendAllText(path, text);
    }
    public ConcurrencySettings Settings => Read<ConcurrencySettings>(Path.Combine(Root, "settings.json")) ?? new();
    public void SaveSettings(ConcurrencySettings settings)
    {
        if (settings.MaxConcurrentVolumes is < 1 or > 32 || settings.MaxConcurrentJobsPerSharedResource is < 1 or > 8) throw new ArgumentException("Invalid concurrency limits.");
        AtomicWrite(Path.Combine(Root, "settings.json"), settings);
    }
    public DesktopSettings DesktopSettings => Read<DesktopSettings>(Path.Combine(Root, "desktop-settings.json")) ?? new();
    public void SaveDesktopSettings(DesktopSettings settings) => AtomicWrite(Path.Combine(Root, "desktop-settings.json"), settings);
    public static T? Read<T>(string path)
    {
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return JsonSerializer.Deserialize<T>(stream, Json); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return default; }
    }
    public static void AtomicWrite<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temporary, path, null, ignoreMetadataErrors: true);
                    else File.Move(temporary, path);
                    break;
                }
                catch (Exception e) when (attempt < 7 && e is IOException or UnauthorizedAccessException)
                {
                    // Windows filters/readers can briefly retain a delete-pending destination.
                    // Preserve the complete temporary file and retry publication, never rewrite in place.
                    Thread.Sleep(Math.Min(100, 5 << attempt));
                }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
