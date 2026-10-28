using Tedd.Defrag.Core;

namespace Tedd.Defrag.Planning;

/// <summary>Continuation for one job against a stable file inventory.</summary>
public sealed class PlanningSession : IDisposable
{
    private readonly HashSet<ulong> _blockedFiles = [];
    private FileLayout[]? _candidateFiles;
    private Operation _candidateOperation;
    private int[]? _candidateOrder;
    private PackPlanningState? _packPlan;
    private FreeSpaceIndex? _freeSpace;
    private ClusterRange _reservedZone;
    internal RelocationPlanningState? RelocationPlan { get; set; }

    public IReadOnlySet<ulong> BlockedFiles => _blockedFiles;
    public void BlockFile(ulong fileId) => _blockedFiles.Add(fileId);

    internal int[]? CandidateOrder(FileLayout[] files, Operation operation) =>
        ReferenceEquals(_candidateFiles, files) && _candidateOperation == operation ? _candidateOrder : null;

    internal void RememberCandidateOrder(FileLayout[] files, Operation operation, ReadOnlySpan<int> order)
    {
        EnsureContext(files, operation);
        _candidateFiles = files;
        _candidateOperation = operation;
        _candidateOrder = order.ToArray();
        _packPlan = null;
    }

    internal FreeSpaceIndex? FreeSpace(FileLayout[] files, Operation operation) =>
        ReferenceEquals(_candidateFiles, files) && _candidateOperation == operation ? _freeSpace : null;

    internal void RememberFreeSpace(FileLayout[] files, Operation operation, FreeSpaceIndex freeSpace, ClusterRange reservedZone = default)
    {
        EnsureContext(files, operation);
        _candidateFiles = files;
        _candidateOperation = operation;
        _freeSpace?.Dispose();
        _freeSpace = freeSpace;
        _reservedZone = reservedZone;
    }

    internal PackPlanningState? PackPlan(FileLayout[] files) =>
        _packPlan != null && ReferenceEquals(_candidateFiles, files) ? _packPlan : null;

    internal void RememberPackPlan(FileLayout[] files, PackPlanningState plan)
    {
        ResetPhysicalPlan();
        _candidateFiles = files;
        _candidateOperation = Operation.Pack;
        _candidateOrder = null;
        _packPlan = plan;
    }

    public bool HasActivePlan(Operation operation) => operation == Operation.Pack
        ? _packPlan != null
        : _candidateOperation == operation && (_candidateOrder != null || _freeSpace != null);

    public void MoveVerified(PlannedMove move)
    {
        long start = move.SourceLcn, end = start + move.Clusters;
        if (_reservedZone.Length == 0 || end <= _reservedZone.Start || start >= _reservedZone.End) Release(start, end - start);
        else
        {
            if (start < _reservedZone.Start) Release(start, _reservedZone.Start - start);
            if (end > _reservedZone.End) Release(_reservedZone.End, end - _reservedZone.End);
        }
        void Release(long address, long length)
        {
            _freeSpace?.Release(address, length);
            RelocationPlan?.SpaceReleased(address, length);
        }
    }
    public void MoveRejected(PlannedMove move) => _freeSpace?.Release(move.DestinationLcn, move.Clusters);

    public void InvalidateAllocationPlan(Operation operation)
    {
        _freeSpace?.Dispose();
        _freeSpace = null;
        if (RelocationPlan != null) RelocationPlan.AllocationInvalid = true;
        if (operation == Operation.Pack)
        {
            _candidateFiles = null;
            _candidateOrder = null;
            _packPlan = null;
        }
    }

    public void ResetPhysicalPlan()
    {
        _candidateFiles = null;
        _candidateOrder = null;
        _packPlan = null;
        RelocationPlan = null;
        _freeSpace?.Dispose();
        _freeSpace = null;
    }

    public void Dispose() => ResetPhysicalPlan();

    public Dictionary<(ulong Id, string Stream), ClusterRange>? CapturePlacementGoals() => RelocationPlan?.CaptureGoals();

    private void EnsureContext(FileLayout[] files, Operation operation)
    {
        if (_candidateFiles == null || ReferenceEquals(_candidateFiles, files) && _candidateOperation == operation) return;
        ResetPhysicalPlan();
    }
}
