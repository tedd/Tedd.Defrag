using System.Text.Json;
using Tedd.Defrag.Core;
using Tedd.Defrag.Persistence;
using Tedd.Defrag.Windows;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class CompressionTests
{
    [Fact]
    public void SupportedModesAreExposedInUiOrder()
    {
        Assert.Equal(
            [CompressionMode.None, CompressionMode.Xpress4K, CompressionMode.Xpress8K,
                CompressionMode.Xpress16K, CompressionMode.Lzx, CompressionMode.Smallest],
            CompressionModes.Supported);
    }

    [Fact]
    public void CompressionTargetsRoundTripThroughJobPersistence()
    {
        var request = new JobRequest
        {
            Volume = "V:\\",
            CompressionTargets = [new(new(@"V:\Data\**\*.dll", PathRuleKind.Glob), CompressionMode.Xpress16K)]
        };

        string json = JsonSerializer.Serialize(request, JobStore.Json);
        var restored = Assert.IsType<JobRequest>(JsonSerializer.Deserialize<JobRequest>(json, JobStore.Json));

        Assert.Equal(request.CompressionTargets, restored.CompressionTargets);
        restored.Validate();
    }

    [Fact]
    public void GlobDistinguishesOneDirectoryFromRecursiveMatching()
    {
        var direct = new PathRules([new(@"V:\Data\*.log", PathRuleKind.Glob)], []);
        var recursive = new PathRules([new(@"V:\Data\**\*.log", PathRuleKind.Glob)], []);

        Assert.True(direct.IsSelected(@"V:\Data\one.log"));
        Assert.False(direct.IsSelected(@"V:\Data\nested\two.log"));
        Assert.True(recursive.IsSelected(@"V:\Data\one.log"));
        Assert.True(recursive.IsSelected(@"V:\Data\nested\two.log"));
    }

    [Fact]
    public void MatchingWofAlgorithmIsNotRewritten()
    {
        var platform = new FakePlatform(100, CompressionMode.Xpress8K);

        var result = CompressionFileProcessor.Apply("file", CompressionMode.Xpress8K, platform);

        Assert.False(result.Changed);
        Assert.Empty(platform.Applied);
        Assert.Equal(0, platform.Decompressions);
    }

    [Fact]
    public void DifferentWofAlgorithmIsDecompressedAndReapplied()
    {
        var platform = new FakePlatform(100, CompressionMode.Xpress4K);
        platform.Sizes[CompressionMode.Xpress4K] = 70;
        platform.Sizes[CompressionMode.Lzx] = 50;

        var result = CompressionFileProcessor.Apply("file", CompressionMode.Lzx, platform);

        Assert.True(result.Changed);
        Assert.Equal(20, result.BytesSaved);
        Assert.Equal(1, platform.Decompressions);
        Assert.Equal([CompressionMode.Lzx], platform.Applied);
        Assert.Equal(CompressionMode.Lzx, platform.Current);
    }

    [Fact]
    public void SmallestDoesNotAttemptAnAlreadyCompressedFile()
    {
        var platform = new FakePlatform(100, CompressionMode.Xpress4K);

        var result = CompressionFileProcessor.Apply("file", CompressionMode.Smallest, platform);

        Assert.False(result.Changed);
        Assert.Empty(platform.Applied);
        Assert.Equal(0, platform.Decompressions);
    }

    [Fact]
    public void SmallestTestsEveryAlgorithmAndReappliesTheWinner()
    {
        var platform = new FakePlatform(100);
        platform.Sizes[CompressionMode.Xpress4K] = 70;
        platform.Sizes[CompressionMode.Xpress8K] = 45;
        platform.Sizes[CompressionMode.Xpress16K] = 55;
        platform.Sizes[CompressionMode.Lzx] = 50;

        var result = CompressionFileProcessor.Apply("file", CompressionMode.Smallest, platform);

        Assert.True(result.Changed);
        Assert.Equal(55, result.BytesSaved);
        Assert.Equal(
            [CompressionMode.Xpress4K, CompressionMode.Xpress8K, CompressionMode.Xpress16K, CompressionMode.Lzx, CompressionMode.Xpress8K],
            platform.Applied);
        Assert.Equal(4, platform.Decompressions);
        Assert.Equal(CompressionMode.Xpress8K, platform.Current);
    }

    [Fact]
    public void NoneRemovesClassicNtfsCompression()
    {
        var platform = new FakePlatform(100, ntfsCompressed: true) { NtfsCompressedSize = 60 };

        var result = CompressionFileProcessor.Apply("file", CompressionMode.None, platform);

        Assert.True(result.Changed);
        Assert.Equal(-40, result.BytesSaved);
        Assert.Equal(1, platform.Decompressions);
        Assert.Null(platform.Current);
        Assert.False(platform.NtfsCompressed);
    }

    [Fact]
    [Trait("Category", "Native")]
    public void OwnedNtfsFileCanRoundTripWofCompression()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tedd-compression-{Guid.NewGuid():N}.bin");
        var volume = VolumeDiscovery.Get(Path.GetPathRoot(path)!);
        if (!volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            File.WriteAllBytes(path, new byte[1024 * 1024]);
            CompressionResult Apply(CompressionMode mode) => NtfsCompression.Run(new()
            {
                Volume = volume.Root,
                Operation = Operation.Analyze,
                Preview = false,
                CompressionTargets = [new(new(path), mode)]
            }, volume, _ => { }, () => { }, default);

            var compressed = Apply(CompressionMode.Xpress4K);
            Assert.True(compressed.FilesChanged == 1, string.Join(Environment.NewLine, compressed.Warnings));
            Assert.Equal(0, compressed.FilesFailed);
            Assert.True(compressed.BytesSaved > 0);

            var unchanged = Apply(CompressionMode.Xpress4K);
            Assert.Equal(0, unchanged.FilesChanged);
            Assert.Equal(1, unchanged.FilesSkipped);

            var decompressed = Apply(CompressionMode.None);
            Assert.True(decompressed.FilesChanged == 1, string.Join(Environment.NewLine, decompressed.Warnings));
            Assert.Equal(0, decompressed.FilesFailed);

            var smallest = Apply(CompressionMode.Smallest);
            Assert.True(smallest.FilesChanged == 1, string.Join(Environment.NewLine, smallest.Warnings));
            Assert.Equal(0, smallest.FilesFailed);
            Assert.True(smallest.BytesSaved > 0);

            var smallestAgain = Apply(CompressionMode.Smallest);
            Assert.Equal(0, smallestAgain.FilesChanged);
            Assert.Equal(1, smallestAgain.FilesSkipped);
            Assert.Equal(1, Apply(CompressionMode.None).FilesChanged);
        }
        finally { File.Delete(path); }
    }

    private sealed class FakePlatform : ICompressionPlatform
    {
        private readonly long _uncompressedSize;
        public Dictionary<CompressionMode, long> Sizes { get; } = [];
        public List<CompressionMode> Applied { get; } = [];
        public CompressionMode? Current { get; private set; }
        public bool NtfsCompressed { get; private set; }
        public long NtfsCompressedSize { get; init; }
        public int Decompressions { get; private set; }

        public FakePlatform(long uncompressedSize, CompressionMode? current = null, bool ntfsCompressed = false)
        {
            _uncompressedSize = uncompressedSize;
            Current = current;
            NtfsCompressed = ntfsCompressed;
        }

        public CompressionFileState GetState(string path) => new(Current.HasValue,
            Current.HasValue ? NativeCompressionPlatform.WofProviderFile : 0,
            Current.HasValue ? NativeCompressionPlatform.Algorithm(Current.Value) : 0,
            NtfsCompressed);

        public long GetAllocatedSize(string path) => Current is { } mode ? Sizes.GetValueOrDefault(mode, _uncompressedSize)
            : NtfsCompressed ? NtfsCompressedSize : _uncompressedSize;

        public bool ApplyWof(string path, CompressionMode mode)
        {
            Applied.Add(mode);
            if (Sizes.GetValueOrDefault(mode, _uncompressedSize) >= _uncompressedSize) return false;
            Current = mode;
            return true;
        }

        public void Decompress(string path, CompressionFileState state)
        {
            Decompressions++;
            Current = null;
            NtfsCompressed = false;
        }
    }
}
