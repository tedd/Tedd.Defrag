namespace Tedd.Defrag.Core.FileSystems;

public sealed class RefsFileSystem : FileSystemSupport
{
    public override string Name => "ReFS";
    public override bool Matches(string fileSystem) => fileSystem.Equals(Name, StringComparison.OrdinalIgnoreCase);
    public override bool Supports(Operation operation) => operation == Operation.Analyze || FileSystemCapabilities.IsWindowsMaintenance(operation);
    public override bool UsesDirectoryScan => true;
    public override string CoverageDescription => "ReFS file coverage includes accessible unnamed data streams. Filesystem metadata, named streams and reparse targets are not enumerated; fragmentation totals describe observed streams only.";
}
