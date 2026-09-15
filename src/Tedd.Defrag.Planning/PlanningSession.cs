using Tedd.Defrag.Core;

namespace Tedd.Defrag.Planning;

/// <summary>Continuation for one job against a stable file inventory.</summary>
public sealed class PlanningSession
{
    private readonly HashSet<ulong> _blockedFiles = [];
    private FileLayout[]? _candidateFiles;
    private Operation _candidateOperation;
    private int[]? _candidateOrder;

    public IReadOnlySet<ulong> BlockedFiles => _blockedFiles;
    public void BlockFile(ulong fileId) => _blockedFiles.Add(fileId);

    internal int[]? CandidateOrder(FileLayout[] files, Operation operation) =>
        ReferenceEquals(_candidateFiles, files) && _candidateOperation == operation ? _candidateOrder : null;

    internal void RememberCandidateOrder(FileLayout[] files, Operation operation, ReadOnlySpan<int> order)
    {
        _candidateFiles = files;
        _candidateOperation = operation;
        _candidateOrder = order.ToArray();
    }

    public void InvalidateCandidateOrder()
    {
        _candidateFiles = null;
        _candidateOrder = null;
    }

    internal int OrderedPosition;
    internal long DestinationCursor;
    internal int FilesConsidered;
    internal int FilesBlocked;
}
