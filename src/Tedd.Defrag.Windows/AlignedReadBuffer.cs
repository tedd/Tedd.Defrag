using System.Runtime.InteropServices;

namespace Tedd.Defrag.Windows;

/// <summary>Sector/page-aligned storage for raw volume I/O. Allocated once per scan, not per record.</summary>
public sealed unsafe class AlignedReadBuffer : IDisposable
{
    private byte* _memory;
    public int Length { get; }
    public AlignedReadBuffer(int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        Length = length;
        nuint allocated = ((nuint)length + 65535) & ~(nuint)65535;
        _memory = (byte*)NativeMemory.AlignedAlloc(allocated, 65536);
        if (_memory == null) throw new OutOfMemoryException();
    }
    public Span<byte> AsSpan(int start, int length)
    {
        ObjectDisposedException.ThrowIf(_memory == null, this);
        return new Span<byte>(_memory, Length).Slice(start, length);
    }
    public void Dispose() { NativeMemory.AlignedFree(_memory); _memory = null; GC.SuppressFinalize(this); }
    ~AlignedReadBuffer() { NativeMemory.AlignedFree(_memory); }
}
