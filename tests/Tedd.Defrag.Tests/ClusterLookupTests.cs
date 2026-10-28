using System.Buffers.Binary;
using System.Text;
using Tedd.Defrag.Client;
using Tedd.Defrag.Core;
using Tedd.Defrag.Windows;
using Xunit;

namespace Tedd.Defrag.Tests;

public sealed class ClusterLookupTests
{
    [Fact]
    public void OwnersAreGroupedByStreamAndDistinctClusterWithoutInventingFileDetails()
    {
        var data = Response((10, @"\data.bin::$DATA", 0x01000000), (11, @"\data.bin::$DATA", 0x01000000),
            (11, @"\data.bin::$DATA", 0x01000000), (12, @"\data.bin:alternate:$DATA", 0x01000000),
            (13, @"\folder:$I30:$INDEX_ALLOCATION", 0x02000004));

        var region = ClusterLookup.Parse(data, @"V:\", 10, 4, 100);

        Assert.True(region.IsLive);
        Assert.Equal(3, region.FileCount);
        var file = region.Files[0];
        Assert.Equal(@"V:\data.bin", file.Path);
        Assert.Equal("", file.Stream);
        Assert.Equal(2, file.ClustersInRegion);
        Assert.Equal(-1, file.Bytes);
        Assert.Equal(-1, file.Clusters);
        Assert.Equal(-1, file.Extents);
        Assert.Equal(0ul, file.FileId);
        Assert.Equal(":alternate", region.Files[1].Stream);
        Assert.Equal(":$I30", region.Files[2].Stream);
        Assert.Equal("Filesystem metadata", region.Files[2].Status);
    }

    [Fact]
    public void EmptyResultRepresentsNoOwners()
    {
        var region = ClusterLookup.Parse(Response(), @"V:\", 10, 1, 100);
        Assert.Empty(region.Files);
        Assert.Equal(0, region.FileCount);
        Assert.True(region.IsLive);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(long.MaxValue, 1)]
    public void InvalidRangesAreRejectedBeforeOpeningAVolume(long start, long length)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ClusterLookup.Query("invalid", start, length));

    [Fact]
    public void BroadRangesRequireZoomingInsteadOfSampling()
        => Assert.Contains("Zoom in", Assert.Throws<InvalidOperationException>(() => ClusterLookup.Query("invalid", 0, 65537)).Message);

    [Theory]
    [InlineData(0)] // A required buffer larger than the returned data.
    [InlineData(1)] // A cyclic entry chain.
    [InlineData(2)] // A missing string terminator.
    [InlineData(3)] // A cluster outside the requested range.
    [InlineData(4)] // An entry offset pointing into the header.
    [InlineData(5)] // A match count larger than the returned chain.
    public void MalformedOrTruncatedResponsesAreRejected(int corruption)
    {
        var data = Response((10, @"\data::$DATA", 0x01000000));
        switch (corruption)
        {
            case 0: BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), data.Length + 1); break;
            case 1: BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 16); break;
            case 2: data[^2] = 65; break;
            case 3: BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32), 11); break;
            case 4: BinaryPrimitives.WriteInt32LittleEndian(data, 4); break;
            case 5: BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 2); break;
        }
        Assert.Throws<IOException>(() => ClusterLookup.Parse(data, @"V:\", 10, 1, 100));
    }

    [Fact]
    public void ReturnedFilesAreBoundedWhileKeepingTheFullOwnerCount()
    {
        var data = Response(Enumerable.Range(0, 501).Select(i => (10L, $@"\file{i}::$DATA", 0x01000000u)).ToArray());
        var region = ClusterLookup.Parse(data, @"V:\", 10, 1, 100);
        Assert.Equal(501, region.FileCount);
        Assert.Equal(500, region.Files.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableCacheFallsBackToALiveQueryForTheSameJobAndRange(bool disconnected)
    {
        var commands = new List<BrokerCommand>();
        Guid id = Guid.NewGuid();
        var region = new MapRegion(10, 2, 100, [], [], 0, IsLive: true);
        var client = new DefragClient((command, _) =>
        {
            commands.Add(command);
            return command.Action switch
            {
                "explore" => disconnected ? throw new TimeoutException() : throw new InvalidOperationException(BrokerProtocol.LayoutUnavailableMessage),
                "ping" => Task.FromResult(new BrokerReply(true, Worker: new(BrokerProtocol.BuildVersion, 1, "worker"))),
                "lookup-clusters" => Task.FromResult(new BrokerReply(true, Region: region)),
                _ => throw new InvalidOperationException("Unexpected command.")
            };
        }, () => throw new InvalidOperationException("A matching broker is running."), (_, _) => Task.CompletedTask,
            () => throw new InvalidOperationException("No disk cache should be opened."));

        Assert.Same(region, await client.LookupClusterFiles(id, 10, 2));
        Assert.Equal(["explore", "ping", "lookup-clusters"], commands.Select(command => command.Action));
        Assert.All(commands.Where(command => command.Action != "ping"), command =>
        {
            Assert.Equal(id, command.Id);
            Assert.Equal(10, command.StartCluster);
            Assert.Equal(2, command.ClusterCount);
        });
    }

    [Fact]
    public async Task AvailableCacheAvoidsLiveDiskLookup()
    {
        var commands = new List<string>();
        var region = new MapRegion(10, 2, 100, [], [], 0);
        var client = new DefragClient((command, _) =>
        {
            commands.Add(command.Action);
            return Task.FromResult(new BrokerReply(true, Region: region));
        }, () => throw new InvalidOperationException(), (_, _) => Task.CompletedTask);

        Assert.Same(region, await client.LookupClusterFiles(Guid.NewGuid(), 10, 2));
        Assert.Equal(["explore"], commands);
    }

    [Fact]
    public async Task UnrelatedErrorsAreNotRetriedAsLiveLookups()
    {
        int calls = 0;
        var client = new DefragClient((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Invalid layout query.");
        }, () => throw new InvalidOperationException(), (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LookupClusterFiles(Guid.NewGuid(), 10, 2));
        Assert.Equal(1, calls);
    }

    private static byte[] Response(params (long Cluster, string Name, uint Flags)[] entries)
    {
        var names = entries.Select(entry => Encoding.Unicode.GetBytes(entry.Name + '\0')).ToArray();
        byte[] data = new byte[16 + names.Sum(name => name.Length + 24)];
        BinaryPrimitives.WriteInt32LittleEndian(data, entries.Length == 0 ? 0 : 16);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), entries.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), data.Length);
        int offset = 16;
        for (int i = 0; i < entries.Length; i++)
        {
            int size = 24 + names[i].Length;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), i + 1 < entries.Length ? size : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4), entries[i].Flags);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset + 16), entries[i].Cluster);
            names[i].CopyTo(data.AsSpan(offset + 24));
            offset += size;
        }
        return data;
    }
}
