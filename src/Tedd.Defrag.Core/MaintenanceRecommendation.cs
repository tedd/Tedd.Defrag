namespace Tedd.Defrag.Core;

public static class MaintenanceRecommendation
{
    // These are preview candidates from the observed filesystem layout, not a device-performance score.
    // Metadata fragmentation is considered separately from the ordinary-file fragment threshold.
    public static Operation[] SelectSteps(bool? seekPenalty, bool? trimEnabled, int mftExtents,
        int fragmentedDirectoryIndexes, int eligibleFilesAtThreshold)
    {
        if (seekPenalty == null) return [Operation.Automatic];

        var steps = new List<Operation>(4);
        if (mftExtents > 1) steps.Add(Operation.OptimizeMft);
        if (fragmentedDirectoryIndexes > 0) steps.Add(Operation.DirectoryIndexes);
        if (eligibleFilesAtThreshold > 0) steps.Add(Operation.MinimumWrite);
        // Run ReTRIM after relocation so newly freed ranges can be included.
        if (trimEnabled == true) steps.Add(Operation.ReTrim);
        else if (seekPenalty == false && trimEnabled == null && steps.Count == 0) steps.Add(Operation.Automatic);
        return steps.ToArray();
    }
}
