using System.Buffers.Binary;
using System.Text;
using Tedd.Defrag.Core;

namespace Tedd.Defrag.Ntfs;

public sealed record NtfsName(ulong ParentId, string Name, byte Namespace);
public sealed record NtfsStream(uint Type, string Name, long Size, Extent[] Extents, bool Incomplete);
public sealed record AttributeReference(uint Type, ulong FileId, long FirstVcn, string Name);
public sealed record NtfsRecord(ulong FileId, ulong BaseFileId, ushort LinkCount, StreamFlags Flags,
    NtfsName[] Names, NtfsStream[] Streams, long Created, long Modified, bool AttributeList, AttributeReference[] References);

/// <summary>Read-only FILE record parser. Bounds, USA fixups, signed run deltas and extension records are validated.
/// Raw metadata is an observation only; execution must obtain fresh retrieval pointers from NTFS.</summary>
public static class NtfsRecordParser
{
    // NTFS uses a fixed 512-byte protection stride, independent of the device sector size.
    private const int FixupStride = 512;

    private static bool ValidateFixupArray(ReadOnlySpan<byte> record, out int offset, out int count)
    {
        offset = count = 0;
        if (record.Length < FixupStride || record.Length % FixupStride != 0 || !record[..4].SequenceEqual("FILE"u8)) return false;
        offset = U16(record, 4); count = U16(record, 6);
        return count == record.Length / FixupStride + 1 && offset >= 42 && (offset & 1) == 0
            && offset <= FixupStride - 2 - count * 2 && offset + count * 2 <= U16(record, 20);
    }

    /// <summary>Validate every raw-record trailer before restoring any bytes. Never use on an FSCTL-returned record.</summary>
    public static bool ApplyFixups(Span<byte> record)
    {
        if (!ValidateFixupArray(record, out int offset, out int count)) return false;
        ushort signature = U16(record, offset);
        for (int i = 1; i < count; i++) if (U16(record, i * FixupStride - 2) != signature) return false;
        for (int i = 1; i < count; i++) record.Slice(offset + i * 2, 2).CopyTo(record.Slice(i * FixupStride - 2, 2));
        return true;
    }

    /// <summary>Parse bytes read directly from the volume, validating and restoring their update sequence array.</summary>
    public static bool TryParse(Span<byte> record, long recordNumber, long totalClusters, out NtfsRecord? result)
    {
        result = null;
        return ApplyFixups(record) && TryParseRestored(record, recordNumber, totalClusters, out result);
    }

    /// <summary>Parse FSCTL_GET_NTFS_FILE_RECORD output. NTFS has already validated and restored its trailers.
    /// This is deliberately separate from raw parsing; a failed raw fixup must never fall back to this path.</summary>
    public static bool TryParseFileSystemRecord(ReadOnlySpan<byte> record, long recordNumber, long totalClusters, out NtfsRecord? result)
    {
        result = null;
        return ValidateFixupArray(record, out _, out _) && TryParseRestored(record, recordNumber, totalClusters, out result);
    }

    private static bool TryParseRestored(ReadOnlySpan<byte> record, long recordNumber, long totalClusters, out NtfsRecord? result)
    {
        result = null;
        if (recordNumber is < 0 or > 0xFFFFFFFFFFFF || totalClusters <= 0) return false;
        ushort headerFlags = U16(record, 22);
        if ((headerFlags & 1) == 0) return false;
        uint usedRaw = U32(record, 24); if (usedRaw > record.Length) return false;
        int used = (int)usedRaw, offset = U16(record, 20);
        if (used > record.Length || used < 48 || offset < 48 || offset >= used) return false;
        ulong id = ((ulong)U16(record, 16) << 48) | ((ulong)recordNumber & 0xFFFFFFFFFFFF);
        StreamFlags flags = (headerFlags & 2) != 0 ? StreamFlags.Directory : 0;
        if (recordNumber < 16) flags |= StreamFlags.Metadata;
        ushort links = U16(record, 18);
        if (links > 1) flags |= StreamFlags.HardLinked;
        var names = new List<NtfsName>(2); var streams = new List<NtfsStream>(2); var references = new List<AttributeReference>();
        long created = 0, modified = 0; bool attrList = false, terminated = false;
        try
        {
            while (offset <= used - 4)
            {
                uint type = U32(record, offset);
                if (type == uint.MaxValue) { terminated = true; break; }
                if (offset > used - 16) return false;
                int len = checked((int)U32(record, offset + 4));
                if (len < 24 || len > used - offset || (len & 7) != 0) return false;
                var attr = record.Slice(offset, len);
                bool nonresident = attr[8] != 0;
                int nameLength = attr[9] * 2, nameOffset = U16(attr, 10);
                if (nameLength > 0 && (nameOffset < 16 || nameOffset > len - nameLength)) return false;
                string name = nameLength == 0 ? "" : Encoding.Unicode.GetString(attr.Slice(nameOffset, nameLength));
                ushort attrFlags = U16(attr, 12);
                if (type == 0x20) attrList = true;
                if (!nonresident)
                {
                    int valueLen = checked((int)U32(attr, 16)), valueOffset = U16(attr, 20);
                    if (valueOffset < 24 || valueLen > len - valueOffset) return false;
                    var value = attr.Slice(valueOffset, valueLen);
                    if (type == 0x20) references.AddRange(ParseAttributeList(value));
                    if (type == 0x30 && value.Length >= 66)
                    {
                        int chars = value[64];
                        if (chars * 2 > value.Length - 66) return false;
                        names.Add(new(U64(value, 0), Encoding.Unicode.GetString(value.Slice(66, chars * 2)), value[65]));
                    }
                    if (type == 0x10 && value.Length >= 36)
                    {
                        created = I64(value, 0); modified = I64(value, 8);
                        uint fa = U32(value, 32);
                        if ((fa & 0x400) != 0) flags |= StreamFlags.ReparsePoint;
                        if ((fa & 0x4000) != 0) flags |= StreamFlags.Encrypted;
                    }
                }
                else
                {
                    if (len < 64) return false;
                    long first = I64(attr, 16), last = I64(attr, 24), size = I64(attr, 48);
                    int runOffset = U16(attr, 32);
                    if (first < 0 || last < first || runOffset < 64 || runOffset >= len || size < 0) return false;
                    if (!TryDecodeRuns(attr[runOffset..], first, totalClusters, out var extents)) return false;
                    if (extents.Length == 0 || extents[^1].Vcn + extents[^1].Length != checked(last + 1)) return false;
                    if ((attrFlags & 1) != 0) flags |= StreamFlags.Compressed;
                    if ((attrFlags & 0x8000) != 0) flags |= StreamFlags.Sparse;
                    if ((attrFlags & 0x4000) != 0) flags |= StreamFlags.Encrypted;
                    if (type is 0x80 or 0xA0 or 0xB0 or 0x20)
                        streams.Add(new(type, name, size, extents, first != 0));
                }
                offset += len;
            }
        }
        catch (Exception e) when (e is OverflowException or ArgumentOutOfRangeException or IOException) { return false; }
        if (!terminated) return false;
        if (attrList) flags |= StreamFlags.Incomplete;
        result = new(id, U64(record, 32), links, flags, names.ToArray(), streams.ToArray(), created, modified, attrList, references.ToArray());
        return true;
    }
    public static AttributeReference[] ParseAttributeList(ReadOnlySpan<byte> data)
    {
        var entries = new List<AttributeReference>(); int p = 0;
        while (p <= data.Length - 26)
        {
            uint type = U32(data, p); if (type is 0 or uint.MaxValue) break;
            int n = U16(data, p + 4), chars = data[p + 6], offset = data[p + 7];
            if (n < 26 || n > data.Length - p || (chars != 0 && (offset < 26 || offset + chars * 2 > n))) throw new IOException("Malformed NTFS attribute list.");
            entries.Add(new(type, U64(data, p + 16), I64(data, p + 8), chars == 0 ? "" : Encoding.Unicode.GetString(data.Slice(p + offset, chars * 2)))); p += n;
        }
        return entries.ToArray();
    }
    public static bool TryDecodeRuns(ReadOnlySpan<byte> runlist, long firstVcn, long totalClusters, out Extent[] extents)
    {
        extents = []; var runs = new List<Extent>(); long vcn = firstVcn, lcn = 0;
        int pos = 0;
        try
        {
            while (pos < runlist.Length)
            {
                byte header = runlist[pos++];
                if (header == 0) { extents = runs.ToArray(); return true; }
                int n = header & 15, d = header >> 4;
                if (n is < 1 or > 8 || d > 8 || pos > runlist.Length - n - d) return false;
                ulong size = 0; for (int i = 0; i < n; i++) size |= (ulong)runlist[pos++] << (8 * i);
                if (size == 0 || size > long.MaxValue) return false;
                long delta = 0;
                if (d > 0)
                {
                    for (int i = 0; i < d; i++) delta |= (long)runlist[pos++] << (8 * i);
                    if (d < 8 && (runlist[pos - 1] & 128) != 0) delta |= -1L << (d * 8);
                    lcn = checked(lcn + delta);
                    if (lcn < 0 || lcn > totalClusters - (long)size) return false;
                }
                runs.Add(new(vcn, d == 0 ? -1 : lcn, (long)size)); vcn = checked(vcn + (long)size);
                if (runs.Count > 65536) return false;
            }
        }
        catch (OverflowException) { return false; }
        return false;
    }
    private static ushort U16(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadUInt16LittleEndian(s[i..]);
    private static uint U32(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadUInt32LittleEndian(s[i..]);
    private static ulong U64(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadUInt64LittleEndian(s[i..]);
    private static long I64(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadInt64LittleEndian(s[i..]);
}
