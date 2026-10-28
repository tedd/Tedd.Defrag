using Tedd.Defrag.Desktop;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class InputBoundsTests
{
    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("abc")]
    [InlineData("18446744073709551616")]
    public void NumericControlsRejectInvalidPastedText(string text)
    {
        Assert.False(InputBounds.MiB.CanEdit(text));
        Assert.False(InputBounds.MiB.IsValid(text));
    }

    [Fact]
    public void QueueAndWorkerControlsRejectValuesAboveTheirLimits()
    {
        Assert.True(InputBounds.Workers.IsValid("32"));
        Assert.False(InputBounds.Workers.CanEdit("33"));
        Assert.True(InputBounds.MoveQueue.IsValid("16"));
        Assert.False(InputBounds.MoveQueue.CanEdit("17"));
        Assert.False(InputBounds.MoveQueue.IsValid("0"));
        Assert.Equal("1", InputBounds.MoveQueue.Normalize("0"));
    }

    [Fact]
    public void FragmentEditingAllowsAPrefixButCannotCommitAnInvalidThreshold()
    {
        Assert.True(InputBounds.Fragments.CanEdit("1"));
        Assert.False(InputBounds.Fragments.IsValid("1"));
        Assert.True(InputBounds.Fragments.IsValid("10"));
        Assert.Equal("2", InputBounds.Fragments.Normalize("1"));
        Assert.Equal("20", InputBounds.Fragments.Normalize(""));
    }

    [Fact]
    public void MemoryAllowsUnlimitedOrAtLeast256MiB()
    {
        Assert.True(InputBounds.Memory.IsValid("0"));
        Assert.False(InputBounds.Memory.IsValid("255"));
        Assert.True(InputBounds.Memory.IsValid("256"));
        Assert.Equal("256", InputBounds.Memory.Normalize("1"));
        Assert.Equal("0", InputBounds.Memory.Normalize(""));
    }

    [Fact]
    public void MiBUpperBoundCannotOverflowTheByteConversion()
    {
        string maximum = InputBounds.MaximumMiB.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(InputBounds.MiB.IsValid(maximum));
        Assert.False(InputBounds.MiB.CanEdit((InputBounds.MaximumMiB + 1).ToString()));
        Assert.True(checked((long)InputBounds.MaximumMiB * 1024 * 1024) <= long.MaxValue);
        Assert.Equal("1024", InputBounds.MiB.Normalize("1,024"));
    }

    [Theory]
    [InlineData("0xFFFF", true)]
    [InlineData("ffffffffffffffff", true)]
    [InlineData("10000000000000000", false)]
    [InlineData("0xG", false)]
    [InlineData("-1", false)]
    public void AffinityAcceptsOnlyA64BitHexadecimalMask(string text, bool valid)
    {
        Assert.Equal(valid, InputBounds.Affinity.IsValid(text));
        Assert.Equal(valid, InputBounds.Affinity.CanEdit(text));
    }

    [Fact]
    public void HexPrefixCanBeTypedBeforeItsDigits()
    {
        Assert.True(InputBounds.Affinity.CanEdit("0x"));
        Assert.False(InputBounds.Affinity.IsValid("0x"));
        Assert.Equal("0", InputBounds.Affinity.Normalize("0x"));
    }

    [Fact]
    public void AffinityRejectsUnavailableProcessorsIncludingGapsInTheMask()
    {
        var bounds = InputBounds.Affinity with { Maximum = 0xAA, AllowedBits = 0xAA };
        Assert.True(bounds.IsValid("80"));
        Assert.False(bounds.IsValid("40"));
        Assert.Equal("AA", bounds.Normalize("FF"));
        var automatic = InputBounds.Affinity with { Maximum = 0, AllowedBits = 0 };
        Assert.True(automatic.IsValid("0"));
        Assert.False(automatic.CanEdit("1"));
    }

    [Fact]
    public void RestoredInvalidValuesBecomeValidControlValues()
    {
        Assert.Equal("32", InputBounds.Workers.Normalize("100"));
        Assert.Equal("16", InputBounds.MoveQueue.Normalize("invalid"));
        Assert.Equal("32", InputBounds.ConcurrentVolumes.Normalize("100"));
        Assert.Equal("2", InputBounds.ConcurrentVolumes.Normalize(""));
    }

    [Fact]
    public void RequiredShrinkBoundaryNeverDefaultsToOneMiB()
    {
        var bounds = new InputBounds(1, 4096, Required: true);
        Assert.False(bounds.IsValid(""));
        Assert.False(bounds.IsValid("0"));
        Assert.Equal("", bounds.Normalize("0"));
        Assert.True(bounds.IsValid("4096"));
        Assert.False(bounds.CanEdit("4097"));
    }

    [Fact]
    public void SizeRangeAllowsUnlimitedMaximumAndRejectsInvertedBounds()
    {
        Assert.True(InputBounds.SizeRangeIsValid(100, 0));
        Assert.True(InputBounds.SizeRangeIsValid(100, 100));
        Assert.False(InputBounds.SizeRangeIsValid(100, 99));
    }
}
