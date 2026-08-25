using System.ComponentModel;
using System.Security.Principal;
using BssManager.Native;
using Microsoft.Win32;

namespace BssManager.Services;

/// <summary>
/// Creates and removes the local Windows accounts that back each alt session.
///
/// Uses the netapi32 functions directly rather than shelling out to `net user`
/// so the generated password never lands in a command line where any process
/// on the box could read it.
/// </summary>
public class LocalUserService
{
    private const string SpecialAccountsKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    public bool UserExists(string username)
    {
        try
        {
            _ = new NTAccount(Environment.MachineName, username).Translate(typeof(SecurityIdentifier));
            return true;
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the account if missing, makes sure it is in Remote Desktop Users,
    /// and optionally hides it from the sign-in screen. Safe to call repeatedly:
    /// an existing account has its password reset to the stored one instead of
    /// failing, which is also how "repair this alt" works.
    /// </summary>
    public void CreateOrRepair(string username, string password, bool hideFromLoginScreen)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required.", nameof(username));

        var info = new NativeMethods.USER_INFO_1
        {
            usri1_name = username,
            usri1_password = password,
            usri1_priv = NativeMethods.USER_PRIV_USER,
            usri1_flags = NativeMethods.UF_SCRIPT
                          | NativeMethods.UF_NORMAL_ACCOUNT
                          | NativeMethods.UF_DONT_EXPIRE_PASSWD,
            usri1_comment = "Alt session account created by BSS Alt Manager.",
            usri1_home_dir = null,
            usri1_script_path = null
        };

        var result = NativeMethods.NetUserAdd(null, 1, ref info, out var parmError);

        if (result == NativeMethods.NERR_UserExists)
        {
            // Account is already there -- realign its password with what we have
            // stored so the saved credential still works.
            SetPassword(username, password);
        }
        else if (result != NativeMethods.NERR_Success)
        {
            throw new Win32Exception(result,
                $"Could not create local user '{username}' (code {result}, field {parmError}).");
        }

        AddToRemoteDesktopUsers(username);

        if (hideFromLoginScreen) SetHiddenFromLoginScreen(username, true);

        Log.Write($"user ready: {username}");
    }

    public void SetPassword(string username, string password)
    {
        var info = new NativeMethods.USER_INFO_1003 { usri1003_password = password };
        var result = NativeMethods.NetUserSetInfo(null, username, 1003, ref info, out var parmError);
        if (result != NativeMethods.NERR_Success)
            throw new Win32Exception(result,
                $"Could not set the password for '{username}' (code {result}, field {parmError}).");
    }

    /// <summary>
    /// Membership is granted by resolving the well-known SID rather than by
    /// hardcoding "Remote Desktop Users", which is localised on non-English
    /// installs of Windows.
    /// </summary>
    public void AddToRemoteDesktopUsers(string username)
    {
        var groupName = ResolveGroupName(WellKnownSidType.BuiltinRemoteDesktopUsersSid);

        var members = new[]
        {
            new NativeMethods.LOCALGROUP_MEMBERS_INFO_3
            {
                lgrmi3_domainandname = $@"{Environment.MachineName}\{username}"
            }
        };

        var result = NativeMethods.NetLocalGroupAddMembers(null, groupName, 3, members, 1);

        if (result != NativeMethods.NERR_Success && result != NativeMethods.ERROR_MEMBER_IN_ALIAS)
            throw new Win32Exception(result,
                $"Could not add '{username}' to '{groupName}' (code {result}).");
    }

    /// <summary>
    /// Keeps the alt accounts off the Windows sign-in screen. They are only ever
    /// used over loopback RDP, so having ten of them cluttering the lock screen
    /// is pure noise.
    /// </summary>
    public void SetHiddenFromLoginScreen(string username, bool hidden)
    {
        using var key = Registry.LocalMachine.CreateSubKey(SpecialAccountsKey);
        if (hidden)
            key.SetValue(username, 0, RegistryValueKind.DWord);
        else
            key.DeleteValue(username, throwOnMissingValue: false);
    }

    /// <summary>
    /// Deletes the account. The profile folder under C:\Users is deliberately
    /// left alone -- it holds that alt's Roblox install and macro settings, and
    /// silently destroying it would be a nasty surprise.
    /// </summary>
    public void DeleteUser(string username)
    {
        SetHiddenFromLoginScreen(username, false);

        var result = NativeMethods.NetUserDel(null, username);
        if (result != NativeMethods.NERR_Success && result != 2221 /* NERR_UserNotFound */)
            throw new Win32Exception(result, $"Could not delete '{username}' (code {result}).");

        Log.Write($"user deleted: {username}");
    }

    private static string ResolveGroupName(WellKnownSidType type)
    {
        var sid = new SecurityIdentifier(type, null);
        var account = (NTAccount)sid.Translate(typeof(NTAccount));
        var name = account.Value;
        var slash = name.IndexOf('\\');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }
}
