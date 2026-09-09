using Tedd.Defrag.Update;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData("0.1.0", "v0.1.1", true)]
    [InlineData("1.9.9+abc123", "2.0.0", true)]
    [InlineData("2.0.0", "v2.0.0", false)]
    [InlineData("2.1.0", "2.0.9", false)]
    [InlineData("invalid", "2.0.0", false)]
    public void ReleaseVersionComparisonIsNumeric(string current, string candidate, bool expected) =>
        Assert.Equal(expected, ReleaseUpdater.IsNewerVersion(current, candidate));
}
