using System.Text.Json;
using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class JobRequestValidationTests
{
    public static IEnumerable<object[]> OperationsWithoutFragmentFiltering() => Enum.GetValues<Operation>()
        .Where(operation => operation is not (Operation.MinimumWrite or Operation.FilesOnly))
        .SelectMany(operation => new[] { new object[] { operation, 0 }, new object[] { operation, 1 } });

    [Theory]
    [MemberData(nameof(OperationsWithoutFragmentFiltering))]
    public void UnusedFragmentThresholdDoesNotPreventSubmission(Operation operation, int threshold)
    {
        var request = new JobRequest
        {
            Volume = "V:\\", Operation = operation, MinimumFragments = threshold,
            ShrinkBoundaryBytes = 1024L * 1024 * 1024
        };
        var restored = JsonSerializer.Deserialize<JobRequest>(JsonSerializer.Serialize(request, JobStore.Json), JobStore.Json)!;
        restored.Validate();
        Assert.Equal(threshold, restored.MinimumFragments);
    }

    [Theory]
    [InlineData(Operation.MinimumWrite, 0)]
    [InlineData(Operation.MinimumWrite, 1)]
    [InlineData(Operation.FilesOnly, 0)]
    [InlineData(Operation.FilesOnly, 1)]
    public void FileDefragExplainsHowToIncludeEveryFragmentedFile(Operation operation, int threshold)
    {
        var request = new JobRequest { Volume = "V:\\", Operation = operation, MinimumFragments = threshold };
        var error = Assert.Throws<ArgumentException>(request.Validate);
        Assert.Contains("Minimum fragments must be at least 2", error.Message);
        Assert.Contains("Use 2 to include every fragmented file", error.Message);
        (request with { MinimumFragments = 2 }).Validate();
    }

    [Fact]
    public void NegativeThresholdStillFailsValidation()
    {
        var request = new JobRequest { Volume = "V:\\", Operation = Operation.Alphabetical, MinimumFragments = -1 };
        Assert.Throws<ArgumentException>(request.Validate);
    }

    [Fact]
    public void ConflictingSizeSettingsIdentifyTheFieldsToCorrect()
    {
        var request = new JobRequest { Volume = "V:\\", MinimumFileBytes = 8192, MaximumFileBytes = 4096 };
        var error = Assert.Throws<ArgumentException>(request.Validate);
        Assert.Contains("Maximum file size must be at least the minimum file size", error.Message);
        (request with { MaximumFileBytes = 0 }).Validate();
    }
}
