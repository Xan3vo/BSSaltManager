using BssManager.Models;

namespace BssManager.Services;

/// <summary>
/// Ties the pieces together: a local account, a saved credential, a generated
/// .rdp file and a live session are all one "alt" from the user's point of view.
/// </summary>
public class AltManager
{
    private readonly ConfigStore _store = new();
    private readonly LocalUserService _users = new();
    private readonly CredentialService _credentials = new();
    private readonly RdpFileService _rdpFiles = new();
    private readonly SessionService _sessions = new();
    private readonly AltSetupService _setup = new();

    public AppConfig Config { get; private set; }

    public AltManager()
    {
        Config = _store.Load();
    }

    public void Save()
    {
        _store.Save(Config);

        // The first-logon script only touches accounts on this list, so it has
        // to follow every add and remove.
        _setup.UpdateAltList(Config.Alts.Select(a => a.WindowsUsername));
    }

    /// <summary>
    /// Creates a fresh alt end to end. The Windows password is generated here
    /// and never shown -- nothing in the workflow requires a human to type it,
    /// so there is no reason for it to be memorable or visible.
    /// </summary>
    public AltProfile CreateAlt(string displayName, string username, int width, int height, bool hideFromLoginScreen)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Windows username is required.");

        if (Config.Alts.Any(a => string.Equals(a.WindowsUsername, username, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"'{username}' is already managed by this app.");

        var alt = new AltProfile
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(),
            WindowsUsername = username.Trim(),
            Width = width,
            Height = height,
            HideFromLoginScreen = hideFromLoginScreen,
            LoopbackAddress = NextLoopbackAddress()
        };

        var password = SecretProtector.GeneratePassword();
        alt.ProtectedPassword = SecretProtector.Protect(password);

        _users.CreateOrRepair(alt.WindowsUsername, password, alt.HideFromLoginScreen);
        _credentials.Save(alt.CredentialTarget, $@"{Environment.MachineName}\{alt.WindowsUsername}", password);
        _rdpFiles.Write(alt);

        Config.Alts.Add(alt);
        Save();

        Log.Write($"created alt {alt.DisplayName} ({alt.WindowsUsername}) on {alt.LoopbackAddress}");
        return alt;
    }

    /// <summary>
    /// Adopts a local account that already exists -- the ones set up by hand
    /// following the usual guides. The password is rotated to a generated one so
    /// the app can log in unattended from here on.
    /// </summary>
    public AltProfile AdoptExistingUser(string displayName, string username, int width, int height, bool hideFromLoginScreen)
    {
        if (!_users.UserExists(username))
            throw new InvalidOperationException($"No local account named '{username}' exists on this machine.");

        return CreateAlt(displayName, username, width, height, hideFromLoginScreen);
    }

    /// <summary>
    /// Re-applies everything this alt needs: the account, its group membership,
    /// the saved credential and the .rdp file. Fixes an alt that was changed
    /// outside the app.
    /// </summary>
    public void Repair(AltProfile alt)
    {
        var password = SecretProtector.Unprotect(alt.ProtectedPassword);

        if (string.IsNullOrEmpty(password))
        {
            // The stored blob is unreadable (config copied from another profile
            // or machine). Issue a new password rather than failing outright.
            password = SecretProtector.GeneratePassword();
            alt.ProtectedPassword = SecretProtector.Protect(password);
        }

        _users.CreateOrRepair(alt.WindowsUsername, password, alt.HideFromLoginScreen);
        _credentials.Save(alt.CredentialTarget, $@"{Environment.MachineName}\{alt.WindowsUsername}", password);
        _rdpFiles.Write(alt);
        Save();

        Log.Write($"repaired alt {alt.WindowsUsername}");
    }

    /// <summary>
    /// Removes an alt from the app. Deleting the Windows account is opt-in and
    /// separate, because that account owns the alt's Roblox install and macro
    /// configuration.
    /// </summary>
    public void Remove(AltProfile alt, bool deleteWindowsAccount)
    {
        try { _sessions.LogOff(alt); } catch (Exception ex) { Log.Write($"logoff during removal failed: {ex.Message}"); }

        _credentials.Delete(alt.CredentialTarget);
        _rdpFiles.Delete(alt);

        if (deleteWindowsAccount) _users.DeleteUser(alt.WindowsUsername);
        else _users.SetHiddenFromLoginScreen(alt.WindowsUsername, false);

        Config.Alts.Remove(alt);
        Save();
    }

    public void Launch(AltProfile alt, bool startHidden = false)
    {
        // Always rewrite the .rdp first so edits to size take effect on the next
        // launch without the user needing to know a file exists.
        _rdpFiles.Write(alt);
        _sessions.Launch(alt, startHidden);

        alt.LastLaunchedUtc = DateTime.UtcNow;
        Save();
    }

    /// <summary>
    /// Assigns each alt its own 127.0.0.x address. Credential Manager keys on
    /// the host, so distinct addresses are what keep saved logins separate.
    /// .1 is skipped -- that is the host's own session.
    /// </summary>
    private string NextLoopbackAddress()
    {
        var used = Config.Alts
            .Select(a => a.LoopbackAddress)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int octet = Math.Max(2, Config.NextLoopbackOctet); octet <= 254; octet++)
        {
            var candidate = $"127.0.0.{octet}";
            if (used.Contains(candidate)) continue;

            Config.NextLoopbackOctet = octet + 1;
            return candidate;
        }

        throw new InvalidOperationException("Ran out of loopback addresses in 127.0.0.0/24.");
    }
}
