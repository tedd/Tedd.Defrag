using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Security;

namespace Tedd.Defrag.Windows;

public static unsafe class StoragePrivileges
{
    /// <summary>Call only inside the elevated, isolated storage worker. The process exits with the job.</summary>
    public static void Enable()
    {
        using var process = Process.GetCurrentProcess();
        if (!PInvoke.OpenProcessToken(process.SafeHandle, TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            foreach (string name in new[] { "SeBackupPrivilege", "SeManageVolumePrivilege" })
            {
                if (!PInvoke.LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
                TOKEN_PRIVILEGES privileges = new() { PrivilegeCount = 1 };
                privileges.Privileges[0] = new() { Luid = luid, Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED };
                if (!PInvoke.AdjustTokenPrivileges(token, false, &privileges, [])) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (Marshal.GetLastWin32Error() == 1300) throw new UnauthorizedAccessException($"The isolated worker requires {name}. Run it elevated.");
            }
        }
    }
}
