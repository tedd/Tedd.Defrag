using Tedd.Defrag.Update;
using System.Runtime.InteropServices;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class UpdateTests
{
    [Fact]
    public void DistributionLookupFindsInstalledFilesOutsideBundleBase()
    {
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string extracted = Path.Combine(root, "single-file-extraction");
            string installed = Path.Combine(root, "installed");
            Directory.CreateDirectory(extracted); Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, "release-manifest.json"), "{}");

            Assert.Equal(installed, ReleaseUpdater.FindDistributionDirectory([extracted, installed]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("0.1.0", "v0.1.1", true)]
    [InlineData("1.9.9+abc123", "2.0.0", true)]
    [InlineData("2.0.0", "v2.0.0", false)]
    [InlineData("2.1.0", "2.0.9", false)]
    [InlineData("invalid", "2.0.0", false)]
    public void ReleaseVersionComparisonIsNumeric(string current, string candidate, bool expected) =>
        Assert.Equal(expected, ReleaseUpdater.IsNewerVersion(current, candidate));

    [Theory]
    [InlineData(ReleasePackageKind.Portable, Architecture.X64, "Tedd.Defrag-win-x64.zip")]
    [InlineData(ReleasePackageKind.Portable, Architecture.Arm64, "Tedd.Defrag-win-arm64.zip")]
    [InlineData(ReleasePackageKind.Installer, Architecture.X64, "Tedd.Defrag-Setup-win-x64.exe")]
    [InlineData(ReleasePackageKind.Installer, Architecture.Arm64, "Tedd.Defrag-Setup-win-arm64.exe")]
    public void UpdateAssetMatchesInstallTypeAndArchitecture(ReleasePackageKind packageKind, Architecture architecture, string expected) =>
        Assert.Equal(expected, ReleaseUpdater.AssetNameFor(packageKind, architecture));

    [Fact]
    public void UnsupportedUpdateArchitectureIsRejected() =>
        Assert.Throws<PlatformNotSupportedException>(() => ReleaseUpdater.AssetNameFor(ReleasePackageKind.Portable, Architecture.X86));

    [Fact]
    public async Task LauncherPathsAreRejectedBeforeDownloadOrPackageInspection()
    {
        var release = new AvailableRelease(new(1, 0, 0), "1.0.0", "package.zip",
            new("https://example.invalid/package.zip"), new("https://example.invalid/package.zip.sha256"),
            new("https://example.invalid/release"), ReleasePackageKind.Portable);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ReleaseUpdater.LaunchUpdateAsync(release, @"..\Tedd.Defrag.Desktop.exe", []));
    }
}
