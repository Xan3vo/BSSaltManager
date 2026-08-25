using BssManager.Models;

namespace BssManager.ViewModels;

/// <summary>
/// One row in the accounts list: the saved Roblox login and its state.
///
/// Which alt runs it is chosen on the Alts tab, not here -- a session runs one
/// account, so the choice belongs to the session. This row only reports it.
/// </summary>
public class AccountRowViewModel : ObservableObject
{
    private readonly Func<RobloxAccount, string?> _resolveAlt;
    private bool _isBusy;

    public AccountRowViewModel(RobloxAccount account, Func<RobloxAccount, string?> resolveAlt)
    {
        Account = account;
        _resolveAlt = resolveAlt;
    }

    public RobloxAccount Account { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Account.DisplayName)
        ? Account.Username
        : Account.DisplayName;

    public string Username => Account.Username;

    /// <summary>
    /// A stored token is only ever "worked a moment ago" or "unknown" -- Roblox
    /// gives no expiry, and one can be revoked without warning. Saying Ready
    /// when it was checked long ago would be a guess dressed up as a fact.
    /// </summary>
    public HealthState State => Account.LastVerifiedUtc switch
    {
        null => HealthState.Warning,
        var t when DateTime.UtcNow - t.Value > TimeSpan.FromHours(12) => HealthState.Unknown,
        _ => HealthState.Ok
    };

    public string StateText => Account.LastVerifiedUtc switch
    {
        null => "Needs re-adding",
        var t when DateTime.UtcNow - t.Value > TimeSpan.FromHours(12) => "Not checked lately",
        _ => "Signed in"
    };

    public string MetaLine
    {
        get
        {
            var parts = new List<string> { $"@{Account.Username}", $"id {Account.UserId}" };

            parts.Add(Account.LastVerifiedUtc is { } verified
                ? $"checked {Ago(verified)}"
                : "never confirmed");

            var alt = _resolveAlt(Account);
            parts.Add(alt is null ? "not assigned to an alt" : $"runs on {alt}");

            return string.Join("   ", parts);
        }
    }

    /// <summary>
    /// The same facts as MetaLine, one at a time. The card shows them as
    /// separate chips, and a chip cannot be cut out of a joined sentence.
    /// </summary>
    public string HandleText => $"@{Account.Username}";

    public string IdText => $"id {Account.UserId}";

    public string CheckedText => Account.LastVerifiedUtc is { } verified
        ? $"checked {Ago(verified)}"
        : "never confirmed";

    public string AssignedText => _resolveAlt(Account) is { } alt
        ? $"runs on {alt}"
        : "not assigned";

    /// <summary>Dims the assignment chip when there is nothing assigned.</summary>
    public bool IsAssigned => _resolveAlt(Account) is not null;

    /// <summary>True while this account is being checked; drives its spinner.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!Set(ref _isBusy, value)) return;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public void RaiseAll()
    {
        Raise(nameof(DisplayName));
        Raise(nameof(Username));
        Raise(nameof(State));
        Raise(nameof(StateText));
        Raise(nameof(MetaLine));
        Raise(nameof(HandleText));
        Raise(nameof(IdText));
        Raise(nameof(CheckedText));
        Raise(nameof(AssignedText));
        Raise(nameof(IsAssigned));
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;

        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";

        return $"{(int)span.TotalDays} d ago";
    }
}
