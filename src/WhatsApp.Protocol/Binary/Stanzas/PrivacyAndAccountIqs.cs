using System.Text;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum PrivacyCategory
{
    Last,
    Online,
    Profile,
    Status,
    GroupAdd,
    ReadReceipts,
    CallAdd,
    Messages,
    DefenseMode,
    Other
}

public enum PrivacyValue
{
    All,
    Contacts,
    None,
    ContactBlacklist,
    MatchLastSeen,
    Known,
    Screen,
    Off,
    OnStandard,
    Other
}

public enum DisallowedListAction
{
    Add,
    Remove
}

public sealed record DisallowedListUserEntry(
    DisallowedListAction Action,
    Jid UserJid,
    Jid? PnJid = null);

public sealed record PrivacySettingEntry(
    PrivacyCategory Category,
    string CategoryRaw,
    PrivacyValue Value,
    string ValueRaw,
    string? Dhash = null);

public sealed record PrivacySettingsSnapshot(IReadOnlyList<PrivacySettingEntry> Settings)
{
    public PrivacySettingEntry? Get(PrivacyCategory category) =>
        Settings.FirstOrDefault(s => s.Category == category);
}

public sealed record SetPrivacyResult(string? Dhash);

/// <summary>
/// Typed Privacy IQ codecs (<c>xmlns="privacy"</c>), Default Disappearing Mode (<c>xmlns="disappearing_mode"</c>),
/// Companion Device removal (<c>xmlns="md"</c>), and Spam Reports (<c>xmlns="spam"</c>),
/// verified against live <c>web.whatsapp.com</c> (<c>WAWebPrivacySettings</c>, <c>WAWap</c>) and <c>wacore</c>.
/// </summary>
public static class PrivacyProtocol
{
    public const string PrivacyXmlns = "privacy";
    public const string DisappearingModeXmlns = "disappearing_mode";

    public static string ToWireString(this PrivacyCategory category, string? customRaw = null) => category switch
    {
        PrivacyCategory.Last => "last",
        PrivacyCategory.Online => "online",
        PrivacyCategory.Profile => "profile",
        PrivacyCategory.Status => "status",
        PrivacyCategory.GroupAdd => "groupadd",
        PrivacyCategory.ReadReceipts => "readreceipts",
        PrivacyCategory.CallAdd => "calladd",
        PrivacyCategory.Messages => "messages",
        PrivacyCategory.DefenseMode => "defense",
        _ => string.IsNullOrWhiteSpace(customRaw) ? throw new ArgumentException("Custom category raw value required.") : customRaw
    };

    public static PrivacyCategory ParseCategory(string wire) => wire switch
    {
        "last" => PrivacyCategory.Last,
        "online" => PrivacyCategory.Online,
        "profile" => PrivacyCategory.Profile,
        "status" => PrivacyCategory.Status,
        "groupadd" => PrivacyCategory.GroupAdd,
        "readreceipts" => PrivacyCategory.ReadReceipts,
        "calladd" => PrivacyCategory.CallAdd,
        "messages" => PrivacyCategory.Messages,
        "defense" => PrivacyCategory.DefenseMode,
        _ => PrivacyCategory.Other
    };

    public static string ToWireString(this PrivacyValue value, string? customRaw = null) => value switch
    {
        PrivacyValue.All => "all",
        PrivacyValue.Contacts => "contacts",
        PrivacyValue.None => "none",
        PrivacyValue.ContactBlacklist => "contact_blacklist",
        PrivacyValue.MatchLastSeen => "match_last_seen",
        PrivacyValue.Known => "known",
        PrivacyValue.Screen => "screen",
        PrivacyValue.Off => "off",
        PrivacyValue.OnStandard => "on_standard",
        _ => string.IsNullOrWhiteSpace(customRaw) ? throw new ArgumentException("Custom value raw string required.") : customRaw
    };

    public static PrivacyValue ParseValue(string wire) => wire switch
    {
        "all" => PrivacyValue.All,
        "contacts" => PrivacyValue.Contacts,
        "none" => PrivacyValue.None,
        "contact_blacklist" => PrivacyValue.ContactBlacklist,
        "match_last_seen" => PrivacyValue.MatchLastSeen,
        "known" => PrivacyValue.Known,
        "screen" => PrivacyValue.Screen,
        "off" => PrivacyValue.Off,
        "on_standard" => PrivacyValue.OnStandard,
        _ => PrivacyValue.Other
    };

    public static bool IsValidCombination(PrivacyCategory category, PrivacyValue value) => category switch
    {
        PrivacyCategory.Last or PrivacyCategory.Profile or PrivacyCategory.Status or PrivacyCategory.GroupAdd =>
            value is PrivacyValue.All or PrivacyValue.Contacts or PrivacyValue.ContactBlacklist or PrivacyValue.None,
        PrivacyCategory.ReadReceipts => value is PrivacyValue.All or PrivacyValue.None,
        PrivacyCategory.Online => value is PrivacyValue.All or PrivacyValue.MatchLastSeen,
        PrivacyCategory.CallAdd => value is PrivacyValue.All or PrivacyValue.Known or PrivacyValue.Contacts or PrivacyValue.Screen,
        PrivacyCategory.Messages => value is PrivacyValue.All or PrivacyValue.Contacts,
        PrivacyCategory.DefenseMode => value is PrivacyValue.Off or PrivacyValue.OnStandard,
        PrivacyCategory.Other => true,
        _ => false
    };

    public static bool SupportsDisallowedList(PrivacyCategory category) =>
        category is PrivacyCategory.Last or PrivacyCategory.Profile or PrivacyCategory.Status or PrivacyCategory.GroupAdd;

    public static Node BuildGetPrivacy(string iqId) =>
        StanzaBuilder.Iq(iqId, "get", PrivacyXmlns, StanzaBuilder.ServerPn, new Node("privacy"));

    public static Node BuildSetPrivacy(string iqId, PrivacyCategory category, PrivacyValue value)
    {
        if (!IsValidCombination(category, value))
        {
            throw new ArgumentException($"Privacy value '{value}' is not valid for category '{category}'.");
        }

        var categoryNode = new Node("category", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["name"] = category.ToWireString(),
            ["value"] = value.ToWireString()
        });
        return StanzaBuilder.Iq(iqId, "set", PrivacyXmlns, StanzaBuilder.ServerPn, new Node("privacy", new[] { categoryNode }));
    }

    public static Node BuildSetPrivacyWithDisallowedList(
        string iqId,
        PrivacyCategory category,
        string dhash,
        IReadOnlyList<DisallowedListUserEntry> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        if (!SupportsDisallowedList(category))
        {
            throw new ArgumentException($"Privacy category '{category}' does not support disallowed lists.", nameof(category));
        }

        var userNodes = new Node[users.Count];
        for (int i = 0; i < users.Count; i++)
        {
            DisallowedListUserEntry entry = users[i];
            var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["action"] = entry.Action == DisallowedListAction.Remove ? "remove" : "add",
                ["jid"] = entry.UserJid.ToNonAd()
            };
            if (entry.PnJid is Jid pn)
            {
                attrs["pn_jid"] = pn.ToNonAd();
            }
            userNodes[i] = new Node("user", attrs);
        }

        var catAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["name"] = category.ToWireString(),
            ["value"] = PrivacyValue.ContactBlacklist.ToWireString(),
            ["dhash"] = dhash ?? string.Empty
        };

        var privacyNode = new Node(
            "privacy",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["addressing_mode"] = "lid" },
            new[] { new Node("category", catAttrs, userNodes) });

        return StanzaBuilder.Iq(iqId, "set", PrivacyXmlns, StanzaBuilder.ServerPn, privacyNode);
    }

    public static PrivacySettingsSnapshot ParseGetPrivacyResponse(Node response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Node privacyNode = response.GetChild("privacy")
            ?? throw new InvalidDataException("Missing <privacy> child in privacy response.");

        var list = new List<PrivacySettingEntry>();
        foreach (Node cat in privacyNode.GetChildren("category"))
        {
            string? name = cat.GetAttr("name");
            string? val = cat.GetAttr("value");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(val))
            {
                throw new InvalidDataException("Privacy <category> missing required 'name' or 'value' attribute.");
            }
            list.Add(new PrivacySettingEntry(
                ParseCategory(name),
                name,
                ParseValue(val),
                val,
                cat.GetAttr("dhash")));
        }
        return new PrivacySettingsSnapshot(list);
    }

    public static SetPrivacyResult ParseSetPrivacyResponse(Node response)
    {
        ArgumentNullException.ThrowIfNull(response);
        string? dhash = response.GetChild("privacy")?.GetChild("category")?.GetAttr("dhash");
        return new SetPrivacyResult(dhash);
    }

    public static Node BuildSetDefaultDisappearingMode(string iqId, uint durationSeconds) =>
        StanzaBuilder.Iq(
            iqId,
            "set",
            DisappearingModeXmlns,
            StanzaBuilder.ServerPn,
            new Node("disappearing_mode", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["duration"] = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }));
}

public static class DeviceManagementIq
{
    public const string Xmlns = "md";

    public static Node BuildRemoveCompanionDevice(string iqId, Jid deviceJid, string reason = "user_initiated") =>
        StanzaBuilder.Iq(
            iqId,
            "set",
            Xmlns,
            StanzaBuilder.ServerPn,
            new Node("remove-companion-device", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["jid"] = deviceJid,
                ["reason"] = reason
            }));
}

public enum SpamFlow
{
    MessageMenu,
    GroupSpamBannerReport,
    GroupInfoReport,
    ContactInfo,
    StatusReport
}

public sealed record SpamReportRequest(
    string MessageId,
    long MessageTimestampSeconds,
    Jid? FromJid = null,
    Jid? ParticipantJid = null,
    Jid? GroupJid = null,
    string? GroupSubject = null,
    SpamFlow Flow = SpamFlow.MessageMenu,
    byte[]? RawMessageBytes = null,
    string? MediaType = null,
    string? LocalMessageType = null,
    byte[]? TcToken = null);

public sealed record SpamReportResult(string? ReportId);

public static class SpamReportIq
{
    public const string Xmlns = "spam";

    public static string ToWireString(this SpamFlow flow) => flow switch
    {
        SpamFlow.GroupSpamBannerReport => "GroupSpamBannerReport",
        SpamFlow.GroupInfoReport => "GroupInfoReport",
        SpamFlow.ContactInfo => "ContactInfo",
        SpamFlow.StatusReport => "StatusReport",
        _ => "MessageMenu"
    };

    public static Node BuildReportSpam(string iqId, SpamReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.MessageId);

        var msgAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = request.MessageId,
            ["t"] = request.MessageTimestampSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (request.FromJid is Jid from) msgAttrs["from"] = from;
        if (request.ParticipantJid is Jid part) msgAttrs["participant"] = part;

        Node[] msgChildren;
        if (request.RawMessageBytes is { Length: > 0 } raw)
        {
            var rawAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["v"] = "3" };
            if (!string.IsNullOrEmpty(request.MediaType)) rawAttrs["mediatype"] = request.MediaType;
            if (!string.IsNullOrEmpty(request.LocalMessageType)) rawAttrs["local_message_type"] = request.LocalMessageType;
            msgChildren = [new Node("raw", rawAttrs, raw)];
        }
        else
        {
            msgChildren = Array.Empty<Node>();
        }

        var messageNode = new Node("message", msgAttrs, msgChildren);
        var listAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["spam_flow"] = request.Flow.ToWireString()
        };
        if (request.GroupJid is Jid grp) listAttrs["jid"] = grp;
        if (!string.IsNullOrEmpty(request.GroupSubject)) listAttrs["subject"] = request.GroupSubject;

        var spamList = new Node("spam_list", listAttrs, new[] { messageNode });
        if (request.TcToken is { Length: > 0 } token)
        {
            return StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerPn, spamList, new Node("tctoken", token));
        }
        return StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerPn, spamList);
    }

    public static SpamReportResult ParseReportSpamResponse(Node response)
    {
        ArgumentNullException.ThrowIfNull(response);
        string? reportId = response.GetChild("report_id")?.Content switch
        {
            NodeContent.StringContent sc => sc.Text,
            NodeContent.BytesContent bc => Encoding.UTF8.GetString(bc.Bytes),
            _ => null
        };
        return new SpamReportResult(reportId);
    }
}
