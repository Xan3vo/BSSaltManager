namespace BssManager.Models;

/// <summary>
/// The slice of Kairos's configuration that differs from one alt to the next.
///
/// Kairos has around a hundred settings across eight sections. Mirroring all of
/// them here would mean re-implementing their GUI and re-breaking every time
/// they add a key. So this holds only what is genuinely per-alt -- which hive,
/// which field, which pattern -- and everything else is left to the macro's own
/// interface, running inside the session.
///
/// Names and defaults match Kairos's <c>[Main]</c> and <c>[Alt]</c> sections. A
/// value we do not write is not lost: the macro fills in its own default.
/// </summary>
public class MacroSettings
{
    /// <summary>
    /// Which alt this is to the macro. Kairos uses it to stagger alts so they
    /// do not all converge on the same spot, so two alts sharing a number is a
    /// real misconfiguration rather than a cosmetic one.
    /// </summary>
    public int AltNumber { get; set; } = 1;

    public int HiveSlot { get; set; } = 1;

    /// <summary>
    /// Walkspeed the macro assumes. Wrong here means every path overshoots.
    /// A decimal on purpose -- Bee Swarm walkspeeds are routinely fractional
    /// (28.5, 30.75), and rounding one to a whole number is a real misread.
    /// </summary>
    public double Movespeed { get; set; } = 29;

    public string DefaultField { get; set; } = "pepper";

    public string Pattern { get; set; } = "GeneralBooster";
    public int PatternSize { get; set; } = 1;
    public int PatternWidth { get; set; } = 1;

    public int RotationAmount { get; set; }
    public string RotationDirection { get; set; } = "Right";

    public bool ShiftLock { get; set; }
    public bool ClaimHive { get; set; } = true;
    public bool UseTool { get; set; } = true;

    public string SprinklerLocation { get; set; } = "Center";
    public int SprinklerDistance { get; set; } = 1;

    public MacroSettings Clone() => (MacroSettings)MemberwiseClone();

    // ------------------------------------------------------------- the lists

    /// <summary>
    /// Fields in the order Kairos lists them. Values are the macro's own
    /// lowercase keys, which is what goes in the ini.
    /// </summary>
    public static readonly string[] Fields =
    [
        "sunflower", "dandelion", "mushroom", "blueflower", "clover",
        "strawberry", "spider", "bamboo", "pineapple", "stump", "cactus",
        "pumpkin", "pinetree", "rose", "mountaintop", "pepper", "coconut"
    ];

    public static readonly string[] SprinklerLocations =
    [
        "Center", "Upper Left", "Left", "Lower Left", "Lower",
        "Lower Right", "Right", "Upper Right", "Upper"
    ];

    public static readonly string[] RotationDirections = ["Right", "Left"];

    /// <summary>
    /// Used only when the installed copy cannot be read. Kairos builds its own
    /// pattern list by scanning its Patterns folder, so the folder is the truth
    /// and this is a stand-in for a machine that has not installed it yet.
    /// </summary>
    public static readonly string[] FallbackPatterns = ["GeneralBooster", "Stationary"];
}
