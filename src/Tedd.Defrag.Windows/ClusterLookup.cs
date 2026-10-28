using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;
using Tedd.Defrag.Core;
using Windows.Win32;

namespace Tedd.Defrag.Windows;

/// <summary>Resolves current NTFS cluster owners without an analyzed layout or writable volume handle.</summary>
public static class ClusterLookup
{
    private const int MaximumClusters = 65536;
    private const int MaximumBuffer = 16 * 1024 * 1024;

    public static MapRegion Query(string root, long start, long length, CancellationToken token = default)
    {
        ValidateRange(start, length);
        token.ThrowIfCancellationRequested();
        if (!new DriveInfo(VolumeDiscovery.Root(root)).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Live cluster lookup is available on NTFS volumes. Analyze this volume again to inspect its files.");
        using var volume = new NtfsVolume(root);
        if (start >= volume.TotalClusters || length > volume.TotalClusters - start)
            throw new ArgumentOutOfRangeException(nameof(start), "The selected clusters are outside the current volume.");
        byte[] input = new byte[8 + (int)length * 8];
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(4), (int)length);
        for (int i = 0; i < length; i++) BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(8 + i * 8), start + i);
        byte[] output = new byte[1024 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int n = NativeIo.Control(volume.Handle, PInvoke.FSCTL_LOOKUP_STREAM_FROM_CLUSTER, input, output, out int error);
            if (error != 0 && error != 234 && error != 122) throw new Win32Exception(error);
            if (n < 12 && error is not (234 or 122)) throw new IOException("The cluster lookup returned an incomplete response.");
            uint required = n >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(8)) : 0;
            if (required > MaximumBuffer)
                throw new InvalidOperationException("Too many cluster owners to display. Zoom in and select a smaller range.");
            if (required > output.Length || error is 234 or 122)
            {
                if (output.Length == MaximumBuffer)
                    throw new InvalidOperationException("Too many cluster owners to display. Zoom in and select a smaller range.");
                output = new byte[Math.Min(MaximumBuffer, Math.Max((int)required, output.Length * 2))];
                continue;
            }
            token.ThrowIfCancellationRequested();
            return Parse(output.AsSpan(0, n), VolumeDiscovery.Root(root), start, length, volume.TotalClusters);
        }
    }

    internal static void ValidateRange(long start, long length)
    {
        if (start < 0 || length <= 0 || start > long.MaxValue - length)
            throw new ArgumentOutOfRangeException(nameof(start), "Invalid cluster range.");
        if (length > MaximumClusters)
            throw new InvalidOperationException($"Live lookup supports up to {MaximumClusters:N0} clusters. Zoom in and select a smaller range.");
    }

    internal static MapRegion Parse(ReadOnlySpan<byte> data, string root, long start, long length, long totalClusters)
    {
        ValidateRange(start, length);
        if (data.Length < 12) throw new IOException("Short cluster lookup response.");
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint matches = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        uint required = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (required > data.Length || matches > (data.Length - 12) / 26 || (offset == 0) != (matches == 0))
            throw new IOException("Incomplete cluster lookup response.");
        var owners = new Dictionary<string, (uint Flags, HashSet<long> Clusters)>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < matches; i++)
        {
            if (offset < 12 || offset > data.Length - 26 || (offset & 1) != 0)
                throw new IOException("Invalid cluster lookup entry offset.");
            var entry = data[(int)offset..];
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            long cluster = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
            if (cluster < start || cluster >= start + length ||
                (i + 1 < matches ? next < 26 || next > entry.Length - 26 || (next & 1) != 0 : next != 0))
                throw new IOException("Invalid cluster lookup entry.");
            var nameBytes = entry.Slice(24, next == 0 ? entry.Length - 24 : (int)next - 24);
            int end = 0;
            while (end + 1 < nameBytes.Length && (nameBytes[end] != 0 || nameBytes[end + 1] != 0)) end += 2;
            if (end == 0 || end + 1 >= nameBytes.Length || end > 65536)
                throw new IOException("Invalid cluster owner name.");
            string name = Encoding.Unicode.GetString(nameBytes[..end]);
            if (!owners.TryGetValue(name, out var owner)) owner = (flags, new HashSet<long>());
            owner.Clusters.Add(cluster);
            owners[name] = (owner.Flags | flags, owner.Clusters);
            offset += next;
        }
        var files = new List<ClusterFile>();
        int textBytes = 0;
        foreach (var (name, owner) in owners)
        {
            var (path, stream, attribute) = SplitName(root, name);
            int bytes = Encoding.UTF8.GetByteCount(path) + Encoding.UTF8.GetByteCount(stream);
            if (files.Count == 500 || textBytes + bytes > 1_000_000) break;
            string status = (owner.Flags & 1) != 0 ? "Page file" : (owner.Flags & 0xC) != 0 ? "Filesystem metadata"
                : (owner.Flags & 2) != 0 ? "Locked from defragmentation" : attribute == "$DATA" ? "Live lookup" : attribute;
            // The owner query does not provide file identity, total allocation or size.
            files.Add(new(0, path, stream, -1, -1, owner.Clusters.Count, -1, status));
            textBytes += bytes;
        }
        return new(start, length, totalClusters, [], files.ToArray(), owners.Count, IsLive: true);
    }

    private static (string Path, string Stream, string Attribute) SplitName(string root, string name)
    {
        int first = name.IndexOf(':'), last = name.LastIndexOf(':');
        string path = first < 0 ? name : name[..first];
        string stream = first < 0 || first == last || last == first + 1 ? "" : name[first..last];
        string attribute = last < 0 ? "Filesystem metadata" : name[(last + 1)..];
        return (root + path.TrimStart('\\'), stream, attribute);
    }
}
