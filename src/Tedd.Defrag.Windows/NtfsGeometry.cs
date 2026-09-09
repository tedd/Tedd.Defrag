using System.Runtime.InteropServices;
using Tedd.Defrag.Core;
using Windows.Win32.System.Ioctl;

namespace Tedd.Defrag.Windows;

internal readonly record struct NtfsGeometry(long TotalClusters, int SectorSize, int ClusterSize, int RecordSize, long MftLength, ClusterRange MftZone)
{
    internal static NtfsGeometry Parse(ReadOnlySpan<byte> response)
    {
        if (response.Length < Marshal.SizeOf<NTFS_VOLUME_DATA_BUFFER>()) throw new IOException("Incomplete NTFS geometry.");
        var data = MemoryMarshal.Read<NTFS_VOLUME_DATA_BUFFER>(response);
        long total = data.TotalClusters, length = data.MftValidDataLength;
        long zoneStart = data.MftZoneStart, zoneEnd = data.MftZoneEnd;
        uint sector = data.BytesPerSector, cluster = data.BytesPerCluster, record = data.BytesPerFileRecordSegment;
        if (total <= 0 || sector is < 512 or > 65536 || (sector & (sector - 1)) != 0
            || cluster < sector || cluster > int.MaxValue || (cluster & (cluster - 1)) != 0
            || record is < 512 or > 65536 || (record & (record - 1)) != 0
            || length <= 0 || length % record != 0 || zoneStart < 0 || zoneEnd < zoneStart || zoneEnd > total)
            throw new IOException("Invalid NTFS geometry.");
        return new(total, (int)sector, (int)cluster, (int)record, length, new(zoneStart, zoneEnd - zoneStart));
    }
}
