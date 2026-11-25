using System.Globalization;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum PresenceStatus : byte
{
    Available = 0,
    Unavailable = 1,
}

public enum ChatActivity : byte
{
    Typing = 0,
    RecordingAudio = 1,
    Idle = 2,
}

public sealed record ParsedPresenceStanza(
    Jid From,
    PresenceStatus Status,
    long? LastSeenSeconds);

public sealed record ParsedChatStateStanza(
    Jid ChatJid,
    Jid? Participant,
    ChatActivity Activity);

/// <summary>
/// <c>&lt;presence&gt;</c> and <c>&lt;chatstate&gt;</c> stanza builders &amp; parsers verified against
/// live <c>WASendPresenceStatusProtocol</c>, <c>WASmaxPresenceSubscribeRPC</c>,
/// <c>WASendChatStateProtocol</c>, and <c>WAWap.encodeStanza</c>.
/// </summary>
public static class PresenceStanzas
{
    public static Node BuildAvailability(PresenceStatus status, string? pushName = null)
    {
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["type"] = status == PresenceStatus.Available ? "available" : "unavailable"
        };
        if (!string.IsNullOrEmpty(pushName))
        {
            attrs["name"] = pushName;
        }

        return new Node("presence", attrs);
    }

    public static Node BuildSubscribe(
        Jid targetJid,
        string? name = null,
        Jid? groupContext = null,
        byte[]? tcToken = null)
    {
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["type"] = "subscribe",
            ["to"] = targetJid.ToNonAd()
        };
        if (!string.IsNullOrEmpty(name))
        {
            attrs["name"] = name;
        }
        if (groupContext is Jid ctx)
        {
            attrs["context"] = ctx.ToNonAd();
        }

        return tcToken is { Length: > 0 }
            ? new Node("presence", attrs, new[] { new Node("tctoken", tcToken) })
            : new Node("presence", attrs);
    }

    public static Node BuildChatState(Jid toChatJid, ChatActivity activity, Jid? participant = null)
    {
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["to"] = toChatJid.ToNonAd()
        };
        if (participant is Jid p)
        {
            attrs["participant"] = p;
        }

        Node child = activity switch
        {
            ChatActivity.Typing => new Node("composing"),
            ChatActivity.RecordingAudio => new Node("composing", new Dictionary<string, NodeValue> { ["media"] = "audio" }),
            _ => new Node("paused")
        };

        return new Node("chatstate", attrs, new[] { child });
    }

    public static ParsedPresenceStanza? ParseIncomingPresence(Node stanza, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(stanza);
        if (stanza.Tag != "presence"
            || stanza.GetAttr("from") is not { Length: > 0 } fromStr
            || !Jid.TryParse(fromStr, out Jid from))
        {
            return null;
        }

        PresenceStatus status = string.Equals(stanza.GetAttr("type"), "unavailable", StringComparison.OrdinalIgnoreCase)
            ? PresenceStatus.Unavailable
            : PresenceStatus.Available;

        long? lastSeen = null;
        if (stanza.GetAttr("last") is { Length: > 0 } lastStr
            && long.TryParse(lastStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedLast))
        {
            lastSeen = parsedLast;
        }

        return new ParsedPresenceStanza(from, status, lastSeen);
    }

    public static ParsedChatStateStanza? ParseIncomingChatState(Node stanza)
    {
        ArgumentNullException.ThrowIfNull(stanza);
        if (stanza.Tag != "chatstate"
            || stanza.GetAttr("from") is not { Length: > 0 } fromStr
            || !Jid.TryParse(fromStr, out Jid from))
        {
            return null;
        }

        Jid? participant = stanza.GetAttr("participant") is { Length: > 0 } pStr && Jid.TryParse(pStr, out Jid pJid)
            ? pJid
            : null;

        ChatActivity activity = ChatActivity.Idle;
        if (stanza.GetChild("composing") is { } composing)
        {
            activity = string.Equals(composing.GetAttr("media"), "audio", StringComparison.OrdinalIgnoreCase)
                ? ChatActivity.RecordingAudio
                : ChatActivity.Typing;
        }
        else if (stanza.GetChild("paused") is not null)
        {
            activity = ChatActivity.Idle;
        }

        return new ParsedChatStateStanza(from, participant, activity);
    }
}
