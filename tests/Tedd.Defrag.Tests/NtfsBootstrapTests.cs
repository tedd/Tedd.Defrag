using System.Buffers.Binary;
using Tedd.Defrag.Core;
using Tedd.Defrag.Ntfs;
using Tedd.Defrag.Windows;
using Xunit;

namespace Tedd.Defrag.Tests;

public class NtfsBootstrapTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(0)]
    public void GeometryUsesAllClustersAndAcceptsAFullVolume(long freeClusters)
    {
        byte[] response = GeometryResponse(freeClusters);
        var geometry = NtfsGeometry.Parse(response);
        Assert.Equal(1000, geometry.TotalClusters);
        Assert.Equal(512, geometry.SectorSize);
        Assert.Equal(4096, geometry.ClusterSize);
        Assert.Equal(1024, geometry.RecordSize);
        Assert.Equal(32 * 4096, geometry.MftLength);
        Assert.Equal(new ClusterRange(8, 8), geometry.MftZone);

        // A valid extent near the end of the volume is beyond the free-cluster count.
        Assert.True(NtfsRecordParser.TryParseFileSystemRecord(RestoredRecord(), 0, geometry.TotalClusters, out var record));
        Assert.Equal(new Extent(0, 900, 32), Assert.Single(Assert.Single(record!.Streams).Extents));
    }

    [Fact]
    public void RawAndFileSystemRecordsProduceTheSameMappingWithoutDoubleFixups()
    {
        byte[] restored = RestoredRecord(), original = restored.ToArray(), raw = Protect(restored);
        Assert.True(NtfsRecordParser.TryParseFileSystemRecord(restored, 0, 1000, out var apiRecord));
        Assert.Equal(original, restored); // Filesystem-returned records are read only.
        Assert.True(NtfsRecordParser.TryParse(raw, 0, 1000, out var rawRecord));
        Assert.Equal(restored, raw);
        Assert.Equal(apiRecord!.FileId, rawRecord!.FileId);
        Assert.Equal(Assert.Single(apiRecord.Streams).Extents, Assert.Single(rawRecord.Streams).Extents);
        Assert.False(NtfsRecordParser.TryParse(restored, 0, 1000, out _));
        Assert.Equal(original, restored); // No permissive fallback for raw records.
    }

    [Fact]
    public void FileSystemExtensionRecordsPreserveSequenceAndBaseIdentity()
    {
        var record = RestoredRecord();
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 7);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), 1UL << 48);
        Assert.True(NtfsRecordParser.TryParseFileSystemRecord(record, 90, 1000, out var extension));
        Assert.Equal((7UL << 48) | 90, extension!.FileId);
        Assert.Equal(1UL << 48, extension.BaseFileId);
    }

    [Fact]
    public void FourKiBRecordsUseEightProtectedStrides()
    {
        var geometryBytes = GeometryResponse(100);
        BinaryPrimitives.WriteUInt32LittleEndian(geometryBytes.AsSpan(40), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(geometryBytes.AsSpan(48), 4096);
        var geometry = NtfsGeometry.Parse(geometryBytes);
        byte[] restored = RestoredRecord(geometry.RecordSize), raw = Protect(restored);
        Assert.True(NtfsRecordParser.TryParse(raw, 0, geometry.TotalClusters, out _));
        Assert.Equal(restored, raw);
        raw = Protect(restored); raw[1534] ^= 1; var original = raw.ToArray();
        Assert.False(NtfsRecordParser.TryParse(raw, 0, geometry.TotalClusters, out _));
        Assert.Equal(original, raw);
    }

    [Theory]
    [InlineData(4, 508)] // USA overlaps the first protected trailer.
    [InlineData(4, 49)]  // Unaligned USA.
    [InlineData(6, 2)]   // Missing fixup entry.
    [InlineData(20, 50)] // Attributes overlap the USA.
    public void MalformedFixupHeadersAreRejectedForBothSources(int offset, ushort value)
    {
        var record = RestoredRecord();
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset), value);
        var original = record.ToArray();
        Assert.False(NtfsRecordParser.TryParseFileSystemRecord(record, 0, 1000, out _));
        Assert.False(NtfsRecordParser.TryParse(record, 0, 1000, out _));
        Assert.Equal(original, record);
    }

    [Fact]
    public void FileSystemRecordsStillValidateSignatureAttributesAndExtentBounds()
    {
        var record = RestoredRecord(); record[0] = 0;
        Assert.False(NtfsRecordParser.TryParseFileSystemRecord(record, 0, 1000, out _));
        record = RestoredRecord();
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(492), 0xFFFFFFF8);
        Assert.False(NtfsRecordParser.TryParseFileSystemRecord(record, 0, 1000, out _));
        Assert.False(NtfsRecordParser.TryParseFileSystemRecord(RestoredRecord(), 0, 920, out _));
        record = RestoredRecord();
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), 568); // No attribute terminator.
        Assert.False(NtfsRecordParser.TryParseFileSystemRecord(record, 0, 1000, out _));
    }

    [Fact]
    public void IncompleteGeometryIsRejectedBeforeReadingFields()
    {
        Assert.Throws<IOException>(() => NtfsGeometry.Parse(new byte[95]));
    }

    private static byte[] GeometryResponse(long freeClusters)
    {
        var bytes = new byte[96];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), 8000);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 1000);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), freeClusters);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), 1024);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(56), 32 * 4096);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(64), 900);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(80), 8);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(88), 16);
        return bytes;
    }

    private static byte[] RestoredRecord(int size = 1024)
    {
        var bytes = new byte[size]; "FILE"u8.CopyTo(bytes);
        int count = size / 512 + 1, first = (48 + count * 2 + 7) & ~7;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), (ushort)first);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 576);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(48), 0x1215);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(first), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(first + 4), (uint)(488 - first));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(first + 16), (uint)(488 - first - 24));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(first + 20), 24);
        // This nonresident header crosses byte 510, matching the failing bootstrap scenario.
        var data = bytes.AsSpan(488, 80);
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], 80); data[8] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(data[24..], 31);
        BinaryPrimitives.WriteUInt16LittleEndian(data[32..], 64);
        BinaryPrimitives.WriteInt64LittleEndian(data[40..], 32 * 4096);
        BinaryPrimitives.WriteInt64LittleEndian(data[48..], 32 * 4096);
        BinaryPrimitives.WriteInt64LittleEndian(data[56..], 32 * 4096);
        new byte[] { 0x21, 32, 0x84, 0x03, 0 }.CopyTo(data[64..]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(568), uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(size - 2), 0x5678);
        for (int i = 1; i < count; i++) bytes.AsSpan(i * 512 - 2, 2).CopyTo(bytes.AsSpan(48 + i * 2, 2));
        return bytes;
    }

    private static byte[] Protect(byte[] restored)
    {
        byte[] raw = restored.ToArray();
        for (int p = 510; p < raw.Length; p += 512) raw.AsSpan(48, 2).CopyTo(raw.AsSpan(p, 2));
        return raw;
    }
}
