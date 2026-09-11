namespace Tedd.Defrag.Core.FileSystems;

public sealed class FatFileSystem : FileSystemSupport
{
    public override string Name => "FAT";
    public override bool Matches(string fileSystem) => fileSystem.Equals("FAT", StringComparison.OrdinalIgnoreCase) ||
        fileSystem.Equals("FAT12", StringComparison.OrdinalIgnoreCase) || fileSystem.Equals("FAT16", StringComparison.OrdinalIgnoreCase) ||
        fileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase);
    public override bool Supports(Operation operation) => operation is Operation.Analyze or Operation.ReTrim or Operation.WindowsDefrag or Operation.Automatic;
    public override bool UsesDirectoryScan => true;
    public override string CoverageDescription => "FAT file coverage includes accessible file data. Directory allocation and filesystem metadata are not enumerated; fragmentation totals describe observed files only.";
}
