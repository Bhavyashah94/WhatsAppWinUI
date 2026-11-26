using System.Security.Cryptography;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Api;

/// <summary>
/// Handles inbound encrypted <c>&lt;message&gt;</c> stanzas (<c>pkmsg</c>/<c>msg</c>/<c>skmsg</c>),
/// <c>SKDM</c> extraction, <c>DeviceSentMessage</c> unwrapping, <c>IMsgSecretStore</c> &amp; history persistence,
/// delivery/read receipts, automatic Signal retry requests on decryption failure, and
/// MediaRetry (<c>server-error</c> / <c>mediaretry</c>) recovery.
/// </summary>
public sealed class MessageReceiver
{
    private readonly NoiseSessionSocket _socket;
    private readonly IWhatsAppBackend _backend;
    private readonly ILidPnCache _lidPnCache;
    private readonly DeviceCredentials _credentials;
    private readonly RetryCoordinator _retryCoordinator;
    private readonly HistorySyncCoordinator? _historySync;
    private readonly MediaTransferClient? _mediaTransfer;
    private readonly Func<WhatsAppEvent, ValueTask> _emitEvent;
    private readonly TimeProvider _timeProvider;

    public MessageReceiver(
        NoiseSessionSocket socket,
        IWhatsAppBackend backend,
        ILidPnCache lidPnCache,
        DeviceCredentials credentials,
        RetryCoordinator retryCoordinator,
        Func<WhatsAppEvent, ValueTask> emitEvent,
        TimeProvider? timeProvider = null,
        HistorySyncCoordinator? historySync = null,
        MediaTransferClient? mediaTransfer = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _retryCoordinator = retryCoordinator ?? throw new ArgumentNullException(nameof(retryCoordinator));
        _historySync = historySync;
        _mediaTransfer = mediaTransfer;
        _emitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task HandleMessageStanzaAsync(Node stanza, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stanza);
        IncomingMessageStanza? parsed = MessageStanzas.ParseIncomingMessage(stanza, _timeProvider);
        if (parsed is null)
        {
            return;
        }

        await LearnSenderMappingsAsync(parsed, ct).ConfigureAwait(false);

        if (parsed.From.IsNewsletter())
        {
            await HandleNewsletterPlaintextAsync(parsed, ct).ConfigureAwait(false);
            return;
        }

        if (parsed.EncPayloads.Count == 0)
        {
            await SendAckOrDeliveryReceiptAsync(parsed, hideDelivery: false, ct).ConfigureAwait(false);
            return;
        }

        // Order pkmsg/msg BEFORE skmsg so any SenderKeyDistributionMessage carried inside the
        // 1:1 envelope is registered before we decrypt the companion skmsg in the same stanza.
        var ordered = parsed.EncPayloads
            .OrderBy(e => e.EncType == "skmsg" ? 1 : 0)
            .ToArray();

        Jid senderDev = parsed.SenderDeviceJid;
        string senderAddr = senderDev.ToProtocolAddress();
        string chatBare = parsed.ChatJid.ToNonAdString();

        bool anySaved = false;
        Exception? lastFailure = null;
        EncryptedMessagePayload? failedEnc = null;

        foreach (EncryptedMessagePayload enc in ordered)
        {
            try
            {
                Message message;
                if (string.Equals(enc.EncType, "msmsg", StringComparison.Ordinal))
                {
                    // Bot msmsg envelopes are AES-256-GCM over unpadded protobuf Message bytes (no PKCS7 padding).
                    byte[] rawProto = await DecryptMsmsgAsync(parsed, enc, senderDev, ct).ConfigureAwait(false);
                    message = Message.Parser.ParseFrom(rawProto);
                }
                else
                {
                    byte[] padded = await DecryptEncPayloadAsync(enc, chatBare, senderDev, senderAddr, ct).ConfigureAwait(false);
                    message = MessageFraming.DecodeAndUnpadMessage(padded);
                }

                bool fromMe = IsOwnUser(senderDev.User);
                string effectiveChat = chatBare;

                if (message.DeviceSentMessage is { } dsm)
                {
                    if (!string.IsNullOrEmpty(dsm.DestinationJid) && Jid.TryParse(dsm.DestinationJid, out Jid destJid))
                    {
                        effectiveChat = destJid.ToNonAdString();
                    }
                    fromMe = true;
                    if (dsm.Message is { } inner)
                    {
                        message = inner;
                    }
                }

                await ProcessSideEffectsAndPersistAsync(parsed, message, effectiveChat, senderDev.ToNonAdString(), fromMe, ct)
                    .ConfigureAwait(false);

                _retryCoordinator.ClearInboundRetry(effectiveChat, parsed.Id);
                anySaved = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex;
                failedEnc = enc;
            }
        }

        if (anySaved)
        {
            await SendAckOrDeliveryReceiptAsync(parsed, hideDelivery: false, ct).ConfigureAwait(false);
        }
        else if (lastFailure is not null)
        {
            await HandleDecryptFailureAsync(parsed, failedEnc!, lastFailure, ct).ConfigureAwait(false);
        }
    }

    public async Task<bool> HandleReceiptStanzaAsync(Node stanza, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stanza);
        ParsedReceiptStanza? parsed = RetryAndMediaRetryStanzas.ParseIncomingReceipt(stanza, _timeProvider);
        if (parsed is null)
        {
            return false;
        }

        if (string.Equals(parsed.Type, ReceiptStanza.TypeRetry, StringComparison.Ordinal))
        {
            bool handled = await _retryCoordinator.HandleRetryReceiptAsync(parsed, ct).ConfigureAwait(false);
            await _socket.SendNodeAsync(
                AckStanza.Build(parsed.MessageId, "receipt", parsed.From, ReceiptStanza.TypeRetry, parsed.Participant),
                ct).ConfigureAwait(false);
            return handled;
        }

        // Ack non-retry receipts and dispatch typed event
        await _socket.SendNodeAsync(
            AckStanza.Build(parsed.MessageId, "receipt", parsed.From, parsed.Type == "delivery" ? null : parsed.Type, parsed.Participant),
            ct).ConfigureAwait(false);

        var ids = new List<string>(1 + parsed.AdditionalMessageIds.Count) { parsed.MessageId };
        ids.AddRange(parsed.AdditionalMessageIds);

        string sender = (parsed.Participant ?? parsed.From).ToNonAdString();
        await _emitEvent(new ReceiptReceivedEvent(parsed.From.ToNonAdString(), sender, ids, parsed.Type)).ConfigureAwait(false);
        return true;
    }

    public async Task SendReadReceiptAsync(
        string messageId,
        Jid chatJid,
        bool isSelf = false,
        bool played = false,
        Jid? participant = null,
        IReadOnlyList<string>? additionalIds = null,
        CancellationToken ct = default)
    {
        string type = played
            ? (isSelf ? ReceiptStanza.TypePlayedSelf : ReceiptStanza.TypePlayed)
            : (isSelf ? ReceiptStanza.TypeReadSelf : ReceiptStanza.TypeRead);

        Node receipt = ReceiptStanza.Build(messageId, chatJid.ToNonAd(), type, participant, additionalMessageIds: additionalIds);
        await _socket.SendNodeAsync(receipt, ct).ConfigureAwait(false);
    }

    public async Task RequestMediaRetryAsync(
        string messageId,
        Jid chatJid,
        bool fromMe,
        byte[] mediaKey32,
        Jid? participant = null,
        CancellationToken ct = default)
    {
        (byte[] encPayload, byte[] iv12) = MediaRetryCrypto.EncryptServerErrorReceipt(mediaKey32, messageId);
        Node receipt = RetryAndMediaRetryStanzas.BuildMediaServerErrorReceipt(
            messageId,
            chatJid,
            fromMe,
            encPayload,
            iv12,
            participant);
        await _socket.SendNodeAsync(receipt, ct).ConfigureAwait(false);
    }

    public async Task<MediaRetryNotificationEvent?> HandleMediaRetryNotificationAsync(
        Node notification,
        byte[]? mediaKey32 = null,
        CancellationToken ct = default)
    {
        ParsedMediaRetryNotification? parsed = RetryAndMediaRetryStanzas.ParseMediaRetryNotification(notification);
        if (parsed is null)
        {
            return null;
        }

        string statusText = parsed.ErrorCode is int code ? $"error:{code}" : "ok";
        string? newDirectPath = null;

        if (mediaKey32 is null && parsed.RmrJid is not null)
        {
            IReadOnlyList<MessageRecord> recent = await _backend.History.GetMessagesAsync(parsed.RmrJid.Value.ToNonAdString(), null, 50, ct)
                .ConfigureAwait(false);
            mediaKey32 = recent.FirstOrDefault(m => string.Equals(m.Id, parsed.StanzaId, StringComparison.Ordinal))?.MediaKey;
        }

        if (mediaKey32 is { Length: 32 } key && parsed.EncPayload.Length > 0 && parsed.EncIv12.Length == 12)
        {
            try
            {
                MediaRetryDecryptedResult decrypted = MediaRetryCrypto.DecryptMediaRetryNotification(
                    key,
                    parsed.StanzaId,
                    parsed.EncIv12,
                    parsed.EncPayload);
                newDirectPath = decrypted.DirectPath;
                statusText = decrypted.Status switch
                {
                    MediaRetryStatus.Success => "ok",
                    MediaRetryStatus.NotFound => "not-found",
                    MediaRetryStatus.DecryptionError => "decryption-error",
                    _ => statusText
                };
            }
            catch (CryptographicException)
            {
                statusText = "decrypt-failed";
            }
        }

        var evt = new MediaRetryNotificationEvent(parsed.StanzaId, statusText, newDirectPath);
        await _emitEvent(evt).ConfigureAwait(false);
        return evt;
    }

    private async Task<byte[]> DecryptEncPayloadAsync(
        EncryptedMessagePayload enc,
        string chatBare,
        Jid senderDev,
        string senderAddr,
        CancellationToken ct)
    {
        switch (enc.EncType)
        {
            case "pkmsg":
            {
                WhatsApp.Protocol.Signal.PreKeySignalMessage pkmsg = WhatsApp.Protocol.Signal.PreKeySignalMessage.Parse(enc.Ciphertext);
                SignalSession? existing = await _backend.Signal.GetSessionAsync(senderAddr, ct).ConfigureAwait(false);
                bool matchesExistingBaseKey = existing is not null
                    && existing.AliceBaseKey is { Length: > 0 } baseKey
                    && baseKey.AsSpan().SequenceEqual(pkmsg.BaseKey)
                    && existing.RemoteIdentity.AsSpan().SequenceEqual(pkmsg.IdentityKey);

                KeyPair signedPreKey = await ResolveSignedPreKeyAsync(pkmsg.SignedPreKeyId, ct).ConfigureAwait(false);
                KeyPair? oneTimePreKey = matchesExistingBaseKey
                    ? null
                    : await ResolveOneTimePreKeyAsync(pkmsg.PreKeyId, ct).ConfigureAwait(false);

                (byte[] plaintext, SignalSession updated) = SignalCipher.DecryptPreKey(
                    pkmsg,
                    _credentials.IdentityKey,
                    signedPreKey,
                    oneTimePreKey,
                    existing);

                await _backend.Signal.SaveIdentityAsync(senderAddr, pkmsg.IdentityKey, ct).ConfigureAwait(false);
                await _backend.Signal.SaveSessionAsync(senderAddr, updated, ct).ConfigureAwait(false);
                if (!matchesExistingBaseKey && pkmsg.PreKeyId is uint usedId)
                {
                    await _backend.Signal.DeletePreKeyAsync(usedId, ct).ConfigureAwait(false);
                }
                return plaintext;
            }

            case "msg":
            {
                SignalSession existing = await _backend.Signal.GetSessionAsync(senderAddr, ct).ConfigureAwait(false)
                    ?? throw new CryptographicException($"No Signal session established for {senderAddr}.");

                WhatsApp.Protocol.Signal.SignalMessage msg = WhatsApp.Protocol.Signal.SignalMessage.Parse(enc.Ciphertext);
                byte[] plaintext = SignalCipher.DecryptSignal(msg, existing, _credentials.IdentityKey);
                await _backend.Signal.SaveSessionAsync(senderAddr, existing, ct).ConfigureAwait(false);
                return plaintext;
            }

            case "skmsg":
            {
                byte[] recordBytes = await _backend.Signal.GetSenderKeyAsync(chatBare, senderAddr, ct).ConfigureAwait(false)
                    ?? throw new CryptographicException($"Missing SenderKey for {senderAddr} in {chatBare}.");

                SenderKeyRecord record = SignalStateCodec.DeserializeSenderKeyRecord(recordBytes);
                byte[] plaintext = SenderKeyCipher.Decrypt(record, enc.Ciphertext);
                await _backend.Signal.SaveSenderKeyAsync(chatBare, senderAddr, SignalStateCodec.SerializeSenderKeyRecord(record), ct)
                    .ConfigureAwait(false);
                return plaintext;
            }

            default:
                throw new NotSupportedException($"Unsupported Signal enc type '{enc.EncType}'.");
        }
    }

    private async Task<byte[]> DecryptMsmsgAsync(
        IncomingMessageStanza parsed,
        EncryptedMessagePayload enc,
        Jid senderDev,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(parsed.MetaTargetId))
        {
            throw new CryptographicException($"msmsg stanza '{parsed.Id}' missing <meta target_id>.");
        }

        (byte[] Secret, long? Timestamp)? stored = await _backend.MsgSecrets.GetMsgSecretAsync(parsed.MetaTargetId, ct).ConfigureAwait(false);
        if (stored is not { Secret.Length: 32 } entry)
        {
            throw new CryptographicException($"No stored messageSecret for msmsg target_id '{parsed.MetaTargetId}'.");
        }

        var senderCandidates = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddCandidate(string? s)
        {
            if (!string.IsNullOrWhiteSpace(s) && seen.Add(s))
            {
                senderCandidates.Add(s);
            }
        }

        if (parsed.MetaTargetSenderJid is Jid ts)
        {
            foreach (string cand in BuildJidCandidates(ts.ToNonAdString(), includeOwn: true))
            {
                AddCandidate(cand);
            }
        }
        else if (parsed.From.Server == Server.Bot || senderDev.IsFbidBot())
        {
            AddCandidate(_credentials.Lid?.ToNonAdString());
            AddCandidate(_credentials.Pn?.ToNonAdString());
        }
        else
        {
            AddCandidate(_credentials.Pn?.ToNonAdString());
            AddCandidate(_credentials.Lid?.ToNonAdString());
        }

        if (senderCandidates.Count == 0)
        {
            throw new CryptographicException($"Unable to resolve target_sender for msmsg stanza '{parsed.Id}'.");
        }

        string botUserJid = senderDev.ToNonAdString();
        bool preferEditTarget = parsed.BotInfo?.EditType is BotEditType.Inner or BotEditType.Last
            && !string.IsNullOrEmpty(parsed.BotInfo.EditTargetId);
        string primaryMsgId = preferEditTarget ? parsed.BotInfo!.EditTargetId! : parsed.Id;
        string? fallbackMsgId = preferEditTarget ? parsed.Id : parsed.BotInfo?.EditTargetId;
        if (string.Equals(fallbackMsgId, primaryMsgId, StringComparison.Ordinal))
        {
            fallbackMsgId = null;
        }

        CryptographicException? lastAuthFailure = null;
        foreach (string targetSenderUser in senderCandidates)
        {
            var primaryCtx = new BotMessageContext(primaryMsgId, targetSenderUser, botUserJid);
            BotMessageContext? fallbackCtx = fallbackMsgId is not null
                ? new BotMessageContext(fallbackMsgId, targetSenderUser, botUserJid)
                : null;
            try
            {
                return BotMessageCrypto.DecryptMsmsgEnvelope(enc.Ciphertext, entry.Secret, primaryCtx, fallbackCtx);
            }
            catch (CryptographicException ex)
            {
                lastAuthFailure = ex;
            }
        }

        throw lastAuthFailure ?? new CryptographicException($"msmsg decryption failed for stanza '{parsed.Id}'.");
    }

    private async Task ProcessSideEffectsAndPersistAsync(
        IncomingMessageStanza parsed,
        Message message,
        string chatBare,
        string senderBare,
        bool fromMe,
        CancellationToken ct)
    {
        if (message.SenderKeyDistributionMessage is { } skdm
            && !string.IsNullOrEmpty(skdm.GroupId)
            && skdm.AxolotlSenderKeyDistributionMessage is { Length: > 0 } skdmBytes)
        {
            string senderAddr = parsed.SenderDeviceJid.ToProtocolAddress();
            byte[]? existingBytes = await _backend.Signal.GetSenderKeyAsync(skdm.GroupId, senderAddr, ct).ConfigureAwait(false);
            SenderKeyRecord record = existingBytes is { Length: > 0 }
                ? SignalStateCodec.DeserializeSenderKeyRecord(existingBytes)
                : new SenderKeyRecord();

            SenderKeyCipher.ProcessDistribution(record, skdmBytes.ToByteArray());
            await _backend.Signal.SaveSenderKeyAsync(
                skdm.GroupId,
                senderAddr,
                SignalStateCodec.SerializeSenderKeyRecord(record),
                ct).ConfigureAwait(false);
        }

        if (message.MessageContextInfo?.MessageSecret is { Length: 32 } secret)
        {
            await _backend.MsgSecrets.PutMsgSecretsAsync(
                [(parsed.Id, secret.ToByteArray(), parsed.TimestampSeconds)],
                ct).ConfigureAwait(false);
        }
        else if (TryGetPollEncKey32(message) is { Length: 32 } pollEncKey)
        {
            await _backend.MsgSecrets.PutMsgSecretsAsync(
                [(parsed.Id, pollEncKey, parsed.TimestampSeconds)],
                ct).ConfigureAwait(false);
        }

        // Unwrap ProtocolMessage -> PeerDataOperationRequestResponseMessage PDO placeholder responses
        if (message.ProtocolMessage?.PeerDataOperationRequestResponseMessage is { } pdoResp)
        {
            foreach (var item in pdoResp.PeerDataOperationResult)
            {
                if (item.PlaceholderMessageResendResponse?.WebMessageInfoBytes is { Length: > 0 } wmiBytes)
                {
                    WebMessageInfo recovered = WebMessageInfo.Parser.ParseFrom(wmiBytes);
                    if (recovered.Key is not null && recovered.Message is not null)
                    {
                        string recId = recovered.Key.Id ?? parsed.Id;
                        string recChat = recovered.Key.RemoteJid ?? chatBare;
                        string? recSender = recovered.Key.Participant ?? recovered.Participant;
                        ulong recTs = recovered.MessageTimestamp != 0 ? recovered.MessageTimestamp : (ulong)parsed.TimestampSeconds;
                        MessageRecord recRecord = ToMessageRecord(recId, recChat, recSender, recovered.Key.FromMe, recTs, recovered.Message);
                        await _backend.History.SaveMessageAsync(recRecord, ct).ConfigureAwait(false);
                        await _emitEvent(new MessageReceivedEvent(recRecord)).ConfigureAwait(false);
                    }
                }
            }
        }

        await ProcessRichAddOnsAsync(parsed, message, chatBare, senderBare, ct).ConfigureAwait(false);

        if (!IsPureSkdmWrapper(message))
        {
            MessageRecord record = ToMessageRecord(parsed.Id, chatBare, senderBare, fromMe, (ulong)parsed.TimestampSeconds, message);
            await _backend.History.SaveMessageAsync(record, ct).ConfigureAwait(false);
            if (message.GroupStatusMessageV2 is not null)
            {
                await _emitEvent(new GroupStatusReceivedEvent(chatBare, senderBare, parsed.Id, record, parsed.TimestampSeconds)).ConfigureAwait(false);
            }
            await _emitEvent(new MessageReceivedEvent(record)).ConfigureAwait(false);
        }
    }

    private async Task ProcessRichAddOnsAsync(
        IncomingMessageStanza parsed,
        Message message,
        string chatBare,
        string senderBare,
        CancellationToken ct)
    {
        Message m = MessageStanzas.UnwrapClassificationMessage(message);

        if (m.ReactionMessage is { } rx)
        {
            string emoji = rx.HasText ? rx.Text : string.Empty;
            string targetId = rx.Key?.Id ?? string.Empty;
            if (!string.IsNullOrEmpty(targetId))
            {
                await _backend.History.UpsertMessageReactionAsync(chatBare, targetId, senderBare, emoji, ct).ConfigureAwait(false);
            }
            await _emitEvent(new MessageAddOnEvent(
                "reaction",
                chatBare,
                senderBare,
                targetId,
                emoji,
                BoolFlag: emoji.Length > 0,
                parsed.TimestampSeconds)).ConfigureAwait(false);
        }

        if (m.EncCommentMessage is { TargetMessageKey: { Id.Length: > 0 } cmtKey, EncPayload.Length: > 0, EncIv.Length: 12 } encCmt
            && await TryDecryptAddonWithFallbackAsync(
                cmtKey,
                chatBare,
                senderBare,
                (sec, parentCand, modCand) => AddonEncryption.DecryptComment(
                    encCmt.EncPayload.Span, encCmt.EncIv.Span, sec, cmtKey.Id, parentCand, modCand),
                ct).ConfigureAwait(false) is { Result: { } decryptedComment })
        {
            MessageRecord cmtRecord = ToMessageRecord(parsed.Id, chatBare, senderBare, IsOwnUser(parsed.SenderDeviceJid.User), (ulong)parsed.TimestampSeconds, decryptedComment)
                with { QuotedMessageId = cmtKey.Id, QuotedSenderJid = cmtKey.HasParticipant ? cmtKey.Participant : null };
            await _backend.History.SaveMessageAsync(cmtRecord, ct).ConfigureAwait(false);
            await _emitEvent(new CommentReceivedEvent(chatBare, cmtKey.Id, senderBare, parsed.Id, cmtRecord, parsed.TimestampSeconds)).ConfigureAwait(false);
            await _emitEvent(new MessageAddOnEvent(
                "enc_comment",
                chatBare,
                senderBare,
                cmtKey.Id,
                cmtRecord.Text,
                BoolFlag: null,
                parsed.TimestampSeconds)).ConfigureAwait(false);
        }

        if (m.EncReactionMessage is { TargetMessageKey: { Id.Length: > 0 } targetKey } encRx
            && await TryDecryptAddonWithFallbackAsync(
                targetKey,
                chatBare,
                senderBare,
                (sec, parentCand, modCand) => AddonEncryption.DecryptReaction(
                    encRx.EncPayload.Span, encRx.EncIv.Span, sec, targetKey.Id, parentCand, modCand),
                ct).ConfigureAwait(false) is { Result: { } decryptedRx })
        {
            await _emitEvent(new MessageAddOnEvent(
                "enc_reaction",
                chatBare,
                senderBare,
                targetKey.Id,
                decryptedRx.HasText ? decryptedRx.Text : string.Empty,
                BoolFlag: decryptedRx.HasText && decryptedRx.Text.Length > 0,
                parsed.TimestampSeconds)).ConfigureAwait(false);
        }

        if (m.PollUpdateMessage is { PollCreationMessageKey: { Id.Length: > 0 } pollKey, Vote: { EncPayload.Length: > 0, EncIv.Length: 12 } vote }
            && await TryDecryptAddonWithFallbackAsync(
                pollKey,
                chatBare,
                senderBare,
                (sec, parentCand, modCand) => AddonEncryption.DecryptPollVote(
                    vote.EncPayload.Span, vote.EncIv.Span, sec, pollKey.Id, parentCand, modCand),
                ct).ConfigureAwait(false) is { Result: { } selectedHashes })
        {
            string hashesHex = string.Join(",", selectedHashes.Select(h => Convert.ToHexString(h).ToLowerInvariant()));
            await _emitEvent(new MessageAddOnEvent(
                "poll_vote",
                chatBare,
                senderBare,
                pollKey.Id,
                hashesHex,
                BoolFlag: selectedHashes.Count > 0,
                parsed.TimestampSeconds)).ConfigureAwait(false);
        }

        if (m.EncEventResponseMessage is { EventCreationMessageKey: { Id.Length: > 0 } evtKey, EncPayload.Length: > 0, EncIv.Length: 12 } encEvt
            && await TryDecryptAddonWithFallbackAsync(
                evtKey,
                chatBare,
                senderBare,
                (sec, parentCand, modCand) => AddonEncryption.DecryptEventResponse(
                    encEvt.EncPayload.Span, encEvt.EncIv.Span, sec, evtKey.Id, parentCand, modCand),
                ct).ConfigureAwait(false) is { Result: { } evtResp })
        {
            await _emitEvent(new MessageAddOnEvent(
                "event_response",
                chatBare,
                senderBare,
                evtKey.Id,
                evtResp.Response.ToString(),
                BoolFlag: evtResp.Response == Message.Types.EventResponseMessage.Types.EventResponseType.Going,
                parsed.TimestampSeconds)).ConfigureAwait(false);
        }

        if (m.SecretEncryptedMessage is { TargetMessageKey: { Id.Length: > 0 } secKey, EncPayload.Length: > 0, EncIv.Length: 12 } secMsg)
        {
            AddonModificationType modType = secMsg.SecretEncType switch
            {
                Message.Types.SecretEncryptedMessage.Types.SecretEncType.EventEdit => AddonModificationType.EventEdit,
                Message.Types.SecretEncryptedMessage.Types.SecretEncType.PollEdit => AddonModificationType.PollEdit,
                Message.Types.SecretEncryptedMessage.Types.SecretEncType.PollAddOption => AddonModificationType.PollAddOption,
                _ => AddonModificationType.MessageEdit
            };

            if (await TryDecryptAddonWithFallbackAsync(
                secKey,
                chatBare,
                senderBare,
                (sec, parentCand, modCand) => AddonEncryption.DecryptSecretMessage(
                    secMsg.EncPayload.Span, secMsg.EncIv.Span, sec, secKey.Id, parentCand, modCand, modType),
                ct).ConfigureAwait(false) is { Result: { } innerMsg, ParentTimestamp: var parentTs })
            {
                if (modType == AddonModificationType.MessageEdit
                    && parentTs is long pts
                    && parsed.TimestampSeconds - pts > RichMessageBuilders.MaxMessageEditWindowSeconds)
                {
                    return;
                }

                string kind = modType switch
                {
                    AddonModificationType.PollAddOption => "poll_add_option",
                    AddonModificationType.PollEdit => "poll_edit",
                    AddonModificationType.EventEdit => "event_edit",
                    _ => "message_edit"
                };

                string? summaryText = innerMsg.ProtocolMessage?.EditedMessage is { } edited
                    ? (edited.HasConversation ? edited.Conversation : edited.ExtendedTextMessage?.Text)
                    : innerMsg.PollAddOptionMessage?.AddOption?.OptionName;

                if (modType == AddonModificationType.MessageEdit)
                {
                    await _backend.History.ApplyMessageEditAsync(chatBare, secKey.Id, summaryText, parsed.TimestampSeconds, ct).ConfigureAwait(false);
                }

                await _emitEvent(new MessageAddOnEvent(
                    kind,
                    chatBare,
                    senderBare,
                    secKey.Id,
                    summaryText,
                    BoolFlag: null,
                    parsed.TimestampSeconds)).ConfigureAwait(false);
            }
        }

        if (m.PinInChatMessage is { Key: { Id.Length: > 0 } pinKey } pin)
        {
            bool isPinned = pin.Type == Message.Types.PinInChatMessage.Types.Type.PinForAll;
            await _emitEvent(new MessageAddOnEvent(
                "pin_in_chat",
                chatBare,
                senderBare,
                pinKey.Id,
                null,
                BoolFlag: isPinned,
                parsed.TimestampSeconds)).ConfigureAwait(false);
        }

        if (m.KeepInChatMessage is { Key: { Id.Length: > 0 } keepKey } keep)
        {
            bool isKept = keep.KeepType == KeepType.KeepForAll;
            await _emitEvent(new MessageAddOnEvent(
                "keep_in_chat",
                chatBare,
                senderBare,
                keepKey.Id,
                null,
                BoolFlag: isKept,
                parsed.TimestampSeconds)).ConfigureAwait(false);
        }

        if (m.ProtocolMessage is { } pm)
        {
            if (pm.Type == Message.Types.ProtocolMessage.Types.Type.Revoke && pm.Key is { Id.Length: > 0 } revKey)
            {
                await _backend.History.MarkMessageRevokedAsync(chatBare, revKey.Id, ct).ConfigureAwait(false);
                await _emitEvent(new MessageAddOnEvent(
                    "revoke",
                    chatBare,
                    senderBare,
                    revKey.Id,
                    null,
                    BoolFlag: string.Equals(parsed.Edit, RichMessageBuilders.EditAttrAdminRevoke, StringComparison.Ordinal),
                    parsed.TimestampSeconds)).ConfigureAwait(false);
            }
            else if (pm.Type == Message.Types.ProtocolMessage.Types.Type.MessageEdit && pm.Key is { Id.Length: > 0 } editKey && pm.EditedMessage is { } ed)
            {
                string? editedText = ed.HasConversation ? ed.Conversation : (ed.ExtendedTextMessage is { HasText: true } ext ? ext.Text : null);
                await _backend.History.ApplyMessageEditAsync(chatBare, editKey.Id, editedText, parsed.TimestampSeconds, ct).ConfigureAwait(false);
                await _emitEvent(new MessageAddOnEvent(
                    "message_edit",
                    chatBare,
                    senderBare,
                    editKey.Id,
                    editedText,
                    BoolFlag: null,
                    parsed.TimestampSeconds)).ConfigureAwait(false);
            }
            else if (pm.Type == Message.Types.ProtocolMessage.Types.Type.HistorySyncNotification && pm.HistorySyncNotification is { } hsn && _historySync is not null)
            {
                await _historySync.ProcessNotificationAsync(hsn, ct).ConfigureAwait(false);
            }
            else if (pm.Type == Message.Types.ProtocolMessage.Types.Type.DraftMessageSync && pm.DraftMessageSync is { } dms)
            {
                string targetChat = dms.HasChatJid && !string.IsNullOrEmpty(dms.ChatJid) ? dms.ChatJid : chatBare;
                bool isSet = dms.Action == DraftMessageSync.Types.Action.Set;
                string? draftText = isSet && dms.Draft is { HasConversation: true } ? dms.Draft.Conversation : null;
                long draftTsMs = dms.HasEditTimestampMs ? dms.EditTimestampMs : (pm.HasTimestampMs ? pm.TimestampMs : parsed.TimestampSeconds * 1000L);
                await _backend.History.SaveDraftAsync(targetChat, draftText, draftTsMs, ct).ConfigureAwait(false);
                await _emitEvent(new DraftUpdatedEvent(targetChat, draftText, draftTsMs, Cleared: !isSet)).ConfigureAwait(false);
                await _emitEvent(new MessageAddOnEvent(
                    "draft_sync",
                    targetChat,
                    senderBare,
                    parsed.Id,
                    draftText,
                    BoolFlag: isSet,
                    parsed.TimestampSeconds)).ConfigureAwait(false);
            }
            else if (pm.Type == Message.Types.ProtocolMessage.Types.Type.GroupMemberLabelChange && pm.MemberLabel is { } ml)
            {
                await _emitEvent(new MemberLabelUpdatedEvent(
                    chatBare,
                    senderBare,
                    ml.HasLabel ? ml.Label : string.Empty,
                    ml.HasLabelTimestamp ? ml.LabelTimestamp : parsed.TimestampSeconds,
                    parsed.TimestampSeconds)).ConfigureAwait(false);
            }
            else if (pm.Type == Message.Types.ProtocolMessage.Types.Type.BotFeedbackMessage && pm.BotFeedbackMessage is { } fb)
            {
                await _emitEvent(new BotFeedbackReceivedEvent(
                    chatBare,
                    senderBare,
                    parsed.Id,
                    fb.MessageKey?.Id ?? string.Empty,
                    fb.Kind,
                    fb.HasText ? fb.Text : null,
                    fb.HasKindNegative ? fb.KindNegative : null,
                    fb.HasKindPositive ? fb.KindPositive : null,
                    parsed.TimestampSeconds)).ConfigureAwait(false);
            }
            else if (pm.Type == Message.Types.ProtocolMessage.Types.Type.AiPsiMetadata && pm.HasAiPsiMetadata)
            {
                await _emitEvent(new BotPsiMetadataReceivedEvent(
                    chatBare,
                    senderBare,
                    parsed.Id,
                    pm.AiPsiMetadata.ToByteArray(),
                    parsed.TimestampSeconds)).ConfigureAwait(false);
            }
            else if (pm.Type == Message.Types.ProtocolMessage.Types.Type.AiQueryFanout && pm.AiQueryFanout is { } fanout)
            {
                string? fanoutText = fanout.Message is { } fm ? ExtractReadableText(fm) : null;
                await _emitEvent(new BotQueryFanoutReceivedEvent(
                    chatBare,
                    senderBare,
                    parsed.Id,
                    fanout.MessageKey?.Id,
                    fanout.HasTimestamp ? fanout.Timestamp : null,
                    fanoutText,
                    parsed.TimestampSeconds)).ConfigureAwait(false);
            }
        }

        if (m.RichResponseMessage is { } rich)
        {
            ParsedBotRichResponse parsedRich = BotStanzas.ExtractRichResponse(rich);
            BotMetadata? botMeta = message.MessageContextInfo?.BotMetadata;
            await _emitEvent(new BotRichResponseReceivedEvent(
                chatBare,
                senderBare,
                parsed.Id,
                parsedRich,
                botMeta?.HasPersonaId == true ? botMeta.PersonaId : null,
                botMeta?.HasBotResponseId == true ? botMeta.BotResponseId : null,
                parsed.BotInfo?.EditTargetId,
                parsed.BotInfo?.EditType,
                parsed.TimestampSeconds)).ConfigureAwait(false);
        }

        if (m.StatusQuestionAnswerMessage is { Key: { Id.Length: > 0 } qaKey } qa)
        {
            await _emitEvent(new MessageAddOnEvent(
                "status_question_answer",
                qaKey.HasRemoteJid ? qaKey.RemoteJid : chatBare,
                senderBare,
                qaKey.Id,
                qa.HasText ? qa.Text : string.Empty,
                BoolFlag: null,
                parsed.TimestampSeconds)).ConfigureAwait(false);
        }

        if (m.StatusStickerInteractionMessage is { Key: { Id.Length: > 0 } stkKey } ssi)
        {
            await _emitEvent(new MessageAddOnEvent(
                "status_sticker_interaction",
                stkKey.HasRemoteJid ? stkKey.RemoteJid : chatBare,
                senderBare,
                stkKey.Id,
                ssi.HasStickerKey ? ssi.StickerKey : ssi.Type.ToString(),
                BoolFlag: null,
                parsed.TimestampSeconds)).ConfigureAwait(false);
        }

        if (m.MessageHistoryBundle is { } bundle && bundle.MessageHistoryMetadata is { } histMeta)
        {
            IReadOnlyList<MessageRecord> decodedRecords = Array.Empty<MessageRecord>();
            if (_mediaTransfer is not null
                && bundle.HasDirectPath
                && bundle.MediaKey is { Length: 32 } mk
                && !GroupHistoryCodec.IsBundleExpired(histMeta, _timeProvider.GetUtcNow().ToUnixTimeSeconds()))
            {
                byte[]? encSha = bundle.FileEncSha256 is { Length: > 0 } fe ? fe.ToByteArray() : null;
                byte[]? plainSha = bundle.FileSha256 is { Length: > 0 } fp ? fp.ToByteArray() : null;
                byte[] compressed = await _mediaTransfer.DownloadAsync(
                    bundle.DirectPath,
                    WaMediaType.GroupHistory,
                    mk.ToByteArray(),
                    plainSha ?? ReadOnlyMemory<byte>.Empty,
                    encSha ?? ReadOnlyMemory<byte>.Empty,
                    ct).ConfigureAwait(false);

                GroupHistory gh = GroupHistoryCodec.DecompressGroupHistory(compressed);
                var records = new List<MessageRecord>(gh.Messages.Count);
                foreach (WebMessageInfo item in gh.Messages)
                {
                    if (item.Key is { Id.Length: > 0 } k && item.Message is { } itemMsg)
                    {
                        MessageRecord rec = ToMessageRecord(
                            k.Id,
                            k.HasRemoteJid ? k.RemoteJid : chatBare,
                            item.HasParticipant ? item.Participant : (k.HasParticipant ? k.Participant : null),
                            k.FromMe,
                            item.HasMessageTimestamp ? item.MessageTimestamp : (ulong)parsed.TimestampSeconds,
                            itemMsg);
                        await _backend.History.SaveMessageAsync(rec, ct).ConfigureAwait(false);
                        records.Add(rec);
                    }
                }
                decodedRecords = records;
            }

            await _emitEvent(new GroupHistoryBundleReceivedEvent(
                chatBare,
                senderBare,
                parsed.Id,
                (int)(histMeta.HasMessageCount ? histMeta.MessageCount : decodedRecords.Count),
                histMeta.HasOldestMessageTimestampInBundle ? histMeta.OldestMessageTimestampInBundle : 0,
                histMeta.HistoryReceivers.ToArray(),
                decodedRecords,
                parsed.TimestampSeconds)).ConfigureAwait(false);
        }
    }

    private sealed record DecryptedAddonResult<T>(T Result, long? ParentTimestamp);

    private async Task<DecryptedAddonResult<T>?> TryDecryptAddonWithFallbackAsync<T>(
        MessageKey targetKey,
        string chatBare,
        string modSenderBare,
        Func<byte[], string, string, T> decryptAttempt,
        CancellationToken ct)
    {
        (byte[] Secret, long? Timestamp)? stored = await _backend.MsgSecrets.GetMsgSecretAsync(targetKey.Id, ct).ConfigureAwait(false);
        if (stored is not { Secret.Length: 32 } entry)
        {
            return null;
        }

        List<string> parentCandidates = targetKey.HasParticipant && !string.IsNullOrEmpty(targetKey.Participant)
            ? BuildJidCandidates(targetKey.Participant, includeOwn: targetKey.FromMe)
            : BuildJidCandidates(chatBare, includeOwn: true);
        List<string> modCandidates = BuildJidCandidates(modSenderBare, includeOwn: IsOwnBareJid(modSenderBare));

        foreach (string pCand in parentCandidates)
        {
            foreach (string mCand in modCandidates)
            {
                try
                {
                    return new DecryptedAddonResult<T>(decryptAttempt(entry.Secret, pCand, mCand), entry.Timestamp);
                }
                catch (CryptographicException)
                {
                    // Retry next addressing form (LID vs PN alias)
                }
            }
        }

        return null;
    }

    private List<string> BuildJidCandidates(string rawJid, bool includeOwn)
    {
        var list = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? s)
        {
            if (!string.IsNullOrWhiteSpace(s) && seen.Add(s))
            {
                list.Add(s);
            }
        }

        string bare = Jid.TryParse(rawJid, out Jid parsed) ? parsed.ToNonAdString() : rawJid;
        Add(bare);
        if (_lidPnCache.TryGetLid(bare, out string? lid)) Add(lid);
        if (_lidPnCache.TryGetPn(bare, out string? pn)) Add(pn);
        if (includeOwn)
        {
            Add(_credentials.Lid?.ToNonAdString());
            Add(_credentials.Pn?.ToNonAdString());
        }
        return list;
    }

    private static byte[]? TryGetPollEncKey32(Message message)
    {
        Message m = MessageStanzas.UnwrapClassificationMessage(message);
        ByteString? key = (m.PollCreationMessage
            ?? m.PollCreationMessageV2
            ?? m.PollCreationMessageV3
            ?? m.PollCreationMessageV5
            ?? m.PollCreationMessageV6)?.EncKey;
        return key is { Length: 32 } ? key.ToByteArray() : null;
    }

    private bool IsOwnBareJid(string bare) =>
        Jid.TryParse(bare, out Jid j) && IsOwnUser(j.User);

    private async Task HandleDecryptFailureAsync(
        IncomingMessageStanza parsed,
        EncryptedMessagePayload failedEnc,
        Exception ex,
        CancellationToken ct)
    {
        string chatBare = parsed.ChatJid.ToNonAdString();
        var (retryCount, shouldSend) = _retryCoordinator.RecordInboundDecryptFailure(chatBare, parsed.Id);
        RetryReason reason = ClassifyRetryReason(ex, failedEnc.EncType);

        if (shouldSend)
        {
            RetryPreKeyUploadBundle? localKeys = retryCount >= RetryAndMediaRetryStanzas.MinRetryCountForKeys
                ? await _retryCoordinator.BuildRetryKeyBundleAsync(ct).ConfigureAwait(false)
                : null;

            bool isGroup = parsed.From.IsGroup();
            Node retryReceipt = RetryAndMediaRetryStanzas.BuildRetryReceipt(
                parsed.Id,
                parsed.From,
                parsed.TimestampSeconds,
                retryCount,
                _credentials.RegistrationId,
                reason,
                participant: isGroup ? parsed.SenderDeviceJid : null,
                recipient: parsed.Recipient,
                localKeys: localKeys,
                hideFailedDecrypt: failedEnc.HideDecryptFail);

            await _socket.SendNodeAsync(retryReceipt, ct).ConfigureAwait(false);
        }

        await _emitEvent(new DecryptionFailureEvent(
            parsed.Id,
            chatBare,
            parsed.SenderDeviceJid.ToNonAdString(),
            reason.ToString(),
            retryCount,
            shouldSend)).ConfigureAwait(false);
    }

    private async Task SendAckOrDeliveryReceiptAsync(IncomingMessageStanza parsed, bool hideDelivery, CancellationToken ct)
    {
        if (hideDelivery || IsOwnUser(parsed.SenderDeviceJid.User))
        {
            await _socket.SendNodeAsync(
                AckStanza.Build(parsed.Id, "message", parsed.From, parsed.Type, parsed.Participant),
                ct).ConfigureAwait(false);
            return;
        }

        Node receipt = ReceiptStanza.Build(
            parsed.Id,
            parsed.From.ToNonAd(),
            type: null,
            participant: parsed.From.IsGroup() ? parsed.SenderDeviceJid : null);
        await _socket.SendNodeAsync(receipt, ct).ConfigureAwait(false);
    }

    private async Task<KeyPair> ResolveSignedPreKeyAsync(uint signedPreKeyId, CancellationToken ct)
    {
        if (signedPreKeyId == _credentials.SignedPreKeyId)
        {
            return _credentials.SignedPreKey;
        }

        SignedPreKeyEntry? stored = await _backend.Signal.GetSignedPreKeyAsync(signedPreKeyId, ct).ConfigureAwait(false);
        if (stored is null)
        {
            throw new CryptographicException($"SignedPreKey {signedPreKeyId} not found.");
        }

        return PreKeyManager.DecodeSignedPreKeyRecord(stored.KeyPairBytes).KeyPair;
    }

    private async Task<KeyPair?> ResolveOneTimePreKeyAsync(uint? preKeyId, CancellationToken ct)
    {
        if (preKeyId is not uint id)
        {
            return null;
        }

        byte[]? bytes = await _backend.Signal.GetPreKeyAsync(id, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            throw new CryptographicException($"OneTimePreKey {id} not found (InvalidKeyId).");
        }

        return PreKeyManager.DecodePreKeyRecord(bytes).KeyPair;
    }

    private async Task LearnSenderMappingsAsync(IncomingMessageStanza parsed, CancellationToken ct)
    {
        Jid? pn = parsed.SenderPn ?? (parsed.From.IsPn() ? parsed.From : (parsed.Participant?.IsPn() == true ? parsed.Participant : null));
        Jid? lid = parsed.SenderLid ?? (parsed.From.IsLid() ? parsed.From : (parsed.Participant?.IsLid() == true ? parsed.Participant : null));

        if (pn is Jid p && lid is Jid l)
        {
            string pnBare = p.ToNonAdString();
            string lidBare = l.ToNonAdString();
            _lidPnCache.Learn(pnBare, lidBare, LidPnLearningSource.PeerJid, parsed.TimestampSeconds);
            await _backend.Protocol.UpsertLidPnMappingsAsync([new LidPnMappingRecord(pnBare, lidBare)], ct).ConfigureAwait(false);
        }
    }

    private static RetryReason ClassifyRetryReason(Exception ex, string encType)
    {
        if (ex.Message.Contains("InvalidKeyId", StringComparison.OrdinalIgnoreCase))
        {
            return RetryReason.InvalidKeyId;
        }
        if (ex.Message.Contains("MAC", StringComparison.OrdinalIgnoreCase))
        {
            return RetryReason.BadMac;
        }
        if (ex.Message.Contains("No Signal session", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Missing SenderKey", StringComparison.OrdinalIgnoreCase))
        {
            return RetryReason.NoSession;
        }
        if (ex.Message.Contains("future", StringComparison.OrdinalIgnoreCase))
        {
            return RetryReason.FutureMessage;
        }
        return encType == "skmsg" ? RetryReason.InvalidSession : RetryReason.InvalidMessage;
    }

    private static bool IsPureSkdmWrapper(Message message) =>
        message.SenderKeyDistributionMessage is not null
        && !message.HasConversation
        && message.ExtendedTextMessage is null
        && message.ImageMessage is null
        && message.VideoMessage is null
        && message.AudioMessage is null
        && message.DocumentMessage is null
        && message.StickerMessage is null
        && message.ProtocolMessage is null
        && message.ReactionMessage is null;

    internal static MessageRecord ToMessageRecord(
        string id,
        string chatJid,
        string? senderJid,
        bool fromMe,
        ulong timestampSeconds,
        Message message)
    {
        Message m = MessageStanzas.UnwrapClassificationMessage(message);
        ContextInfo? ctx = ExtractContextInfo(m);
        string? quotedId = ctx is { HasStanzaId: true } ? ctx.StanzaId : null;
        string? quotedSender = ctx is { HasParticipant: true } ? ctx.Participant : null;
        string? quotedText = ctx?.QuotedMessage is { } qm ? ExtractReadableText(qm) : null;

        if (m.ImageMessage is { } img)
        {
            return new MessageRecord(id, chatJid, senderJid, fromMe, timestampSeconds, MessageType.Image, img.HasCaption ? img.Caption : null,
                img.HasMimetype ? img.Mimetype : null, img.HasDirectPath ? img.DirectPath : null, img.MediaKey?.ToByteArray(), img.FileSha256?.ToByteArray(), img.FileEncSha256?.ToByteArray(),
                quotedId, quotedSender, quotedText, FileLength: img.HasFileLength ? img.FileLength : null);
        }
        if (m.StickerMessage is { } stk)
        {
            return new MessageRecord(id, chatJid, senderJid, fromMe, timestampSeconds, MessageType.Sticker, null,
                stk.HasMimetype ? stk.Mimetype : null, stk.HasDirectPath ? stk.DirectPath : null, stk.MediaKey?.ToByteArray(), stk.FileSha256?.ToByteArray(), stk.FileEncSha256?.ToByteArray(),
                quotedId, quotedSender, quotedText, FileLength: stk.HasFileLength ? stk.FileLength : null);
        }
        if (m.VideoMessage is { } vid)
        {
            return new MessageRecord(id, chatJid, senderJid, fromMe, timestampSeconds, MessageType.Video, vid.HasCaption ? vid.Caption : null,
                vid.HasMimetype ? vid.Mimetype : null, vid.HasDirectPath ? vid.DirectPath : null, vid.MediaKey?.ToByteArray(), vid.FileSha256?.ToByteArray(), vid.FileEncSha256?.ToByteArray(),
                quotedId, quotedSender, quotedText, FileLength: vid.HasFileLength ? vid.FileLength : null);
        }
        if (m.AudioMessage is { } aud)
        {
            return new MessageRecord(id, chatJid, senderJid, fromMe, timestampSeconds, MessageType.Audio, null,
                aud.HasMimetype ? aud.Mimetype : null, aud.HasDirectPath ? aud.DirectPath : null, aud.MediaKey?.ToByteArray(), aud.FileSha256?.ToByteArray(), aud.FileEncSha256?.ToByteArray(),
                quotedId, quotedSender, quotedText, FileLength: aud.HasFileLength ? aud.FileLength : null);
        }
        if (m.DocumentMessage is { } doc)
        {
            string? docText = doc.HasCaption ? doc.Caption : (doc.HasFileName ? doc.FileName : null);
            return new MessageRecord(id, chatJid, senderJid, fromMe, timestampSeconds, MessageType.Document, docText,
                doc.HasMimetype ? doc.Mimetype : null, doc.HasDirectPath ? doc.DirectPath : null, doc.MediaKey?.ToByteArray(), doc.FileSha256?.ToByteArray(), doc.FileEncSha256?.ToByteArray(),
                quotedId, quotedSender, quotedText, FileLength: doc.HasFileLength ? doc.FileLength : null);
        }
        if (m.ProtocolMessage is not null)
        {
            return new MessageRecord(id, chatJid, senderJid, fromMe, timestampSeconds, MessageType.Protocol, null);
        }

        string? text = ExtractReadableText(m);
        MessageType msgType = text is not null ? MessageType.Text : MessageType.Unknown;
        return new MessageRecord(id, chatJid, senderJid, fromMe, timestampSeconds, msgType, text,
            QuotedMessageId: quotedId, QuotedSenderJid: quotedSender, QuotedText: quotedText);
    }

    private static ContextInfo? ExtractContextInfo(Message m)
    {
        Message cur = MessageStanzas.UnwrapClassificationMessage(m);
        return cur.ExtendedTextMessage?.ContextInfo
            ?? cur.ImageMessage?.ContextInfo
            ?? cur.VideoMessage?.ContextInfo
            ?? cur.AudioMessage?.ContextInfo
            ?? cur.DocumentMessage?.ContextInfo
            ?? cur.StickerMessage?.ContextInfo
            ?? cur.MusicMessage?.ContextInfo
            ?? cur.NewsletterAdminInviteMessage?.ContextInfo
            ?? cur.NewsletterFollowerInviteMessageV2?.ContextInfo
            ?? cur.ProductMessage?.ContextInfo
            ?? cur.OrderMessage?.ContextInfo
            ?? cur.RichResponseMessage?.ContextInfo;
    }

    private static string? ExtractReadableText(Message m)
    {
        Message u = MessageStanzas.UnwrapClassificationMessage(m);
        return u.HasConversation
            ? u.Conversation
            : (u.ExtendedTextMessage is { HasText: true } ext
                ? ext.Text
                : (u.PollCreationMessage ?? u.PollCreationMessageV2 ?? u.PollCreationMessageV3 ?? u.PollCreationMessageV5 ?? u.PollCreationMessageV6)?.Name
                    ?? (u.EventMessage is { HasName: true } evt ? evt.Name : null)
                    ?? (u.LocationMessage is { } loc ? (loc.HasName ? loc.Name : (loc.HasAddress ? loc.Address : $"{loc.DegreesLatitude},{loc.DegreesLongitude}")) : null)
                    ?? (u.LiveLocationMessage is { HasCaption: true } live ? live.Caption : null)
                    ?? (u.MusicMessage?.EmbeddedMusic is { } em ? $"{em.Author} - {em.Title}" : null)
                    ?? (u.NewsletterAdminInviteMessage is { } nai ? (nai.HasCaption ? nai.Caption : (nai.HasNewsletterName ? nai.NewsletterName : null)) : null)
                    ?? (u.NewsletterFollowerInviteMessageV2 is { } nfi ? (nfi.HasCaption ? nfi.Caption : (nfi.HasNewsletterName ? nfi.NewsletterName : null)) : null)
                    ?? (u.ProductMessage is { } pm ? (pm.Product?.HasTitle == true ? pm.Product.Title : (pm.HasBody ? pm.Body : null)) : null)
                    ?? (u.OrderMessage is { } om ? (om.HasOrderTitle ? om.OrderTitle : (om.HasMessage ? om.Message : (om.HasOrderId ? $"Order {om.OrderId}" : null))) : null)
                    ?? (u.RichResponseMessage is { } rich ? BotStanzas.ExtractRichResponse(rich).SummaryText : null)
                    ?? (u.StatusQuestionAnswerMessage is { HasText: true } sqa ? sqa.Text : null)
                    ?? (u.ReactionMessage is { HasText: true } rx ? rx.Text : null));
    }

    private async Task HandleNewsletterPlaintextAsync(IncomingMessageStanza parsed, CancellationToken ct)
    {
        string chatBare = parsed.From.ToNonAdString();
        bool isRevoke = string.Equals(parsed.Edit, RichMessageBuilders.EditAttrAdminRevoke, StringComparison.Ordinal);

        if (parsed.PlaintextBytes is { Length: > 0 } ptBytes)
        {
            Message msg = Message.Parser.ParseFrom(ptBytes);
            MessageRecord record = ToMessageRecord(parsed.Id, chatBare, chatBare, fromMe: false, (ulong)parsed.TimestampSeconds, msg);
            if (string.Equals(parsed.Edit, NewsletterStanzas.EditAttrAdminEdit, StringComparison.Ordinal))
            {
                await _backend.History.ApplyMessageEditAsync(chatBare, parsed.Id, record.Text, parsed.TimestampSeconds, ct).ConfigureAwait(false);
            }
            await _backend.History.SaveMessageAsync(record, ct).ConfigureAwait(false);
            await _emitEvent(new NewsletterMessageReceivedEvent(
                chatBare,
                parsed.Id,
                parsed.ServerId ?? 0,
                parsed.Type,
                parsed.Edit,
                IsSender: false,
                record,
                parsed.TimestampSeconds,
                parsed.IsOffline)).ConfigureAwait(false);
            await _emitEvent(new MessageReceivedEvent(record)).ConfigureAwait(false);
        }
        else if (isRevoke)
        {
            await _backend.History.MarkMessageRevokedAsync(chatBare, parsed.Id, ct).ConfigureAwait(false);
            var revokedRecord = new MessageRecord(
                parsed.Id,
                chatBare,
                chatBare,
                FromMe: false,
                (ulong)parsed.TimestampSeconds,
                MessageType.Protocol,
                Text: null,
                IsRevoked: true);
            await _emitEvent(new NewsletterMessageReceivedEvent(
                chatBare,
                parsed.Id,
                parsed.ServerId ?? 0,
                parsed.Type,
                parsed.Edit,
                IsSender: false,
                revokedRecord,
                parsed.TimestampSeconds,
                parsed.IsOffline)).ConfigureAwait(false);
        }

        await _socket.SendNodeAsync(
            AckStanza.Build(parsed.Id, "message", parsed.From, parsed.Type, parsed.Participant),
            ct).ConfigureAwait(false);
    }

    private bool IsOwnUser(string user) =>
        (_credentials.Pn is Jid pn && string.Equals(pn.User, user, StringComparison.Ordinal))
        || (_credentials.Lid is Jid lid && string.Equals(lid.User, user, StringComparison.Ordinal));
}
