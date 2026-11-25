using System.Globalization;

namespace WhatsApp.Protocol.Binary.Stanzas;

/// <summary>
/// Cold-boot and foreground/background state IQ builders (<c>xmlns="passive"</c>).
/// </summary>
public static class PassiveIq
{
    public const string Xmlns = "passive";

    public static Node BuildActive(string iqId) => Build(iqId, passive: false);

    public static Node BuildPassive(string iqId) => Build(iqId, passive: true);

    public static Node Build(string iqId, bool passive) =>
        StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerPn, new Node(passive ? "passive" : "active"));

    public static bool IsSuccessResponse(Node response, bool expectPassive) =>
        response.Tag == "iq"
        && response.GetAttr("type") == "result"
        && response.GetChild(expectPassive ? "passive" : "active") is not null;
}

/// <summary>
/// Keepalive ping IQ builder (<c>xmlns="w:p"</c>).
/// </summary>
public static class PingIq
{
    public const string Xmlns = "w:p";

    public static Node BuildPing(string iqId) =>
        StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn);

    public static bool IsPingRequest(Node iqNode) =>
        iqNode.Tag == "iq"
        && string.Equals(iqNode.GetAttr("type"), "get", StringComparison.Ordinal)
        && string.Equals(iqNode.GetAttr("xmlns"), Xmlns, StringComparison.Ordinal);

    public static Node BuildPong(Node pingRequest)
    {
        ArgumentNullException.ThrowIfNull(pingRequest);
        string? fromAttr = pingRequest.GetAttr("from");
        Jid to = !string.IsNullOrEmpty(fromAttr) && Jid.TryParse(fromAttr, out var parsed)
            ? parsed
            : StanzaBuilder.ServerPn;

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["to"] = to,
            ["type"] = "result"
        };
        if (pingRequest.GetAttr("id") is { Length: > 0 } id)
        {
            attrs["id"] = id;
        }
        return new Node("iq", attrs);
    }
}

/// <summary>
/// Client-paced offline backlog <c>&lt;ib&gt;</c> builders and parsers.
/// </summary>
public static class OfflineBatchIb
{
    public const int DefaultBatchSize = 200;

    /// <summary>
    /// Builds <c>&lt;ib&gt;&lt;offline_batch count="..."/&gt;&lt;/ib&gt;</c>.
    /// </summary>
    public static Node BuildOfflineBatch(int count = DefaultBatchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var batchNode = new Node(
            "offline_batch",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["count"] = count.ToString(CultureInfo.InvariantCulture)
            });

        return new Node("ib", new Node[] { batchNode });
    }

    public static bool TryParseOfflinePreview(Node ibNode, out int count)
    {
        count = 0;
        if (ibNode.Tag != "ib") return false;
        var preview = ibNode.GetChild("offline_preview");
        if (preview is null) return false;
        return int.TryParse(preview.GetAttr("count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
    }
}

public sealed record DirtyBit(string Type, ulong? Timestamp = null);

/// <summary>
/// Dirty-bit clearing IQ builder (<c>xmlns="urn:xmpp:whatsapp:dirty"</c>).
/// </summary>
public static class DirtyBitsIq
{
    public const string Xmlns = "urn:xmpp:whatsapp:dirty";
    public const string TypeAccountSync = "account_sync";
    public const string TypeGroups = "groups";
    public const string TypeSyncdAppState = "syncd_app_state";
    public const string TypeNewsletterMetadata = "newsletter_metadata";

    public static Node BuildClean(string iqId, params DirtyBit[] bits)
    {
        ArgumentNullException.ThrowIfNull(bits);
        if (bits.Length == 0)
        {
            throw new ArgumentException("At least one dirty bit must be specified.", nameof(bits));
        }

        var children = new Node[bits.Length];
        for (int i = 0; i < bits.Length; i++)
        {
            var bit = bits[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(bit.Type);

            var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["type"] = bit.Type
            };
            if (bit.Timestamp is ulong ts)
            {
                attrs["timestamp"] = ts.ToString(CultureInfo.InvariantCulture);
            }

            children[i] = new Node("clean", attrs);
        }

        return StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerPn, children);
    }
}
