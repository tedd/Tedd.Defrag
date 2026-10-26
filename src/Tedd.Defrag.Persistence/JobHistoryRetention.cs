using Tedd.Defrag.Core;

namespace Tedd.Defrag.Persistence;

public static class JobHistoryRetention
{
    public const int MaximumCompletedJobs = 20;
    public const long MaximumBytes = 8 * 1024 * 1024;
    private static readonly string[] JobFiles = ["request.json", "snapshot.json", "report.json", "control.json",
        "layout.bin", "moves.jsonl", "maintenance.log", "error.txt", "worker-error.txt"];

    // Called by the broker after recovery; active and cached workers are explicitly protected.
    public static void Prune(JobStore store, IReadOnlySet<Guid> protectedJobs, DateTimeOffset now)
    {
        var completed = new List<JobSnapshot>();
        foreach (string directory in Directory.EnumerateDirectories(Path.Combine(store.Root, "jobs")))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) || protectedJobs.Contains(id) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                if (JobFiles.Any(name => File.Exists(Path.Combine(directory, name)) &&
                    (File.GetAttributes(Path.Combine(directory, name)) & FileAttributes.ReparsePoint) != 0)) continue;
                var snapshot = JobStore.Read<JobSnapshot>(Path.Combine(directory, "snapshot.json"));
                if (snapshot is not { IsTerminal: true } || snapshot.Id != id) continue;
                var compact = JobStore.Compact(snapshot);
                store.Save(compact);
                foreach (string name in new[] { "layout.bin", "moves.jsonl", "maintenance.log", "error.txt", "worker-error.txt" })
                    DeleteFile(directory, name);
                foreach (string file in Directory.EnumerateFiles(directory, "*.tmp"))
                {
                    string name = Path.GetFileName(file);
                    if (JobFiles.Any(known => name.StartsWith(known + ".", StringComparison.Ordinal)) &&
                        Guid.TryParseExact(name.Split('.')[^2], "N", out _)) DeleteFile(directory, name);
                }
                completed.Add(compact);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        long bytes = 0; int retained = 0;
        foreach (var snapshot in completed.OrderByDescending(s => s.UpdatedAt))
        {
            string directory = store.JobDirectory(snapshot.Id);
            try
            {
                long size = JobFiles.Sum(name => File.Exists(Path.Combine(directory, name)) ? new FileInfo(Path.Combine(directory, name)).Length : 0);
                if (retained >= MaximumCompletedJobs || snapshot.UpdatedAt < now.AddDays(-7) || bytes + size > MaximumBytes)
                {
                    foreach (string name in JobFiles) DeleteFile(directory, name);
                    // Unknown files and subdirectories are preserved.
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                    store.Forget(snapshot.Id);
                }
                else { retained++; bytes += size; store.Forget(snapshot.Id); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void DeleteFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path);
    }
}
