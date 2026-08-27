using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BssManager.Models;
using BssManager.Services;
using BssManager.Views;

namespace BssManager.ViewModels;

public record NewAltRequest(string DisplayName, string Username, int Width, int Height, bool HideFromLoginScreen, bool AdoptExisting);

/// <summary>Which pane the window is showing.</summary>
public enum MainTab
{
    Alts,
    Accounts,
    Health
}

public class MainViewModel : ObservableObject
{
    private readonly AltManager _alts = new();
    private readonly SessionService _sessions = new();
    private readonly RdpWrapService _rdpWrap = new();
    private readonly AltSetupService _altSetup = new();
    private readonly RdpSigningService _signing = new();
    private readonly RobloxAccountService _accounts;
    private readonly RobloxLaunchService _robloxLaunch = new();
    private readonly SessionCommandService _inSession = new();
    private readonly KairosService _kairos = new();
    private readonly SessionWindowService _windows = new();
    private readonly DispatcherTimer _timer;

    private string _status = "Ready.";
    private bool _busy;
    private HealthCheck? _selectedCheck;
    private bool _isCheckingHealth;
    private AltRowViewModel? _selected;
    private MainTab _activeTab = MainTab.Alts;

    public MainViewModel()
    {
        _accounts = new RobloxAccountService(_alts);

        RebuildAccountTargets();

        foreach (var profile in _alts.Config.Alts)
            Alts.Add(NewAltRow(profile));

        foreach (var account in _accounts.All)
            Accounts.Add(NewAccountRow(account));

        Alts.CollectionChanged += (_, _) =>
        {
            Raise(nameof(HasNoAlts));
            Raise(nameof(AltCountText));

            // An alt appearing or disappearing changes which account is
            // assigned where, and the accounts list reports that.
            foreach (var row in Accounts) row.RaiseAll();
        };

        Accounts.CollectionChanged += (_, _) =>
        {
            Raise(nameof(HasNoAccounts));
            Raise(nameof(AccountCountText));

            // The alts' account pickers list these.
            RebuildAccountTargets();
            foreach (var row in Alts) row.RaiseAll();
        };

        GoToSessionsCommand = new RelayCommand(() => ActiveTab = MainTab.Alts);
        GoToAccountsCommand = new RelayCommand(() => ActiveTab = MainTab.Accounts);
        GoToHealthCommand = new RelayCommand(() => ActiveTab = MainTab.Health);

        AddAltCommand = new RelayCommand(AddAlt, () => !Busy);
        // Repair and Remove act on an alt, so they take the row as a parameter
        // and live on the alt's own card. They fall back to the selected row so
        // a keyboard-driven path still works.
        RemoveAltCommand = new RelayCommand(p => RemoveAlt(AsRow(p) ?? Selected), p => (AsRow(p) ?? Selected) is not null && !Busy);
        RepairCommand = new RelayCommand(p => _ = RepairAltAsync(AsRow(p) ?? Selected), p => (AsRow(p) ?? Selected) is { IsBusy: false } && !Busy);
        LaunchCommand = new RelayCommand(p => LaunchOne(AsRow(p)), p => AsRow(p) is not null && !Busy);
        DisconnectCommand = new RelayCommand(p => DisconnectOne(AsRow(p)), p => AsRow(p) is { IsRunning: true });
        LogOffCommand = new RelayCommand(p => LogOffOne(AsRow(p)), p => AsRow(p) is { IsRunning: true });
        LaunchAllCommand = new RelayCommand(async () => await LaunchAllAsync(), () => Alts.Count > 0 && !Busy);
        LogOffAllCommand = new RelayCommand(LogOffAll, () => Alts.Any(a => a.IsRunning) && !Busy);
        RefreshHealthCommand = new RelayCommand(() => _ = RefreshHealthAsync(), () => !IsCheckingHealth && !Busy);
        ApplyFixCommand = new RelayCommand(p => _ = ApplyFixAsync(p as HealthCheck), _ => !IsCheckingHealth && !Busy);
        OpenDataFolderCommand = new RelayCommand(() => OpenInExplorer(AppPaths.Root));
        OpenLogCommand = new RelayCommand(OpenLog);

        AddAccountCommand = new RelayCommand(AddAccount, () => !Busy);
        RemoveAccountCommand = new RelayCommand(p => RemoveAccount(AsAccount(p)), p => AsAccount(p) is not null && !Busy);
        VerifyAccountCommand = new RelayCommand(p => _ = VerifyAccountAsync(AsAccount(p)), p => AsAccount(p) is { IsBusy: false });
        VerifyAllAccountsCommand = new RelayCommand(async () => await VerifyAllAsync(), () => Accounts.Count > 0 && !Busy);
        OpenProfileCommand = new RelayCommand(p => OpenProfile(AsAccount(p)), p => AsAccount(p) is not null);
        SignInCommand = new RelayCommand(p => _ = SignInAsync(AsRow(p)), p => AsRow(p) is { CanSignIn: true });
        MacroCommand = new RelayCommand(p => ConfigureMacro(AsRow(p)), p => AsRow(p) is not null && !Busy);
        SignInPhaseCommand = new RelayCommand(
            p => { if (AsRow(p) is { } r) _ = RunSignInPhaseAsync(r); },
            p => AsRow(p) is { HasAccount: true, IsBusy: false });
        StartMacroCommand = new RelayCommand(p => _ = StartMacroAsync(AsRow(p)), p => AsRow(p) is { CanStartMacro: true });
        ToggleWindowCommand = new RelayCommand(p => ToggleWindow(AsRow(p)), p => AsRow(p) is { CanToggleWindow: true });

        _altSetup.RefreshBlockList();
        _ = RefreshHealthAsync();
        RefreshSessions();

        // Session state changes underneath us constantly -- a macro crashing,
        // a window being closed, Windows dropping a session. Poll so the list
        // reflects reality rather than what we last did.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => RefreshSessions();
        _timer.Start();
    }

    // ------------------------------------------------------------------- state

    public ObservableCollection<AltRowViewModel> Alts { get; } = new();
    public ObservableCollection<AccountRowViewModel> Accounts { get; } = new();

    /// <summary>What an alt's account picker offers: nothing, or a saved account.</summary>
    public ObservableCollection<AccountLinkTarget> AccountTargets { get; } = new();
    public ObservableCollection<HealthCheck> HealthChecks { get; } = new();

    /// <summary>Set by the view so the view model can raise the add-alt dialog.</summary>
    public Func<NewAltRequest?>? PromptForNewAlt { get; set; }

    public AltRowViewModel? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool Busy
    {
        get => _busy;
        set { if (Set(ref _busy, value)) Raise(nameof(NotBusy)); }
    }

    public bool NotBusy => !Busy;

    // Deliberately no machine name: it shows up in screenshots and recordings,
    // and nothing here needs it. The .rdp files still use it internally.
    public string HostSummary => $"termsrv.dll {_rdpWrap.TermSrvVersion}";

    public string AltCountText => Alts.Count == 1 ? "1 RDP" : $"{Alts.Count} RDPs";

    public string AccountCountText => Accounts.Count == 1 ? "1 account" : $"{Accounts.Count} accounts";

    public bool HasNoAccounts => Accounts.Count == 0;

    public MainTab ActiveTab
    {
        get => _activeTab;
        set
        {
            if (!Set(ref _activeTab, value)) return;
            Raise(nameof(IsAltsTab));
            Raise(nameof(IsAccountsTab));
            Raise(nameof(IsHealthTab));
        }
    }

    /// <summary>
    /// Settable, so the tab buttons can bind two-way. A one-way binding plus a
    /// command looks equivalent and is not: arrow-keying between radio buttons
    /// changes the checked one without ever raising Click, and the pane would
    /// stay on the tab you left.
    /// </summary>
    public bool IsAltsTab
    {
        get => ActiveTab == MainTab.Alts;
        set { if (value) ActiveTab = MainTab.Alts; }
    }

    public bool IsAccountsTab
    {
        get => ActiveTab == MainTab.Accounts;
        set { if (value) ActiveTab = MainTab.Accounts; }
    }

    public bool IsHealthTab
    {
        get => ActiveTab == MainTab.Health;
        set { if (value) ActiveTab = MainTab.Health; }
    }

    /// <summary>
    /// The check the web's inspector is describing. Every check is always on
    /// the diagram -- hiding the passing ones would cut the strands that
    /// explain the failing ones -- so what changes is which one is being read,
    /// not which ones exist.
    /// </summary>
    public HealthCheck? SelectedCheck
    {
        get => _selectedCheck;
        set
        {
            if (!Set(ref _selectedCheck, value)) return;
            Raise(nameof(HasSelectedCheck));
            Raise(nameof(SelectedWhat));
            Raise(nameof(SelectedManually));
        }
    }

    public bool HasSelectedCheck => SelectedCheck is not null;

    /// <summary>Plain-English "what is this thing", from the view's copy.</summary>
    public string SelectedWhat => HealthCopy.What(SelectedCheck?.Name);

    /// <summary>What to do when the button cannot do it for you.</summary>
    public string SelectedManually => HealthCopy.Manually(SelectedCheck?.Name);

    public string HealthSummary
    {
        get
        {
            var failed = HealthChecks.Count(c => c.State == HealthState.Failed);
            var warned = HealthChecks.Count(c => c.State == HealthState.Warning);
            if (failed > 0) return $"{failed} blocking problem{(failed == 1 ? "" : "s")}";
            if (warned > 0) return $"{warned} warning{(warned == 1 ? "" : "s")}";
            return $"All {HealthChecks.Count} checks passing";
        }
    }

    /// <summary>
    /// The same verdict as HealthSummary, short enough for the badge on the
    /// navigation rail. That badge is the only sign of trouble while you are
    /// on another page, so it has to fit in about eight characters.
    /// </summary>
    public string HealthBadge
    {
        get
        {
            var failed = HealthChecks.Count(c => c.State == HealthState.Failed);
            var warned = HealthChecks.Count(c => c.State == HealthState.Warning);
            if (failed > 0) return failed.ToString();
            if (warned > 0) return warned.ToString();
            return "OK";
        }
    }

    public bool HasBlockingProblem => HealthChecks.Any(c => c.State == HealthState.Failed);

    public bool HasWarning => HealthChecks.Any(c => c.State == HealthState.Warning);

    public bool HostIsHealthy => HealthChecks.Count > 0 && !HasBlockingProblem && !HasWarning;

    public string BannerText => HasBlockingProblem
        ? "Multi-session is not working right now. Launching an alt will take over your own session instead of opening a new one."
        : "";

    public bool HasNoAlts => Alts.Count == 0;

    /// <summary>Drives the spinner beside the health panel heading.</summary>
    public bool IsCheckingHealth
    {
        get => _isCheckingHealth;
        set
        {
            if (!Set(ref _isCheckingHealth, value)) return;

            // WPF only re-evaluates CanExecute on user input, so without this
            // the Re-check button stays greyed out after the check finishes
            // until the mouse happens to move.
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ---------------------------------------------------------------- commands

    /// <summary>
    /// Ctrl-1 through Ctrl-3. The rail can be clicked, but a tool somebody
    /// leaves open all day should not need the mouse to move between pages.
    /// </summary>
    public RelayCommand GoToSessionsCommand { get; }
    public RelayCommand GoToAccountsCommand { get; }
    public RelayCommand GoToHealthCommand { get; }

    public RelayCommand AddAltCommand { get; }
    public RelayCommand RemoveAltCommand { get; }
    public RelayCommand RepairCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand LogOffCommand { get; }
    public RelayCommand LaunchAllCommand { get; }
    public RelayCommand LogOffAllCommand { get; }
    public RelayCommand RefreshHealthCommand { get; }
    public RelayCommand ApplyFixCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand AddAccountCommand { get; }
    public RelayCommand RemoveAccountCommand { get; }
    public RelayCommand VerifyAccountCommand { get; }
    public RelayCommand VerifyAllAccountsCommand { get; }
    public RelayCommand OpenProfileCommand { get; }
    public RelayCommand SignInCommand { get; }
    public RelayCommand SignInPhaseCommand { get; }
    public RelayCommand MacroCommand { get; }
    public RelayCommand StartMacroCommand { get; }
    public RelayCommand ToggleWindowCommand { get; }

    // ----------------------------------------------------------------- actions

    private void AddAlt()
    {
        var request = PromptForNewAlt?.Invoke();
        if (request is null) return;

        Run(() =>
        {
            var alt = request.AdoptExisting
                ? _alts.AdoptExistingUser(request.DisplayName, request.Username, request.Width, request.Height, request.HideFromLoginScreen)
                : _alts.CreateAlt(request.DisplayName, request.Username, request.Width, request.Height, request.HideFromLoginScreen);

            _inSession.EnsureTask(alt.WindowsUsername);

            var row = NewAltRow(alt);
            Alts.Add(row);
            Selected = row;
            Status = $"Added {alt.DisplayName} on {alt.LoopbackAddress}.";
        }, "Could not add the alt");
    }

    private void RemoveAlt(AltRowViewModel? row)
    {
        if (row is null) return;

        var answer = MessageDialog.Show(
            $"Remove {row.DisplayName}?",
            @"Either way, its Roblox install stays on disk under C:\Users. Its copy of the macro is deleted -- it belongs to this alt and nothing else can use it.",
            primary: "Remove and delete account",
            secondary: "Remove, keep account",
            cancel: "Cancel",
            kind: DialogKind.Danger);

        if (answer == DialogChoice.Cancel) return;

        Run(() =>
        {
            _inSession.RemoveTask(row.WindowsUsername);
            _kairos.Remove(row.WindowsUsername);
            _alts.Remove(row.Profile, deleteWindowsAccount: answer == DialogChoice.Primary);
            Alts.Remove(row);
            Status = $"Removed {row.DisplayName}.";
        }, "Could not remove the alt");
    }

    private async Task RepairAltAsync(AltRowViewModel? row)
    {
        if (row is null || row.IsBusy) return;

        row.IsBusy = true;
        Status = $"Repairing {row.DisplayName}...";

        try
        {
            var started = DateTime.UtcNow;
            await Task.Run(() =>
            {
                _alts.Repair(row.Profile);
                _inSession.EnsureTask(row.WindowsUsername);
            });

            // Same reasoning as the health check: repair is usually instant,
            // and an instant result is indistinguishable from a dead button.
            var minimum = TimeSpan.FromMilliseconds(520);
            var elapsed = DateTime.UtcNow - started;
            if (elapsed < minimum) await Task.Delay(minimum - elapsed);

            row.RaiseAll();
            Status = $"Repaired {row.DisplayName}: account, group membership, saved credential, .rdp file and sign-in task are back in sync.";
        }
        catch (Exception ex)
        {
            Log.Write($"repair failed: {ex}");
            Status = $"Repair failed: {ex.Message}";
            MessageDialog.Show("Repair failed", ex.Message, primary: "OK", kind: DialogKind.Danger);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private void LaunchOne(AltRowViewModel? row)
    {
        if (row is null) return;

        if (HasBlockingProblem)
        {
            var proceed = MessageDialog.Show(
                "Health check failed",
                "Launching now will most likely fail, or take over your own session instead of opening a new one.",
                primary: "Launch anyway", cancel: "Cancel", kind: DialogKind.Warning);
            if (proceed != DialogChoice.Primary) return;
        }

        Run(() =>
        {
            // Always launch out of sight: the session runs, but its window never
            // comes to the screen until Show is pressed.
            _alts.Launch(row.Profile, startHidden: true);
            row.Profile.HideWindow = true;
            row.RaiseAll();
            Status = $"Launching {row.DisplayName} (hidden)...";

            // Signing in has to wait for the session, which takes a while, so it
            // runs on its own rather than holding the UI.
            if (row.HasAccount) _ = SignInWhenReadyAsync(row);

            // Belt to the hidden-window start's braces: on builds where mstsc
            // ignores the hidden flag, this catches the window the moment it
            // appears and takes it off screen before it is really seen.
            _ = _windows.HideWhenReadyAsync(row.Profile, TimeSpan.FromSeconds(45));
        }, "Launch failed");
    }

    private async Task LaunchAllAsync()
    {
        var queue = Alts.Where(a => a.Profile.IncludeInLaunchAll && !a.IsRunning).ToList();
        if (queue.Count == 0)
        {
            Status = "Nothing to launch: every RDP is either running or excluded.";
            return;
        }

        if (HasBlockingProblem)
        {
            var proceed = MessageDialog.Show(
                "Health check failed",
                "The health panel reports a blocking problem. Launching every alt now will most likely fail.",
                primary: "Launch anyway", cancel: "Cancel", kind: DialogKind.Warning);
            if (proceed != DialogChoice.Primary) return;
        }

        Busy = true;
        try
        {
            var delay = Math.Max(0, _alts.Config.StaggerSeconds);
            for (int i = 0; i < queue.Count; i++)
            {
                var row = queue[i];
                Status = $"Launching {row.DisplayName}  ({i + 1} of {queue.Count})...";

                try
                {
                    _alts.Launch(row.Profile, startHidden: true);
                    row.Profile.HideWindow = true;
                    row.RaiseAll();

                    // Each sign-in waits on its own session, so they overlap
                    // with the launches that follow instead of serialising.
                    if (row.HasAccount) _ = SignInWhenReadyAsync(row);

                    // And take the window off screen as soon as it shows.
                    _ = _windows.HideWhenReadyAsync(row.Profile, TimeSpan.FromSeconds(45));
                }
                catch (Exception ex)
                {
                    Log.Write($"launch-all failed for {row.WindowsUsername}: {ex}");
                    Status = $"{row.DisplayName} failed: {ex.Message}";
                }

                // Sessions are expensive to spin up -- firing them all at once
                // makes Windows fight itself and some connections time out.
                if (i < queue.Count - 1) await Task.Delay(TimeSpan.FromSeconds(delay));
            }
            var signingIn = queue.Count(a => a.HasAccount);
            Status = signingIn == 0
                ? $"Launched {queue.Count} RDP(s)."
                : $"Launched {queue.Count} RDP(s). {signingIn} will sign in to Roblox once their sessions are up.";
        }
        finally
        {
            Busy = false;
        }
    }

    private void DisconnectOne(AltRowViewModel? row)
    {
        if (row is null) return;

        var proceed = MessageDialog.Show(
            "Detach this session?",
            "The session keeps running, but a detached session may stop drawing its desktop, and a macro that reads pixels can stall until you reconnect. Minimising the window is usually safer.",
            primary: "Detach", cancel: "Cancel", kind: DialogKind.Warning);
        if (proceed != DialogChoice.Primary) return;

        Run(() =>
        {
            _sessions.Disconnect(row.Profile);
            Status = $"Detached {row.DisplayName}.";
        }, "Disconnect failed");
    }

    private void LogOffOne(AltRowViewModel? row)
    {
        if (row is null) return;

        var proceed = MessageDialog.Show(
            $"Log off {row.DisplayName}?",
            "Everything running in that session, including its macro, will be closed.",
            primary: "Log off", cancel: "Cancel", kind: DialogKind.Danger);
        if (proceed != DialogChoice.Primary) return;

        Run(() =>
        {
            CloseClientFirst(row);
            _sessions.LogOff(row.Profile);
            Status = $"Logged off {row.DisplayName}.";
        }, "Log off failed");
    }

    private void LogOffAll()
    {
        var running = Alts.Where(a => a.IsRunning).ToList();
        var proceed = MessageDialog.Show(
            "Log off every running session?",
            $"{running.Count} session(s) will be closed, and every macro inside them with them.",
            primary: "Log off all", cancel: "Cancel", kind: DialogKind.Danger);
        if (proceed != DialogChoice.Primary) return;

        Run(() =>
        {
            foreach (var row in running) CloseClientFirst(row);

            foreach (var row in running)
            {
                try { _sessions.LogOff(row.Profile); }
                catch (Exception ex) { Log.Write($"log off failed for {row.WindowsUsername}: {ex.Message}"); }
            }
            Status = $"Logged off {running.Count} session(s).";
        }, "Log off failed");
    }

    /// <summary>
    /// Runs the checks off the UI thread. They touch the registry, the service
    /// manager and the file system, so on a slow machine doing this inline
    /// would freeze the window mid-click.
    /// </summary>
    private async Task RefreshHealthAsync()
    {
        if (IsCheckingHealth) return;
        IsCheckingHealth = true;

        try
        {
            var started = DateTime.UtcNow;

            var results = await Task.Run(() =>
            {
                var list = _rdpWrap.RunAll();
                list.AddRange(_altSetup.Checks());
                list.Add(_signing.Check());
                return list;
            });

            // Hold the spinner briefly if the checks came back instantly:
            // a one-frame flicker reads as "nothing happened".
            var minimum = TimeSpan.FromMilliseconds(420);
            var elapsed = DateTime.UtcNow - started;
            if (elapsed < minimum) await Task.Delay(minimum - elapsed);

            HealthChecks.Clear();
            foreach (var check in results) HealthChecks.Add(check);

            SelectWorstCheck();

            Raise(nameof(HealthSummary));
            Raise(nameof(HealthBadge));
            Raise(nameof(HasBlockingProblem));
            Raise(nameof(HasWarning));
            Raise(nameof(HostIsHealthy));
            Raise(nameof(BannerText));
            Raise(nameof(HostSummary));
        }
        catch (Exception ex)
        {
            Log.Write($"health check failed: {ex}");
            Status = $"Health check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingHealth = false;
        }
    }

    /// <summary>
    /// Puts the inspector on whatever most needs reading. Landing on the page
    /// after a scan should not mean hunting the diagram for the red one.
    /// </summary>
    private void SelectWorstCheck()
    {
        var keep = SelectedCheck is null
            ? null
            : HealthChecks.FirstOrDefault(c => c.Name == SelectedCheck.Name);

        SelectedCheck = HealthChecks
            .OrderBy(c => c.State switch
            {
                HealthState.Failed => 0,
                HealthState.Warning => 1,
                HealthState.Unknown => 2,
                _ => 3
            })
            .FirstOrDefault(c => c.State is HealthState.Failed or HealthState.Warning)
            ?? keep;
    }

    /// <summary>
    /// Applies a repair off the UI thread.
    ///
    /// Every branch here does something that can block for a noticeable time --
    /// the Roblox fix downloads an installer, the alt-setup fix walks the whole
    /// Task Scheduler and loads a registry hive, and even the "open the download
    /// page" fix hands off to the shell. Run inline on the click, any of them
    /// freezes the window; the Roblox one froze it for good, because awaiting a
    /// download while the UI thread blocked on its result is a deadlock. So the
    /// work goes to a background thread and the panel shows its spinner meanwhile.
    /// </summary>
    private async Task ApplyFixAsync(HealthCheck? check)
    {
        if (check is null || !check.CanFix) return;
        if (IsCheckingHealth || Busy) return;

        if (check.Fix == FixAction.UpdateRdpWrapIni)
        {
            var proceed = MessageDialog.Show(
                "Update rdpwrap.ini?",
                "The updater downloads a fresh copy and restarts Terminal Services. Any session running right now will be dropped.",
                primary: "Run updater", cancel: "Cancel", kind: DialogKind.Warning);
            if (proceed != DialogChoice.Primary) return;
        }

        bool ok;
        string message;

        // Progress created on the UI thread, so its callbacks marshal back here
        // and can touch Status directly while the work runs off-thread.
        var progress = new Progress<string>(msg => Status = msg);

        IsCheckingHealth = true;
        try
        {
            Status = check.Fix switch
            {
                FixAction.InstallRdpWrap => "Installing RDP Wrapper...",
                FixAction.UpdateRdpWrapIni => "Updating rdpwrap.ini...",
                FixAction.StageRoblox => "Downloading the Roblox installer...",
                _ => "Applying fix..."
            };

            (ok, message) = check.Fix switch
            {
                FixAction.SkipFirstLoginSetup => await Task.Run(() => _altSetup.ApplySetup()),
                FixAction.StageRoblox => await _altSetup.StageRobloxAsync(),
                FixAction.InstallRdpWrap => await _rdpWrap.InstallRdpWrapperAsync(progress),
                FixAction.UpdateRdpWrapIni => await _rdpWrap.UpdateIniAsync(progress),
                FixAction.TrustRdpFiles => await Task.Run(() => _signing.Apply()),
                _ => await Task.Run(() => _rdpWrap.ApplyFix(check.Fix))
            };
        }
        finally
        {
            IsCheckingHealth = false;
        }

        Status = message;
        if (ok) await RefreshHealthAsync();
        else MessageDialog.Show("Fix failed", message, primary: "OK", kind: DialogKind.Danger);
    }

    // ----------------------------------------------------------- signing in

    /// <summary>
    /// Signs the alt's Roblox account in, inside its own session, and joins the
    /// game.
    ///
    /// The stored token never travels: it is exchanged here for a single-use
    /// ticket that expires in about a minute, and only the ticket crosses into
    /// the session.
    /// </summary>
    private async Task SignInAsync(
        AltRowViewModel? row, bool quiet = false, bool startMacroAfter = true)
    {
        if (row is null || row.IsBusy) return;

        var account = _accounts.ForAlt(row.Profile);
        if (account is null)
        {
            Status = $"{row.DisplayName} has no Roblox account assigned.";
            return;
        }

        if (!row.IsRunning)
        {
            Status = $"{row.DisplayName} is not running. Launch it first.";
            return;
        }

        row.IsBusy = true;
        row.Activity = $"Signing in as {account.Username}...";
        Status = $"Asking Roblox for a launch ticket for {account.Username}...";

        // Decided inside the try, acted on after it: starting the macro takes
        // the row busy again, which cannot happen until this has let go of it.
        var startMacro = false;

        try
        {
            var cookie = _accounts.GetCookie(account);

            PrivateServerLink.TryParse(
                row.Profile.PrivateServerUrl, _alts.Config.PlaceId, out var privateServer, out _);

            var (url, problem) = await _robloxLaunch.BuildLaunchUrlAsync(
                cookie, _alts.Config.PlaceId, privateServer);

            if (url is null)
            {
                // Roblox refusing the token is worth surfacing loudly: every
                // later launch for this account fails the same way until it is
                // signed in again.
                account.LastVerifiedUtc = null;
                _alts.Save();
                foreach (var accountRow in Accounts) accountRow.RaiseAll();

                Status = $"{row.DisplayName}: {problem}";
                if (!quiet) MessageDialog.Show("Could not sign in", problem,
                    primary: "OK", kind: DialogKind.Warning);
                return;
            }

            row.Activity = "Opening Roblox in the session...";
            var (ok, message) = await _inSession.OpenInSessionAsync(row.WindowsUsername, url);

            if (ok)
            {
                // Roblox answered for this token, so it is alive whatever the
                // last check said.
                account.LastVerifiedUtc = DateTime.UtcNow;
                _alts.Save();
                foreach (var accountRow in Accounts) accountRow.RaiseAll();

                row.Activity = "Waiting for Roblox to start...";
                Status = $"{row.DisplayName} took the launch. Waiting for Roblox...";

                var launched = await WaitForRobloxAsync(row);

                var where = privateServer is null ? "a public server" : "its private server";

                Status = launched
                    ? $"{row.DisplayName} is in the game as {account.Username}, on {where}."
                    : $"{row.DisplayName} took the launch but Roblox has not appeared. It may not be installed in that session yet.";

                // Only once the client is actually up. Kairos looks for the
                // Roblox window as it starts and cannot recover from not
                // finding one, so starting it early is worse than not at all.
                if (launched) startMacro = startMacroAfter && row.HasMacro;
            }
            else
            {
                Status = $"{row.DisplayName}: {message}";
                if (!quiet) MessageDialog.Show("Could not sign in", message,
                    primary: "OK", kind: DialogKind.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"sign-in failed for {row.WindowsUsername}: {ex}");
            Status = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            row.Activity = "";
            row.IsBusy = false;
        }

        if (startMacro) await StartMacroAsync(row, quiet);
    }

    /// <summary>
    /// Waits for the Roblox client to actually come up in the session. The
    /// first launch on a fresh profile is slow, and the client has to download
    /// itself before it appears at all.
    /// </summary>
    private async Task<bool> WaitForRobloxAsync(AltRowViewModel row)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);

        while (DateTime.UtcNow < deadline)
        {
            if (_sessions.IsRobloxRunning(row.SessionId)) return true;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return _sessions.IsRobloxRunning(row.SessionId);
    }

    /// <summary>
    /// Waits for a freshly launched session to be usable, then signs in.
    ///
    /// A session reports Active well before its desktop can open anything -- on
    /// a first sign-in Windows is still building the profile. Sending a ticket
    /// into that gap wastes it, and tickets do not survive being retried.
    /// </summary>
    private async Task SignInWhenReadyAsync(AltRowViewModel row)
    {
        if (_accounts.ForAlt(row.Profile) is null) return;

        if (!await WaitForSessionAsync(row, TimeSpan.FromMinutes(3)))
        {
            Status = $"{row.DisplayName} did not finish signing in to Windows, so Roblox was not started.";
            return;
        }

        row.Activity = "Waiting for the desktop...";
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _alts.Config.SignInDelaySeconds)));

        await SignInAsync(row, quiet: true);
    }

    /// <summary>
    /// Waits for Windows to report the session active, updating the row as it
    /// goes so callers do not have to wait for the next poll to see it.
    /// </summary>
    private async Task<bool> WaitForSessionAsync(AltRowViewModel row, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));

            var live = _sessions.Enumerate().FirstOrDefault(session =>
                string.Equals(session.Username, row.WindowsUsername, StringComparison.OrdinalIgnoreCase));

            if (live?.State != SessionState.Active) continue;

            row.State = live.State;
            row.SessionId = live.SessionId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Signs an account in for the first time, then closes the session again.
    ///
    /// The first sign-in on a fresh profile is the slow, ugly one: Roblox has to
    /// install itself, walk its own first-run screens and settle. Doing it once
    /// as a deliberate step -- rather than in the middle of the first real
    /// launch -- is what makes every launch after it uneventful. The session is
    /// logged off at the end because nothing needs it yet; the point was to
    /// leave Roblox signed in on disk.
    /// </summary>
    private async Task RunSignInPhaseAsync(AltRowViewModel row)
    {
        var account = _accounts.ForAlt(row.Profile);
        if (account is null || row.IsBusy) return;

        // Never tear down a session that is already doing something. Its macro
        // is in there.
        if (row.IsRunning)
        {
            Status = $"{row.DisplayName} is running, so its sign-in was not started. " +
                     "Log it off and it will run on the next launch.";
            return;
        }

        row.IsBusy = true;
        row.Activity = $"Signing in as {account.Username}...";
        Status = $"{row.DisplayName}: opening a session to sign {account.Username} in. This takes a few minutes the first time.";

        try
        {
            _alts.Launch(row.Profile);

            if (!await WaitForSessionAsync(row, TimeSpan.FromMinutes(3)))
            {
                Status = $"{row.DisplayName} did not finish signing in to Windows. Its Roblox sign-in has not run.";
                return;
            }

            row.Activity = "Waiting for the desktop...";
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _alts.Config.SignInDelaySeconds)));

            // SignInAsync owns the busy flag while it runs, and must not start
            // the macro: the session is about to go away.
            row.IsBusy = false;
            await SignInAsync(row, quiet: true, startMacroAfter: false);
            row.IsBusy = true;

            row.Activity = "Closing the session...";
            CloseClientFirst(row);
            _sessions.LogOff(row.Profile);

            row.Profile.SignedInAccountId = row.Profile.RobloxAccountId;
            _alts.Save();

            Status = $"{row.DisplayName} is signed in as {account.Username}. Launching it now goes straight into the game.";
        }
        catch (Exception ex)
        {
            Log.Write($"sign-in phase failed for {row.WindowsUsername}: {ex}");
            Status = $"{row.DisplayName}: sign-in did not finish. {ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
            row.Activity = "";
            row.RaiseAll();
        }
    }

    // ----------------------------------------------------------------- windows

    /// <summary>
    /// Closes the RDP client before the session under it goes away, so it never
    /// gets the chance to report the log-off as an error.
    /// </summary>
    private void CloseClientFirst(AltRowViewModel row)
    {
        try
        {
            if (!_windows.CloseClient(row.Profile)) return;

            // Long enough for mstsc to act on the close, short enough not to
            // read as the app hanging. Logging off anyway is the right call:
            // the session going is what was asked for.
            Thread.Sleep(600);
        }
        catch (Exception ex)
        {
            Log.Write($"could not close the client for {row.WindowsUsername}: {ex.Message}");
        }
    }

    /// <summary>
    /// Hides or shows one alt's RDP window.
    ///
    /// The choice is remembered on the alt, so relaunching it later puts the
    /// window straight back out of the way rather than into the taskbar.
    /// </summary>
    private void ToggleWindow(AltRowViewModel? row)
    {
        if (row is null) return;

        Run(() =>
        {
            // A window that is on screen gets hidden.
            if (row.HasWindow && !row.IsWindowHidden)
            {
                _windows.Hide(row.Profile);
                row.Profile.HideWindow = true;
                _alts.Save();
                RefreshWindowState();
                Status = $"{row.DisplayName}'s window is hidden. The session keeps running.";
                return;
            }

            // Otherwise the user wants it back. A hidden window is simply shown.
            // A running session whose window has gone -- detached, or closed
            // while hidden -- is reconnected, visibly, since Show is a request to
            // see it.
            if (_windows.Reveal(row.Profile))
            {
                Status = $"{row.DisplayName}'s window is back.";
            }
            else if (row.IsRunning)
            {
                _alts.Launch(row.Profile, startHidden: false);
                Status = $"Reconnecting to {row.DisplayName}...";
            }
            else
            {
                Status = $"{row.DisplayName} has no RDP window open.";
                RefreshWindowState();
                return;
            }

            row.Profile.HideWindow = false;
            _alts.Save();
            RefreshWindowState();
        }, "Could not change the RDP window");
    }

    /// <summary>
    /// Puts every hidden window back. Called as the app closes: a hidden window
    /// outlives this process, and nothing else could bring it back.
    /// </summary>
    public void RevealWindowsOnExit()
    {
        try { _windows.RevealAll(); }
        catch (Exception ex) { Log.Write($"could not reveal RDP windows on exit: {ex.Message}"); }
    }

    // ------------------------------------------------------------------- macro

    /// <summary>
    /// Opens this alt's macro settings. The dialog installs Kairos itself when
    /// it is missing, so this is the only entry point that needs to exist.
    /// </summary>
    private void ConfigureMacro(AltRowViewModel? row)
    {
        if (row is null) return;

        var choice = MacroDialog.Show(
            Application.Current?.MainWindow, _kairos, row.Profile, _alts.Config.PlaceId);

        // Installing happens inside the dialog and is not a setting, so the row
        // is re-read either way -- cancelling still leaves an install to show.
        row.RaiseAll();
        CommandManager.InvalidateRequerySuggested();

        if (choice is null) return;

        Run(() =>
        {
            row.Profile.Macro = choice.Settings;
            row.Profile.PrivateServerUrl = choice.PrivateServerUrl;

            // The link is the one setting worth copying across alts: they
            // usually share a server, and pasting it per alt is how the two
            // drift apart.
            var servers = choice.ApplyToAll ? Alts.ToList() : [row];
            foreach (var target in servers)
                target.Profile.PrivateServerUrl = choice.PrivateServerUrl;

            _alts.Save();
            foreach (var target in servers) target.RaiseAll();

            var where = choice.PrivateServerUrl.Length == 0
                ? "public servers"
                : "its private server";

            Status = choice.ApplyToAll
                ? $"Saved. All {servers.Count} RDP(s) now join {where}."
                : $"Saved. {row.DisplayName} joins {where} and starts its macro on launch.";
        }, "Could not save the macro settings");
    }

    /// <summary>
    /// Starts the macro in an alt's session now. Writes the current settings
    /// first, so what runs is always what the dialog last showed.
    /// </summary>
    private async Task StartMacroAsync(AltRowViewModel? row, bool quiet = false)
    {
        if (row is null || row.IsBusy) return;

        row.IsBusy = true;
        row.Activity = "Starting the macro...";

        try
        {
            var (ok, message) = await _kairos.StartAsync(row.Profile, _inSession);

            Status = ok
                ? $"{row.DisplayName}: macro started."
                : $"{row.DisplayName}: {message}";
        }
        catch (Exception ex)
        {
            Log.Write($"macro start failed for {row.WindowsUsername}: {ex}");
            if (!quiet) Status = $"{row.DisplayName}: could not start the macro. {ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
            row.Activity = "";
        }
    }

    // ---------------------------------------------------------------- accounts

    private AltRowViewModel NewAltRow(AltProfile profile) =>
        new(profile, AccountTargets, (alt, accountId) =>
        {
            _accounts.Assign(alt, accountId);

            // Assigning moves an account off whatever alt had it, so every row
            // has to be re-read, not just this one.
            foreach (var row in Alts) row.RaiseAll();
            foreach (var row in Accounts) row.RaiseAll();

            if (accountId is null)
            {
                Status = $"{alt.DisplayName} will not sign in to Roblox.";
                return;
            }

            // Picking an account is the trigger. Doing it here rather than
            // behind a button is the point: an alt whose account was switched
            // but never signed in looks ready and is not.
            var changed = Alts.FirstOrDefault(r => r.Profile == alt);
            if (changed is { NeedsSignIn: true }) _ = RunSignInPhaseAsync(changed);
        });

    private AccountRowViewModel NewAccountRow(RobloxAccount account) =>
        new(account, a => _accounts.AltFor(a)?.DisplayName);

    private void RebuildAccountTargets()
    {
        AccountTargets.Clear();
        AccountTargets.Add(new AccountLinkTarget(null, "No account"));

        foreach (var account in _accounts.All)
            AccountTargets.Add(new AccountLinkTarget(account.Id, account.Username));
    }

    private string AccountName(string accountId) =>
        _accounts.All.FirstOrDefault(a => a.Id == accountId)?.Username ?? "that account";

    /// <summary>
    /// Opens Roblox's login page in a throwaway browser and keeps whatever
    /// token comes back. The password is typed into Roblox, not into this app.
    /// </summary>
    private void AddAccount()
    {
        var captured = RobloxLoginWindow.Capture(Application.Current?.MainWindow);
        if (captured is null)
        {
            Status = "Sign-in cancelled.";
            return;
        }

        Run(() =>
        {
            var (account, replaced) = _accounts.Save(captured.Identity, captured.Cookie);

            var existing = Accounts.FirstOrDefault(r => r.Account.UserId == account.UserId);
            if (existing is not null) existing.RaiseAll();
            else Accounts.Add(NewAccountRow(account));

            ActiveTab = MainTab.Accounts;
            Status = replaced
                ? $"Refreshed the saved login for {account.Username}."
                : $"Added {account.Username}. Link it to an alt to say which session signs in as it.";
        }, "Could not save the account");
    }

    private void RemoveAccount(AccountRowViewModel? row)
    {
        if (row is null) return;

        var answer = MessageDialog.Show(
            $"Remove {row.DisplayName}?",
            "The saved login is deleted from this machine. The Roblox account itself is untouched, and you can add it again by signing in.",
            primary: "Remove",
            cancel: "Cancel",
            kind: DialogKind.Danger);

        if (answer != DialogChoice.Primary) return;

        Run(() =>
        {
            _accounts.Remove(row.Account);
            Accounts.Remove(row);
            Status = $"Removed {row.DisplayName}.";
        }, "Could not remove the account");
    }

    /// <summary>
    /// Asks Roblox whether a stored login still works. Worth doing before a run
    /// rather than discovering it from an alt sitting at a login screen.
    /// </summary>
    private async Task VerifyAccountAsync(AccountRowViewModel? row)
    {
        if (row is null || row.IsBusy) return;

        row.IsBusy = true;
        Status = $"Checking {row.DisplayName} with Roblox...";

        try
        {
            var started = DateTime.UtcNow;
            var (_, message) = await _accounts.VerifyAsync(row.Account);

            var minimum = TimeSpan.FromMilliseconds(520);
            var elapsed = DateTime.UtcNow - started;
            if (elapsed < minimum) await Task.Delay(minimum - elapsed);

            row.RaiseAll();
            Status = message;
        }
        catch (Exception ex)
        {
            Log.Write($"account check failed: {ex}");
            Status = $"Check failed: {ex.Message}";
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private async Task VerifyAllAsync()
    {
        Busy = true;
        try
        {
            var dead = 0;
            foreach (var row in Accounts.ToList())
            {
                row.IsBusy = true;
                try
                {
                    var (ok, _) = await _accounts.VerifyAsync(row.Account);
                    if (!ok) dead++;
                    row.RaiseAll();
                }
                finally
                {
                    row.IsBusy = false;
                }
            }

            Status = dead == 0
                ? $"All {Accounts.Count} account(s) are still signed in."
                : $"{dead} of {Accounts.Count} account(s) need adding again.";
        }
        finally
        {
            Busy = false;
        }
    }

    private void OpenProfile(AccountRowViewModel? row)
    {
        if (row is null) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = row.Account.ProfileUrl,
            UseShellExecute = true
        });
    }

    private void RefreshSessions()
    {
        var live = _sessions.Enumerate();

        foreach (var row in Alts)
        {
            var match = live.FirstOrDefault(s =>
                string.Equals(s.Username, row.WindowsUsername, StringComparison.OrdinalIgnoreCase));

            row.State = match?.State ?? SessionState.None;
            row.SessionId = match?.SessionId ?? -1;
        }

        RefreshWindowState();
    }

    /// <summary>
    /// Re-reads whether each alt has a window and whether it is hidden.
    ///
    /// Read from the windows themselves rather than trusted from the saved
    /// setting: the window can be closed, or revealed by the app shutting
    /// down, without anything here being told.
    /// </summary>
    private void RefreshWindowState()
    {
        foreach (var row in Alts)
        {
            row.HasWindow = _windows.HasWindow(row.Profile);
            row.IsWindowHidden = row.HasWindow && _windows.IsHidden(row.Profile);
        }
    }

    // ----------------------------------------------------------------- helpers

    private static AltRowViewModel? AsRow(object? parameter) => parameter as AltRowViewModel;

    private static AccountRowViewModel? AsAccount(object? parameter) => parameter as AccountRowViewModel;

    /// <summary>Runs an action, turning any failure into a visible message rather than a crash.</summary>
    private void Run(Action action, string failureTitle)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Write($"{failureTitle}: {ex}");
            Status = $"{failureTitle}: {ex.Message}";
            MessageDialog.Show(failureTitle, ex.Message, primary: "OK", kind: DialogKind.Danger);
        }
    }

    private static void OpenInExplorer(string path)
    {
        AppPaths.EnsureCreated();
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void OpenLog()
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.LogFile)) File.WriteAllText(AppPaths.LogFile, "");
        Process.Start(new ProcessStartInfo { FileName = AppPaths.LogFile, UseShellExecute = true });
    }
}
