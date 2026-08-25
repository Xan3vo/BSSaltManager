namespace BssManager.Views;

/// <summary>
/// What each check actually is, and what to do when the button cannot fix it.
///
/// The checks themselves carry a Detail (the raw value that was read) and a
/// Consequence (what breaks). Neither answers the question somebody stuck at
/// midnight is actually asking, which is "what is this thing and where do I go
/// to change it by hand". That answer is copy, not data, so it lives here in
/// the view rather than in the service that does the probing.
/// </summary>
internal static class HealthCopy
{
    private static readonly Dictionary<string, (string What, string Manually)> Notes = new()
    {
        ["RDP Wrapper installed"] = (
            "RDP Wrapper sits in front of Windows' Terminal Services and removes the one-session-at-a-time limit. Everything else on this page assumes it is there.",
            "Install RDP Wrapper from its GitHub releases, then run RDPWInst -i as administrator. If your antivirus quarantines rdpwrap.dll, exempt the folder before reinstalling."),

        ["TermService points at the wrapper"] = (
            "Terminal Services loads whichever DLL its ServiceDll registry value names. The wrapper only does anything if that value points at rdpwrap.dll instead of the stock termsrv.dll.",
            "Reinstall the wrapper. A Windows update can quietly put the original path back, which leaves the wrapper installed but doing nothing."),

        ["Terminal Services running"] = (
            "The Windows service that accepts RDP connections. Every alt is a loopback RDP connection, so nothing launches while it is stopped.",
            "services.msc, find Remote Desktop Services, set it to Automatic and start it. If it stops again on its own, check Event Viewer for a crash in termsrv.dll."),

        ["rdpwrap.ini supports this Windows build"] = (
            "rdpwrap.ini is a table of memory offsets keyed by the exact version of termsrv.dll. Windows updates termsrv.dll; when it does, the old offsets are wrong.",
            "Run an rdpwrap.ini updater (autoupdate.ps1 from the community fork) as administrator, then restart Terminal Services. This is the single most common cause of multi-session breaking after an update."),

        ["Remote Desktop connections allowed"] = (
            "The master switch in Settings, stored as fDenyTSConnections. With it on, Windows refuses every connection including the loopback ones this app makes.",
            "Settings, System, Remote Desktop, turn it on. It needs Windows Pro; Home has no Remote Desktop host."),

        ["Multiple sessions per user allowed"] = (
            "fSingleSessionPerUser decides what happens when an account that is already signed in connects again: a second session, or a takeover of the first.",
            "Safe to leave as is while every alt has its own Windows account. Turn it off the moment two alts share one, or the second launch will steal the first's session."),

        ["Minimised sessions keep rendering"] = (
            "Windows stops drawing a remote desktop the moment its window is minimised, to save work. A macro reading pixels goes blind when that happens.",
            "The fix writes RemoteDesktop_SuppressWhenMinimized = 2 under the Terminal Server Client policy. Until it is applied, hide alt windows instead of minimising them."),

        ["Listening on port 3389"] = (
            "The RDP endpoint itself. If nothing is bound to 3389 there is nowhere for a launch to connect to.",
            "Usually a symptom rather than a cause: start Terminal Services and this comes back. If it does not, something else has taken 3389 -- netstat -ano -p tcp will name it."),

        ["New alts configure themselves"] = (
            "A brand new Windows account walks through privacy screens and starts a pile of first-run apps. Left alone, an alt sits on those screens instead of playing.",
            "The fix pre-answers the setup screens and blocks the startup apps for accounts created from here. It only affects alts this app makes."),

        ["Roblox is staged for new alts"] = (
            "Every alt account would otherwise download and install Roblox from scratch on its first launch. Staging keeps one copy ready to hand over.",
            "The fix downloads the current installer once. Re-run it if Roblox has updated and new alts are stalling on a version mismatch."),

        ["Launch prompts suppressed"] = (
            "Unsigned .rdp files make mstsc ask for confirmation on every launch. With ten alts that is ten dialogs.",
            "The fix signs the session files with a local certificate and trusts it. Purely a convenience: launches work without it, they just ask first.")
    };

    public static string What(string? name) =>
        name is not null && Notes.TryGetValue(name, out var note) ? note.What : "";

    public static string Manually(string? name) =>
        name is not null && Notes.TryGetValue(name, out var note) ? note.Manually : "";
}
