using System.Buffers.Binary;
using System.Management;
using System.Security.Principal;
using Tedd.Defrag.Core;
using Windows.Win32;

namespace Tedd.Defrag.Windows;

public static class VolumeDiscovery
{
    public static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static string Root(string input)
    {
        string path = input.Trim().Replace('/', '\\');
        if (path.Length == 2 && path[1] == ':') path += "\\";
        if (path.Length != 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            throw new ArgumentException("Select a local drive root, for example D:\\.");
        return path.ToUpperInvariant();
    }
    public static string Device(string root) => @"\\.\" + Root(root)[..2];
    public static VolumeInfo[] List() => DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
        .Select(d => Get(d.Name)).ToArray();
    public static unsafe VolumeInfo Get(string input)
    {
        string root = Root(input); var drive = new DriveInfo(root);
        string id = root; char[] guid = new char[128];
        if (PInvoke.GetVolumeNameForVolumeMountPoint(root, guid)) id = new string(guid).TrimEnd('\0');
        uint sectors = 0, sectorBytes = 0, freeClusters = 0, total = 0;
        int clusterSize = PInvoke.GetDiskFreeSpace(root, out sectors, out sectorBytes, out freeClusters, out total) ? checked((int)(sectors * sectorBytes)) : 4096;
        bool? seek = null, trim = null; string[] resources = ["unresolved-storage"]; string confidence = "Unknown; conservatively serialized";
        try
        {
            using var handle = NativeIo.Open(Device(root));
            byte[] extents = new byte[65536];
            int n = NativeIo.Control(handle, PInvoke.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, [], extents);
            if (n >= 8)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(extents);
                if (count is > 0 and <= 1024 && 8L + count * 24 <= n)
                {
                    resources = new string[count];
                    for (int i = 0; i < count; i++) resources[i] = "disk:" + BinaryPrimitives.ReadUInt32LittleEndian(extents.AsSpan(8 + i * 24));
                    confidence = "OS disk extents; deeper virtual/RAID backing may be unknown";
                }
            }
            seek = QueryBoolean(handle, 7); trim = QueryBoolean(handle, 8);
            // Disk extents may describe a virtual disk, not its backing disks. Mark such devices unresolved.
            using var search = new ManagementObjectSearcher("SELECT Index,Model,InterfaceType FROM Win32_DiskDrive");
            using var disks = search.Get();
            foreach (ManagementObject disk in disks)
            {
                using (disk)
                {
                    string model = disk["Model"]?.ToString() ?? "";
                    if (resources.Contains("disk:" + disk["Index"]) && (model.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || model.Contains("Storage Space", StringComparison.OrdinalIgnoreCase) || model.Contains("RAID", StringComparison.OrdinalIgnoreCase)))
                    { resources = [.. resources, "unresolved-storage"]; confidence = "Virtual/RAID backing unresolved; manual groups recommended"; }
                }
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ManagementException or UnauthorizedAccessException) { }
        return new(id, root, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace, clusterSize, seek, trim, resources, confidence);
    }
    private static bool? QueryBoolean(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int property)
    {
        Span<byte> input = stackalloc byte[12]; input.Clear(); BinaryPrimitives.WriteInt32LittleEndian(input, property);
        Span<byte> output = stackalloc byte[64];
        int count = NativeIo.Control(handle, PInvoke.IOCTL_STORAGE_QUERY_PROPERTY, input, output, out int error);
        return error == 0 && count >= 9 ? output[8] != 0 : null;
    }
}
