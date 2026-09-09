using System.Text.Json;
using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Scheduling;
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

        // Saving a disabled schedule must retain its legacy marker on the next restart.
        var roundTrip = JsonSerializer.Deserialize<JobRequest>(JsonSerializer.Serialize(request, JobStore.Json), JobStore.Json)!;
        Assert.Throws<NotSupportedException>(roundTrip.Validate);
        var schedule = new ScheduleDefinition("old simulation", roundTrip, [DayOfWeek.Wednesday], new(2, 0));
        Assert.False(ScheduleClock.IsDue(schedule, new(2026, 9, 9, 14, 0, 0)));
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
