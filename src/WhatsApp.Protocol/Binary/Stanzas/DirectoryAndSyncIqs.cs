using System.Globalization;

namespace WhatsApp.Protocol.Binary.Stanzas;

/// <summary>
/// AppState (<c>syncd</c>) synchronization IQ builder (<c>xmlns="w:sync:app:state"</c>).
/// </summary>
public static class AppStateSyncIq
{
    public const string Xmlns = "w:sync:app:state";

    public static Node BuildCollectionRequest(
        string collectionName,
        ulong? version = null,
        bool returnSnapshot = false,
        byte[]? encodedPatch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["name"] = collectionName
        };
        if (version is ulong v)
        {
            attrs["version"] = v.ToString(CultureInfo.InvariantCulture);
        }
        if (returnSnapshot)
        {
            attrs["return_snapshot"] = "true";
        }

        return encodedPatch is { Length: > 0 }
            ? new Node("collection", attrs, new Node[] { new("patch", encodedPatch) })
            : new Node("collection", attrs);
    }

    public static Node BuildSync(string iqId, IReadOnlyList<Node> collectionNodes, bool authoritative = false)
    {
        ArgumentNullException.ThrowIfNull(collectionNodes);
        if (collectionNodes.Count == 0)
        {
            throw new ArgumentException("At least one collection node is required.", nameof(collectionNodes));
        }

        Dictionary<string, NodeValue>? syncAttrs = authoritative
            ? new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["data_namespace"] = "3" }
            : null;

        var syncNode = new Node("sync", syncAttrs, new NodeContent.NodesContent(collectionNodes));
        return StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerPn, syncNode);
    }
}

/// <summary>
/// User synchronization (<c>usync</c>) IQ builders for device lists and LID/contact resolution.
/// </summary>
public static class UsyncIq
{
    public const string Xmlns = "usync";

    public static Node BuildDeviceListQuery(
        string iqId,
        string sid,
        IReadOnlyList<Jid> jids,
        string context = "interactive")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        ArgumentNullException.ThrowIfNull(jids);
        if (jids.Count == 0)
        {
            throw new ArgumentException("At least one JID is required.", nameof(jids));
        }

        var devicesQuery = new Node(
            "devices",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["version"] = "2" });

        var queryNode = new Node("query", new Node[] { devicesQuery, new Node("lid") });
        var users = new Node[jids.Count];
        for (int i = 0; i < jids.Count; i++)
        {
            users[i] = new Node("user", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = jids[i] });
        }

        return BuildUsyncEnvelope(iqId, sid, "query", context, queryNode, new Node("list", users));
    }

    public static Node BuildContactLookupQuery(
        string iqId,
        string sid,
        IReadOnlyList<string> phoneNumbers,
        string context = "interactive")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        ArgumentNullException.ThrowIfNull(phoneNumbers);
        if (phoneNumbers.Count == 0)
        {
            throw new ArgumentException("At least one phone number is required.", nameof(phoneNumbers));
        }

        var queryNode = new Node("query", new Node[]
        {
            new("contact"),
            new("lid"),
            new("business", new Node[] { new("verified_name") })
        });

        var users = new Node[phoneNumbers.Count];
        for (int i = 0; i < phoneNumbers.Count; i++)
        {
            users[i] = new Node("user", new Node[] { new("contact", phoneNumbers[i]) });
        }

        return BuildUsyncEnvelope(iqId, sid, "query", context, queryNode, new Node("list", users));
    }

    private static Node BuildUsyncEnvelope(
        string iqId,
        string sid,
        string mode,
        string context,
        Node queryNode,
        Node listNode)
    {
        var usyncAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["sid"] = sid,
            ["mode"] = mode,
            ["last"] = "true",
            ["index"] = "0",
            ["context"] = context
        };

        var usyncNode = new Node("usync", usyncAttrs, new Node[] { queryNode, listNode });
        return StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn, usyncNode);
    }
}

/// <summary>
/// Media host discovery IQ builder (<c>xmlns="w:m"</c>).
/// </summary>
public static class MediaConnIq
{
    public const string Xmlns = "w:m";

    public static Node BuildGetMediaConn(string iqId) =>
        StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerPn, new Node("media_conn"));
}

/// <summary>
/// Privacy settings IQ builder (<c>xmlns="privacy"</c>).
/// </summary>
public static class PrivacyIq
{
    public const string Xmlns = "privacy";

    public static Node BuildGetPrivacy(string iqId) =>
        StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn, new Node("privacy"));

    public static Node BuildSetPrivacy(string iqId, string category, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var categoryNode = new Node(
            "category",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["name"] = category,
                ["value"] = value
            });

        return StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerPn, new Node("privacy", new Node[] { categoryNode }));
    }
}

/// <summary>
/// Blocklist IQ builder (<c>xmlns="blocklist"</c>).
/// </summary>
public static class BlocklistIq
{
    public const string Xmlns = "blocklist";

    public static Node BuildGetBlocklist(string iqId) =>
        StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn);

    public static Node BuildUpdateItem(string iqId, Jid jid, bool block, Jid? pnJid = null)
    {
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["jid"] = jid,
            ["action"] = block ? "block" : "unblock"
        };
        if (pnJid is Jid pn)
        {
            attrs["pn_jid"] = pn;
        }

        return StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerPn, new Node("item", attrs));
    }
}
