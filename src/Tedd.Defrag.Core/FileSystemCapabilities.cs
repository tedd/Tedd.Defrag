using Tedd.Defrag.Core.FileSystems;

namespace Tedd.Defrag.Core;

public static class FileSystemCapabilities
{
    private static readonly FileSystemSupport[] FileSystems = [new NtfsFileSystem(), new RefsFileSystem(), new FatFileSystem()];
    public static FileSystemSupport? Find(string fileSystem) => FileSystems.FirstOrDefault(fs => fs.Matches(fileSystem));
    public static FileSystemSupport Get(string fileSystem) => Find(fileSystem) ?? throw new NotSupportedException("Select an NTFS, ReFS, FAT or FAT32 volume.");
    public static bool IsNtfs(string fileSystem) => Find(fileSystem) is NtfsFileSystem;
    public static bool IsRefs(string fileSystem) => Find(fileSystem) is RefsFileSystem;
    public static bool IsFat(string fileSystem) => Find(fileSystem) is FatFileSystem;
    public static bool IsSupported(string fileSystem) => Find(fileSystem) != null;
    public static bool IsWindowsMaintenance(Operation operation) => operation is
        Operation.ReTrim or Operation.SlabConsolidate or Operation.Automatic or Operation.WindowsDefrag;

    public static bool Supports(string fileSystem, Operation operation) => Find(fileSystem)?.Supports(operation) == true;

    public static void Validate(string fileSystem, Operation operation)
    {
        var support = Get(fileSystem);
        if (!support.Supports(operation)) throw new NotSupportedException(
            $"{operation} is unavailable on {fileSystem}; custom placement and metadata optimization require NTFS. Supported operations: {string.Join(", ", Enum.GetValues<Operation>().Where(support.Supports))}. Windows determines maintenance availability for the volume.");
    }
}
