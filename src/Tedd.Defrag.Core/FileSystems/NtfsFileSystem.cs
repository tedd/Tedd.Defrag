namespace Tedd.Defrag.Core.FileSystems;

public sealed class NtfsFileSystem : FileSystemSupport
{
    public override string Name => "NTFS";
    public override bool Matches(string fileSystem) => fileSystem.Equals(Name, StringComparison.OrdinalIgnoreCase);
    public override bool Supports(Operation operation) => Enum.IsDefined(operation);
    public override bool UsesDirectoryScan => false;
    public override string CoverageDescription => "NTFS records, metadata and named streams are read from the MFT where available.";
}
