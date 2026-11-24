namespace WhatsApp.Protocol.AppState;

/// <summary>
/// WhatsApp AppState sync protocol constants and action types.
/// </summary>
public enum WmrSyncAction
{
    Set = 0,
    Remove = 1
}

public static class AppStateCollections
{
    public const string Regular = "regular";
    public const string RegularLow = "regular_low";
    public const string RegularHigh = "regular_high";
    public const string CriticalUnblockLow = "critical_unblock_low";
    public const string CriticalBlock = "critical_block";
}
