namespace Tedd.Defrag.Core;

public static class FileSystemCapabilities
{
    public static bool IsNtfs(string fileSystem) => fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    public static bool IsRefs(string fileSystem) => fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
    public static bool IsSupported(string fileSystem) => IsNtfs(fileSystem) || IsRefs(fileSystem);
    public static bool IsWindowsMaintenance(Operation operation) => operation is
        Operation.ReTrim or Operation.SlabConsolidate or Operation.Automatic or Operation.WindowsDefrag;

    public static bool Supports(string fileSystem, Operation operation) => Enum.IsDefined(operation) &&
        (IsNtfs(fileSystem) || IsRefs(fileSystem) && (operation == Operation.Analyze || IsWindowsMaintenance(operation)));

    public static void Validate(string fileSystem, Operation operation)
    {
        if (!IsSupported(fileSystem)) throw new NotSupportedException("Select an NTFS or ReFS volume.");
        if (!Supports(fileSystem, operation)) throw new NotSupportedException(
            $"{operation} requires NTFS. For ReFS, select Analyze, ReTrim, Automatic, SlabConsolidate, or WindowsDefrag. Windows determines maintenance availability for the volume.");
    }
}
