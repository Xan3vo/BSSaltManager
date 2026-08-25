using System.IO;
using System.Net.Http;
using BssManager.Models;
using BssManager.Native;
using Microsoft.Win32;

namespace BssManager.Services;

/// <summary>
/// Everything that has to be true inside an alt's session before it is useful:
/// no first-run wizards, no visual effects burning frames, and Roblox already
/// installed.
///
/// The awkward part is that almost all of it is per-user, and an alt's HKCU
/// does not exist until it first signs in. The mechanism is Active Setup: a
/// command Windows runs once per user at first sign-in, as that user. That is
/// early enough to beat the setup screens and late enough to have a real HKCU.
///
/// Active Setup runs for EVERY account though, including yours. So the stub
/// points at a script that checks the signing-in user against the list of alts
/// this app manages and exits immediately for anyone else.
/// </summary>
public class AltSetupService
{
    private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string OobePolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\OOBE";
    private const string CloudContentKey = @"SOFTWARE\Policies\Microsoft\Windows\CloudContent";
    private const string EdgePolicyKey = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string NewNetworkWindowKey = @"SYSTEM\CurrentControlSet\Control\Network\NewNetworkWindowOff";
    private const string TerminalServicesPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    private const string GameDvrPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";

    /// <summary>
    /// Fixed component id. Windows records "already ran for this user" against
    /// it, so it must stay stable. Bump <see cref="StubVersion"/> instead when
    /// the script changes -- that makes it re-run for everyone.
    /// </summary>
    private const string ActiveSetupKey =
        @"SOFTWARE\Microsoft\Active Setup\Installed Components\{B55A17E0-4C1D-4E3B-9A21-6D3F1A7C0B21}";

    private const string StubVersion = "2";

    private const string RobloxDownloadUrl = "https://www.roblox.com/download/client?os=win";

    public static string SharedFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BssAltManager");

    private static string AltListFile => Path.Combine(SharedFolder, "alts.txt");
    private static string LogonScript => Path.Combine(SharedFolder, "alt-first-logon.cmd");
    private static string RobloxScript => Path.Combine(SharedFolder, "install-roblox.cmd");
    private static string RobloxInstaller => Path.Combine(SharedFolder, "RobloxPlayerInstaller.exe");
    private static string BlockListFile => Path.Combine(SharedFolder, "blocked-apps.txt");
    private static string SuppressScript => Path.Combine(SharedFolder, "suppress-startup.cmd");
    private static string SuppressLauncher => Path.Combine(SharedFolder, "suppress-startup.vbs");
    private static string CustomBlockListFile => Path.Combine(SharedFolder, "blocked-apps-custom.txt");

    /// <summary>A program the host launches at every sign-in, alts included.</summary>
    public record StartupEntry(string Name, string Executable, string Source);

    // ----------------------------------------------------------------- checks

    public IEnumerable<HealthCheck> Checks()
    {
        yield return SetupCheck();
        yield return RobloxCheck();
    }

    private HealthCheck SetupCheck()
    {
        var applied = PoliciesApplied() && File.Exists(LogonScript) && File.Exists(SuppressScript);

        if (!applied)
        {
            return new HealthCheck
            {
                Name = "New alts configure themselves",
                State = HealthState.Warning,
                Detail = "alts will show setup screens, full visual effects and your startup apps",
                Consequence = "Each new alt otherwise opens onto the Windows privacy and \"finish setting up your device\" pages, runs its desktop with animations and Game Bar capture competing with Roblox for frames, and launches every program this machine starts for all users -- Cloudflare WARP and the rest -- inside the session.",
                Fix = FixAction.SkipFirstLoginSetup,
                FixLabel = "Set up alt sessions"
            };
        }

        // The host gains startup programs over time. Anything installed since
        // the last scan would start appearing in alt sessions again.
        var known = CurrentBlockList();
        var present = BuildBlockList();
        var unhandled = present
            .Where(app => !known.Contains(app, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unhandled.Count > 0)
        {
            return new HealthCheck
            {
                Name = "New alts configure themselves",
                State = HealthState.Warning,
                Detail = $"{unhandled.Count} new startup app(s) since the last scan: {string.Join(", ", unhandled.Take(3))}",
                Consequence = "These were installed after alt sessions were last set up, so they are not on the block list yet and will launch inside alt sessions.",
                Fix = FixAction.SkipFirstLoginSetup,
                FixLabel = "Rescan startup apps"
            };
        }

        return new HealthCheck
        {
            Name = "New alts configure themselves",
            State = HealthState.Ok,
            Detail = $"setup screens off, tuned for performance, {known.Count} startup app(s) blocked",
            Consequence = "",
            Fix = FixAction.None
        };
    }

    private HealthCheck RobloxCheck()
    {
        var staged = File.Exists(RobloxInstaller);
        var age = staged ? DateTime.Now - File.GetLastWriteTime(RobloxInstaller) : TimeSpan.Zero;

        return new HealthCheck
        {
            Name = "Roblox is staged for new alts",
            State = staged ? HealthState.Ok : HealthState.Warning,
            Detail = staged
                ? $"installer ready ({age.TotalDays:F0} days old)"
                : "no installer staged; new alts start with no Roblox",
            Consequence = "Without this you have to install Roblox by hand inside every alt session before it can do anything.",
            Fix = staged ? FixAction.None : FixAction.StageRoblox,
            FixLabel = "Download installer"
        };
    }

    private static bool PoliciesApplied()
    {
        var privacy = Registry.LocalMachine.OpenSubKey(OobePolicyKey)?.GetValue("DisablePrivacyExperience");
        var animation = Registry.LocalMachine.OpenSubKey(WinlogonKey)?.GetValue("EnableFirstLogonAnimation");
        var gpu = Registry.LocalMachine.OpenSubKey(TerminalServicesPolicyKey)?.GetValue("bEnumerateHWBeforeSW");
        var stub = Registry.LocalMachine.OpenSubKey(ActiveSetupKey)?.GetValue("Version") as string;

        return privacy is int p && p == 1
            && animation is int a && a == 0
            && gpu is int g && g == 1
            && stub == StubVersion;
    }

    // ------------------------------------------------------------------ apply

    public (bool ok, string message) ApplySetup()
    {
        try
        {
            ApplyMachinePolicies();
            Directory.CreateDirectory(SharedFolder);
            WriteScripts();
            RegisterActiveSetup();

            // Best effort. If it fails, the Active Setup script covers the same
            // ground at sign-in, so it must not fail the whole operation.
            TryApplyToDefaultUserHive();

            var blocked = CurrentBlockList().Count;
            return (true, $"Alt sessions configured: no setup screens, visual effects off, and {blocked} host startup app(s) closed on sign-in.");
        }
        catch (Exception ex)
        {
            Log.Write($"alt setup failed: {ex}");
            return (false, ex.Message);
        }
    }

    public async Task<(bool ok, string message)> StageRobloxAsync()
    {
        try
        {
            Directory.CreateDirectory(SharedFolder);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync(RobloxDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            // Download beside the target and swap, so a failed download never
            // leaves a truncated installer that alts would then try to run.
            var temp = RobloxInstaller + ".part";
            await using (var file = File.Create(temp))
            {
                await response.Content.CopyToAsync(file);
            }
            File.Move(temp, RobloxInstaller, overwrite: true);

            var size = new FileInfo(RobloxInstaller).Length;
            Log.Write($"roblox installer staged ({size} bytes)");

            return (true, $"Roblox installer staged ({size / 1024 / 1024} MB). New alts will install it themselves on first sign-in.");
        }
        catch (Exception ex)
        {
            Log.Write($"roblox staging failed: {ex}");
            return (false, $"Could not download the Roblox installer: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps the list the logon script checks against in sync. Called whenever
    /// alts are added or removed, so the script never touches a human's account.
    /// </summary>
    public void UpdateAltList(IEnumerable<string> usernames)
    {
        try
        {
            Directory.CreateDirectory(SharedFolder);
            File.WriteAllLines(AltListFile, usernames.Select(u => u.Trim()).Where(u => u.Length > 0));
        }
        catch (Exception ex)
        {
            Log.Write($"could not update alt list: {ex.Message}");
        }
    }

    private static void ApplyMachinePolicies()
    {
        // --- first-run screens ------------------------------------------------
        SetDword(OobePolicyKey, "DisablePrivacyExperience", 1);
        SetDword(WinlogonKey, "EnableFirstLogonAnimation", 0);
        SetDword(CloudContentKey, "DisableWindowsConsumerFeatures", 1);
        SetDword(CloudContentKey, "DisableSoftLanding", 1);
        SetDword(CloudContentKey, "DisableWindowsSpotlightFeatures", 1);
        SetDword(EdgePolicyKey, "HideFirstRunExperience", 1);
        Registry.LocalMachine.CreateSubKey(NewNetworkWindowKey)?.Dispose();

        // --- graphics ---------------------------------------------------------
        // The single biggest win for running a game over RDP. Without it a
        // Remote Desktop session can fall back to the software renderer and
        // Roblox crawls no matter how fast the machine is.
        SetDword(TerminalServicesPolicyKey, "bEnumerateHWBeforeSW", 1);

        // Background game recording, on by default, costs frames for nothing.
        SetDword(GameDvrPolicyKey, "AllowGameDVR", 0);

        Log.Write("machine policies applied");
    }

    // ---------------------------------------------------------------- scripts

    private static void WriteScripts()
    {
        File.WriteAllText(LogonScript, BuildLogonScript());
        File.WriteAllText(RobloxScript, BuildRobloxScript());
        File.WriteAllText(SuppressScript, BuildSuppressScript());
        File.WriteAllText(SuppressLauncher, BuildSuppressLauncher());
        WriteBlockList();
    }

    /// <summary>
    /// Closes the host's startup programs inside an alt session.
    ///
    /// Windows records "this startup item is disabled" for all-users entries in
    /// HKLM, not HKCU, so the built-in mechanism cannot be used: switching
    /// Cloudflare WARP off for an alt would switch it off for the real user too.
    /// Nor can the entries be deleted, for the same reason. Closing them inside
    /// the session is the only thing that is genuinely per-account.
    ///
    /// It keeps sweeping for a while because these launch at their own pace, and
    /// some relaunch themselves once before settling.
    /// </summary>
    private static string BuildSuppressScript() =>
        $"""
        @echo off
        setlocal enableextensions

        rem Written by BSS Alt Manager. Runs at every sign-in of an alt account.
        rem Edit blocked-apps.txt to change what gets closed.

        set "ROOT={SharedFolder}"

        if not exist "%ROOT%\alts.txt" exit /b 0
        findstr /i /x /c:"%USERNAME%" "%ROOT%\alts.txt" >nul 2>&1 || exit /b 0

        rem Two lists: the generated one, and yours. The app rewrites the first
        rem whenever it scans this machine and never touches the second.
        rem ~40 seconds of sweeps. ping is the sleep here: timeout needs a
        rem console and this runs hidden.
        for /L %%N in (1,1,20) do (
            for %%F in ("%ROOT%\blocked-apps.txt" "%ROOT%\blocked-apps-custom.txt") do (
                if exist %%F (
                    for /F "usebackq eol=# tokens=*" %%A in (%%F) do (
                        taskkill /F /IM "%%A" >nul 2>&1
                    )
                )
            )
            ping -n 3 127.0.0.1 >nul 2>&1
        )

        exit /b 0
        """;

    /// <summary>
    /// One-line launcher so the sweeper runs with no console window. A .cmd in
    /// the Run key would flash a black box in the session every sign-in.
    /// </summary>
    // Four-quote delimiter: the VBS below contains a run of three quotes, which
    // would close a normal raw string early.
    private static string BuildSuppressLauncher() =>
        $""""
        ' Written by BSS Alt Manager. Runs suppress-startup.cmd hidden.
        CreateObject("WScript.Shell").Run "cmd.exe /c ""{SuppressScript}""", 0, False
        """";

    /// <summary>
    /// Runs during logon, before the desktop appears, so it must stay fast.
    /// Anything slow is handed to RunOnce instead.
    /// </summary>
    private static string BuildLogonScript() =>
        $"""
        @echo off
        setlocal enableextensions

        rem Written by BSS Alt Manager. Runs once per user at first sign-in via
        rem Active Setup. Windows runs it for EVERY account, so it exits straight
        rem away for anyone that is not an alt this app manages.

        set "ROOT={SharedFolder}"
        set "LIST=%ROOT%\alts.txt"

        if not exist "%LIST%" exit /b 0
        findstr /i /x /c:"%USERNAME%" "%LIST%" >nul 2>&1 || exit /b 0

        rem ---- leftover first-run prompts -----------------------------------
        reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement" /v ScoobeSystemSettingEnabled /t REG_DWORD /d 0 /f >nul 2>&1
        for %%V in (ContentDeliveryAllowed SilentInstalledAppsEnabled PreInstalledAppsEnabled OemPreInstalledAppsEnabled SystemPaneSuggestionsEnabled SoftLandingEnabled SubscribedContent-310093Enabled SubscribedContent-338389Enabled) do (
            reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v %%V /t REG_DWORD /d 0 /f >nul 2>&1
        )

        rem ---- strip the desktop back --------------------------------------
        rem 2 = "adjust for best performance". Everything below is what that
        rem setting turns off, applied directly so it takes effect this session.
        reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects" /v VisualFXSetting /t REG_DWORD /d 2 /f >nul 2>&1
        reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" /v EnableTransparency /t REG_DWORD /d 0 /f >nul 2>&1
        reg add "HKCU\Software\Microsoft\Windows\DWM" /v EnableAeroPeek /t REG_DWORD /d 0 /f >nul 2>&1
        reg add "HKCU\Control Panel\Desktop\WindowMetrics" /v MinAnimate /t REG_SZ /d 0 /f >nul 2>&1
        reg add "HKCU\Control Panel\Desktop" /v DragFullWindows /t REG_SZ /d 0 /f >nul 2>&1
        reg add "HKCU\Control Panel\Desktop" /v MenuShowDelay /t REG_SZ /d 0 /f >nul 2>&1
        reg add "HKCU\Control Panel\Desktop" /v AutoEndTasks /t REG_SZ /d 1 /f >nul 2>&1

        rem ---- game bar and capture ----------------------------------------
        reg add "HKCU\System\GameConfigStore" /v GameDVR_Enabled /t REG_DWORD /d 0 /f >nul 2>&1
        reg add "HKCU\Software\Microsoft\GameBar" /v AutoGameModeEnabled /t REG_DWORD /d 0 /f >nul 2>&1
        reg add "HKCU\Software\Microsoft\GameBar" /v ShowStartupPanel /t REG_DWORD /d 0 /f >nul 2>&1
        reg add "HKCU\Software\Microsoft\GameBar" /v UseNexusForGameBarEnabled /t REG_DWORD /d 0 /f >nul 2>&1

        rem ---- nothing else competing for the session ----------------------
        reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v OneDriveSetup /f >nul 2>&1
        reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ShowSyncProviderNotifications /t REG_DWORD /d 0 /f >nul 2>&1

        rem ---- close the host's startup apps in this session ---------------
        rem Every sign-in, not once: they relaunch each time the alt logs on.
        reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v BssSuppressStartup /t REG_SZ /d "wscript.exe \"%ROOT%\suppress-startup.vbs\"" /f >nul 2>&1

        rem ---- Roblox, after the desktop is up -----------------------------
        rem Deliberately RunOnce and not inline: Active Setup blocks the logon
        rem screen until it returns, and installing Roblox takes minutes.
        if not exist "%LOCALAPPDATA%\Roblox\Versions" (
            reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce" /v BssInstallRoblox /t REG_SZ /d "cmd.exe /c \"\"%ROOT%\install-roblox.cmd\"\"" /f >nul 2>&1
        )

        exit /b 0
        """;

    /// <summary>
    /// Runs from RunOnce after the desktop loads, as the alt, in its own session.
    /// </summary>
    private static string BuildRobloxScript() =>
        $"""
        @echo off
        setlocal enableextensions

        rem Written by BSS Alt Manager. Installs Roblox into this account.
        rem Roblox lives in %LOCALAPPDATA%, so every alt needs its own copy.

        set "ROOT={SharedFolder}"
        set "STAGED=%ROOT%\RobloxPlayerInstaller.exe"

        if exist "%LOCALAPPDATA%\Roblox\Versions" exit /b 0

        rem Prefer the copy the app already downloaded: no network needed here,
        rem and every alt installs the same build.
        if exist "%STAGED%" (
            start "" /wait "%STAGED%"
            exit /b 0
        )

        rem Nothing staged, so fetch it from Roblox directly. curl ships with
        rem Windows 10 and 11.
        curl.exe -L -s -o "%TEMP%\RobloxPlayerInstaller.exe" "{RobloxDownloadUrl}"
        if exist "%TEMP%\RobloxPlayerInstaller.exe" start "" /wait "%TEMP%\RobloxPlayerInstaller.exe"

        exit /b 0
        """;

    private static void RegisterActiveSetup()
    {
        using var key = Registry.LocalMachine.CreateSubKey(ActiveSetupKey);
        key?.SetValue("", "BSS Alt Manager - alt session setup");
        key?.SetValue("Version", StubVersion);
        key?.SetValue("IsInstalled", 1, RegistryValueKind.DWord);
        key?.SetValue("StubPath", $"cmd.exe /c \"\"{LogonScript}\"\"");

        Log.Write($"active setup registered (version {StubVersion})");
    }

    // ------------------------------------------------------------ default hive

    private const string HiveMount = "BssManagerDefaultUser";

    private static string DefaultHivePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System)[..3],
            "Users", "Default", "NTUSER.DAT");

    /// <summary>
    /// Seeds the same per-user values into the default profile, so they are
    /// already correct before Active Setup even runs. Purely belt and braces:
    /// hive loading is refused on some machines, and the logon script covers it.
    /// </summary>
    private static void TryApplyToDefaultUserHive()
    {
        var hive = DefaultHivePath;
        if (!File.Exists(hive)) return;

        try
        {
            PrivilegedRegistry.LoadHive(HiveMount, hive);
        }
        catch (Exception ex)
        {
            Log.Write($"default hive skipped: {ex.Message}");
            return;
        }

        try
        {
            SetDword($@"{HiveMount}\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
                "VisualFXSetting", 2);
            SetDword($@"{HiveMount}\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency", 0);
            SetDword($@"{HiveMount}\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement",
                "ScoobeSystemSettingEnabled", 0);
        }
        finally
        {
            try { PrivilegedRegistry.UnloadHive(HiveMount); }
            catch (Exception ex) { Log.Write($"WARNING: default hive left mounted: {ex.Message}"); }
        }
    }

    private static void SetDword(string subKey, string name, int value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(subKey);
        key?.SetValue(name, value, RegistryValueKind.DWord);
    }

    // ------------------------------------------------------ host startup apps

    /// <summary>
    /// Everything this machine launches for every account that signs in: the
    /// all-users Run keys and the all-users Startup folder. A per-user Run entry
    /// is not included -- alts get their own empty HKCU and never see yours.
    /// </summary>
    public static List<StartupEntry> EnumerateMachineStartupApps()
    {
        var found = new List<StartupEntry>();

        foreach (var (root, label) in new[]
        {
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "all-users Run"),
            (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "all-users Run (32-bit)")
        })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;

            foreach (var name in key.GetValueNames())
            {
                var exe = ExecutableFromCommandLine(key.GetValue(name) as string ?? "");
                if (exe.Length > 0) found.Add(new StartupEntry(name, exe, label));
            }
        }

        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        if (Directory.Exists(startupFolder))
        {
            foreach (var shortcut in Directory.GetFiles(startupFolder, "*.lnk"))
            {
                var exe = ResolveShortcutTarget(shortcut);
                if (exe.Length > 0)
                    found.Add(new StartupEntry(Path.GetFileName(shortcut), exe, "all-users Startup folder"));
            }
        }

        found.AddRange(EnumerateLogonScheduledTasks());

        return found;
    }

    /// <summary>
    /// Scheduled tasks that fire on any user logging on. A lot of vendor
    /// software starts this way rather than through a Run key -- updaters,
    /// GPU control panels, launcher helpers -- so a scan that ignored them
    /// would miss plenty on other people's machines.
    ///
    /// Uses the Task Scheduler COM API rather than parsing schtasks.exe output,
    /// which is localised and would fall apart on a non-English Windows.
    /// </summary>
    private static IEnumerable<StartupEntry> EnumerateLogonScheduledTasks()
    {
        const int TASK_TRIGGER_LOGON = 9;
        var found = new List<StartupEntry>();

        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return found;

            dynamic? scheduler = Activator.CreateInstance(type);
            if (scheduler is null) return found;

            scheduler.Connect();
            Walk(scheduler.GetFolder("\\"));
        }
        catch (Exception ex)
        {
            // Never fatal: a machine with a locked-down or broken Task Scheduler
            // still gets the Run key and Startup folder coverage.
            Log.Write($"scheduled task scan skipped: {ex.Message}");
        }

        return found;

        void Walk(dynamic folder)
        {
            try
            {
                foreach (dynamic task in folder.GetTasks(0))
                {
                    try
                    {
                        if (!task.Enabled) continue;

                        var definition = task.Definition;
                        var logon = false;

                        foreach (dynamic trigger in definition.Triggers)
                        {
                            if ((int)trigger.Type == TASK_TRIGGER_LOGON) { logon = true; break; }
                        }
                        if (!logon) continue;

                        foreach (dynamic action in definition.Actions)
                        {
                            string path = action.Path as string ?? "";
                            if (path.Length == 0) continue;

                            var exe = Path.GetFileName(path.Trim().Trim('"'));
                            if (exe.Length > 0)
                                found.Add(new StartupEntry((string)task.Name, exe, "logon scheduled task"));
                        }
                    }
                    catch
                    {
                        // Individual tasks can refuse to be read. Skip and continue.
                    }
                }

                foreach (dynamic child in folder.GetFolders(0)) Walk(child);
            }
            catch
            {
                // Folder unreadable; the rest of the tree is still worth scanning.
            }
        }
    }

    /// <summary>
    /// Pulls the executable out of a Run command line. Handles both a quoted
    /// path and an unquoted one containing spaces (XPG-Prime registers itself
    /// that way) by cutting at the first ".exe" rather than the first space.
    /// </summary>
    private static string ExecutableFromCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";

        var cut = commandLine.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        var path = cut >= 0 ? commandLine[..(cut + 4)] : commandLine;

        path = path.Trim().Trim('"');

        try { return Path.GetFileName(path); }
        catch { return ""; }
    }

    private static string ResolveShortcutTarget(string shortcutPath)
    {
        try
        {
            // Late-bound WScript.Shell: far less code than IShellLink, and this
            // only ever runs on the host at scan time.
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return FallbackName(shortcutPath);

            var shell = Activator.CreateInstance(type);
            if (shell is null) return FallbackName(shortcutPath);

            var link = type.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });

            var target = link?.GetType().InvokeMember("TargetPath",
                System.Reflection.BindingFlags.GetProperty, null, link, null) as string;

            return string.IsNullOrWhiteSpace(target) ? FallbackName(shortcutPath) : Path.GetFileName(target);
        }
        catch
        {
            return FallbackName(shortcutPath);
        }

        // "Cloudflare WARP.lnk" -> "Cloudflare WARP.exe". Usually right, and
        // only used when the shortcut cannot be read.
        static string FallbackName(string path) => Path.GetFileNameWithoutExtension(path) + ".exe";
    }

    /// <summary>
    /// Executables that must never end up on the block list, whatever a machine
    /// happens to launch them for.
    ///
    /// This matters far more on someone else's PC than on the author's. Plenty
    /// of real startup entries are of the form "rundll32.exe something.dll", and
    /// a logon scheduled task can legitimately run cmd or PowerShell. Killing
    /// those in a loop would break the session instead of tidying it. wscript,
    /// cmd and ping are on here for a more direct reason: they are what the
    /// sweeper itself runs on.
    /// </summary>
    private static readonly HashSet<string> NeverBlock = new(StringComparer.OrdinalIgnoreCase)
    {
        // shell and session
        "explorer.exe", "dwm.exe", "winlogon.exe", "userinit.exe", "csrss.exe",
        "lsass.exe", "services.exe", "smss.exe", "svchost.exe", "sihost.exe",
        "ctfmon.exe", "taskhostw.exe", "fontdrvhost.exe", "dllhost.exe",
        "runtimebroker.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe",
        "searchhost.exe", "textinputhost.exe", "applicationframehost.exe",
        // generic hosts and tools: the entry says nothing about what is really
        // being run, and these are shared with the rest of the session
        "rundll32.exe", "regsvr32.exe", "msiexec.exe", "control.exe",
        "sc.exe", "net.exe", "net1.exe", "reg.exe", "schtasks.exe",
        "wmic.exe", "mshta.exe", "forfiles.exe", "where.exe", "findstr.exe",
        // scripting hosts, including the sweeper's own
        "cmd.exe", "conhost.exe", "powershell.exe", "pwsh.exe",
        "wscript.exe", "cscript.exe", "ping.exe", "taskkill.exe",
        // the reason the alts exist
        "robloxplayerbeta.exe", "robloxplayerinstaller.exe", "robloxplayerlauncher.exe"
    };

    /// <summary>
    /// Builds the kill list from what this machine actually launches at sign-in.
    /// Nothing here is specific to any one PC: it is discovered fresh wherever
    /// the app runs.
    /// </summary>
    public static List<string> BuildBlockList()
    {
        return EnumerateMachineStartupApps()
            .Select(e => e.Executable)
            .Where(exe => exe.Length > 0)
            .Where(exe => !NeverBlock.Contains(exe))
            // Belt and braces on top of the explicit list above.
            .Where(exe => !exe.Contains("roblox", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(exe => exe, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> CurrentBlockList() =>
        File.Exists(BlockListFile)
            ? File.ReadAllLines(BlockListFile)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToList()
            : Array.Empty<string>();

    private static void WriteBlockList()
    {
        var apps = BuildBlockList();

        var lines = new List<string>
        {
            "# Processes that alt sessions close on sign-in.",
            "# GENERATED from this machine's own startup entries -- all-users Run",
            "# keys, the all-users Startup folder, and logon scheduled tasks.",
            "#",
            "# This file is rewritten every time the app scans, so do not edit it.",
            "# Put your own additions in blocked-apps-custom.txt instead.",
            ""
        };
        lines.AddRange(apps);

        File.WriteAllLines(BlockListFile, lines);
        EnsureCustomBlockList();

        Log.Write($"block list written ({apps.Count} apps)");
    }

    /// <summary>
    /// Creates the user-owned list once and never touches it again. Keeping it
    /// separate from the generated file is what makes rescanning safe.
    /// </summary>
    private static void EnsureCustomBlockList()
    {
        if (File.Exists(CustomBlockListFile)) return;

        File.WriteAllLines(CustomBlockListFile, new[]
        {
            "# Your own additions. One executable name per line, for example:",
            "#",
            "#   SomeLauncher.exe",
            "#",
            "# Alt sessions close these alongside the generated list. The app",
            "# never rewrites this file, so anything you put here survives a rescan.",
            ""
        });
    }

    /// <summary>
    /// Re-scans the machine and refreshes the generated list. Called on every
    /// app start so a program installed since last time is picked up without
    /// anyone having to remember to press anything.
    /// </summary>
    public void RefreshBlockList()
    {
        // Only meaningful once the session scripts exist.
        if (!File.Exists(SuppressScript)) return;

        try
        {
            WriteBlockList();
        }
        catch (Exception ex)
        {
            Log.Write($"block list refresh failed: {ex.Message}");
        }
    }
}
