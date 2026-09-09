using System.Buffers.Binary;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Tedd.Defrag.Core;

public sealed record BrokerCommand(string Action, JobRequest? Job = null, Guid Id = default, SchedulerSettings? Settings = null, ScheduleDefinition? Schedule = null, string? Name = null);
public sealed record BrokerReply(bool Success, string? Error = null, Guid Id = default, JobSnapshot? Snapshot = null,
    JobSnapshot[]? Jobs = null, SchedulerSettings? Settings = null, ScheduleDefinition[]? Schedules = null);
public static class BrokerProtocol
{
    public static string PipeName => "Tedd.Defrag." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..24];
    public static async Task Write<T>(Stream stream, T data, JsonSerializerOptions options, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, options);
        if (bytes.Length > 16 * 1024 * 1024) throw new IOException("Broker message exceeds the size limit.");
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await stream.WriteAsync(length, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<T> Read<T>(Stream stream, JsonSerializerOptions options, CancellationToken token)
    {
        byte[] length = new byte[4]; await stream.ReadExactlyAsync(length, token);
        int n = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (n is <= 0 or > 16 * 1024 * 1024) throw new IOException("Invalid broker message length.");
        byte[] bytes = new byte[n]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, options) ?? throw new IOException("Empty broker message.");
    }
}
