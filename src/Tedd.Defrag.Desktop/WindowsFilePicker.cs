using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Tedd.Defrag.Desktop;

/// <summary>Uses the stable Win32 file dialog instead of the WinRT picker, which can fail-fast unpackaged WinUI apps.</summary>
internal static class WindowsFilePicker
{
    private const int MaxPathBuffer = 32_768;
    private const uint FileMustExist = 0x00001000;
    private const uint PathMustExist = 0x00000800;
    private const uint NoChangeDirectory = 0x00000008;
    private const uint Explorer = 0x00080000;
    private const uint DoNotAddToRecent = 0x02000000;
    private const uint ReturnOnlyFileSystemDirectories = 0x00000001;
    private const uint EditBox = 0x00000010;
    private const uint Validate = 0x00000020;
    private const uint NewDialogStyle = 0x00000040;

    public static string? Pick(Microsoft.UI.Xaml.Window? owner, string title)
    {
        var path = new StringBuilder(MaxPathBuffer);
        var dialog = new OpenFileName
        {
            StructureSize = Marshal.SizeOf<OpenFileName>(),
            Owner = owner == null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(owner),
            Filter = "All files\0*.*\0\0",
            File = path,
            MaxFile = path.Capacity,
            Title = title,
            Flags = FileMustExist | PathMustExist | NoChangeDirectory | Explorer | DoNotAddToRecent
        };

        if (GetOpenFileName(ref dialog)) return path.ToString();
        int error = CommDlgExtendedError();
        if (error == 0) return null;
        throw new Win32Exception(error, $"The file dialog failed (0x{error:X4}).");
    }

    public static string? PickFolder(Microsoft.UI.Xaml.Window? owner, string title)
    {
        IntPtr displayName = Marshal.AllocHGlobal(MaxPathBuffer * sizeof(char));
        try
        {
            var dialog = new BrowseInfo
            {
                Owner = owner == null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(owner),
                DisplayName = displayName,
                Title = title,
                Flags = ReturnOnlyFileSystemDirectories | EditBox | Validate | NewDialogStyle
            };
            IntPtr item = SHBrowseForFolder(ref dialog);
            if (item == IntPtr.Zero) return null;
            try
            {
                var path = new StringBuilder(MaxPathBuffer);
                if (!SHGetPathFromIDListEx(item, path, (uint)path.Capacity, 0))
                    throw new InvalidOperationException("The selected folder has no filesystem path.");
                return path.ToString();
            }
            finally { Marshal.FreeCoTaskMem(item); }
        }
        finally { Marshal.FreeHGlobal(displayName); }
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName dialog);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BrowseInfo browseInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListEx(IntPtr item, StringBuilder path, uint pathLength, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructureSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public string? Filter;
        public string? CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public StringBuilder? File;
        public int MaxFile;
        public StringBuilder? FileTitle;
        public int MaxFileTitle;
        public string? InitialDirectory;
        public string? Title;
        public uint Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        public string? TemplateName;
        public IntPtr Reserved;
        public int ReservedSize;
        public uint ExtendedFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr Owner;
        public IntPtr Root;
        public IntPtr DisplayName;
        public string? Title;
        public uint Flags;
        public IntPtr Callback;
        public IntPtr CallbackData;
        public int Image;
    }
}
