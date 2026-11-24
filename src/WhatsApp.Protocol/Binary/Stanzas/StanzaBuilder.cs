namespace WhatsApp.Protocol.Binary.Stanzas;

/// <summary>
/// Shared constructors for WhatsApp XMPP-style binary protocol <see cref="Node"/> stanzas and IQs.
/// </summary>
public static class StanzaBuilder
{
    /// <summary>Primary WhatsApp server JID (<c>s.whatsapp.net</c>).</summary>
    public static readonly Jid ServerPn = new(string.Empty, Server.Pn);

    /// <summary>Group domain JID (<c>g.us</c>).</summary>
    public static readonly Jid ServerGroup = new(string.Empty, Server.Group);

    /// <summary>
    /// Builds a typed <c>&lt;iq&gt;</c> stanza with standard attributes and optional child nodes.
    /// </summary>
    public static Node Iq(
        string id,
        string type,
        string xmlns,
        Jid? to = null,
        params Node[] children)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlns);

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["type"] = type,
            ["xmlns"] = xmlns,
            ["to"] = to ?? ServerPn
        };

        return children.Length > 0
            ? new Node("iq", attrs, children)
            : new Node("iq", attrs);
    }

    /// <summary>
    /// Encodes a 24-bit Signal key ID as 3 big-endian bytes matching WhatsApp wire format.
    /// </summary>
    public static byte[] EncodeKeyId24(uint keyId) =>
    [
        (byte)(keyId >> 16),
        (byte)(keyId >> 8),
        (byte)keyId
    ];

    /// <summary>
    /// Encodes a 32-bit unsigned integer as 4 big-endian bytes matching WhatsApp wire format.
    /// </summary>
    public static byte[] EncodeUInt32BigEndian(uint value) =>
    [
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value
    ];
}
