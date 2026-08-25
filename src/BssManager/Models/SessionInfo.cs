namespace BssManager.Models;

public enum SessionState
{
    /// <summary>No Windows session exists for this account.</summary>
    None,
    /// <summary>Session exists and is connected to a client.</summary>
    Active,
    /// <summary>Session exists but the RDP client detached. Macro keeps running.</summary>
    Disconnected,
    /// <summary>Session exists in some other/transitional state.</summary>
    Other
}

public record SessionInfo(int SessionId, string Username, SessionState State, string WindowStationName);
