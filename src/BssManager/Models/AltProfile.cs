using System.Text.Json.Serialization;

namespace BssManager.Models;

/// <summary>
/// One alt: a local Windows account that gets its own RDP session, its own
/// Roblox install and (later) its own macro instance.
/// </summary>
public class AltProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Friendly label shown in the UI, e.g. "Alt 1 - Gifted Bear".</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>The local Windows account name this session logs in as.</summary>
    public string WindowsUsername { get; set; } = "";

    /// <summary>DPAPI-protected password blob (base64). Never stored in plaintext.</summary>
    public string ProtectedPassword { get; set; } = "";

    /// <summary>
    /// Loopback address this alt connects to (127.0.0.2, 127.0.0.3, ...).
    /// Every alt needs a distinct one because cmdkey stores exactly one
    /// credential per TERMSRV/&lt;host&gt; target. Same machine either way.
    /// </summary>
    public string LoopbackAddress { get; set; } = "127.0.0.2";

    /// <summary>Pinned session size. Must stay fixed or pixel-based macros break.</summary>
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;

    /// <summary>Launch this alt when "Launch All" is used.</summary>
    public bool IncludeInLaunchAll { get; set; } = true;

    /// <summary>Hide this account from the Windows sign-in screen.</summary>
    public bool HideFromLoginScreen { get; set; } = true;

    /// <summary>
    /// <see cref="RobloxAccount.Id"/> of the account this session signs in as,
    /// or null if nothing is assigned. Held on the alt rather than the account
    /// because a session runs one account at a time, and that is the constraint
    /// worth making impossible to break.
    /// </summary>
    public string? RobloxAccountId { get; set; }

    /// <summary>
    /// Private server this alt joins, as pasted. Stored as the link rather than
    /// the parsed code so what you gave it is what you get back, and so a
    /// change in what Roblox accepts does not strand saved data.
    /// </summary>
    public string PrivateServerUrl { get; set; } = "";

    /// <summary>
    /// Per-alt macro configuration. Held here rather than in the macro's own
    /// folder so it survives reinstalling or updating Kairos, and so adding an
    /// alt does not mean configuring one twice.
    /// </summary>
    public MacroSettings Macro { get; set; } = new();

    /// <summary>
    /// The account this session has actually been signed in as, once its
    /// sign-in phase has run through.
    ///
    /// Separate from <see cref="RobloxAccountId"/> because assigning an account
    /// is a decision and signing it in is work: the first sign-in on a fresh
    /// profile installs Roblox, walks its first-run screens and takes minutes.
    /// When these two disagree, that work still has to happen.
    /// </summary>
    public string? SignedInAccountId { get; set; }

    /// <summary>
    /// Keep this alt's RDP window off the screen and out of the taskbar.
    /// The session keeps running either way: hiding a window is not minimising
    /// it, so the remote desktop carries on composing and the macro keeps
    /// seeing it. Remembered per alt, so a launch puts it straight back.
    /// </summary>
    public bool HideWindow { get; set; }

    public string Notes { get; set; } = "";

    public DateTime? LastLaunchedUtc { get; set; }

    [JsonIgnore]
    public string ClientTarget => LoopbackAddress;

    [JsonIgnore]
    public string CredentialTarget => $"TERMSRV/{LoopbackAddress}";
}
