namespace WhatsApp.Protocol.Binary;

/// <summary>
/// Convenience helpers for <see cref="Jid"/> server classification and Signal store addressing.
/// </summary>
public static class JidExtensions
{
    public static bool IsPn(this Jid jid) => jid.Server is Server.Pn or Server.Hosted;

    public static bool IsLid(this Jid jid) => jid.Server is Server.Lid or Server.HostedLid;

    public static bool IsGroup(this Jid jid) => jid.Server == Server.Group;

    public static bool IsHosted(this Jid jid) => jid.Server is Server.Hosted or Server.HostedLid;

    /// <summary>
    /// Formats a JID as the canonical Signal store address (<c>{user}:{device}@{server}</c>),
    /// matching live WhatsApp Web and <see cref="Storage.Sqlite"/> identity records.
    /// </summary>
    public static string ToProtocolAddress(this Jid jid) =>
        $"{jid.User}:{jid.Device}@{jid.Server.ToServerString()}";
}
