using Tedd.Defrag.Core;

namespace Tedd.Defrag.Planning;

/// <summary>Continuation for one job against a stable file inventory.</summary>
public sealed class PlanningSession
{
    private readonly HashSet<ulong> _blockedFiles = [];
    private FileLayout[]? _candidateFiles;
    private Operation _candidateOperation;
    private int[]? _candidateOrder;
    private PackPlanningState? _packPlan;

    public IReadOnlySet<ulong> BlockedFiles => _blockedFiles;
    public void BlockFile(ulong fileId) => _blockedFiles.Add(fileId);

    internal int[]? CandidateOrder(FileLayout[] files, Operation operation) =>
        ReferenceEquals(_candidateFiles, files) && _candidateOperation == operation ? _candidateOrder : null;

    internal void RememberCandidateOrder(FileLayout[] files, Operation operation, ReadOnlySpan<int> order)
    {
        _candidateFiles = files;
        _candidateOperation = operation;
        _candidateOrder = order.ToArray();
        _packPlan = null;
    }

    internal PackPlanningState? PackPlan(FileLayout[] files) =>
        _packPlan != null && ReferenceEquals(_candidateFiles, files) ? _packPlan : null;

    internal void RememberPackPlan(FileLayout[] files, PackPlanningState plan)
    {
        _candidateFiles = files;
        _candidateOperation = Operation.Pack;
        _candidateOrder = null;
        _packPlan = plan;
    }

    public bool HasActivePackPlan => _packPlan != null;

    public void ResetPhysicalPlan()
    {
        _candidateFiles = null;
        _candidateOrder = null;
        _packPlan = null;
    }

    internal int OrderedPosition;
    internal long DestinationCursor;
    internal int FilesConsidered;
    internal int FilesBlocked;
}
