using Tedd.Defrag.Update;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public async Task PortableUpdateOnExitReplacesFilesWithoutRestarting()
    {
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string current = Path.Combine(root, "current");
            string next = Path.Combine(root, "next");
            Directory.CreateDirectory(current);
            Directory.CreateDirectory(next);
            File.WriteAllText(Path.Combine(current, "release-manifest.json"), "{\"version\":\"0.1.8\"}");
            foreach (string file in new[] { "Tedd.Defrag.Cli.exe", "Tedd.Defrag.Desktop.exe", "Tedd.Defrag.Worker.exe" })
                File.WriteAllText(Path.Combine(next, file), "test payload");
            File.WriteAllText(Path.Combine(next, "release-manifest.json"), "{\"version\":\"0.1.9\"}");
            string archive = Path.Combine(root, "update.zip");
            ZipFile.CreateFromDirectory(next, archive);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                ParentProcessId = int.MaxValue, TargetDirectory = current, ArchivePath = archive,
                ExpectedSha256 = hash, Version = "0.1.9", LauncherName = "Tedd.Defrag.Desktop.exe",
                LaunchArguments = Array.Empty<string>(), RestartAfterUpdate = false
            })));

            Assert.Equal(0, await ReleaseUpdater.ApplyUpdateAsync(payload));
            ReleaseUpdater.VerifyInstalledVersion(current, "0.1.9");
            Assert.True(File.Exists(Path.Combine(current, "Tedd.Defrag.Desktop.exe")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void InstalledVersionMustMatchBeforeReportingSuccess()
    {
        string root = Path.Combine(Path.GetTempPath(), "Tedd.Defrag.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "release-manifest.json"), "{\"version\":\"0.1.8\"}");
            Assert.Throws<InvalidDataException>(() => ReleaseUpdater.VerifyInstalledVersion(root, "0.1.9"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
