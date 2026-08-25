using System.Runtime.InteropServices;

namespace BssManager.Native;

internal static class NativeMethods
{
    // ---------------------------------------------------------------- WTS API
    // Used to see which alt sessions actually exist on this machine and whether
    // their RDP client is attached. This is ground truth -- far more reliable
    // than tracking mstsc.exe processes, which lie when a client reconnects.

    public const int WTS_CURRENT_SERVER_HANDLE = 0;

    public enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }

    public enum WTS_INFO_CLASS
    {
        WTSInitialProgram = 0,
        WTSApplicationName = 1,
        WTSWorkingDirectory = 2,
        WTSOEMId = 3,
        WTSSessionId = 4,
        WTSUserName = 5,
        WTSWinStationName = 6,
        WTSDomainName = 7,
        WTSConnectState = 8
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WTSEnumerateSessionsW(
        IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, int sessionId, WTS_INFO_CLASS infoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSLogoffSession(IntPtr hServer, int sessionId, bool bWait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSDisconnectSession(IntPtr hServer, int sessionId, bool bWait);

    // ------------------------------------------------------------ netapi32 API
    // Local account management. Preferred over shelling out to `net user`
    // because the password never appears in a command line.

    public const int NERR_Success = 0;
    public const int NERR_UserExists = 2224;
    public const int ERROR_MEMBER_IN_ALIAS = 1378;

    public const uint USER_PRIV_USER = 1;
    public const uint UF_SCRIPT = 0x0001;
    public const uint UF_NORMAL_ACCOUNT = 0x0200;
    public const uint UF_DONT_EXPIRE_PASSWD = 0x10000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_1
    {
        public string usri1_name;
        public string usri1_password;
        public uint usri1_password_age;
        public uint usri1_priv;
        public string? usri1_home_dir;
        public string? usri1_comment;
        public uint usri1_flags;
        public string? usri1_script_path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_1003
    {
        public string usri1003_password;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOCALGROUP_MEMBERS_INFO_3
    {
        public string lgrmi3_domainandname;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int NetUserAdd(
        string? serverName, uint level, ref USER_INFO_1 buf, out uint parmError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int NetUserSetInfo(
        string? serverName, string userName, uint level, ref USER_INFO_1003 buf, out uint parmError);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int NetUserDel(string? serverName, string userName);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int NetLocalGroupAddMembers(
        string? serverName, string groupName, uint level,
        [MarshalAs(UnmanagedType.LPArray)] LOCALGROUP_MEMBERS_INFO_3[] buf, uint totalEntries);

    // ----------------------------------------------------------- Credential API
    // Stores the per-alt RDP password in Windows Credential Manager, which is
    // what lets mstsc connect without a prompt. Same thing `cmdkey` does, but
    // without putting the password on a command line.

    public const uint CRED_TYPE_DOMAIN_PASSWORD = 2;
    public const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    public static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    public static extern bool CredDelete(string targetName, uint type, uint flags);
}
