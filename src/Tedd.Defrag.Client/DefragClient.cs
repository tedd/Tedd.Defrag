using System.Diagnostics;
using System.IO.Pipes;
using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;

namespace Tedd.Defrag.Client;

public sealed class DefragClient
{
    public async Task<BrokerReply> Send(BrokerCommand command, bool startBroker = false, CancellationToken token = default)
    {
        try { return await Connect(command, token); }
        catch (TimeoutException) when (!startBroker && command.Action is "list" or "get" or "schedules" || !startBroker && command.Action == "settings" && command.Settings == null)
        {
            var store = new JobStore();
            return command.Action switch
            {
                "list" => new(true, Jobs: store.List().Select(s => s with { Map = null, Files = null }).ToArray()),
                "get" => new(true, Snapshot: store.ReadSnapshot(command.Id)),
                "schedules" => new(true, Schedules: store.Schedules),
                _ => new(true, Settings: store.Settings)
            };
        }
        catch (TimeoutException) when (startBroker)
        {
            var start = WorkerStart("--broker");
            start.UseShellExecute = true; start.WindowStyle = ProcessWindowStyle.Hidden;
            start.Verb = "runas";
            using var process = Process.Start(start) ?? throw new IOException("Worker could not be started.");
            for (int i = 0; i < 60; i++)
            {
                token.ThrowIfCancellationRequested();
                try { return await Connect(command, token); }
                catch (TimeoutException) { await Task.Delay(250, token); }
            }
            throw new TimeoutException("Worker did not become available.");
        }
    }
    private static async Task<BrokerReply> Connect(BrokerCommand command, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", BrokerProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(500, token);
        await BrokerProtocol.Write(pipe, command, JobStore.Json, token);
        var reply = await BrokerProtocol.Read<BrokerReply>(pipe, JobStore.Json, token);
        if (!reply.Success) throw new InvalidOperationException(reply.Error);
        return reply;
    }
    public static ProcessStartInfo WorkerStart(params string[] args)
    {
        string? configured = Environment.GetEnvironmentVariable("TEDD_DEFRAG_WORKER");
        string? worker = !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured : null;
        worker ??= new[] { Path.Combine(AppContext.BaseDirectory, "Tedd.Defrag.Worker.exe"), Path.Combine(AppContext.BaseDirectory, "worker", "Tedd.Defrag.Worker.exe"), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "worker", "Tedd.Defrag.Worker.exe")) }.FirstOrDefault(File.Exists);
        // Development checkout: locate an actually built worker, never build or execute downloaded code implicitly.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); worker == null && directory != null; directory = directory.Parent)
        {
            string project = Path.Combine(directory.FullName, "src", "Tedd.Defrag.Worker", "bin");
            if (!Directory.Exists(project)) continue;
            worker = Directory.EnumerateFiles(project, "Tedd.Defrag.Worker.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        if (worker == null) throw new FileNotFoundException("Build Tedd.Defrag.Worker or place its published folder beside the application as 'worker'.");
        var info = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(worker)! };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }
}
