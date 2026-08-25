using System.Collections.ObjectModel;
using BssManager.Models;
using BssManager.Services;

namespace BssManager.ViewModels;

/// <summary>
/// An entry in an alt's account picker. A record so the combo box matches the
/// stored selection by value rather than by object identity.
/// </summary>
public record AccountLinkTarget(string? AccountId, string Name);

/// <summary>One row in the alt list: the saved profile plus its live session state.</summary>
public class AltRowViewModel : ObservableObject
{
    private readonly ObservableCollection<AccountLinkTarget> _targets;
    private readonly Action<AltProfile, string?> _assign;

    private SessionState _state = SessionState.None;
    private int _sessionId = -1;
    private bool _isBusy;
    private string _activity = "";
    private bool _windowHidden;
    private bool _hasWindow;

    public AltRowViewModel(
        AltProfile profile,
        ObservableCollection<AccountLinkTarget> targets,
        Action<AltProfile, string?> assign)
    {
        Profile = profile;
        _targets = targets;
        _assign = assign;
    }

    public AltProfile Profile { get; }

    public string DisplayName => Profile.DisplayName;
    public string WindowsUsername => Profile.WindowsUsername;
    public string Target => Profile.LoopbackAddress;
    public string Resolution => $"{Profile.Width} x {Profile.Height}";

    /// <summary>Which Roblox account this session signs in as.</summary>
    public AccountLinkTarget LinkedAccount
    {
        get => _targets.FirstOrDefault(t => t.AccountId == Profile.RobloxAccountId)
               ?? _targets.FirstOrDefault()
               ?? new AccountLinkTarget(null, "No account");
        set
        {
            if (value is null || value.AccountId == Profile.RobloxAccountId) return;
            _assign(Profile, value.AccountId);
            Raise(nameof(LinkedAccount));
            Raise(nameof(HasAccount));
        }
    }

    public bool HasAccount => Profile.RobloxAccountId is not null;

    /// <summary>The private server this session joins, if one is set.</summary>
    public PrivateServerLink? PrivateServer =>
        PrivateServerLink.TryParse(Profile.PrivateServerUrl, 0, out var link, out _) ? link : null;

    public bool HasPrivateServer => PrivateServer is not null;

    /// <summary>
    /// Whether this alt has the macro downloaded. Read from disk rather than
    /// stored, because the folder can be deleted from under us and a config
    /// that insists otherwise is worse than a check.
    /// </summary>
    public bool HasMacro => KairosService.IsInstalled(Profile.WindowsUsername);

    /// <summary>Needs a live session, an installed macro, and nothing else in flight.</summary>
    public bool CanStartMacro => IsRunning && HasMacro && !IsBusy;

    /// <summary>
    /// True when an account is assigned that this session has never signed in
    /// as. Until the sign-in phase has run, launching it would land on a
    /// Roblox that is signed in as somebody else, or as nobody.
    /// </summary>
    public bool NeedsSignIn =>
        Profile.RobloxAccountId is not null &&
        Profile.RobloxAccountId != Profile.SignedInAccountId;

    /// <summary>
    /// Whether this alt's RDP window is currently off screen. Polled rather
    /// than assumed -- the window can be closed or revealed by other means.
    /// </summary>
    public bool IsWindowHidden
    {
        get => _windowHidden;
        set { if (Set(ref _windowHidden, value)) Raise(nameof(WindowToggleLabel)); }
    }

    /// <summary>True when there is an RDP window to act on at all.</summary>
    public bool HasWindow
    {
        get => _hasWindow;
        set
        {
            if (!Set(ref _hasWindow, value)) return;
            Raise(nameof(CanToggleWindow));
            Raise(nameof(WindowToggleLabel));
        }
    }

    /// <summary>
    /// Show/Hide is reachable whenever there is a window to move, and also on a
    /// running session that has lost its window (detached, or closed) -- there
    /// Show means reconnect and bring it back.
    /// </summary>
    public bool CanToggleWindow => HasWindow || IsRunning;

    /// <summary>
    /// "Hide" only when a window is actually on screen; otherwise "Show", which
    /// covers a hidden window and a running session whose window has gone.
    /// </summary>
    public string WindowToggleLabel => HasWindow && !IsWindowHidden ? "Hide" : "Show";

    /// <summary>
    /// The macro's state in one phrase for the card's meta line: whether it is
    /// installed, and if so what it is set to run.
    /// </summary>
    public string MacroSummary
    {
        get
        {
            if (!HasMacro) return "no macro";

            var m = Profile.Macro;
            return $"macro {m.DefaultField}/{m.Pattern}";
        }
    }

    public SessionState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(StateText));
                Raise(nameof(IsRunning));
                Raise(nameof(LaunchLabel));
                Raise(nameof(CanSignIn));
                Raise(nameof(CanStartMacro));
                // Running state gates the power toggle and enables Show-as-reconnect.
                Raise(nameof(CanToggleWindow));
                Raise(nameof(WindowToggleLabel));
            }
        }
    }

    public int SessionId
    {
        get => _sessionId;
        set { if (Set(ref _sessionId, value)) Raise(nameof(StateText)); }
    }

    /// <summary>True while this alt is being repaired or signed in; drives its spinner.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(CanSignIn));
            Raise(nameof(CanStartMacro));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// What this row is doing right now, shown on the card. Signing in runs for
    /// half a minute or more, and a spinner alone does not say what for.
    /// </summary>
    public string Activity
    {
        get => _activity;
        set => Set(ref _activity, value);
    }

    public bool IsRunning => State is SessionState.Active or SessionState.Disconnected;

    /// <summary>Signing in needs a live session and an account to sign in as.</summary>
    public bool CanSignIn => IsRunning && HasAccount && !IsBusy;

    public string StateText => State switch
    {
        SessionState.Active => $"Running  (session {SessionId})",
        SessionState.Disconnected => $"Detached  (session {SessionId})",
        SessionState.Other => $"Session {SessionId}",
        _ => "Not started"
    };

    /// <summary>
    /// mstsc reattaches to an existing session rather than making a second one,
    /// so the button is honest about which of the two is about to happen.
    /// </summary>
    public string LaunchLabel => IsRunning ? "Reconnect" : "Launch";

    /// <summary>
    /// When this alt last ran, or nothing at all. The card shows facts as
    /// separate chips rather than as one joined sentence, so each fact has to
    /// be reachable on its own.
    /// </summary>
    public string LastRunText => Profile.LastLaunchedUtc is { } last
        ? $"ran {last.ToLocalTime():ddd HH:mm}"
        : "";

    /// <summary>The private server in a few words, or nothing.</summary>
    public string ServerText => PrivateServer is { } server ? server.Summary : "";

    public string MetaLine
    {
        get
        {
            var parts = new List<string>
            {
                Profile.WindowsUsername,
                Profile.LoopbackAddress,
                $"{Profile.Width}x{Profile.Height}"
            };

            if (NeedsSignIn) parts.Add("needs sign-in");

            if (PrivateServer is { } server) parts.Add(server.Summary);

            // Where the macro button's label used to say this. Eight buttons
            // did not fit the row, and the state belongs here anyway.
            parts.Add(MacroSummary);

            if (Profile.LastLaunchedUtc is { } last)
                parts.Add($"last run {last.ToLocalTime():ddd HH:mm}");

            return string.Join("   ", parts);
        }
    }

    public void RaiseAll()
    {
        Raise(nameof(DisplayName));
        Raise(nameof(WindowsUsername));
        Raise(nameof(Target));
        Raise(nameof(Resolution));
        Raise(nameof(MetaLine));
        Raise(nameof(StateText));
        Raise(nameof(LaunchLabel));
        Raise(nameof(LinkedAccount));
        Raise(nameof(HasAccount));
        Raise(nameof(CanSignIn));
        Raise(nameof(PrivateServer));
        Raise(nameof(HasPrivateServer));
        Raise(nameof(HasMacro));
        Raise(nameof(CanStartMacro));
        Raise(nameof(MacroSummary));
        Raise(nameof(CanToggleWindow));
        Raise(nameof(WindowToggleLabel));
        Raise(nameof(NeedsSignIn));
        Raise(nameof(LastRunText));
        Raise(nameof(ServerText));
    }
}
