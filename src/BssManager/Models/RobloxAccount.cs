using System.Text.Json.Serialization;

namespace BssManager.Models;

/// <summary>
/// One Roblox account the app can sign in on an alt's behalf.
///
/// What is actually stored is the login token Roblox hands the browser, not a
/// password -- the password is typed into Roblox's own page and never reaches
/// this app. The token is enough to act as the account, so it is sealed with
/// DPAPI under the current Windows user, exactly like the alt passwords.
/// </summary>
public class RobloxAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Roblox username, read back from Roblox rather than typed in.</summary>
    public string Username { get; set; } = "";

    /// <summary>Roblox display name, which can differ from the username.</summary>
    public string DisplayName { get; set; } = "";

    public long UserId { get; set; }

    /// <summary>DPAPI-protected .ROBLOSECURITY value. Never stored in plaintext.</summary>
    public string ProtectedCookie { get; set; } = "";

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When Roblox last confirmed the token still works. Tokens die on password
    /// change, on "log out of all sessions", and on their own schedule, and
    /// nothing tells you -- so the age of this is the honest signal.
    /// </summary>
    public DateTime? LastVerifiedUtc { get; set; }

    [JsonIgnore]
    public string ProfileUrl => $"https://www.roblox.com/users/{UserId}/profile";
}
