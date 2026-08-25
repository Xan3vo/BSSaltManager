namespace BssManager.Models;

public enum HealthState
{
    Ok,
    Warning,
    Failed,
    Unknown
}

/// <summary>Identifies a repair the app knows how to perform for a failing check.</summary>
public enum FixAction
{
    None,
    UpdateRdpWrapIni,
    EnableRdpConnections,
    AllowMultipleSessionsPerUser,
    SetSuppressWhenMinimized,
    StartTermService,
    InstallRdpWrap,
    SkipFirstLoginSetup,
    StageRoblox,
    TrustRdpFiles
}

public class HealthCheck
{
    public string Name { get; init; } = "";
    public HealthState State { get; init; } = HealthState.Unknown;
    public string Detail { get; init; } = "";

    /// <summary>Plain-English explanation of what breaks if this stays broken.</summary>
    public string Consequence { get; init; } = "";

    public FixAction Fix { get; init; } = FixAction.None;
    public string FixLabel { get; init; } = "";

    public bool CanFix => Fix != FixAction.None;
}
