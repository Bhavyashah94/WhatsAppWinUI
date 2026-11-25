using System.Security.Cryptography;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Protocol.Binary.Stanzas;

/// <summary>
/// Rich WhatsApp <see cref="Message"/> builders matching live <c>web.whatsapp.com</c>
/// (<c>WAWebSpoilerFutureproofProtoUtils</c>, <c>WAWebCastToPinInChatMsg</c>, <c>WAWebAddonEncryption</c>,
/// <c>WAWebUpdateDraftMessageChatJob</c>) and <c>wacore/src/{proto_helpers,reaction,poll,event,message_edit}.rs</c>.
/// </summary>
public static class RichMessageBuilders
{
    public const string EditAttrMessageEdit = "1";
    public const string EditAttrPinInChat = "2";
    public const string EditAttrSenderRevoke = "7";
    public const string EditAttrAdminRevoke = "8";

    public const long MaxMessageEditWindowSeconds = 20 * 60; // 1200s verified protocol edit limit

    public static Message BuildReplyText(
        string text,
        MessageKey quotedKey,
        Message quotedMessage,
        string? quotedParticipantJid = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(quotedKey);
        ArgumentNullException.ThrowIfNull(quotedMessage);

        var ctx = new ContextInfo
        {
            StanzaId = quotedKey.Id,
            QuotedMessage = quotedMessage.Clone(),
            RemoteJid = quotedKey.RemoteJid
        };
        if (!string.IsNullOrEmpty(quotedParticipantJid))
        {
            ctx.Participant = quotedParticipantJid;
        }
        else if (quotedKey.HasParticipant && !string.IsNullOrEmpty(quotedKey.Participant))
        {
            ctx.Participant = quotedKey.Participant;
        }

        return new Message
        {
            ExtendedTextMessage = new Message.Types.ExtendedTextMessage
            {
                Text = text,
                ContextInfo = ctx
            }
        };
    }

    public static Message BuildReaction(MessageKey targetKey, string emojiText, long senderTimestampMs)
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        return new Message
        {
            ReactionMessage = new Message.Types.ReactionMessage
            {
                Key = targetKey.Clone(),
                Text = emojiText ?? string.Empty,
                SenderTimestampMs = senderTimestampMs
            }
        };
    }

    public static Message BuildEncReaction(
        MessageKey targetKey,
        string emojiText,
        long senderTimestampMs,
        ReadOnlySpan<byte> parentSecret32,
        string parentSenderJid,
        string reactorJid)
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        (byte[] encPayload, byte[] iv) = AddonEncryption.EncryptReaction(
            emojiText,
            senderTimestampMs,
            parentSecret32,
            targetKey.Id,
            parentSenderJid,
            reactorJid);

        return new Message
        {
            EncReactionMessage = new Message.Types.EncReactionMessage
            {
                TargetMessageKey = targetKey.Clone(),
                EncPayload = ByteString.CopyFrom(encPayload),
                EncIv = ByteString.CopyFrom(iv)
            }
        };
    }

    public static Message BuildLegacyMessageEdit(
        MessageKey originalKey,
        Message newEditedContent,
        long timestampMs)
    {
        ArgumentNullException.ThrowIfNull(originalKey);
        ArgumentNullException.ThrowIfNull(newEditedContent);
        return new Message
        {
            ProtocolMessage = new Message.Types.ProtocolMessage
            {
                Key = originalKey.Clone(),
                Type = Message.Types.ProtocolMessage.Types.Type.MessageEdit,
                EditedMessage = newEditedContent.Clone(),
                TimestampMs = timestampMs
            }
        };
    }

    public static Message BuildSecretMessageEdit(
        MessageKey originalKey,
        Message newEditedContent,
        long timestampMs,
        ReadOnlySpan<byte> originalSecret32,
        string originalSenderJid,
        string editorJid)
    {
        ArgumentNullException.ThrowIfNull(originalKey);
        ArgumentNullException.ThrowIfNull(newEditedContent);

        Message innerEdit = BuildLegacyMessageEdit(originalKey, newEditedContent, timestampMs);
        (byte[] encPayload, byte[] iv) = AddonEncryption.EncryptSecretMessage(
            innerEdit,
            originalSecret32,
            originalKey.Id,
            originalSenderJid,
            editorJid,
            AddonModificationType.MessageEdit);

        return new Message
        {
            SecretEncryptedMessage = new Message.Types.SecretEncryptedMessage
            {
                TargetMessageKey = originalKey.Clone(),
                EncPayload = ByteString.CopyFrom(encPayload),
                EncIv = ByteString.CopyFrom(iv),
                SecretEncType = Message.Types.SecretEncryptedMessage.Types.SecretEncType.MessageEdit
            }
        };
    }

    public static (Message Proto, string EditAttribute) BuildRevoke(MessageKey targetKey, bool isAdminRevoke = false)
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        var proto = new Message
        {
            ProtocolMessage = new Message.Types.ProtocolMessage
            {
                Key = targetKey.Clone(),
                Type = Message.Types.ProtocolMessage.Types.Type.Revoke
            }
        };
        return (proto, isAdminRevoke ? EditAttrAdminRevoke : EditAttrSenderRevoke);
    }

    public static Message BuildPinInChat(MessageKey targetKey, bool pin, long senderTimestampMs)
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        return new Message
        {
            PinInChatMessage = new Message.Types.PinInChatMessage
            {
                Key = targetKey.Clone(),
                Type = pin
                    ? Message.Types.PinInChatMessage.Types.Type.PinForAll
                    : Message.Types.PinInChatMessage.Types.Type.UnpinForAll,
                SenderTimestampMs = senderTimestampMs
            }
        };
    }

    public static Message BuildKeepInChat(MessageKey targetKey, bool keep, long timestampMs)
    {
        ArgumentNullException.ThrowIfNull(targetKey);
        return new Message
        {
            KeepInChatMessage = new Message.Types.KeepInChatMessage
            {
                Key = targetKey.Clone(),
                KeepType = keep ? KeepType.KeepForAll : KeepType.UndoKeepForAll,
                TimestampMs = timestampMs
            }
        };
    }

    public static Message BuildPollCreation(
        string question,
        IReadOnlyList<string> options,
        uint selectableOptionsCount = 1,
        int version = 3,
        string? quizCorrectOption = null,
        bool allowAddOption = false,
        long? endTimeSeconds = null,
        byte[]? messageSecret32 = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(question);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count < 2)
        {
            throw new ArgumentException("A poll requires at least 2 options.", nameof(options));
        }

        byte[] secret = messageSecret32 is { Length: 32 } ? messageSecret32 : RandomNumberGenerator.GetBytes(32);
        var poll = new Message.Types.PollCreationMessage
        {
            EncKey = ByteString.CopyFrom(secret),
            Name = question,
            SelectableOptionsCount = selectableOptionsCount,
            PollContentType = Message.Types.PollContentType.Text,
            PollType = quizCorrectOption is not null ? Message.Types.PollType.Quiz : Message.Types.PollType.Poll,
            AllowAddOption = allowAddOption
        };

        foreach (string opt in options)
        {
            poll.Options.Add(new Message.Types.PollCreationMessage.Types.Option { OptionName = opt });
        }

        if (!string.IsNullOrEmpty(quizCorrectOption))
        {
            poll.CorrectAnswer = new Message.Types.PollCreationMessage.Types.Option { OptionName = quizCorrectOption };
        }
        if (endTimeSeconds is long endTs && endTs > 0)
        {
            poll.EndTime = endTs;
        }

        var msg = new Message
        {
            MessageContextInfo = new MessageContextInfo
            {
                MessageSecret = ByteString.CopyFrom(secret)
            }
        };

        switch (version)
        {
            case 1: msg.PollCreationMessage = poll; break;
            case 2: msg.PollCreationMessageV2 = poll; break;
            case 3: msg.PollCreationMessageV3 = poll; break;
            case 5: msg.PollCreationMessageV5 = poll; break;
            case 6: msg.PollCreationMessageV6 = poll; break;
            default: msg.PollCreationMessageV3 = poll; break;
        }
        return msg;
    }

    public static Message BuildPollVote(
        MessageKey pollKey,
        IReadOnlyList<string> selectedOptionNames,
        ReadOnlySpan<byte> pollSecret32,
        string pollCreatorJid,
        string voterJid,
        long senderTimestampMs)
    {
        ArgumentNullException.ThrowIfNull(pollKey);
        ArgumentNullException.ThrowIfNull(selectedOptionNames);

        byte[][] hashes = selectedOptionNames.Select(AddonEncryption.HashPollOption).ToArray();
        (byte[] encPayload, byte[] iv) = AddonEncryption.EncryptPollVote(
            hashes,
            pollSecret32,
            pollKey.Id,
            pollCreatorJid,
            voterJid);

        return new Message
        {
            PollUpdateMessage = new Message.Types.PollUpdateMessage
            {
                PollCreationMessageKey = pollKey.Clone(),
                Vote = new Message.Types.PollEncValue
                {
                    EncPayload = ByteString.CopyFrom(encPayload),
                    EncIv = ByteString.CopyFrom(iv)
                },
                SenderTimestampMs = senderTimestampMs
            }
        };
    }

    public static Message BuildPollAddOption(
        MessageKey pollKey,
        string newOptionName,
        ReadOnlySpan<byte> pollSecret32,
        string pollCreatorJid,
        string adderJid)
    {
        ArgumentNullException.ThrowIfNull(pollKey);
        ArgumentException.ThrowIfNullOrEmpty(newOptionName);

        var inner = new Message
        {
            PollAddOptionMessage = new Message.Types.PollAddOptionMessage
            {
                PollCreationMessageKey = pollKey.Clone(),
                AddOption = new Message.Types.PollCreationMessage.Types.Option { OptionName = newOptionName }
            }
        };

        (byte[] encPayload, byte[] iv) = AddonEncryption.EncryptSecretMessage(
            inner,
            pollSecret32,
            pollKey.Id,
            pollCreatorJid,
            adderJid,
            AddonModificationType.PollAddOption);

        return new Message
        {
            SecretEncryptedMessage = new Message.Types.SecretEncryptedMessage
            {
                TargetMessageKey = pollKey.Clone(),
                EncPayload = ByteString.CopyFrom(encPayload),
                EncIv = ByteString.CopyFrom(iv),
                SecretEncType = Message.Types.SecretEncryptedMessage.Types.SecretEncType.PollAddOption
            }
        };
    }

    public static Message BuildEvent(
        string name,
        string? description,
        long startTimeSeconds,
        long? endTimeSeconds = null,
        string? locationName = null,
        string? joinLink = null,
        bool isScheduleCall = false,
        bool extraGuestsAllowed = true,
        bool isCanceled = false,
        byte[]? messageSecret32 = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        byte[] secret = messageSecret32 is { Length: 32 } ? messageSecret32 : RandomNumberGenerator.GetBytes(32);

        var evt = new Message.Types.EventMessage
        {
            Name = name,
            StartTime = startTimeSeconds,
            ExtraGuestsAllowed = extraGuestsAllowed,
            IsScheduleCall = isScheduleCall,
            IsCanceled = isCanceled
        };
        if (!string.IsNullOrEmpty(description)) evt.Description = description;
        if (endTimeSeconds is long et && et > 0) evt.EndTime = et;
        if (!string.IsNullOrEmpty(joinLink)) evt.JoinLink = joinLink;
        if (!string.IsNullOrEmpty(locationName))
        {
            evt.Location = new Message.Types.LocationMessage { Name = locationName };
        }

        return new Message
        {
            EventMessage = evt,
            MessageContextInfo = new MessageContextInfo
            {
                MessageSecret = ByteString.CopyFrom(secret)
            }
        };
    }

    public static Message BuildEventResponse(
        MessageKey eventKey,
        Message.Types.EventResponseMessage.Types.EventResponseType responseType,
        long timestampMs,
        ReadOnlySpan<byte> eventSecret32,
        string eventCreatorJid,
        string responderJid,
        int extraGuestCount = 0)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        var respProto = new Message.Types.EventResponseMessage
        {
            Response = responseType,
            TimestampMs = timestampMs,
            ExtraGuestCount = extraGuestCount
        };

        (byte[] encPayload, byte[] iv) = AddonEncryption.EncryptEventResponse(
            respProto,
            eventSecret32,
            eventKey.Id,
            eventCreatorJid,
            responderJid);

        return new Message
        {
            EncEventResponseMessage = new Message.Types.EncEventResponseMessage
            {
                EventCreationMessageKey = eventKey.Clone(),
                EncPayload = ByteString.CopyFrom(encPayload),
                EncIv = ByteString.CopyFrom(iv)
            }
        };
    }

    public static Message BuildLocation(double latitude, double longitude, string? name = null, string? address = null, string? url = null)
    {
        var loc = new Message.Types.LocationMessage
        {
            DegreesLatitude = latitude,
            DegreesLongitude = longitude
        };
        if (!string.IsNullOrEmpty(name)) loc.Name = name;
        if (!string.IsNullOrEmpty(address)) loc.Address = address;
        if (!string.IsNullOrEmpty(url)) loc.Url = url;
        return new Message { LocationMessage = loc };
    }

    public static Message BuildLiveLocation(double latitude, double longitude, string? caption = null, uint? accuracyMeters = null, long sequenceNumber = 1)
    {
        var live = new Message.Types.LiveLocationMessage
        {
            DegreesLatitude = latitude,
            DegreesLongitude = longitude,
            SequenceNumber = sequenceNumber
        };
        if (!string.IsNullOrEmpty(caption)) live.Caption = caption;
        if (accuracyMeters is uint acc) live.AccuracyInMeters = acc;
        return new Message { LiveLocationMessage = live };
    }

    public static Message BuildAlbum(uint expectedImageCount, uint expectedVideoCount) =>
        new()
        {
            AlbumMessage = new Message.Types.AlbumMessage
            {
                ExpectedImageCount = expectedImageCount,
                ExpectedVideoCount = expectedVideoCount
            }
        };

    public static Message WrapEphemeral(Message inner, uint expirationSeconds)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Message clone = inner.Clone();
        if (expirationSeconds > 0 && clone.MessageContextInfo is null)
        {
            clone.MessageContextInfo = new MessageContextInfo();
        }
        return new Message
        {
            EphemeralMessage = new Message.Types.FutureProofMessage { Message = clone }
        };
    }

    public static Message WrapViewOnce(Message inner, bool v2 = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var wrapped = new Message.Types.FutureProofMessage { Message = inner.Clone() };
        return v2
            ? new Message { ViewOnceMessageV2 = wrapped }
            : new Message { ViewOnceMessage = wrapped };
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> inside <c>spoilerMessage</c> matching live WA Web
    /// <c>WAWebSpoilerFutureproofProtoUtils.createSpoilerFutureproofMessage</c>.
    /// </summary>
    public static Message WrapSpoiler(Message inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        Message target = inner.DocumentWithCaptionMessage?.Message is { } dwc ? dwc.Clone() : inner.Clone();
        return new Message
        {
            SpoilerMessage = new Message.Types.FutureProofMessage { Message = target }
        };
    }

    public static Message BuildDraftMessageSync(
        Jid chatJid,
        string? draftText,
        long editTimestampMs,
        DraftMessageSync.Types.ClearReason? clearReason = null)
    {
        bool isSet = !string.IsNullOrEmpty(draftText) && clearReason is null;
        var sync = new DraftMessageSync
        {
            ChatJid = chatJid.ToNonAdString(),
            Action = isSet ? DraftMessageSync.Types.Action.Set : DraftMessageSync.Types.Action.Clear,
            EditTimestampMs = editTimestampMs
        };

        if (isSet)
        {
            sync.Draft = new Message { Conversation = draftText! };
        }
        else if (clearReason is { } cr)
        {
            sync.ClearReason = cr;
        }

        return new Message
        {
            ProtocolMessage = new Message.Types.ProtocolMessage
            {
                Type = Message.Types.ProtocolMessage.Types.Type.DraftMessageSync,
                DraftMessageSync = sync
            }
        };
    }

    public static Message? TryGetPollCreationMessage(Message message) =>
        message.PollCreationMessage is not null
        || message.PollCreationMessageV2 is not null
        || message.PollCreationMessageV3 is not null
        || message.PollCreationMessageV5 is not null
        || message.PollCreationMessageV6 is not null
            ? message
            : null;
}
