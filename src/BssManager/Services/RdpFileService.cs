using System.IO;
using System.Text;
using BssManager.Models;

namespace BssManager.Services;

/// <summary>
/// Generates the .rdp file each alt session is launched from.
///
/// The settings here are not cosmetic. A macro that finds things by pixel or
/// image only works if the session it looks at is exactly the size it was
/// tuned for, at full colour depth, and never rescaled -- so resolution is
/// pinned, smart sizing is off, and dynamic resolution is off. Getting any of
/// those wrong produces a macro that "randomly" misclicks.
/// </summary>
public class RdpFileService
{
    private readonly RdpSigningService _signing = new();

    public string GetPath(AltProfile alt) =>
        Path.Combine(AppPaths.RdpFolder, $"{Sanitize(alt.WindowsUsername)}-{alt.Id}.rdp");

    public string Write(AltProfile alt)
    {
        AppPaths.EnsureCreated();
        var path = GetPath(alt);
        File.WriteAllText(path, Build(alt), new UTF8Encoding(false));

        // Signing has to happen after every write, not once: the signature
        // covers the address and redirection settings, so rewriting the file
        // invalidates it and mstsc goes back to warning about the publisher.
        // A no-op until the certificate is set up.
        _signing.TrySign(path);

        return path;
    }

    public void Delete(AltProfile alt)
    {
        var path = GetPath(alt);
        if (File.Exists(path)) File.Delete(path);
    }

    public string Build(AltProfile alt)
    {
        var sb = new StringBuilder();

        // --- target -----------------------------------------------------------
        sb.AppendLine($"full address:s:{alt.LoopbackAddress}");
        sb.AppendLine($"username:s:{Environment.MachineName}\\{alt.WindowsUsername}");
        sb.AppendLine($"domain:s:{Environment.MachineName}");

        // --- geometry: the part macros depend on -------------------------------
        sb.AppendLine("screen mode id:i:1");          // windowed, never fullscreen
        sb.AppendLine($"desktopwidth:i:{alt.Width}");
        sb.AppendLine($"desktopheight:i:{alt.Height}");
        sb.AppendLine("desktopscalefactor:i:100");    // no DPI scaling inside
        sb.AppendLine("dynamic resolution:i:0");      // never resize with the window
        sb.AppendLine("smart sizing:i:0");            // never rescale the image
        sb.AppendLine("use multimon:i:0");
        sb.AppendLine("session bpp:i:32");            // full colour for pixel checks

        // --- credentials ------------------------------------------------------
        // The password lives in Credential Manager, keyed on this alt's own
        // loopback address, so no prompt appears.
        sb.AppendLine("prompt for credentials:i:0");
        sb.AppendLine("promptcredentialonce:i:0");
        sb.AppendLine("enablecredsspsupport:i:1");
        sb.AppendLine("authentication level:i:0");    // do not warn about the self-signed loopback cert
        sb.AppendLine("administrative session:i:0");

        // --- connection -------------------------------------------------------
        sb.AppendLine("connection type:i:6");         // LAN profile; this never leaves the box
        sb.AppendLine("networkautodetect:i:0");
        sb.AppendLine("bandwidthautodetect:i:0");
        sb.AppendLine("compression:i:0");             // no point compressing a loopback stream
        sb.AppendLine("autoreconnection enabled:i:1");

        // Nothing leaves the machine, so every "make the stream cheaper" feature
        // is pure overhead: it costs CPU to save bandwidth that is free here.
        sb.AppendLine("videoplaybackmode:i:0");       // no multimedia redirection
        sb.AppendLine("disable cursor setting:i:1");  // skip cursor shadow updates
        sb.AppendLine("enablesuperpan:i:0");
        sb.AppendLine("bitmapcachesize:i:32000");     // 32 MB, plenty for a static UI

        // --- keep the host usable ---------------------------------------------
        sb.AppendLine("keyboardhook:i:0");            // Win-key combos stay on YOUR desktop
        sb.AppendLine("audiomode:i:2");               // do not play alt audio
        sb.AppendLine("audiocapturemode:i:0");
        sb.AppendLine("redirectclipboard:i:1");       // handy for pasting cookies in by hand
        sb.AppendLine("redirectprinters:i:0");
        sb.AppendLine("redirectcomports:i:0");
        sb.AppendLine("redirectsmartcards:i:0");
        sb.AppendLine("redirectlocation:i:0");
        sb.AppendLine("redirectwebauthn:i:0");
        sb.AppendLine("devicestoredirect:s:");
        sb.AppendLine("drivestoredirect:s:");
        sb.AppendLine("usbdevicestoredirect:s:");

        // --- cosmetics that cost CPU ------------------------------------------
        // Themes stay ON: turning them off changes how the desktop is drawn, and
        // anything colour-matching inside the session would see different pixels.
        sb.AppendLine("disable wallpaper:i:1");
        sb.AppendLine("disable full window drag:i:1");
        sb.AppendLine("disable menu anims:i:1");
        sb.AppendLine("disable themes:i:0");
        sb.AppendLine("allow font smoothing:i:1");
        sb.AppendLine("allow desktop composition:i:1");
        sb.AppendLine("bitmapcachepersistenable:i:1");

        return sb.ToString();
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "alt" : clean;
    }
}
