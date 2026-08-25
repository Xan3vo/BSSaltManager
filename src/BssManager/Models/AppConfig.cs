namespace BssManager.Models;

public class AppConfig
{
    public List<AltProfile> Alts { get; set; } = new();

    /// <summary>Roblox logins captured through the Accounts tab.</summary>
    public List<RobloxAccount> Accounts { get; set; } = new();

    /// <summary>Seconds to wait between launches when starting several at once.</summary>
    public int StaggerSeconds { get; set; } = 8;

    /// <summary>Default session size applied to newly created alts.</summary>
    public int DefaultWidth { get; set; } = 1280;
    public int DefaultHeight { get; set; } = 720;

    /// <summary>
    /// The game an alt joins when it signs in. Defaults to Bee Swarm Simulator;
    /// there is no UI for it yet, but the app is not otherwise BSS-specific.
    /// </summary>
    public long PlaceId { get; set; } = 1537690962;

    /// <summary>
    /// How long to let a session settle after Windows reports it active before
    /// sending Roblox in. Too short and the desktop is not ready; the ticket is
    /// single use, so a wasted one means launching again.
    /// </summary>
    public int SignInDelaySeconds { get; set; } = 20;

    /// <summary>Next free 127.0.0.x host byte to hand out. Starts at 2; .1 is the host.</summary>
    public int NextLoopbackOctet { get; set; } = 2;
}
