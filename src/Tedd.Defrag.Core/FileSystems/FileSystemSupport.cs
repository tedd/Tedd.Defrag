namespace Tedd.Defrag.Core.FileSystems;

public abstract class FileSystemSupport
{
    public abstract string Name { get; }
    public abstract bool Matches(string fileSystem);
    public abstract bool Supports(Operation operation);
    public abstract bool UsesDirectoryScan { get; }
    public abstract string CoverageDescription { get; }
    public Operation DefaultDefrag => UsesDirectoryScan ? Operation.WindowsDefrag : Operation.MinimumWrite;
    public Operation DefaultOptimization => UsesDirectoryScan ? Operation.Automatic : Operation.MinimumWrite;
}
