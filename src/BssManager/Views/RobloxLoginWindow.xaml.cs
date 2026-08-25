using System.IO;
using System.Windows;
using System.Windows.Threading;
using BssManager.Services;
using Microsoft.Web.WebView2.Core;

namespace BssManager.Views;

/// <summary>What a completed sign-in produced.</summary>
public record CapturedLogin(RobloxIdentity Identity, string Cookie);

/// <summary>
/// Signs in to Roblox and keeps the login token.
///
/// The page is Roblox's own, in a real Chromium (the Edge WebView2 runtime).
/// That matters for two reasons: the password is typed into Roblox rather than
/// into this app, and captchas and 2-step verification work because it is a
/// genuine browser rather than something imitating one.
///
/// Each sign-in gets a throwaway browser profile, so adding a second account
/// does not land on the first one already signed in, and nothing is left behind
/// afterwards.
/// </summary>
public partial class RobloxLoginWindow : Window
{
    private const string LoginUrl = "https://www.roblox.com/login";
    private const string CookieName = ".ROBLOSECURITY";

    /// <summary>
    /// Real tokens are hundreds of characters and carry Roblox's own warning
    /// banner. The length check keeps a placeholder value from being mistaken
    /// for a completed sign-in.
    /// </summary>
    private const int MinimumTokenLength = 100;

    private readonly string _profileFolder;
    private readonly DispatcherTimer _poll;
    private CapturedLogin? _result;
    private bool _capturing;

    private RobloxLoginWindow()
    {
        InitializeComponent();

        _profileFolder = Path.Combine(AppPaths.LoginProfilesFolder, Guid.NewGuid().ToString("N")[..8]);

        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _poll.Tick += async (_, _) => await CheckForTokenAsync();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// Opens the browser and returns once the user has signed in, or null if
    /// they closed it first.
    /// </summary>
    public static CapturedLogin? Capture(Window? owner)
    {
        var window = new RobloxLoginWindow();
        if (owner is not null && owner.IsLoaded) window.Owner = owner;

        window.ShowDialog();
        return window._result;
    }

    // ------------------------------------------------------------------ setup

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppPaths.SweepLoginProfiles();

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _profileFolder);

            await Browser.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            Log.Write($"webview2 failed to start: {ex}");
            ShowRuntimeMissing(ex);
            return;
        }

        var settings = Browser.CoreWebView2.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsSwipeNavigationEnabled = false;

        // Nothing should offer to remember a password that belongs in Roblox's
        // hands, not in an Edge profile this app is about to delete.
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        // Roblox opens the odd link in a new window; keep it in this one so the
        // user never ends up signed in somewhere this window cannot see.
        Browser.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            Browser.CoreWebView2.Navigate(args.Uri);
        };

        Browser.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            if (!_capturing) StatusText.Text = "Sign in above. Nothing is saved until Roblox accepts the login.";
        };

        Browser.CoreWebView2.Navigate(LoginUrl);
        _poll.Start();
    }

    // ---------------------------------------------------------------- capture

    /// <summary>
    /// Watches the browser's own cookie jar rather than the page. Roblox sets
    /// the token at the end of whatever path the user took -- password, 2-step,
    /// captcha, an existing session -- so the cookie appearing is the one
    /// reliable signal that any of them finished.
    /// </summary>
    private async Task CheckForTokenAsync()
    {
        if (_capturing || Browser.CoreWebView2 is null) return;

        string token;
        try
        {
            var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
            var match = cookies.FirstOrDefault(c => c.Name == CookieName);

            if (match is null || match.Value.Length < MinimumTokenLength) return;
            token = match.Value;
        }
        catch (Exception ex)
        {
            Log.Write($"could not read cookies: {ex.Message}");
            return;
        }

        _capturing = true;
        _poll.Stop();
        StatusText.Text = "Signed in. Checking with Roblox...";

        var identity = await RobloxApi.WhoAmIAsync(token);

        if (identity is null)
        {
            // Either the token was already dead or Roblox is unreachable.
            // Neither is worth saving, and the user may simply try again.
            StatusText.Text = "Roblox did not accept that login. Try signing in again.";
            _capturing = false;
            _poll.Start();
            return;
        }

        _result = new CapturedLogin(identity, token);
        StatusText.Text = $"Signed in as {identity.Username}.";

        // A beat so the name is readable before the window disappears.
        await Task.Delay(600);
        Close();
    }

    // ---------------------------------------------------------------- closing

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _poll.Stop();

        // Read this before disposing; afterwards CoreWebView2 is gone and there
        // is no way to tell which process to wait on.
        uint browserProcessId = 0;
        try { browserProcessId = Browser.CoreWebView2?.BrowserProcessId ?? 0; }
        catch { /* never initialised */ }

        Browser.Dispose();

        var folder = _profileFolder;
        _ = Task.Run(() => AppPaths.DeleteLoginProfileWhenFree(folder, browserProcessId));
    }

    private void ShowRuntimeMissing(Exception ex)
    {
        var missing = ex is WebView2RuntimeNotFoundException;

        var answer = MessageDialog.Show(
            missing ? "Microsoft Edge WebView2 is missing" : "The browser could not start",
            missing
                ? "Signing in needs the Edge WebView2 runtime, which is part of Windows 11 but can be absent on Windows 10. Install it, then add the account again."
                : $"The sign-in browser failed to start: {ex.Message}",
            primary: missing ? "Open the download page" : "OK",
            cancel: missing ? "Cancel" : null,
            kind: DialogKind.Warning);

        if (missing && answer == DialogChoice.Primary)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://developer.microsoft.com/microsoft-edge/webview2/",
                UseShellExecute = true
            });
        }

        Close();
    }
}
