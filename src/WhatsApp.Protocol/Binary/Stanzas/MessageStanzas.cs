using System.Globalization;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Protocol.Binary.Stanzas;

public sealed record EncryptedMessagePayload(
    int Version,
    string EncType,
    byte[] Ciphertext,
    string? MediaType = null,
    bool HideDecryptFail = false);

public sealed record IncomingMessageStanza(
    string Id,
    Jid From,
    Jid? Recipient,
    Jid? Participant,
    Jid? SenderLid,
    Jid? SenderPn,
    Jid? PeerRecipientPn,
    string? PeerRecipientUsername,
    long TimestampSeconds,
    string Type,
    string? Edit,
    string? Category,
    bool IsOffline,
    string? PushName,
    IReadOnlyList<EncryptedMessagePayload> EncPayloads)
{
    public Jid ChatJid => Recipient is { User.Length: > 0 } r ? r.ToNonAd() : From.ToNonAd();
    public Jid SenderDeviceJid => Participant ?? From;
}

/// <summary>
/// Outbound &amp; inbound <c>&lt;message&gt;</c> E2EE classification, stanza building,
/// and encrypted node parsing matching <c>WAWebE2EProtoUtils</c> and <c>wacore/src/send</c>.
/// </summary>
public static class MessageStanzas
{
    public static Message UnwrapClassificationMessage(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Message cur = message;
        while (true)
        {
            if (cur.EphemeralMessage?.Message is { } eph) { cur = eph; continue; }
            if (cur.ViewOnceMessage?.Message is { } vo) { cur = vo; continue; }
            if (cur.ViewOnceMessageV2?.Message is { } vo2) { cur = vo2; continue; }
            if (cur.ViewOnceMessageV2Extension?.Message is { } vo2e) { cur = vo2e; continue; }
            if (cur.DocumentWithCaptionMessage?.Message is { } dwc) { cur = dwc; continue; }
            if (cur.LottieStickerMessage?.Message is { } ls) { cur = ls; continue; }
            if (cur.GroupStatusMessageV2?.Message is { } gs2) { cur = gs2; continue; }
            if (cur.SpoilerMessage?.Message is { } sp) { cur = sp; continue; }
            if (cur.BotForwardedMessage?.Message is { } bfm) { cur = bfm; continue; }
            if (cur.DeviceSentMessage?.Message is { } dsm) { cur = dsm; continue; }
            return cur;
        }
    }

    public static string ClassifyStanzaType(Message message)
    {
        Message m = UnwrapClassificationMessage(message);

        if (m.ReactionMessage is not null || m.EncReactionMessage is not null)
        {
            return "reaction";
        }
        if (m.EventMessage is not null || m.EncEventResponseMessage is not null)
        {
            return "event";
        }
        if (m.PollCreationMessage is not null
            || m.PollCreationMessageV2 is not null
            || m.PollCreationMessageV3 is not null
            || m.PollCreationMessageV5 is not null
            || m.PollCreationMessageV6 is not null
            || m.PollUpdateMessage is not null
            || m.PollAddOptionMessage is not null)
        {
            return "poll";
        }
        if (m.SecretEncryptedMessage is { } sec)
        {
            return sec.SecretEncType switch
            {
                Message.Types.SecretEncryptedMessage.Types.SecretEncType.PollEdit or Message.Types.SecretEncryptedMessage.Types.SecretEncType.PollAddOption => "poll",
                Message.Types.SecretEncryptedMessage.Types.SecretEncType.EventEdit => "event",
                _ => "text"
            };
        }
        if (m.HasConversation
            || m.ProtocolMessage is not null
            || m.KeepInChatMessage is not null
            || m.EditedMessage is not null
            || m.PinInChatMessage is not null
            || m.InteractiveMessage is not null
            || m.TemplateButtonReplyMessage is not null
            || m.RequestPhoneNumberMessage is not null
            || m.EncCommentMessage is not null
            || m.AlbumMessage is not null
            || m.RichResponseMessage is not null)
        {
            return "text";
        }
        if (m.ExtendedTextMessage is { } ext)
        {
            return ext.HasMatchedText && !string.IsNullOrWhiteSpace(ext.MatchedText) ? "media" : "text";
        }
        return "media";
    }

    public static string? ClassifyMediaType(Message message)
    {
        Message m = UnwrapClassificationMessage(message);
        if (m.ImageMessage is not null) return "image";
        if (m.VideoMessage is { GifPlayback: true }) return "gif";
        if (m.VideoMessage is not null) return "video";
        if (m.AudioMessage is { Ptt: true }) return "ptt";
        if (m.AudioMessage is not null) return "audio";
        if (m.DocumentMessage is not null) return "document";
        if (m.StickerMessage is not null) return "sticker";
        if (m.LocationMessage is not null) return "location";
        if (m.LiveLocationMessage is not null) return "livelocation";
        if (m.ContactMessage is not null) return "vcard";
        if (m.ContactsArrayMessage is not null) return "contact_array";
        if (m.ExtendedTextMessage is { MatchedText.Length: > 0 }) return "url";
        return null;
    }

    public static bool ShouldHideDecryptFail(Message message, string? editAttribute = null)
    {
        if (editAttribute is "1")
        {
            return true;
        }
        Message m = UnwrapClassificationMessage(message);
        return m.ProtocolMessage?.Type == Message.Types.ProtocolMessage.Types.Type.Revoke
            || m.ProtocolMessage?.Type == Message.Types.ProtocolMessage.Types.Type.MessageEdit
            || m.ReactionMessage is not null
            || m.EncReactionMessage is not null
            || m.EncEventResponseMessage is not null
            || m.SecretEncryptedMessage is not null
            || m.PollUpdateMessage is not null
            || m.PollAddOptionMessage is not null
            || m.KeepInChatMessage is not null
            || m.PinInChatMessage is not null;
    }

    public static Node BuildEncryptedEncNode(
        string encType,
        byte[] ciphertext,
        string? mediaType = null,
        bool hideDecryptFail = false,
        int? retryCount = null)
    {
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["v"] = "2",
            ["type"] = encType
        };
        if (!string.IsNullOrEmpty(mediaType))
        {
            attrs["mediatype"] = mediaType;
        }
        if (hideDecryptFail)
        {
            attrs["decrypt-fail"] = "hide";
        }
        if (retryCount is int rc && rc > 0)
        {
            attrs["count"] = rc.ToString(CultureInfo.InvariantCulture);
        }
        return new Node("enc", attrs, ciphertext);
    }

    public static Node BuildDmStanza(
        string messageId,
        Jid toJid,
        string stanzaType,
        IReadOnlyList<(Jid DeviceJid, string EncType, byte[] Ciphertext)> participantDevices,
        string? mediaType = null,
        bool hideDecryptFail = false,
        byte[]? deviceIdentityBytes = null,
        ReportingTokenResult? reportingToken = null,
        string? editAttribute = null,
        Jid? peerRecipientPn = null,
        string? category = null,
        IReadOnlyList<Node>? extraNodes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var toNodes = new Node[participantDevices.Count];
        for (int i = 0; i < participantDevices.Count; i++)
        {
            var (devJid, encType, ct) = participantDevices[i];
            toNodes[i] = new Node(
                "to",
                new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = devJid },
                new[] { BuildEncryptedEncNode(encType, ct, mediaType, hideDecryptFail) });
        }

        var children = new List<Node> { new("participants", toNodes) };
        if (deviceIdentityBytes is { Length: > 0 })
        {
            children.Add(new Node("device-identity", deviceIdentityBytes));
        }
        if (reportingToken is not null)
        {
            children.Add(BuildReportingNode(reportingToken));
        }
        if (extraNodes is { Count: > 0 })
        {
            children.AddRange(extraNodes);
        }

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = messageId,
            ["to"] = toJid.ToNonAd(),
            ["type"] = stanzaType
        };
        if (!string.IsNullOrEmpty(editAttribute))
        {
            attrs["edit"] = editAttribute;
        }
        if (peerRecipientPn is Jid pn)
        {
            attrs["peer_recipient_pn"] = pn.ToNonAd();
        }
        if (!string.IsNullOrEmpty(category))
        {
            attrs["category"] = category;
        }

        return new Node("message", attrs, new NodeContent.NodesContent(children));
    }

    public static Node BuildGroupStanza(
        string messageId,
        Jid groupJid,
        string stanzaType,
        byte[] skmsgCiphertext,
        string? mediaType = null,
        bool hideDecryptFail = false,
        string? phash = null,
        IReadOnlyList<(Jid DeviceJid, string EncType, byte[] SkdmCiphertext)>? skdmTargets = null,
        byte[]? deviceIdentityBytes = null,
        ReportingTokenResult? reportingToken = null,
        string? editAttribute = null,
        string? addressingMode = null,
        IReadOnlyList<Node>? extraNodes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var children = new List<Node>();

        if (skdmTargets is { Count: > 0 })
        {
            var toNodes = new Node[skdmTargets.Count];
            for (int i = 0; i < skdmTargets.Count; i++)
            {
                var (devJid, encType, skdmCt) = skdmTargets[i];
                toNodes[i] = new Node(
                    "to",
                    new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = devJid },
                    new[] { BuildEncryptedEncNode(encType, skdmCt, mediaType: null, hideDecryptFail) });
            }
            children.Add(new Node("participants", toNodes));
        }

        children.Add(BuildEncryptedEncNode("skmsg", skmsgCiphertext, mediaType, hideDecryptFail));

        if (deviceIdentityBytes is { Length: > 0 })
        {
            children.Add(new Node("device-identity", deviceIdentityBytes));
        }
        if (reportingToken is not null)
        {
            children.Add(BuildReportingNode(reportingToken));
        }
        if (extraNodes is { Count: > 0 })
        {
            children.AddRange(extraNodes);
        }

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = messageId,
            ["to"] = groupJid.ToNonAd(),
            ["type"] = stanzaType
        };
        if (!string.IsNullOrEmpty(phash))
        {
            attrs["phash"] = phash;
        }
        if (!string.IsNullOrEmpty(editAttribute))
        {
            attrs["edit"] = editAttribute;
        }
        if (!string.IsNullOrEmpty(addressingMode))
        {
            attrs["addressing_mode"] = addressingMode;
        }

        return new Node("message", attrs, new NodeContent.NodesContent(children));
    }

    public static Node BuildRetryResendStanza(
        string messageId,
        Jid toChatJid,
        Jid targetDeviceJid,
        string encType,
        byte[] ciphertext,
        string stanzaType,
        int retryCount,
        string? mediaType = null,
        bool hideDecryptFail = false,
        byte[]? deviceIdentityBytes = null,
        string? addressingMode = null)
    {
        var encNode = BuildEncryptedEncNode(encType, ciphertext, mediaType, hideDecryptFail, retryCount);
        var children = new List<Node> { encNode };
        if (deviceIdentityBytes is { Length: > 0 })
        {
            children.Add(new Node("device-identity", deviceIdentityBytes));
        }

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = messageId,
            ["type"] = stanzaType
        };

        if (toChatJid.IsGroup())
        {
            attrs["to"] = toChatJid.ToNonAd();
            attrs["participant"] = targetDeviceJid;
            if (!string.IsNullOrEmpty(addressingMode))
            {
                attrs["addressing_mode"] = addressingMode;
            }
        }
        else
        {
            attrs["to"] = targetDeviceJid;
        }

        return new Node("message", attrs, new NodeContent.NodesContent(children));
    }

    public static Node BuildReportingNode(ReportingTokenResult token) =>
        new("reporting", new Node[]
        {
            new("reporting_token", new Dictionary<string, NodeValue> { ["v"] = token.Version.ToString(CultureInfo.InvariantCulture) }, token.ReportingToken16)
        });

    public static Message BuildPlaceholderResendMessage(IReadOnlyList<MessageKey> messageKeys)
    {
        ArgumentNullException.ThrowIfNull(messageKeys);
        var req = new Message.Types.PeerDataOperationRequestMessage
        {
            PeerDataOperationRequestType = Message.Types.PeerDataOperationRequestType.PlaceholderMessageResend
        };
        foreach (MessageKey key in messageKeys)
        {
            req.PlaceholderMessageResendRequest.Add(new Message.Types.PeerDataOperationRequestMessage.Types.PlaceholderMessageResendRequest
            {
                MessageKey = key.Clone()
            });
        }

        return new Message
        {
            ProtocolMessage = new Message.Types.ProtocolMessage
            {
                Type = Message.Types.ProtocolMessage.Types.Type.PeerDataOperationRequestMessage,
                PeerDataOperationRequestMessage = req
            }
        };
    }

    public static Message BuildHistorySyncOnDemandMessage(
        Jid chatJid,
        string oldestMsgId,
        bool oldestMsgFromMe,
        long oldestMsgTimestampMs,
        int onDemandMsgCount = 50,
        Jid? accountLid = null,
        bool supportInlineResponse = true,
        bool initialReverseHistory = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldestMsgId);
        var odReq = new Message.Types.PeerDataOperationRequestMessage.Types.HistorySyncOnDemandRequest
        {
            ChatJid = chatJid.ToNonAdString(),
            OldestMsgId = oldestMsgId,
            OldestMsgFromMe = oldestMsgFromMe,
            OldestMsgTimestampMs = oldestMsgTimestampMs,
            OnDemandMsgCount = Math.Clamp(onDemandMsgCount, 1, 500),
            SupportInlineResponse = supportInlineResponse
        };
        if (accountLid is Jid lid)
        {
            odReq.AccountLid = lid.ToNonAdString();
        }
        if (initialReverseHistory)
        {
            odReq.Mode = Message.Types.PeerDataOperationRequestMessage.Types.HistorySyncOnDemandRequest.Types.Mode.InitialReverseHistory;
        }

        var req = new Message.Types.PeerDataOperationRequestMessage
        {
            PeerDataOperationRequestType = Message.Types.PeerDataOperationRequestType.HistorySyncOnDemand,
            HistorySyncOnDemandRequest = odReq
        };

        return new Message
        {
            ProtocolMessage = new Message.Types.ProtocolMessage
            {
                Type = Message.Types.ProtocolMessage.Types.Type.PeerDataOperationRequestMessage,
                PeerDataOperationRequestMessage = req
            }
        };
    }

    public static IncomingMessageStanza? ParseIncomingMessage(Node stanza, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(stanza);
        if (stanza.Tag != "message"
            || stanza.GetAttr("id") is not { Length: > 0 } id
            || stanza.GetAttr("from") is not { Length: > 0 } fromStr
            || !Jid.TryParse(fromStr, out Jid from))
        {
            return null;
        }

        Jid? recipient = TryParseAttrJid(stanza, "recipient");
        Jid? participant = TryParseAttrJid(stanza, "participant");
        Jid? senderLid = TryParseAttrJid(stanza, "sender_lid");
        Jid? senderPn = TryParseAttrJid(stanza, "sender_pn");
        Jid? peerRecipientPn = TryParseAttrJid(stanza, "peer_recipient_pn");

        long ts = long.TryParse(stanza.GetAttr("t"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedTs) && parsedTs > 0
            ? parsedTs
            : (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();

        var encPayloads = new List<EncryptedMessagePayload>();
        foreach (Node encNode in stanza.GetChildren("enc"))
        {
            if (encNode.Content is NodeContent.BytesContent bc && bc.Bytes.Length > 0)
            {
                int version = int.TryParse(encNode.GetAttr("v"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 2;
                string encType = encNode.GetAttr("type") ?? "msg";
                bool hide = string.Equals(encNode.GetAttr("decrypt-fail"), "hide", StringComparison.OrdinalIgnoreCase);
                encPayloads.Add(new EncryptedMessagePayload(version, encType, bc.Bytes, encNode.GetAttr("mediatype"), hide));
            }
        }

        return new IncomingMessageStanza(
            id,
            from,
            recipient,
            participant,
            senderLid,
            senderPn,
            peerRecipientPn,
            stanza.GetAttr("peer_recipient_username"),
            ts,
            stanza.GetAttr("type") ?? "text",
            stanza.GetAttr("edit"),
            stanza.GetAttr("category"),
            stanza.GetAttr("offline") is not null,
            stanza.GetAttr("notify"),
            encPayloads);
    }

    private static Jid? TryParseAttrJid(Node node, string attrName) =>
        node.GetAttr(attrName) is { Length: > 0 } s && Jid.TryParse(s, out Jid j) ? j : null;
}
