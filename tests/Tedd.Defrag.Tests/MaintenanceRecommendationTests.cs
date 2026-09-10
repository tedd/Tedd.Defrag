using Tedd.Defrag.Core;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class MaintenanceRecommendationTests
{
    [Fact]
    public void SsdWithFragmentedMetadataAndFilesOffersTargetedStepsBeforeRetrim()
    {
        var steps = MaintenanceRecommendation.SelectSteps(false, true,
            mftExtents: 64, fragmentedDirectoryIndexes: 12, eligibleFilesAtThreshold: 7);

        Assert.Equal(new[] { Operation.OptimizeMft, Operation.DirectoryIndexes, Operation.MinimumWrite, Operation.ReTrim }, steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void UnavailableOrUnknownTrimDoesNotSuppressSsdMetadataMaintenance(bool? trimEnabled)
    {
        var steps = MaintenanceRecommendation.SelectSteps(false, trimEnabled,
            mftExtents: 2, fragmentedDirectoryIndexes: 1, eligibleFilesAtThreshold: 0);

        Assert.Equal(new[] { Operation.OptimizeMft, Operation.DirectoryIndexes }, steps);
    }

    [Fact]
    public void OrdinaryFileThresholdDoesNotSuppressFragmentedMft()
    {
        var steps = MaintenanceRecommendation.SelectSteps(false, true,
            mftExtents: 2, fragmentedDirectoryIndexes: 0, eligibleFilesAtThreshold: 0);

        Assert.Equal(new[] { Operation.OptimizeMft, Operation.ReTrim }, steps);
    }

    [Fact]
    public void SsdWithoutIdentifiedRelocationTargetsOffersOnlyRetrim()
    {
        var steps = MaintenanceRecommendation.SelectSteps(false, true,
            mftExtents: 1, fragmentedDirectoryIndexes: 0, eligibleFilesAtThreshold: 0);

        Assert.Equal(new[] { Operation.ReTrim }, steps);
    }

    [Fact]
    public void HddUsesTheSameMetadataAndFileCriteria()
    {
        var steps = MaintenanceRecommendation.SelectSteps(true, false,
            mftExtents: 2, fragmentedDirectoryIndexes: 1, eligibleFilesAtThreshold: 3);

        Assert.Equal(new[] { Operation.OptimizeMft, Operation.DirectoryIndexes, Operation.MinimumWrite }, steps);
    }

    [Fact]
    public void UnknownMediaDelegatesToWindows()
    {
        Assert.Equal(new[] { Operation.Automatic },
            MaintenanceRecommendation.SelectSteps(null, true, 64, 12, 7));
    }
}
