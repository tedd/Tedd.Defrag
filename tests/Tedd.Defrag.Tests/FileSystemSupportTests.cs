using Tedd.Defrag.Core;
using Tedd.Defrag.Core.FileSystems;
using Tedd.Defrag.Engine;
using Tedd.Defrag.Engine.FileSystems;
using Tedd.Defrag.Windows;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class FileSystemSupportTests
{
    [Theory]
    [InlineData("FAT")]
    [InlineData("FAT12")]
    [InlineData("fat16")]
    [InlineData("fat32")]
    public void FatVariantsHaveDedicatedCapabilitiesAndVolumeImplementation(string fileSystem)
    {
        var support = Assert.IsType<FatFileSystem>(FileSystemCapabilities.Get(fileSystem));
        Assert.True(support.UsesDirectoryScan);
        Assert.Equal(Operation.WindowsDefrag, support.DefaultDefrag);
        Assert.Equal(Operation.Automatic, support.DefaultOptimization);
        foreach (var operation in Enum.GetValues<Operation>())
            Assert.Equal(operation is Operation.Analyze or Operation.ReTrim or Operation.WindowsDefrag or Operation.Automatic,
                support.Supports(operation));
        var info = Info(fileSystem);
        using var volume = JobVolume.Open(new() { Volume = info.Root }, info);
        Assert.IsType<FatJobVolume>(volume);
        Assert.Throws<NotSupportedException>(() => volume.ExecuteMove(null!, default, new([], [])));
    }

    [Fact]
    public void OtherFilesystemsKeepDistinctImplementations()
    {
        var ntfs = Assert.IsType<NtfsFileSystem>(FileSystemCapabilities.Get("ntfs"));
        Assert.False(ntfs.UsesDirectoryScan);
        Assert.Equal(Operation.MinimumWrite, ntfs.DefaultDefrag);
        Assert.Equal(Operation.MinimumWrite, ntfs.DefaultOptimization);
        foreach (var operation in Enum.GetValues<Operation>()) Assert.True(ntfs.Supports(operation));
        var refs = Assert.IsType<RefsFileSystem>(FileSystemCapabilities.Get("rEfS"));
        foreach (var operation in Enum.GetValues<Operation>())
            Assert.Equal(operation == Operation.Analyze || FileSystemCapabilities.IsWindowsMaintenance(operation), refs.Supports(operation));
        using var volume = JobVolume.Open(new() { Volume = @"V:\" }, Info("ReFS"));
        Assert.IsType<RefsJobVolume>(volume);
        Assert.False(ntfs.Supports((Operation)999));
        Assert.False(refs.Supports((Operation)999));
        Assert.False(FileSystemCapabilities.Supports("FAT", (Operation)999));
    }

    [Theory]
    [InlineData("exFAT")]
    [InlineData("FAT64")]
    [InlineData("UDF")]
    [InlineData("")]
    public void UnsupportedFilesystemsNeverFallBackToNtfs(string fileSystem)
    {
        Assert.False(FileSystemCapabilities.IsSupported(fileSystem));
        Assert.Throws<NotSupportedException>(() => JobVolume.Open(new() { Volume = @"V:\" }, Info(fileSystem)));
    }

    [Fact]
    public void ScannersRejectTheWrongFilesystemBeforeOpeningAVolume()
    {
        Assert.Throws<NotSupportedException>(() => new FatScanner().Scan(Info("NTFS"), new(), (_, _, _) => { }, () => { }, default));
        Assert.Throws<NotSupportedException>(() => new RefsScanner().Scan(Info("FAT32"), new(), (_, _, _) => { }, () => { }, default));
    }

    private static VolumeInfo Info(string fileSystem) => new("fixture", @"V:\", "fixture", fileSystem, 409600, 40960, 4096, true, true, [], "fixture");
}
