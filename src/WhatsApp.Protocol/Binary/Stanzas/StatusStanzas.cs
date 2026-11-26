using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum StatusPrivacySetting
{
    Contacts = 0,
    AllowList = 1,
    DenyList = 2
}

public sealed record StatusSendOptions(
    StatusPrivacySetting Privacy = StatusPrivacySetting.Contacts,
    string? MessageIdOverride = null,
    string? StanzaIdOverride = null,
    string? AddressingMode = "lid");

/// <summary>
/// Pure protocol builders and stanza helpers for WhatsApp Statuses / Stories
/// (<c>status@broadcast</c>, <c>GroupStatusMessageV2</c>, text/media/music statuses,
/// sticker interactions, Q&amp;A, revokes, and <c>&lt;meta status_setting&gt;</c> rules)
/// matching <c>wacore/src/send/status.rs</c> and live <c>web.whatsapp.com</c>.
/// </summary>
public static class StatusStanzas
{
    public static Jid StatusBroadcastJid => new("status", Server.Broadcast);

    public static string ToWireString(this StatusPrivacySetting setting) => setting switch
    {
        StatusPrivacySetting.AllowList => "allowlist",
        StatusPrivacySetting.DenyList => "denylist",
        _ => "contacts"
    };

    public static bool StatusCarriesPrivacyMeta(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Message inner = MessageStanzas.UnwrapClassificationMessage(message);
        bool isRevoke = inner.ProtocolMessage?.Type == Message.Types.ProtocolMessage.Types.Type.Revoke;
        bool isReaction = inner.ReactionMessage is not null || inner.EncReactionMessage is not null;
        return !isRevoke && !isReaction;
    }

    public static string? GetStatusRevokeTargetId(Message message)
    {
        Message inner = MessageStanzas.UnwrapClassificationMessage(message);
        if (inner.ProtocolMessage?.Type != Message.Types.ProtocolMessage.Types.Type.Revoke)
        {
            return null;
        }
        return inner.ProtocolMessage.Key?.Id;
    }

    /// <summary>
    /// Deduplicates resolved user JIDs and guarantees the sender's own JID is included.
    /// </summary>
    public static IReadOnlyList<Jid> AssembleStatusParticipants(IEnumerable<Jid> recipients, Jid ownBare)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        var result = new List<Jid>();
        var seenUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Jid r in recipients)
        {
            if (!(r.IsPn() || r.IsLid()))
            {
                throw new ArgumentException($"Status recipient '{r}' must be a user JID (PN or LID).");
            }
            Jid bare = r.ToNonAd();
            if (seenUsers.Add(bare.User))
            {
                result.Add(bare);
            }
        }

        if (result.Count == 0)
        {
            throw new ArgumentException("At least one valid user recipient is required for status@broadcast.", nameof(recipients));
        }

        Jid ownNormalized = ownBare.ToNonAd();
        if (seenUsers.Add(ownNormalized.User))
        {
            result.Add(ownNormalized);
        }

        return result;
    }

    /// <summary>
    /// Builds a <c>&lt;message to="status@broadcast"&gt;</c> stanza combining SKDM encrypted
    /// <c>&lt;to jid="user:dev@..."&gt;&lt;enc .../&gt;&lt;/to&gt;</c> nodes with bare user
    /// <c>&lt;to jid="user@..."/&gt;</c> entries in <c>&lt;participants&gt;</c>, plus
    /// <c>&lt;meta status_setting="..."/&gt;</c> when permitted.
    /// </summary>
    public static Node BuildStatusBroadcastStanza(
        string messageId,
        IReadOnlyList<Jid> audienceUsers,
        Message message,
        byte[] skmsgCiphertext,
        IReadOnlyList<(Jid DeviceJid, string EncType, byte[] SkdmCiphertext)>? skdmTargets = null,
        StatusPrivacySetting privacy = StatusPrivacySetting.Contacts,
        string? addressingMode = "lid",
        byte[]? deviceIdentityBytes = null,
        ReportingTokenResult? reportingToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(audienceUsers);
        ArgumentNullException.ThrowIfNull(message);

        string stanzaType = MessageStanzas.ClassifyStanzaType(message);
        string? mediaType = MessageStanzas.ClassifyMediaType(message);
        bool hideDecryptFail = MessageStanzas.ShouldHideDecryptFail(message);

        var participantNodes = new List<Node>();
        var usersCoveredBySkdm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (skdmTargets is { Count: > 0 })
        {
            foreach (var (devJid, encType, skdmCt) in skdmTargets)
            {
                usersCoveredBySkdm.Add(devJid.User);
                participantNodes.Add(new Node(
                    "to",
                    new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = devJid },
                    new[] { MessageStanzas.BuildEncryptedEncNode(encType, skdmCt, mediaType: null, hideDecryptFail) }));
            }
        }

        foreach (Jid user in audienceUsers)
        {
            Jid bare = user.ToNonAd();
            if (usersCoveredBySkdm.Add(bare.User))
            {
                participantNodes.Add(new Node("to", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = bare }));
            }
        }

        var children = new List<Node>
        {
            new("participants", participantNodes),
            MessageStanzas.BuildEncryptedEncNode("skmsg", skmsgCiphertext, mediaType, hideDecryptFail)
        };

        if (StatusCarriesPrivacyMeta(message))
        {
            children.Add(new Node("meta", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["status_setting"] = privacy.ToWireString()
            }));
        }

        if (deviceIdentityBytes is { Length: > 0 })
        {
            children.Add(new Node("device-identity", deviceIdentityBytes));
        }

        if (reportingToken is not null)
        {
            children.Add(MessageStanzas.BuildReportingNode(reportingToken));
        }

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = messageId,
            ["to"] = StatusBroadcastJid,
            ["type"] = stanzaType
        };
        if (!string.IsNullOrEmpty(addressingMode))
        {
            attrs["addressing_mode"] = addressingMode;
        }

        return new Node("message", attrs, new NodeContent.NodesContent(children));
    }

    public static Message BuildTextStatus(
        string text,
        uint backgroundArgb = 0xFF1E6E4F,
        Message.Types.ExtendedTextMessage.Types.FontType font = Message.Types.ExtendedTextMessage.Types.FontType.System)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new Message
        {
            ExtendedTextMessage = new Message.Types.ExtendedTextMessage
            {
                Text = text,
                BackgroundArgb = backgroundArgb,
                Font = font
            }
        };
    }

    public static Message BuildImageStatus(
        string directPath,
        string mimetype,
        byte[] fileSha256,
        byte[] fileEncSha256,
        byte[] mediaKey32,
        ulong fileLength,
        long mediaKeyTimestampSeconds,
        byte[]? jpegThumbnail = null,
        string? caption = null)
    {
        var img = new Message.Types.ImageMessage
        {
            DirectPath = directPath,
            Mimetype = mimetype,
            FileSha256 = ByteString.CopyFrom(fileSha256),
            FileEncSha256 = ByteString.CopyFrom(fileEncSha256),
            MediaKey = ByteString.CopyFrom(mediaKey32),
            FileLength = fileLength,
            MediaKeyTimestamp = mediaKeyTimestampSeconds
        };
        if (jpegThumbnail is { Length: > 0 }) img.JpegThumbnail = ByteString.CopyFrom(jpegThumbnail);
        if (!string.IsNullOrEmpty(caption)) img.Caption = caption;
        return new Message { ImageMessage = img };
    }

    public static Message BuildVideoStatus(
        string directPath,
        string mimetype,
        byte[] fileSha256,
        byte[] fileEncSha256,
        byte[] mediaKey32,
        ulong fileLength,
        long mediaKeyTimestampSeconds,
        uint durationSeconds,
        byte[]? jpegThumbnail = null,
        string? caption = null)
    {
        var vid = new Message.Types.VideoMessage
        {
            DirectPath = directPath,
            Mimetype = mimetype,
            FileSha256 = ByteString.CopyFrom(fileSha256),
            FileEncSha256 = ByteString.CopyFrom(fileEncSha256),
            MediaKey = ByteString.CopyFrom(mediaKey32),
            FileLength = fileLength,
            MediaKeyTimestamp = mediaKeyTimestampSeconds,
            Seconds = durationSeconds
        };
        if (jpegThumbnail is { Length: > 0 }) vid.JpegThumbnail = ByteString.CopyFrom(jpegThumbnail);
        if (!string.IsNullOrEmpty(caption)) vid.Caption = caption;
        return new Message { VideoMessage = vid };
    }

    public static Message BuildMusicStatus(
        string songTitle,
        string artistName,
        string? songUri = null,
        string? artworkUri = null,
        int style = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(songTitle);
        var music = new Message.Types.MusicMessage
        {
            Style = style,
            EmbeddedMusic = new EmbeddedMusic
            {
                Title = songTitle,
                Author = artistName
            }
        };
        if (!string.IsNullOrEmpty(songUri)) music.SongUri = songUri;
        if (!string.IsNullOrEmpty(artworkUri)) music.ArtworkUri = artworkUri;
        return new Message { MusicMessage = music };
    }

    public static Message BuildGroupStatusV2(Message innerMessage)
    {
        ArgumentNullException.ThrowIfNull(innerMessage);
        return new Message
        {
            GroupStatusMessageV2 = new Message.Types.FutureProofMessage
            {
                Message = innerMessage.Clone()
            }
        };
    }

    public static Message BuildStatusRevoke(string targetMessageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMessageId);
        return new Message
        {
            ProtocolMessage = new Message.Types.ProtocolMessage
            {
                Type = Message.Types.ProtocolMessage.Types.Type.Revoke,
                Key = new MessageKey
                {
                    RemoteJid = StatusBroadcastJid.ToString(),
                    FromMe = true,
                    Id = targetMessageId
                }
            }
        };
    }

    public static Message BuildStatusQuestionAnswer(MessageKey statusKey, string answerText)
    {
        ArgumentNullException.ThrowIfNull(statusKey);
        ArgumentException.ThrowIfNullOrEmpty(answerText);
        return new Message
        {
            StatusQuestionAnswerMessage = new Message.Types.StatusQuestionAnswerMessage
            {
                Key = statusKey.Clone(),
                Text = answerText
            }
        };
    }

    public static Message BuildStatusStickerInteraction(
        MessageKey statusKey,
        string stickerKey,
        Message.Types.StatusStickerInteractionMessage.Types.StatusStickerType stickerType)
    {
        ArgumentNullException.ThrowIfNull(statusKey);
        ArgumentException.ThrowIfNullOrEmpty(stickerKey);
        return new Message
        {
            StatusStickerInteractionMessage = new Message.Types.StatusStickerInteractionMessage
            {
                Key = statusKey.Clone(),
                StickerKey = stickerKey,
                Type = stickerType
            }
        };
    }
}
