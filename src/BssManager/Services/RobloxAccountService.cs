using BssManager.Models;

namespace BssManager.Services;

/// <summary>
/// Stores the captured Roblox logins.
///
/// It borrows <see cref="AltManager"/>'s config rather than loading its own:
/// alts and accounts live in the same file, and two services each holding their
/// own copy would quietly overwrite each other on save.
/// </summary>
public class RobloxAccountService
{
    private readonly AltManager _alts;

    public RobloxAccountService(AltManager alts) => _alts = alts;

    public List<RobloxAccount> All => _alts.Config.Accounts;

    /// <summary>
    /// Saves a freshly captured login. Signing in again as an account that is
    /// already here refreshes its token instead of adding a duplicate -- which
    /// is exactly what you want, since re-adding is how you fix an expired one.
    /// </summary>
    public (RobloxAccount account, bool replaced) Save(RobloxIdentity identity, string cookie)
    {
        var existing = All.FirstOrDefault(a => a.UserId == identity.UserId);

        var account = existing ?? new RobloxAccount();
        account.Username = identity.Username;
        account.DisplayName = identity.DisplayName;
        account.UserId = identity.UserId;
        account.ProtectedCookie = SecretProtector.Protect(cookie);
        account.LastVerifiedUtc = DateTime.UtcNow;

        if (existing is null) All.Add(account);

        _alts.Save();
        Log.Write($"{(existing is null ? "added" : "refreshed")} roblox account {identity.Username} ({identity.UserId})");

        return (account, existing is not null);
    }

    public void Remove(RobloxAccount account)
    {
        foreach (var alt in _alts.Config.Alts.Where(a => a.RobloxAccountId == account.Id))
            alt.RobloxAccountId = null;

        All.Remove(account);
        _alts.Save();
        Log.Write($"removed roblox account {account.Username}");
    }

    /// <summary>
    /// Says which account an alt signs in as. Assigning one that is already on
    /// another alt moves it: the same Roblox account cannot be playing in two
    /// sessions at once, so letting both rows claim it would only ever produce
    /// a confusing lie on screen.
    /// </summary>
    public void Assign(AltProfile alt, string? accountId)
    {
        if (accountId is not null)
        {
            foreach (var other in _alts.Config.Alts.Where(
                         a => !ReferenceEquals(a, alt) && a.RobloxAccountId == accountId))
            {
                other.RobloxAccountId = null;
            }
        }

        alt.RobloxAccountId = accountId;
        _alts.Save();
    }

    /// <summary>The account an alt is set to sign in as, if there is one.</summary>
    public RobloxAccount? ForAlt(AltProfile alt) =>
        alt.RobloxAccountId is null
            ? null
            : All.FirstOrDefault(a => a.Id == alt.RobloxAccountId);

    /// <summary>The alt an account is assigned to, if any.</summary>
    public AltProfile? AltFor(RobloxAccount account) =>
        _alts.Config.Alts.FirstOrDefault(a => a.RobloxAccountId == account.Id);

    /// <summary>
    /// Asks Roblox whether the stored token still works. Returns the message to
    /// show; a dead token is a normal outcome, not an error.
    /// </summary>
    public async Task<(bool ok, string message)> VerifyAsync(RobloxAccount account)
    {
        var cookie = SecretProtector.Unprotect(account.ProtectedCookie);

        if (string.IsNullOrEmpty(cookie))
            return (false, $"{account.Username}: no stored login. Remove it and add it again.");

        var identity = await RobloxApi.WhoAmIAsync(cookie);

        if (identity is null)
        {
            account.LastVerifiedUtc = null;
            _alts.Save();
            return (false, $"{account.Username}: Roblox rejected the stored login. Add the account again to refresh it.");
        }

        // A username can change, and the stored one going stale would make the
        // list quietly wrong.
        account.Username = identity.Username;
        account.DisplayName = identity.DisplayName;
        account.LastVerifiedUtc = DateTime.UtcNow;
        _alts.Save();

        return (true, $"{identity.Username} is still signed in.");
    }

    /// <summary>The stored token, for whatever eventually signs the alt in.</summary>
    public string GetCookie(RobloxAccount account) =>
        SecretProtector.Unprotect(account.ProtectedCookie);
}
