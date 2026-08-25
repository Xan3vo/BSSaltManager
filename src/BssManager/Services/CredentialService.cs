using System.ComponentModel;
using System.Runtime.InteropServices;
using BssManager.Native;

namespace BssManager.Services;

/// <summary>
/// Writes each alt's RDP password into Windows Credential Manager so mstsc
/// connects without prompting.
///
/// Credential Manager keys on the target host, and it stores exactly one
/// credential per target -- which is precisely why every alt gets its own
/// loopback address (127.0.0.2, 127.0.0.3, ...). All of them reach this same
/// machine, but TERMSRV/127.0.0.2 and TERMSRV/127.0.0.3 are separate entries,
/// so alts do not overwrite each other's saved logins.
/// </summary>
public class CredentialService
{
    public void Save(string target, string username, string password)
    {
        var blob = Marshal.StringToCoTaskMemUni(password);
        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        var userPtr = Marshal.StringToCoTaskMemUni(username);
        var commentPtr = Marshal.StringToCoTaskMemUni("BSS Alt Manager");

        try
        {
            var cred = new NativeMethods.CREDENTIAL
            {
                Type = NativeMethods.CRED_TYPE_DOMAIN_PASSWORD,
                TargetName = targetPtr,
                UserName = userPtr,
                Comment = commentPtr,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(password.Length * 2),
                Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE
            };

            if (!NativeMethods.CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Could not save the credential for {target}.");
        }
        finally
        {
            // Zero the password copy before releasing it rather than just freeing.
            for (int i = 0; i < password.Length; i++) Marshal.WriteInt16(blob, i * 2, 0);
            Marshal.FreeCoTaskMem(blob);
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userPtr);
            Marshal.FreeCoTaskMem(commentPtr);
        }
    }

    public void Delete(string target)
    {
        // Missing credentials are not an error -- this runs during cleanup.
        NativeMethods.CredDelete(target, NativeMethods.CRED_TYPE_DOMAIN_PASSWORD, 0);
    }
}
