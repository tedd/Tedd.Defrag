using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Tedd.Defrag.Windows;

public static unsafe class NativeIo
{
    public static SafeFileHandle Open(string path, bool write = false)
    {
        var handle = PInvoke.CreateFile(path, (uint)(write ? 0xC0000000 : 0x80000000),
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT, null);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, $"Cannot open {path}: {new Win32Exception(error).Message}"); }
        return handle;
    }
    public static int Control(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output, out int error)
    {
        {
            bool ok = PInvoke.DeviceIoControl(handle, code, input, output, out uint returned, null);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return checked((int)returned);
        }
    }
    public static int Control(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output)
    {
        int count = Control(handle, code, input, output, out int error);
        if (error != 0 && error != 234 && error != 38) throw new Win32Exception(error);
        return count;
    }
    public static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (!buffer.IsEmpty)
        {
            int count = RandomAccess.Read(handle, buffer, offset);
            if (count == 0) throw new EndOfStreamException("Short raw-volume read.");
            buffer = buffer[count..]; offset += count;
        }
    }
}
