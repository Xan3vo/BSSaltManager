using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using BssManager.Models;

namespace BssManager.Services;

/// <summary>
/// Installs and configures Kairos, the in-game macro, for one alt.
///
/// Kairos is GPL-3.0. We deliberately do not ship a copy: this downloads it
/// from the project's own release page when an alt needs it, which keeps this
/// app an orchestrator of software the user obtained themselves rather than a
/// redistributor with obligations of its own. It is also why the version is
/// pinned to a release tag instead of tracking main -- an ini schema that
/// changes underneath us silently is worse than one that is a version behind.
///
/// Each alt gets its own copy. That is not wasteful so much as required:
/// Kairos resolves its settings against the working directory and declares
/// #SingleInstance Force, so two alts sharing a folder would share one config
/// and then kill each other's process.
/// </summary>
public class KairosService
{
    /// <summary>
    /// Pinned deliberately. Bump this after checking the ini keys in
    /// <see cref="MacroSettings"/> still exist in the new release.
    /// </summary>
    public const string Version = "v0.3.1";

    private const string DownloadUrl =
        $"https://github.com/KairosMacro/Kairos/releases/download/{Version}/Kairos_{Version}.zip";

    public const string ProjectUrl = "https://github.com/KairosMacro/Kairos";

    /// <summary>The window Kairos puts up, and the class AutoHotkey gives it.</summary>
    private const string GuiWindowTitle = "Kairos ahk_class AutoHotkeyGUI";

    private static string MacroRoot => Path.Combine(AltSetupService.SharedFolder, "macro");

    public static string FolderFor(string username) => Path.Combine(MacroRoot, Sanitise(username));

    private static string MainScript(string username) =>
        Path.Combine(FolderFor(username), "scripts", "Main.ahk");

    /// <summary>Marker that says our auto-start has already been woven into Main.ahk.</summary>
    private const string AutoStartMarker = "__BssAutoStart";

    private static string Interpreter(string username) =>
        Path.Combine(FolderFor(username), "scripts", "executables", "AutoHotkey64.exe");

    private static string SettingsFolder(string username) =>
        Path.Combine(FolderFor(username), "settings");

    private static string PatternsFolder(string username) =>
        Path.Combine(FolderFor(username), "Patterns");

    /// <summary>Our launcher, not theirs. Starts the macro and presses Start.</summary>
    private static string StarterScript(string username) =>
        Path.Combine(FolderFor(username), "bss-start.ahk");

    // ------------------------------------------------------------------ state

    /// <summary>True once this alt has a usable copy, not merely a folder.</summary>
    public static bool IsInstalled(string username) =>
        File.Exists(MainScript(username)) && File.Exists(Interpreter(username));

    /// <summary>
    /// Patterns Kairos will actually offer. It builds its own list by scanning
    /// this folder at startup, so the folder is the authority -- a hardcoded
    /// list here would go stale the moment they add or drop one.
    /// </summary>
    public IReadOnlyList<string> AvailablePatterns(string username)
    {
        try
        {
            var folder = PatternsFolder(username);
            if (Directory.Exists(folder))
            {
                var found = Directory.GetFiles(folder, "*.ahk")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (found.Count > 0) return found;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"could not read patterns for {username}: {ex.Message}");
        }

        return MacroSettings.FallbackPatterns;
    }

    // --------------------------------------------------------------- install

    /// <summary>
    /// Downloads Kairos and puts a fresh copy in this alt's folder, replacing
    /// anything already there. Settings are not preserved because they do not
    /// live here -- they are written from the alt's profile on every launch.
    /// </summary>
    public async Task<(bool ok, string message)> InstallAsync(
        string username, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var target = FolderFor(username);
        var staging = target + ".new";
        var zipPath = target + ".zip";

        try
        {
            Directory.CreateDirectory(MacroRoot);
            CleanUp(staging, zipPath);

            progress?.Report($"Downloading Kairos {Version}...");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                using var response = await http.GetAsync(
                    DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                await using var file = File.Create(zipPath);
                await response.Content.CopyToAsync(file, token);
            }

            progress?.Report("Extracting...");
            ZipFile.ExtractToDirectory(zipPath, staging);

            // The release may or may not wrap everything in a version folder,
            // and that is theirs to change between releases. Find the directory
            // that actually holds the macro rather than assuming a shape.
            var extracted = FindMacroRoot(staging)
                ?? throw new InvalidOperationException(
                    "The download did not contain scripts\\Main.ahk. The release layout may have changed.");

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(extracted, target);

            GrantAccess(target, username);
            WriteStarter(username);
            PatchAutoStart(username);

            Log.Write($"installed Kairos {Version} for {username}");
            return (true, $"Kairos {Version} installed for {username}.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Cancelled.");
        }
        catch (Exception ex)
        {
            Log.Write($"kairos install failed for {username}: {ex}");
            return (false, $"Could not install Kairos: {ex.Message}");
        }
        finally
        {
            CleanUp(staging, zipPath);
        }
    }

    public void Remove(string username)
    {
        try
        {
            var folder = FolderFor(username);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Write($"could not remove the macro folder for {username}: {ex.Message}");
        }
    }

    private static string? FindMacroRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, "scripts", "Main.ahk"))) return staging;

        return Directory.GetDirectories(staging)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "scripts", "Main.ahk")));
    }

    private static void CleanUp(string staging, string zipPath)
    {
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
    }

    /// <summary>
    /// Gives the alt its own folder outright. Kairos writes settings, logs and
    /// crash dumps beside itself, so read access is not enough. Inheritance is
    /// dropped so one alt cannot read another's -- ProgramData grants every
    /// account on the machine read access by default.
    /// </summary>
    private static void GrantAccess(string folder, string username)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var account in new IdentityReference[]
                     {
                         new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                         new NTAccount(Environment.MachineName, username)
                     })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    account, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }

            new DirectoryInfo(folder).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            // Worth continuing: the macro may still run, and a permissions
            // problem shows up as a clear failure at start rather than here.
            Log.Write($"could not set macro folder permissions for {username}: {ex.Message}");
        }
    }

    // --------------------------------------------------------------- settings

    /// <summary>
    /// Writes this alt's preset and points Kairos at it.
    ///
    /// Two files, two formats, and they are not interchangeable. The preset is
    /// read by Kairos's own parser, which is happy with the BOM its writer
    /// emits. global.ini is read through the Win32 profile API, which is not --
    /// a BOM there stops it finding the section at all, and the macro silently
    /// loads the wrong preset.
    /// </summary>
    public void WriteSettings(AltProfile alt)
    {
        var username = alt.WindowsUsername;
        var settings = alt.Macro;
        var preset = Sanitise(username);

        Directory.CreateDirectory(SettingsFolder(username));

        var ini = new StringBuilder();

        // This app only ever runs alts, so the macro is always in alt mode.
        ini.Append("[Main]\r\n");
        ini.Append("AccountType=Alt\r\n");
        ini.Append("AltMacroEnabled=1\r\n");

        ini.Append("\r\n[Alt]\r\n");
        ini.Append($"AltNumber={settings.AltNumber}\r\n");
        ini.Append($"HiveSlot={settings.HiveSlot}\r\n");
        // Invariant so a machine with a comma decimal separator does not write
        // "28,5", which the macro would read as a different number or not at all.
        ini.Append(FormattableString.Invariant($"Movespeed={settings.Movespeed}\r\n"));
        ini.Append($"DefaultField={settings.DefaultField}\r\n");
        ini.Append($"Pattern={settings.Pattern}\r\n");
        ini.Append($"PatternSize={settings.PatternSize}\r\n");
        ini.Append($"PatternWidth={settings.PatternWidth}\r\n");
        ini.Append($"RotationAmount={settings.RotationAmount}\r\n");
        ini.Append($"RotationDirection={settings.RotationDirection}\r\n");
        ini.Append($"ShiftLock={(settings.ShiftLock ? 1 : 0)}\r\n");
        ini.Append($"SprinklerLocation={settings.SprinklerLocation}\r\n");
        ini.Append($"SprinklerDistance={settings.SprinklerDistance}\r\n");
        ini.Append($"ClaimHive={(settings.ClaimHive ? 1 : 0)}\r\n");
        ini.Append($"UseTool={(settings.UseTool ? 1 : 0)}\r\n");

        // The macro wants the private server too, and we already have it from
        // the RDP card. Asking for it twice is how the two drift apart.
        ini.Append($"PrivServer={alt.PrivateServerUrl}\r\n");

        File.WriteAllText(
            Path.Combine(SettingsFolder(username), preset + ".ini"),
            ini.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        File.WriteAllText(
            Path.Combine(SettingsFolder(username), "global.ini"),
            $"[Global]\r\nLastPreset={preset}\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        WriteStarter(username);
    }

    /// <summary>
    /// Writes the script that starts the macro without anyone at the keyboard.
    ///
    /// Kairos has no command line -- it opens a window and waits for its start
    /// hotkey. Clicking its Start button is steadier than sending that key: a
    /// keystroke goes wherever focus happens to be, and in a session that has
    /// just launched Roblox that is not reliably the macro.
    /// </summary>
    private static void WriteStarter(string username)
    {
        // Two dollars: the script is full of braces, so interpolation is
        // {{ }} here and a lone brace stays a brace.
        var script = $$"""
            ; Starts Kairos inside this alt's session.
            ; Written by BSS Alt Manager -- edits are overwritten on every launch.
            #Requires AutoHotkey v2.0
            #SingleInstance Force

            root := A_ScriptDir
            title := "{{GuiWindowTitle}}"

            if !FileExist(root "\scripts\Main.ahk")
                ExitApp 2

            ; Already up from an earlier launch: leave it be rather than
            ; restarting it, which would drop whatever run is in progress.
            if !WinExist(title) {
                Run('"' root '\scripts\executables\AutoHotkey64.exe" "' root '\scripts\Main.ahk"', root)
                if !WinWait(title, , 90)
                    ExitApp 3
            }

            ; The window appears before its controls are laid out and before it
            ; has found the Roblox client, so clicking Start straight away is
            ; clicking at nothing.
            Sleep 5000

            ; The macro labels its start control "Start (<key>)". Finding it by
            ; that prefix is why no start key has to be agreed on in two places.
            ; A control that no longer says Start means a run is already going,
            ; so finding none is a reason to leave the session alone.
            for ctrl in WinGetControls(title) {
                try {
                    if (SubStr(ControlGetText(ctrl, title), 1, 7) = "Start (") {
                        ControlClick(ctrl, title)
                        ExitApp 0
                    }
                }
            }

            ExitApp 4
            """;

        try
        {
            File.WriteAllText(StarterScript(username), script, new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            Log.Write($"could not write the macro starter for {username}: {ex.Message}");
        }
    }

    /// <summary>
    /// Weaves a self-start into Kairos's own Main.ahk.
    ///
    /// Kairos has no way to start unattended: it opens its window and waits for
    /// the Start hotkey. Clicking that button from outside the process is what
    /// used to leave the macro sitting idle -- an owner-drawn button in a session
    /// that has just launched Roblox is not a reliable click target. Pressing
    /// Start from inside the macro, once it has found the Roblox client, is.
    ///
    /// This is a modification of a GPL-3.0 program, which is allowed. It is kept
    /// out of the source tree and applied to the copy on disk so this app still
    /// redistributes nothing -- the user downloaded Kairos, and this edits their
    /// copy in place. The block is idempotent: a second call is a no-op.
    /// </summary>
    private static void PatchAutoStart(string username)
    {
        var path = MainScript(username);

        try
        {
            if (!File.Exists(path)) return;

            var source = File.ReadAllText(path);
            if (source.Contains(AutoStartMarker)) return;

            // Kairos constructs its GUI singleton here, near the top, well before
            // its first hotkey. AutoHotkey stops its auto-run thread at the first
            // hotkey, so the timer has to be injected above that line to ever arm.
            const string anchor = "Main := MainGui()";
            if (!source.Contains(anchor))
            {
                Log.Write($"could not auto-start-patch Kairos for {username}: anchor not found");
                return;
            }

            // start() is guarded by `this.ran`, so pressing it here and by hotkey
            // or by our starter cannot double-run it. The timer stops itself once
            // the macro is going or once it has fired.
            var block =
                anchor + "\r\n" +
                "\r\n" +
                "; ===== BSS Alt Manager: auto-start =====\r\n" +
                "; Added so the alt begins playing on its own, with no keypress.\r\n" +
                "SetTimer(" + AutoStartMarker + ", 750)\r\n" +
                AutoStartMarker + "() {\r\n" +
                "\tglobal Main\r\n" +
                "\tif !IsSet(Main)\r\n" +
                "\t\treturn\r\n" +
                "\tif Main.ran {\r\n" +
                "\t\tSetTimer(" + AutoStartMarker + ", 0)\r\n" +
                "\t\treturn\r\n" +
                "\t}\r\n" +
                "\tif GetRobloxClientPos() {\r\n" +
                "\t\tSetTimer(" + AutoStartMarker + ", 0)\r\n" +
                "\t\ttry Main.start()\r\n" +
                "\t}\r\n" +
                "}\r\n" +
                "; ===== end BSS Alt Manager =====";

            // Only the first occurrence, and there is only one.
            var patched = ReplaceFirst(source, anchor, block);

            // Preserve the BOM AutoHotkey wrote -- Kairos reads its own file and
            // some includes resolve paths relative to it; rewriting the encoding
            // is a needless way to break that.
            File.WriteAllText(path, patched, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Log.Write($"auto-start patched into Kairos for {username}");
        }
        catch (Exception ex)
        {
            // A failed patch is not fatal: the starter's button-click path still
            // runs, so the macro can still be started the old way.
            Log.Write($"could not auto-start-patch Kairos for {username}: {ex.Message}");
        }
    }

    private static string ReplaceFirst(string text, string search, string replacement)
    {
        var at = text.IndexOf(search, StringComparison.Ordinal);
        return at < 0 ? text : text[..at] + replacement + text[(at + search.Length)..];
    }

    // ----------------------------------------------------------------- launch

    /// <summary>
    /// Writes the current settings and starts the macro in the alt's session.
    /// Assumes Roblox is already up: Kairos looks for the client as it starts,
    /// and finding nothing is one of the few things it cannot recover from.
    /// </summary>
    public async Task<(bool ok, string message)> StartAsync(
        AltProfile alt, SessionCommandService sessions, CancellationToken token = default)
    {
        var username = alt.WindowsUsername;

        if (!IsInstalled(username))
            return (false, $"Kairos is not installed for {alt.DisplayName}. Use Set up macro first.");

        try
        {
            WriteSettings(alt);

            // Copies installed before auto-start existed get it woven in now, so
            // an old install does not have to be reinstalled to start on its own.
            PatchAutoStart(username);
        }
        catch (Exception ex)
        {
            Log.Write($"could not write macro settings for {username}: {ex}");
            return (false, $"Could not write the macro settings: {ex.Message}");
        }

        return await sessions.RunInSessionAsync(
            username,
            Interpreter(username),
            $"\"{StarterScript(username)}\"",
            FolderFor(username),
            token);
    }

    // ------------------------------------------------------------------ misc

    /// <summary>
    /// Usernames are already validated when an alt is created, but this folder
    /// name is built into paths and ini keys, so it does not take that on faith.
    /// </summary>
    private static string Sanitise(string username)
    {
        var clean = new string(username
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            .ToArray());

        return clean.Length > 0 ? clean : "alt";
    }
}
