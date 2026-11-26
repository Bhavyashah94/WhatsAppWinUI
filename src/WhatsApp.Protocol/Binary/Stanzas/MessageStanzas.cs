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
    IReadOnlyList<EncryptedMessagePayload> EncPayloads,
    byte[]? PlaintextBytes = null,
    string? PlaintextMediaType = null,
    ulong? ServerId = null,
    bool HasPlaintextNode = false,
    string? MetaTargetId = null,
    Jid? MetaTargetSenderJid = null,
    Jid? MetaTargetChatJid = null,
    BotStanzaInfo? BotInfo = null)
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
            if (cur.NewsletterScheduledMessage?.Message is { } nsm) { cur = nsm; continue; }
            if (cur.NewsletterAdminProfileMessage?.Message is { } nap) { cur = nap; continue; }
            if (cur.BotForwardedMessage?.Message is { } bfm) { cur = bfm; continue; }
            if (cur.BotInvokeMessage?.Message is { } bim) { cur = bim; continue; }
            if (cur.BotTaskMessage?.Message is { } btm) { cur = btm; continue; }
            if (cur.BotGroupParticipantMessage?.Message is { } bgm) { cur = bgm; continue; }
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
            || m.PollAddOptionMessage is not null
            || m.PollResultSnapshotMessage is not null
            || m.PollResultSnapshotMessageV3 is not null)
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
            || m.MessageHistoryNotice is not null
            || m.StatusQuestionAnswerMessage is not null
            || m.StatusStickerInteractionMessage is not null
            || m.StatusQuotedMessage is not null
            || m.AlbumMessage is not null
            || m.NewsletterAdminInviteMessage is not null
            || m.NewsletterFollowerInviteMessageV2 is not null
            || m.RichResponseMessage is not null
            || m.ScheduledCallCreationMessage is not null
            || m.ScheduledCallEditMessage is not null
            || m.CallLogMesssage is not null)
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
        if (m.GroupInviteMessage is not null) return "url";
        if (m.MessageHistoryBundle is not null) return "group_history";
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
            || m.ProtocolMessage?.Type == Message.Types.ProtocolMessage.Types.Type.GroupMemberLabelChange
            || m.ProtocolMessage?.Type == Message.Types.ProtocolMessage.Types.Type.EphemeralSyncResponse
            || m.ProtocolMessage?.Type == Message.Types.ProtocolMessage.Types.Type.RequestWelcomeMessage
            || m.ProtocolMessage?.Type == Message.Types.ProtocolMessage.Types.Type.BotFeedbackMessage
            || m.MessageHistoryNotice is not null
            || m.ReactionMessage is not null
            || m.EncReactionMessage is not null
            || m.EncEventResponseMessage is not null
            || m.SecretEncryptedMessage is not null
            || m.PollUpdateMessage is not null
            || m.PollAddOptionMessage is not null
            || m.KeepInChatMessage is not null
            || m.PinInChatMessage is not null;
    }

    public static Node? InferMetaNode(Message message)
    {
        Message m = UnwrapClassificationMessage(message);
        var metaAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal);

        if (m.PollCreationMessage is not null || m.PollCreationMessageV2 is not null
            || m.PollCreationMessageV3 is not null || m.PollCreationMessageV5 is not null || m.PollCreationMessageV6 is not null)
        {
            metaAttrs["polltype"] = "creation";
        }
        else if (m.PollUpdateMessage?.Vote is not null)
        {
            metaAttrs["polltype"] = "vote";
        }
        else if (m.EventMessage is not null)
        {
            metaAttrs["event_type"] = "creation";
        }
        else if (m.EncEventResponseMessage is not null)
        {
            metaAttrs["event_type"] = "response";
        }
        else if (m.SecretEncryptedMessage?.SecretEncType == Message.Types.SecretEncryptedMessage.Types.SecretEncType.EventEdit)
        {
            metaAttrs["event_type"] = "edit";
        }
        else if (m.ProtocolMessage?.MemberLabel is { } ml)
        {
            metaAttrs["appdata"] = "member_tag";
            metaAttrs["tag_reason"] = string.IsNullOrEmpty(ml.Label) ? "user_delete" : "user_update";
        }

        return metaAttrs.Count > 0 ? new Node("meta", metaAttrs) : null;
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

    /// <summary>
    /// Builds a pairwise-encrypted <c>&lt;message to="{groupJid}"&gt;</c> stanza with
    /// <c>&lt;participants&gt;&lt;to jid="..."&gt;&lt;enc ...&gt;&lt;/enc&gt;&lt;/to&gt;...&lt;/participants&gt;</c>
    /// and NO <c>skmsg</c> node — used for selective Group History Bundle delivery so only
    /// opted-in newly added receivers and own companion devices receive media keys.
    /// </summary>
    public static Node BuildGroupDirectStanza(
        string messageId,
        Jid groupJid,
        string stanzaType,
        IReadOnlyList<(Jid DeviceJid, string EncType, byte[] Ciphertext)> participantDevices,
        string? mediaType = null,
        bool hideDecryptFail = false,
        string? phash = null,
        byte[]? deviceIdentityBytes = null,
        ReportingTokenResult? reportingToken = null,
        string? addressingMode = null,
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
            ["to"] = groupJid.ToNonAd(),
            ["type"] = stanzaType
        };
        if (!string.IsNullOrEmpty(phash))
        {
            attrs["phash"] = phash;
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

        Node? plaintextNode = stanza.GetChild("plaintext");
        byte[]? plaintextBytes = plaintextNode?.Content switch
        {
            NodeContent.BytesContent bc when bc.Bytes.Length > 0 => bc.Bytes,
            _ => null
        };
        string? plaintextMediaType = plaintextNode?.GetAttr("mediatype");
        ulong? serverId = ulong.TryParse(stanza.GetAttr("server_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsedSid)
            ? parsedSid
            : null;

        Node? metaNode = stanza.GetChild("meta");
        string? metaTargetId = metaNode?.GetAttr("target_id");
        Jid? metaTargetSenderJid = metaNode is not null ? TryParseAttrJid(metaNode, "target_sender_jid") : null;
        Jid? metaTargetChatJid = metaNode is not null ? TryParseAttrJid(metaNode, "target_chat_jid") : null;

        BotStanzaInfo? botInfo = null;
        if (stanza.GetChild("bot") is { } botNode)
        {
            long? botSenderTs = long.TryParse(botNode.GetAttr("sender_timestamp_ms"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long bts)
                ? bts
                : null;
            botInfo = new BotStanzaInfo(
                BotStanzas.ParseBotEditType(botNode.GetAttr("edit")),
                botNode.GetAttr("edit_target_id"),
                botSenderTs,
                botNode.GetAttr("biz_bot"),
                botNode.GetAttr("type"));
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
            encPayloads,
            plaintextBytes,
            plaintextMediaType,
            serverId,
            plaintextNode is not null,
            metaTargetId,
            metaTargetSenderJid,
            metaTargetChatJid,
            botInfo);
    }

    /// <summary>
    /// Builds an unencrypted newsletter publish stanza (<c>&lt;plaintext&gt;proto&lt;/plaintext&gt;</c>),
    /// matching WA Web's <c>OutMessagePublishNewsletterRequest</c>.
    /// Adds newsletter-specific <c>contenttype</c> to poll creation <c>&lt;meta&gt;</c>.
    /// </summary>
    public static Node BuildNewsletterPlaintextStanza(
        string messageId,
        Jid channelJid,
        Message message,
        string? stanzaTypeOverride = null,
        IReadOnlyList<Node>? extraNodes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(message);
        if (!channelJid.IsNewsletter())
        {
            throw new ArgumentException($"Expected a @newsletter JID, got '{channelJid}'.", nameof(channelJid));
        }

        string stanzaType = string.IsNullOrWhiteSpace(stanzaTypeOverride)
            ? ClassifyStanzaType(message)
            : stanzaTypeOverride;

        var plaintextAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal);
        if (ClassifyMediaType(message) is { Length: > 0 } mt)
        {
            plaintextAttrs["mediatype"] = mt;
        }

        var children = new List<Node>
        {
            new("plaintext", plaintextAttrs, message.ToByteArray())
        };

        Node? meta = InferMetaNode(message);
        Message unwrapped = UnwrapClassificationMessage(message);
        Message.Types.PollCreationMessage? poll = unwrapped.PollCreationMessageV3
            ?? unwrapped.PollCreationMessage
            ?? unwrapped.PollCreationMessageV2
            ?? unwrapped.PollCreationMessageV5
            ?? unwrapped.PollCreationMessageV6;
        if (poll is not null && meta is not null)
        {
            var metaAttrs = new Dictionary<string, NodeValue>(meta.Attrs, StringComparer.Ordinal)
            {
                ["contenttype"] = poll.PollContentType == Message.Types.PollContentType.Image ? "image" : "text"
            };
            meta = new Node("meta", metaAttrs, meta.Content);
        }

        if (meta is not null && (extraNodes is null || !extraNodes.Any(n => n.Tag == "meta")))
        {
            children.Add(meta);
        }

        if (extraNodes is { Count: > 0 })
        {
            children.AddRange(extraNodes);
        }

        return new Node(
            "message",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["to"] = channelJid.ToNonAd(),
                ["type"] = stanzaType,
                ["id"] = messageId
            },
            new NodeContent.NodesContent(children));
    }

    /// <summary>
    /// Builds a plaintext newsletter edit (<c>edit="3"</c>) or revoke (<c>edit="8"</c>) stanza
    /// keyed by the target message's stanza <paramref name="messageId"/> (never <c>server_id</c>).
    /// </summary>
    public static Node BuildNewsletterEditStanza(
        Jid channelJid,
        string messageId,
        Message? replacementContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (!channelJid.IsNewsletter())
        {
            throw new ArgumentException($"Expected a @newsletter JID, got '{channelJid}'.", nameof(channelJid));
        }

        bool isRevoke = replacementContent is null;
        string editAttr = isRevoke ? RichMessageBuilders.EditAttrAdminRevoke : NewsletterStanzas.EditAttrAdminEdit;
        string stanzaType = isRevoke ? "text" : ClassifyStanzaType(replacementContent!);
        byte[] body = isRevoke ? Array.Empty<byte>() : replacementContent!.ToByteArray();

        var plaintextAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal);
        if (!isRevoke && ClassifyMediaType(replacementContent!) is { Length: > 0 } mt)
        {
            plaintextAttrs["mediatype"] = mt;
        }

        return new Node(
            "message",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["to"] = channelJid.ToNonAd(),
                ["id"] = messageId,
                ["type"] = stanzaType,
                ["edit"] = editAttr
            },
            new Node[] { new("plaintext", plaintextAttrs, body) });
    }

    public static Node BuildNewsletterServerReactionStanza(
        Jid toJid,
        string requestId,
        ulong serverId,
        string reactionCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        return new Node(
            "message",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["to"] = toJid.ToNonAd(),
                ["type"] = "reaction",
                ["id"] = requestId,
                ["server_id"] = serverId.ToString(CultureInfo.InvariantCulture)
            },
            new Node[]
            {
                new("reaction", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["code"] = reactionCode ?? string.Empty })
            });
    }

    public static Node BuildNewsletterPollVoteStanza(
        Jid channelJid,
        string requestId,
        ulong serverId,
        IReadOnlyList<byte[]> optionHashes32)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(optionHashes32);
        if (!channelJid.IsNewsletter())
        {
            throw new ArgumentException($"Expected a @newsletter JID, got '{channelJid}'.", nameof(channelJid));
        }

        var voteNodes = new Node[optionHashes32.Count];
        for (int i = 0; i < optionHashes32.Count; i++)
        {
            if (optionHashes32[i] is not { Length: 32 } h)
            {
                throw new ArgumentException($"Poll vote option hash at index {i} must be 32 bytes.", nameof(optionHashes32));
            }
            voteNodes[i] = new Node("vote", h);
        }

        return new Node(
            "message",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["to"] = channelJid.ToNonAd(),
                ["id"] = requestId,
                ["server_id"] = serverId.ToString(CultureInfo.InvariantCulture),
                ["type"] = "poll"
            },
            new Node[]
            {
                new("meta", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["polltype"] = "vote" }),
                new("votes", voteNodes)
            });
    }

    private static Jid? TryParseAttrJid(Node node, string attrName) =>
        node.GetAttr(attrName) is { Length: > 0 } s && Jid.TryParse(s, out Jid j) ? j : null;
}
