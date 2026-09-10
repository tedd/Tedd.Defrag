using System.Text.Json;
using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Xunit;

namespace Tedd.Defrag.Tests;

public class LegacySimulationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PersistedSimulationRequestsCannotBecomeRealDiskOperations(bool preview)
    {
        string json = $$"""{"Volume":"C:","Operation":"MinimumWrite","Preview":{{preview.ToString().ToLowerInvariant()}},"Demo":true}""";
        var request = JsonSerializer.Deserialize<JobRequest>(json, JobStore.Json)!;
        Assert.True(request.LegacySimulation);
        Assert.Throws<NotSupportedException>(request.Validate);

        var roundTrip = JsonSerializer.Deserialize<JobRequest>(JsonSerializer.Serialize(request, JobStore.Json), JobStore.Json)!;
        Assert.Throws<NotSupportedException>(roundTrip.Validate);
    }

    [Fact]
    public void ExistingRealRequestsRetainTheirPreviewSetting()
    {
        const string json = """{"Volume":"C:","Operation":"MinimumWrite","Preview":true,"Demo":false}""";
        var request = JsonSerializer.Deserialize<JobRequest>(json, JobStore.Json)!;
        request.Validate();
        Assert.True(request.Preview);
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(request, JobStore.Json));
        Assert.False(saved.RootElement.TryGetProperty("Demo", out _));
        Assert.True(saved.RootElement.GetProperty("Preview").GetBoolean());
    }
}
