using System.Diagnostics;
using Tedd.Defrag.Core;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Maintenance;

public static class WindowsMaintenance
{
    public static string Validate(JobRequest request, VolumeInfo volume)
    {
        FileSystemCapabilities.Validate(volume.FileSystem, request.Operation);
        if (request.Exclusions.Length != 0 && request.Operation is not Operation.ReTrim)
            throw new NotSupportedException("Windows volume optimization cannot enforce file exclusions. Select a custom layout policy.");
        if (request.SelectedPaths.Length != 0) throw new NotSupportedException("This maintenance operation applies to the entire volume.");
        if (request.Operation == Operation.ReTrim && volume.TrimEnabled == false) throw new NotSupportedException("The storage stack reports that TRIM is unavailable.");
        if (request.Operation == Operation.WindowsDefrag && (request.MaxMoveBytes != 0 || request.MinimumFileBytes != 0 ||
            request.MaximumFileBytes != 0 || request.MinimumFragments != 20))
            throw new NotSupportedException("Windows defrag cannot enforce relocation-byte budgets, file-size filters, or custom fragment thresholds.");
        return request.Operation switch { Operation.ReTrim => "/L", Operation.SlabConsolidate => "/K", Operation.Automatic => "/O", Operation.WindowsDefrag => "/D", _ => throw new NotSupportedException() };
    }

    public static void Run(JobRequest request, VolumeInfo volume, Action<string> report, Action checkpoint, CancellationToken token)
    {
        string flag = Validate(request, volume);
        if (request.Preview) throw new InvalidOperationException("A maintenance preview cannot submit storage changes.");
        using var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "defrag.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add(VolumeDiscovery.Root(volume.Root)[..2]); process.StartInfo.ArgumentList.Add(flag); process.StartInfo.ArgumentList.Add("/U");
        process.StartInfo.ArgumentList.Add("/V");
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && lines.Count < 100) lines.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && lines.Count < 100) lines.Enqueue(e.Data); };
        token.ThrowIfCancellationRequested(); checkpoint(); process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        string? lastLine = null;
        try
        {
            while (!process.WaitForExit(250))
            {
                token.ThrowIfCancellationRequested();
                // An external optimizer cannot be suspended safely. Stop it if an execution condition changes.
                if (!ActivityGate.MayRun(request.Resources, out var reason)) throw new OperationCanceledException(reason);
                checkpoint();
                Drain();
            }
            process.WaitForExit();
            Drain();
            token.ThrowIfCancellationRequested(); checkpoint();
            if (process.ExitCode != 0) throw new IOException($"Windows {request.Operation} on {volume.FileSystem} exited with code 0x{process.ExitCode:X8}. {lastLine}");
            report("Windows maintenance completed. Host-side reclaimed capacity is unknown.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        }
        void Drain()
        {
            while (lines.TryDequeue(out var line)) { lastLine = line; report(line); }
        }
    }
}
