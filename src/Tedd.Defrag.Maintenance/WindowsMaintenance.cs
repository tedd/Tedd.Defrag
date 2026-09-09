using System.Diagnostics;
using Tedd.Defrag.Core;
using Tedd.Defrag.Windows;

namespace Tedd.Defrag.Maintenance;

public static class WindowsMaintenance
{
    public static void Run(JobRequest request, VolumeInfo volume, Action<string> report, Action checkpoint, CancellationToken token)
    {
        if (request.Exclusions.Length != 0 && request.Operation is not Operation.ReTrim)
            throw new NotSupportedException("Windows volume optimization cannot enforce file exclusions. Select a custom layout policy.");
        if (request.SelectedPaths.Length != 0) throw new NotSupportedException("This maintenance operation applies to the entire volume.");
        if (request.Operation == Operation.ReTrim && volume.TrimEnabled == false) throw new NotSupportedException("The storage stack reports that TRIM is unavailable.");
        string flag = request.Operation switch { Operation.ReTrim => "/L", Operation.SlabConsolidate => "/K", Operation.Automatic => "/O", _ => throw new NotSupportedException() };
        using var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "defrag.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add(volume.Root[..2]); process.StartInfo.ArgumentList.Add(flag); process.StartInfo.ArgumentList.Add("/U");
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && lines.Count < 100) lines.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && lines.Count < 100) lines.Enqueue(e.Data); };
        checkpoint(); process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            while (!process.WaitForExit(250))
            {
                token.ThrowIfCancellationRequested();
                // An external optimizer cannot be suspended safely. Stop it if an execution condition changes.
                if (!ActivityGate.MayRun(request.Resources, out var reason)) throw new OperationCanceledException(reason);
                checkpoint();
                while (lines.TryDequeue(out var line)) report(line);
            }
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException($"Windows optimization exited with code {process.ExitCode}.");
            report("Windows maintenance completed. Host-side reclaimed capacity is unknown.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        }
    }
}
