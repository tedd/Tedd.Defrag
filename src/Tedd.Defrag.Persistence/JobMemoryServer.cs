using System.IO.Pipes;
using Tedd.Defrag.Core;

namespace Tedd.Defrag.Persistence;

/// <summary>Serves detailed results from the isolated execution process without disk caches.</summary>
public static class JobMemoryServer
{
    private static string PipeName(Guid id) => BrokerProtocol.PipeName + ".job." + id.ToString("N");

    public static async Task Listen(JobStore store, Guid id, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(PipeName(id), PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var command = await BrokerProtocol.Read<BrokerCommand>(pipe, JobStore.Json, timeout.Token);
                BrokerReply reply;
                try
                {
                    if (command.Id != id) throw new ArgumentException("Job identity mismatch.");
                    reply = command.Action switch
                    {
                        "get" => new(true, Snapshot: store.ReadSnapshot(id)),
                        "explore" => new(true, Region: new LayoutExplorerStore(store).Explore(id, command.StartCluster,
                            command.ClusterCount, command.MapCells, command.IncludeFiles, command.Path, command.FileId, command.Stream)),
                        _ => new(false, "Unknown job query.")
                    };
                }
                catch (Exception e) { reply = new(false, e.Message); }
                await BrokerProtocol.Write(pipe, reply, JobStore.Json, timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
        }
    }

    public static async Task<BrokerReply> Query(BrokerCommand command, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName(command.Id), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(500, token);
        await BrokerProtocol.Write(pipe, command, JobStore.Json, token);
        return await BrokerProtocol.Read<BrokerReply>(pipe, JobStore.Json, token);
    }
}
