using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BssManager.Native;

/// <summary>
/// Loads and unloads registry hives.
///
/// Being an administrator is not enough. SeBackupPrivilege and
/// SeRestorePrivilege sit in the token in a *disabled* state, and hive loading
/// fails until they are switched on. Shelling out to reg.exe cannot work
/// either: the child process gets its own copy of the token with those
/// privileges disabled again. It has to happen in-process, which is what this
/// does. The failure mode is worth knowing because the error Windows returns
/// is "The filename or extension is too long", which says nothing at all about
/// privileges.
/// </summary>
internal static class PrivilegedRegistry
{
    // Must sign-extend. Windows defines predefined keys by casting through a
    // signed LONG, so on x64 HKEY_LOCAL_MACHINE is 0xFFFFFFFF80000002. Passing
    // the unextended 0x0000000080000002 is not recognised as a predefined key
    // and every call fails with ERROR_INVALID_PARAMETER (87).
    private static readonly IntPtr HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));
    private static readonly IntPtr HKEY_USERS = new(unchecked((int)0x80000003));

    private const int TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const int TOKEN_QUERY = 0x0008;
    private const int SE_PRIVILEGE_ENABLED = 0x0002;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;

    private const string SE_BACKUP_NAME = "SeBackupPrivilege";
    private const string SE_RESTORE_NAME = "SeRestorePrivilege";

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
        int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegLoadKeyW(IntPtr key, string subKey, string file);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegUnLoadKeyW(IntPtr key, string subKey);

    /// <summary>
    /// Mounts <paramref name="hiveFile"/> at HKLM\<paramref name="mountName"/>.
    /// Throws with a readable message rather than the misleading Win32 text.
    /// </summary>
    public static void LoadHive(string mountName, string hiveFile, bool underUsers = false)
    {
        EnableHivePrivileges();

        var result = RegLoadKeyW(underUsers ? HKEY_USERS : HKEY_LOCAL_MACHINE, mountName, hiveFile);
        if (result != 0)
            throw new Win32Exception(result,
                $"Could not load '{hiveFile}' (code {result}). " +
                "This usually means the hive is already in use, or backup/restore privileges were refused.");
    }

    public static void UnloadHive(string mountName, bool underUsers = false)
    {
        // Any RegistryKey still open against the hive keeps it mounted, so make
        // sure finalisers have run before asking Windows to let go.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var result = RegUnLoadKeyW(underUsers ? HKEY_USERS : HKEY_LOCAL_MACHINE, mountName);
        if (result != 0)
            throw new Win32Exception(result, $"Could not unload HKLM\\{mountName} (code {result}).");
    }

    private static void EnableHivePrivileges()
    {
        Enable(SE_BACKUP_NAME);
        Enable(SE_RESTORE_NAME);
    }

    private static void Enable(string privilegeName)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the process token.");

        try
        {
            if (!LookupPrivilegeValueW(null, privilegeName, out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unknown privilege '{privilegeName}'.");

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };

            if (!AdjustTokenPrivileges(token, false, ref privileges,
                    Marshal.SizeOf<TOKEN_PRIVILEGES>(), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not adjust '{privilegeName}'.");

            // AdjustTokenPrivileges reports success even when it silently
            // assigned nothing, so the real answer is in the last error.
            if (Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED)
                throw new InvalidOperationException(
                    $"'{privilegeName}' is not held by this process. The app must run elevated.");
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
