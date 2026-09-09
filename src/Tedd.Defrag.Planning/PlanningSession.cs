namespace Tedd.Defrag.Planning;

/// <summary>Continuation for one job against a stable file inventory.</summary>
public sealed class PlanningSession
{
    private readonly HashSet<ulong> _blockedFiles = [];
    public IReadOnlySet<ulong> BlockedFiles => _blockedFiles;
    public void BlockFile(ulong fileId) => _blockedFiles.Add(fileId);

    internal int OrderedPosition;
    internal long DestinationCursor;
    internal int FilesConsidered;
    internal int FilesBlocked;
}
