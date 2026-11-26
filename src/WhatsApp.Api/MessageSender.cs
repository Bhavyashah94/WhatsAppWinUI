using System.Collections.Concurrent;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Api;

public sealed record SendMessageResult(
    string MessageId,
    string ChatJid,
    int TargetDeviceCount,
    string? Phash);

/// <summary>
/// Outbound 1:1 DM, companion <see cref="DeviceSentMessage"/> sync, Group <c>SenderKey</c> +
/// selective <c>SKDM</c> fan-out (<c>phashV2</c>), Privacy Reporting Token attachment,
/// and PDO <c>PLACEHOLDER_MESSAGE_RESEND</c> requester.
/// </summary>
public sealed class MessageSender
{
    public static readonly TimeSpan DefaultAckTimeout = TimeSpan.FromSeconds(30);

    private readonly NoiseSessionSocket _socket;
    private readonly UsyncClient _usync;
    private readonly PreKeyFetchClient _preKeyFetcher;
    private readonly IWhatsAppBackend _backend;
    private readonly ILidPnCache _lidPnCache;
    private readonly DeviceCredentials _credentials;
    private readonly RetryCoordinator _retryCoordinator;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(string? ErrorCode, string? ErrorText)>> _ackWaiters = new(StringComparer.Ordinal);
    private long _msgSeq;

    public MessageSender(
        NoiseSessionSocket socket,
        UsyncClient usync,
        PreKeyFetchClient preKeyFetcher,
        IWhatsAppBackend backend,
        ILidPnCache lidPnCache,
        DeviceCredentials credentials,
        RetryCoordinator retryCoordinator,
        TimeProvider? timeProvider = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _usync = usync ?? throw new ArgumentNullException(nameof(usync));
        _preKeyFetcher = preKeyFetcher ?? throw new ArgumentNullException(nameof(preKeyFetcher));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _retryCoordinator = retryCoordinator ?? throw new ArgumentNullException(nameof(retryCoordinator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string NextMessageId()
    {
        long n = Interlocked.Increment(ref _msgSeq);
        long ts = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        return $"3EB0{ts:X8}{n:X6}";
    }

    public Task<SendMessageResult> SendTextAsync(
        Jid toJid,
        string text,
        IReadOnlyList<Jid>? groupParticipants = null,
        string? messageId = null,
        bool includeReportingToken = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var msg = new Message { Conversation = text };
        if (toJid.IsNewsletter())
        {
            return SendNewsletterMessageAsync(toJid, msg, messageId, ct: ct);
        }
        if (toJid.IsGroup())
        {
            if (groupParticipants is null)
            {
                throw new ArgumentException("Group participant JIDs are required when sending to a @g.us chat.", nameof(groupParticipants));
            }
            return SendGroupMessageAsync(toJid, msg, groupParticipants, messageId, includeReportingToken: includeReportingToken, ct: ct);
        }

        return SendDmAsync(toJid, msg, messageId, includeReportingToken: includeReportingToken, ct: ct);
    }

    public async Task<SendMessageResult> SendNewsletterMessageAsync(
        Jid channelJid,
        Message message,
        string? messageId = null,
        string? stanzaTypeOverride = null,
        IReadOnlyList<Node>? extraNodes = null,
        bool awaitServerAck = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!channelJid.IsNewsletter())
        {
            throw new ArgumentException($"Expected a @newsletter JID, got '{channelJid}'.", nameof(channelJid));
        }

        string msgId = string.IsNullOrWhiteSpace(messageId) ? NextMessageId() : messageId;
        Jid channelBare = channelJid.ToNonAd();
        Node stanza = MessageStanzas.BuildNewsletterPlaintextStanza(
            msgId,
            channelBare,
            message,
            stanzaTypeOverride,
            extraNodes);

        var (_, errCode, errText) = await SendWithOptionalAckWaitAsync(stanza, msgId, awaitServerAck, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(errCode))
        {
            throw new InvalidOperationException($"Newsletter publish failed for '{msgId}': error={errCode} ({errText ?? "unknown"})");
        }

        string ownBare = (_credentials.Lid ?? _credentials.Pn)?.ToNonAdString() ?? channelBare.ToString();
        await PersistSentMetadataAsync(msgId, channelBare.ToString(), ownBare, message, messageSecret32: null, ct).ConfigureAwait(false);
        return new SendMessageResult(msgId, channelBare.ToString(), TargetDeviceCount: 0, Phash: null);
    }

    public async Task<SendMessageResult> SendDmAsync(
        Jid toJid,
        Message message,
        string? messageId = null,
        bool includeReportingToken = true,
        bool allowCachedDeviceLists = true,
        string? editAttribute = null,
        string? category = null,
        IReadOnlyList<Node>? extraNodes = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        Jid toBare = toJid.ToNonAd();
        string msgId = string.IsNullOrWhiteSpace(messageId) ? NextMessageId() : messageId;

        Jid ownBare = GetOwnUserForAddressing(toBare);
        ushort ownDeviceId = GetOwnDeviceId();
        Jid ownFull = ownBare with { Device = ownDeviceId };

        var queryUsers = new List<Jid> { toBare };
        if (!string.Equals(ownBare.ToString(), toBare.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            queryUsers.Add(ownBare);
        }

        IReadOnlyDictionary<string, ResolvedDeviceList> deviceLists =
            await _usync.GetDeviceListsAsync(queryUsers, UsyncContext.Message, allowCachedDeviceLists, ct).ConfigureAwait(false);

        var targetDevices = new List<Jid>();
        var allParticipantDevices = new List<Jid>();
        bool isSelfChat = string.Equals(ownBare.ToString(), toBare.ToString(), StringComparison.OrdinalIgnoreCase);

        if (deviceLists.TryGetValue(toBare.ToString(), out ResolvedDeviceList? peerList))
        {
            foreach (uint devId in peerList.DeviceIds)
            {
                Jid dev = toBare with { Device = (ushort)devId };
                allParticipantDevices.Add(dev);
                if (!isSelfChat || devId != ownDeviceId)
                {
                    targetDevices.Add(dev);
                }
            }
        }
        else
        {
            allParticipantDevices.Add(toBare);
            if (!isSelfChat)
            {
                targetDevices.Add(toBare);
            }
        }

        if (!isSelfChat && deviceLists.TryGetValue(ownBare.ToString(), out ResolvedDeviceList? ownList))
        {
            foreach (uint devId in ownList.DeviceIds)
            {
                Jid dev = ownBare with { Device = (ushort)devId };
                allParticipantDevices.Add(dev);
                if (devId != ownDeviceId)
                {
                    targetDevices.Add(dev);
                }
            }
        }

        SessionEnsureResult ensured = await _preKeyFetcher.EnsureSessionsAsync(targetDevices, reason: null, ct).ConfigureAwait(false);
        var rejectedSet = ensured.RejectedDevices
            .Where(r => r.Code == 406)
            .Select(r => r.DeviceJid.ToProtocolAddress())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Message msgToSend = message.Clone();
        ReportingTokenResult? reportingToken = null;
        if (includeReportingToken && category is null)
        {
            byte[]? preSecret = msgToSend.MessageContextInfo?.MessageSecret is { Length: 32 } ps ? ps.ToByteArray() : null;
            reportingToken = ReportingTokenGenerator.Generate(msgToSend, msgId, ownFull.ToString(), toBare.ToString(), existingSecret32: preSecret);
            if (reportingToken is not null)
            {
                msgToSend = ReportingTokenGenerator.AttachMessageContext(msgToSend, reportingToken.MessageSecret32, reportingToken.Version);
            }
        }

        byte[] unpaddedBytes = msgToSend.ToByteArray();
        byte[] paddedDm = MessageFraming.PadMessageV2(unpaddedBytes);
        string? dsmPhash = allParticipantDevices.Count > 1
            ? MessageFraming.ComputeParticipantListHash(allParticipantDevices)
            : null;
        Message dsmMsg = MessageFraming.WrapDeviceSentMessage(msgToSend, toBare.ToString());
        if (!string.IsNullOrEmpty(dsmPhash))
        {
            dsmMsg.DeviceSentMessage.Phash = dsmPhash;
        }
        byte[] paddedDsm = MessageFraming.EncodeAndPadMessage(dsmMsg);

        var encryptedDevices = new List<(Jid DeviceJid, string EncType, byte[] Ciphertext)>(targetDevices.Count);
        bool anyPkmsg = false;

        foreach (Jid dev in targetDevices)
        {
            if (rejectedSet.Contains(dev.ToProtocolAddress()) || !ensured.Sessions.TryGetValue(dev, out SignalSession? session))
            {
                continue;
            }

            bool isOwnCompanion = string.Equals(dev.User, ownBare.User, StringComparison.OrdinalIgnoreCase) && !isSelfChat;
            byte[] plaintext = isOwnCompanion ? paddedDsm : paddedDm;
            SignalEncryptedPayload enc = SignalCipher.Encrypt(session, plaintext, _credentials.IdentityKey, _credentials.RegistrationId);
            await _backend.Signal.SaveSessionAsync(dev.ToProtocolAddress(), session, ct).ConfigureAwait(false);

            string encType = enc.WireType;
            if (enc.Type == SignalCiphertextType.PreKeyMessage)
            {
                anyPkmsg = true;
            }
            encryptedDevices.Add((dev, encType, enc.Bytes));
        }

        string stanzaType = MessageStanzas.ClassifyStanzaType(msgToSend);
        string? mediaType = MessageStanzas.ClassifyMediaType(msgToSend);
        bool hideDecryptFail = MessageStanzas.ShouldHideDecryptFail(msgToSend, editAttribute);
        Jid? peerRecipientPn = isSelfChat && toBare.IsLid() ? _credentials.Pn?.ToNonAd() : null;
        IReadOnlyList<Node>? resolvedExtraNodes = AppendInferredMetaIfAbsent(msgToSend, extraNodes);

        Node stanza = MessageStanzas.BuildDmStanza(
            msgId,
            toBare,
            stanzaType,
            encryptedDevices,
            mediaType,
            hideDecryptFail,
            deviceIdentityBytes: anyPkmsg ? _credentials.AccountIdentityBytes : null,
            reportingToken: reportingToken,
            editAttribute: editAttribute,
            peerRecipientPn: peerRecipientPn,
            category: category,
            extraNodes: resolvedExtraNodes);

        var allowedUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { toBare.ToString(), ownBare.ToString() };
        _retryCoordinator.CacheSentMessage(new CachedSentMessage(
            msgId,
            toBare,
            stanzaType,
            mediaType,
            hideDecryptFail,
            unpaddedBytes,
            AddressingMode: null,
            allowedUsers));

        await _socket.SendNodeAsync(stanza, ct).ConfigureAwait(false);
        await PersistSentMetadataAsync(msgId, toBare.ToString(), ownBare.ToString(), msgToSend, reportingToken?.MessageSecret32, ct)
            .ConfigureAwait(false);

        return new SendMessageResult(msgId, toBare.ToString(), encryptedDevices.Count, dsmPhash);
    }

    public async Task<SendMessageResult> SendGroupMessageAsync(
        Jid groupJid,
        Message message,
        IReadOnlyList<Jid> participantUsers,
        string? messageId = null,
        bool includeReportingToken = true,
        bool allowCachedDeviceLists = true,
        string? editAttribute = null,
        string? addressingMode = null,
        IReadOnlyList<Node>? extraNodes = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(participantUsers);

        Jid groupBare = groupJid.ToNonAd();
        string groupStr = groupBare.ToString();
        string msgId = string.IsNullOrWhiteSpace(messageId) ? NextMessageId() : messageId;

        bool preferLid = string.Equals(addressingMode, "lid", StringComparison.OrdinalIgnoreCase)
            || participantUsers.Any(p => p.IsLid());
        Jid ownBare = preferLid && _credentials.Lid is Jid lid ? lid.ToNonAd() : (_credentials.Pn?.ToNonAd() ?? _credentials.Lid!.Value.ToNonAd());
        ushort ownDeviceId = GetOwnDeviceId();
        Jid ownFull = ownBare with { Device = ownDeviceId };
        string ownSenderAddr = ownFull.ToProtocolAddress();

        var uniqueUsers = new List<Jid>();
        var seenUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Jid p in participantUsers.Append(ownBare))
        {
            Jid b = p.ToNonAd();
            if (seenUsers.Add(b.ToString()))
            {
                uniqueUsers.Add(b);
            }
        }

        IReadOnlyDictionary<string, ResolvedDeviceList> deviceLists =
            await _usync.GetDeviceListsAsync(uniqueUsers, UsyncContext.Message, allowCachedDeviceLists, ct).ConfigureAwait(false);

        var allDevices = new List<Jid>();
        foreach (Jid user in uniqueUsers)
        {
            if (deviceLists.TryGetValue(user.ToString(), out ResolvedDeviceList? list))
            {
                foreach (uint devId in list.DeviceIds)
                {
                    allDevices.Add(user with { Device = (ushort)devId });
                }
            }
            else
            {
                allDevices.Add(user);
            }
        }

        string phash = MessageFraming.ComputeParticipantListHash(allDevices);

        byte[]? skBytes = await _backend.Signal.GetSenderKeyAsync(groupStr, ownSenderAddr, ct).ConfigureAwait(false);
        SenderKeyRecord senderRecord = skBytes is { Length: > 0 }
            ? SignalStateCodec.DeserializeSenderKeyRecord(skBytes)
            : new SenderKeyRecord();

        byte[] skdmBytes = SenderKeyCipher.CreateDistribution(senderRecord);

        IReadOnlySet<string> distributedDevices = await _backend.Signal.GetSenderKeyDistributedDevicesAsync(groupStr, ct)
            .ConfigureAwait(false);

        string ownFullStr = ownFull.ToString();
        var needSkdmDevices = allDevices
            .Where(d => !string.Equals(d.ToString(), ownFullStr, StringComparison.OrdinalIgnoreCase)
                && !distributedDevices.Contains(d.ToString()))
            .ToArray();

        var skdmTargets = new List<(Jid DeviceJid, string EncType, byte[] SkdmCiphertext)>(needSkdmDevices.Length);
        var newlyDistributed = new List<string>(needSkdmDevices.Length);
        bool anyPkmsg = false;

        if (needSkdmDevices.Length > 0)
        {
            SessionEnsureResult ensured = await _preKeyFetcher.EnsureSessionsAsync(needSkdmDevices, reason: null, ct).ConfigureAwait(false);
            var rejectedSet = ensured.RejectedDevices
                .Where(r => r.Code == 406)
                .Select(r => r.DeviceJid.ToProtocolAddress())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            byte[] paddedSkdm = MessageFraming.EncodeAndPadSkdmWrapper(groupStr, skdmBytes);

            foreach (Jid targetDev in needSkdmDevices)
            {
                if (rejectedSet.Contains(targetDev.ToProtocolAddress()) || !ensured.Sessions.TryGetValue(targetDev, out SignalSession? session))
                {
                    continue;
                }

                SignalEncryptedPayload enc = SignalCipher.Encrypt(session, paddedSkdm, _credentials.IdentityKey, _credentials.RegistrationId);
                await _backend.Signal.SaveSessionAsync(targetDev.ToProtocolAddress(), session, ct).ConfigureAwait(false);

                string encType = enc.WireType;
                if (enc.Type == SignalCiphertextType.PreKeyMessage)
                {
                    anyPkmsg = true;
                }

                skdmTargets.Add((targetDev, encType, enc.Bytes));
                newlyDistributed.Add(targetDev.ToString());
            }
        }

        Message msgToSend = message.Clone();
        ReportingTokenResult? reportingToken = null;
        if (includeReportingToken)
        {
            byte[]? preSecret = msgToSend.MessageContextInfo?.MessageSecret is { Length: 32 } ps ? ps.ToByteArray() : null;
            reportingToken = ReportingTokenGenerator.Generate(msgToSend, msgId, ownFull.ToString(), groupBare.ToString(), existingSecret32: preSecret);
            if (reportingToken is not null)
            {
                msgToSend = ReportingTokenGenerator.AttachMessageContext(msgToSend, reportingToken.MessageSecret32, reportingToken.Version);
            }
        }

        byte[] unpaddedBytes = msgToSend.ToByteArray();
        byte[] paddedGroupPayload = MessageFraming.PadMessageV2(unpaddedBytes);
        byte[] skmsgCiphertext = SenderKeyCipher.Encrypt(senderRecord, paddedGroupPayload);

        await _backend.Signal.SaveSenderKeyAsync(
            groupStr,
            ownSenderAddr,
            SignalStateCodec.SerializeSenderKeyRecord(senderRecord),
            ct).ConfigureAwait(false);

        string stanzaType = MessageStanzas.ClassifyStanzaType(msgToSend);
        string? mediaType = MessageStanzas.ClassifyMediaType(msgToSend);
        bool hideDecryptFail = MessageStanzas.ShouldHideDecryptFail(msgToSend, editAttribute);
        IReadOnlyList<Node>? resolvedExtraNodes = AppendInferredMetaIfAbsent(msgToSend, extraNodes);

        Node stanza = MessageStanzas.BuildGroupStanza(
            msgId,
            groupBare,
            stanzaType,
            skmsgCiphertext,
            mediaType,
            hideDecryptFail,
            phash,
            skdmTargets,
            deviceIdentityBytes: anyPkmsg ? _credentials.AccountIdentityBytes : null,
            reportingToken: reportingToken,
            editAttribute: editAttribute,
            addressingMode: addressingMode,
            extraNodes: resolvedExtraNodes);

        _retryCoordinator.CacheSentMessage(new CachedSentMessage(
            msgId,
            groupBare,
            stanzaType,
            mediaType,
            hideDecryptFail,
            unpaddedBytes,
            addressingMode,
            seenUsers));

        await _socket.SendNodeAsync(stanza, ct).ConfigureAwait(false);

        if (newlyDistributed.Count > 0)
        {
            await _backend.Signal.MarkSenderKeyDistributedAsync(groupStr, newlyDistributed, ct).ConfigureAwait(false);
        }

        await PersistSentMetadataAsync(msgId, groupStr, ownBare.ToString(), msgToSend, reportingToken?.MessageSecret32, ct)
            .ConfigureAwait(false);

        return new SendMessageResult(msgId, groupStr, allDevices.Count, phash);
    }

    /// <summary>
    /// Sends a quoted reply text message in a DM or group chat.
    /// </summary>
    public Task<SendMessageResult> SendReplyTextAsync(
        Jid toJid,
        string text,
        MessageKey quotedKey,
        Message quotedMessage,
        IReadOnlyList<Jid>? groupParticipants = null,
        string? messageId = null,
        CancellationToken ct = default)
    {
        Message reply = RichMessageBuilders.BuildReplyText(text, quotedKey, quotedMessage);
        return SendRichMessageAsync(toJid, reply, groupParticipants, messageId, ct: ct);
    }

    public Task<SendMessageResult> SendReactionAsync(
        Jid toJid,
        MessageKey targetKey,
        string emojiText,
        bool encrypted = false,
        string? parentOriginalSenderJid = null,
        IReadOnlyList<Jid>? groupParticipants = null,
        CancellationToken ct = default)
    {
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (!encrypted)
        {
            Message rx = RichMessageBuilders.BuildReaction(targetKey, emojiText, nowMs);
            return SendRichMessageAsync(toJid, rx, groupParticipants, ct: ct);
        }

        return SendEncryptedReactionAsync(toJid, targetKey, emojiText, nowMs, parentOriginalSenderJid, groupParticipants, ct);
    }

    private async Task<SendMessageResult> SendEncryptedReactionAsync(
        Jid toJid,
        MessageKey targetKey,
        string emojiText,
        long nowMs,
        string? parentOriginalSenderJid,
        IReadOnlyList<Jid>? groupParticipants,
        CancellationToken ct)
    {
        (byte[] Secret, long? Timestamp)? stored = await _backend.MsgSecrets.GetMsgSecretAsync(targetKey.Id, ct).ConfigureAwait(false);
        if (stored is not { Secret.Length: 32 } entry)
        {
            throw new InvalidOperationException($"Parent message secret not found for '{targetKey.Id}'.");
        }

        string reactor = GetOwnUserForAddressing(toJid).ToNonAdString();
        string parentSender = parentOriginalSenderJid
            ?? (targetKey.HasParticipant && !string.IsNullOrEmpty(targetKey.Participant) ? targetKey.Participant : toJid.ToNonAdString());

        Message encRx = RichMessageBuilders.BuildEncReaction(targetKey, emojiText, nowMs, entry.Secret, parentSender, reactor);
        return await SendRichMessageAsync(toJid, encRx, groupParticipants, ct: ct).ConfigureAwait(false);
    }

    public async Task<SendMessageResult> SendMessageEditAsync(
        Jid toJid,
        MessageKey originalKey,
        Message newContent,
        bool preferSecretEncrypted = true,
        string? originalSenderJid = null,
        IReadOnlyList<Jid>? groupParticipants = null,
        CancellationToken ct = default)
    {
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        Message editProto;

        if (preferSecretEncrypted
            && await _backend.MsgSecrets.GetMsgSecretAsync(originalKey.Id, ct).ConfigureAwait(false) is { Secret.Length: 32 } entry)
        {
            string editor = GetOwnUserForAddressing(toJid).ToNonAdString();
            string origSender = originalSenderJid ?? editor;
            editProto = RichMessageBuilders.BuildSecretMessageEdit(originalKey, newContent, nowMs, entry.Secret, origSender, editor);
        }
        else
        {
            editProto = RichMessageBuilders.BuildLegacyMessageEdit(originalKey, newContent, nowMs);
        }

        return await SendRichMessageAsync(
            toJid,
            editProto,
            groupParticipants,
            editAttribute: RichMessageBuilders.EditAttrMessageEdit,
            ct: ct).ConfigureAwait(false);
    }

    public Task<SendMessageResult> SendRevokeAsync(
        Jid toJid,
        MessageKey targetKey,
        bool isAdminRevoke = false,
        IReadOnlyList<Jid>? groupParticipants = null,
        CancellationToken ct = default)
    {
        (Message revokeProto, string editAttr) = RichMessageBuilders.BuildRevoke(targetKey, isAdminRevoke);
        return SendRichMessageAsync(toJid, revokeProto, groupParticipants, editAttribute: editAttr, ct: ct);
    }

    public Task<SendMessageResult> SendPinInChatAsync(
        Jid toJid,
        MessageKey targetKey,
        bool pin,
        IReadOnlyList<Jid>? groupParticipants = null,
        CancellationToken ct = default)
    {
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        Message msg = RichMessageBuilders.BuildPinInChat(targetKey, pin, nowMs);
        return SendRichMessageAsync(toJid, msg, groupParticipants, ct: ct);
    }

    public Task<SendMessageResult> SendKeepInChatAsync(
        Jid toJid,
        MessageKey targetKey,
        bool keep,
        IReadOnlyList<Jid>? groupParticipants = null,
        CancellationToken ct = default)
    {
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        Message msg = RichMessageBuilders.BuildKeepInChat(targetKey, keep, nowMs);
        return SendRichMessageAsync(toJid, msg, groupParticipants, ct: ct);
    }

    public Task<SendMessageResult> SendPollAsync(
        Jid toJid,
        string question,
        IReadOnlyList<string> options,
        uint selectableCount = 1,
        int version = 3,
        string? quizCorrectOption = null,
        bool allowAddOption = false,
        IReadOnlyList<Jid>? groupParticipants = null,
        string? messageId = null,
        CancellationToken ct = default)
    {
        Message poll = RichMessageBuilders.BuildPollCreation(
            question, options, selectableCount, version, quizCorrectOption, allowAddOption);
        return SendRichMessageAsync(toJid, poll, groupParticipants, messageId, ct: ct);
    }

    public async Task<SendMessageResult> SendPollVoteAsync(
        Jid toJid,
        MessageKey pollKey,
        IReadOnlyList<string> selectedOptions,
        string pollCreatorJid,
        IReadOnlyList<Jid>? groupParticipants = null,
        CancellationToken ct = default)
    {
        (byte[] Secret, long? Timestamp)? stored = await _backend.MsgSecrets.GetMsgSecretAsync(pollKey.Id, ct).ConfigureAwait(false);
        if (stored is not { Secret.Length: 32 } entry)
        {
            throw new InvalidOperationException($"Poll encryption secret not found for '{pollKey.Id}'.");
        }

        string voter = GetOwnUserForAddressing(toJid).ToNonAdString();
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        Message voteMsg = RichMessageBuilders.BuildPollVote(pollKey, selectedOptions, entry.Secret, pollCreatorJid, voter, nowMs);
        return await SendRichMessageAsync(toJid, voteMsg, groupParticipants, ct: ct).ConfigureAwait(false);
    }

    public Task<SendMessageResult> SendEventAsync(
        Jid toJid,
        string name,
        string? description,
        long startTimeSeconds,
        long? endTimeSeconds = null,
        string? locationName = null,
        string? joinLink = null,
        IReadOnlyList<Jid>? groupParticipants = null,
        CancellationToken ct = default)
    {
        Message evt = RichMessageBuilders.BuildEvent(name, description, startTimeSeconds, endTimeSeconds, locationName, joinLink);
        return SendRichMessageAsync(toJid, evt, groupParticipants, ct: ct);
    }

    public async Task<SendMessageResult> SendEventResponseAsync(
        Jid toJid,
        MessageKey eventKey,
        Message.Types.EventResponseMessage.Types.EventResponseType responseType,
        string eventCreatorJid,
        int extraGuests = 0,
        IReadOnlyList<Jid>? groupParticipants = null,
        CancellationToken ct = default)
    {
        (byte[] Secret, long? Timestamp)? stored = await _backend.MsgSecrets.GetMsgSecretAsync(eventKey.Id, ct).ConfigureAwait(false);
        if (stored is not { Secret.Length: 32 } entry)
        {
            throw new InvalidOperationException($"Event secret not found for '{eventKey.Id}'.");
        }

        string responder = GetOwnUserForAddressing(toJid).ToNonAdString();
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        Message resp = RichMessageBuilders.BuildEventResponse(eventKey, responseType, nowMs, entry.Secret, eventCreatorJid, responder, extraGuests);
        return await SendRichMessageAsync(toJid, resp, groupParticipants, ct: ct).ConfigureAwait(false);
    }

    public Task<SendMessageResult> SendDraftSyncAsync(
        Jid chatJid,
        string? draftText,
        DraftMessageSync.Types.ClearReason? clearReason = null,
        CancellationToken ct = default)
    {
        Jid ownPeerTarget = _credentials.Lid?.ToNonAd()
            ?? _credentials.Pn?.ToNonAd()
            ?? throw new InvalidOperationException("Device credentials have neither LID nor PN assigned.");

        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        Message syncMsg = RichMessageBuilders.BuildDraftMessageSync(chatJid, draftText, nowMs, clearReason);
        return SendDmAsync(ownPeerTarget, syncMsg, includeReportingToken: false, category: "peer", ct: ct);
    }

    public Task<SendMessageResult> SendRichMessageAsync(
        Jid toJid,
        Message message,
        IReadOnlyList<Jid>? groupParticipants = null,
        string? messageId = null,
        string? editAttribute = null,
        CancellationToken ct = default)
    {
        if (toJid.Server == Server.Broadcast)
        {
            if (message.ReactionMessage?.Key?.Participant is { Length: > 0 } authorStr && Jid.TryParse(authorStr, out Jid authorJid))
            {
                return SendDmAsync(authorJid.ToNonAd(), message, messageId, editAttribute: editAttribute, ct: ct);
            }
            throw new ArgumentException("status@broadcast sends require StatusClient (or ReactionMessage targeting an author).", nameof(toJid));
        }

        if (toJid.IsGroup())
        {
            if (groupParticipants is null)
            {
                throw new ArgumentException("Group participant JIDs are required when sending to a @g.us chat.", nameof(groupParticipants));
            }
            return SendGroupMessageAsync(toJid, message, groupParticipants, messageId, editAttribute: editAttribute, ct: ct);
        }

        return SendDmAsync(toJid, message, messageId, editAttribute: editAttribute, ct: ct);
    }

    /// <summary>
    /// Requests the primary phone (peer device) to resend a placeholder/undecrypted message via
    /// <c>ProtocolMessageType.PeerDataOperationRequestMessage</c> (<c>PLACEHOLDER_MESSAGE_RESEND</c>).
    /// </summary>
    public async Task<SendMessageResult> RequestPlaceholderResendAsync(
        IReadOnlyList<MessageKey> missingMessageKeys,
        string? messageId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(missingMessageKeys);
        if (missingMessageKeys.Count == 0)
        {
            throw new ArgumentException("At least one MessageKey is required for placeholder resend.", nameof(missingMessageKeys));
        }

        Jid ownPeerTarget = _credentials.Lid?.ToNonAd()
            ?? _credentials.Pn?.ToNonAd()
            ?? throw new InvalidOperationException("Device credentials have neither LID nor PN assigned.");

        Message pdo = MessageStanzas.BuildPlaceholderResendMessage(missingMessageKeys);
        return await SendDmAsync(
            ownPeerTarget,
            pdo,
            messageId: messageId,
            includeReportingToken: false,
            category: "peer",
            ct: ct).ConfigureAwait(false);
    }

    public bool TryResolveMessageAck(Node ackNode)
    {
        ArgumentNullException.ThrowIfNull(ackNode);
        if (!string.Equals(ackNode.Tag, "ack", StringComparison.Ordinal))
        {
            return false;
        }

        string? id = ackNode.GetAttr("id");
        if (string.IsNullOrEmpty(id) || !_ackWaiters.TryRemove(id, out var tcs))
        {
            return false;
        }

        return tcs.TrySetResult((ackNode.GetAttr("error"), ackNode.GetAttr("error_text") ?? ackNode.GetAttr("text")));
    }

    /// <summary>
    /// Sends a pairwise-encrypted <c>&lt;message to="{groupJid}"&gt;</c> stanza ONLY to the specified
    /// <paramref name="targetUsers"/> and own companion devices (no group <c>skmsg</c>), used for
    /// selective Group History Bundle delivery.
    /// </summary>
    public async Task<SendDirectGroupResult> SendGroupDirectAsync(
        Jid groupJid,
        IReadOnlyList<Jid> targetUsers,
        Message message,
        string? messageId = null,
        string? addressingMode = null,
        bool awaitServerAck = true,
        IReadOnlyList<Node>? extraNodes = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetUsers);
        ArgumentNullException.ThrowIfNull(message);

        Jid groupBare = groupJid.ToNonAd();
        string msgId = string.IsNullOrWhiteSpace(messageId) ? NextMessageId() : messageId;

        bool preferLid = string.Equals(addressingMode, "lid", StringComparison.OrdinalIgnoreCase)
            || targetUsers.Any(p => p.IsLid());
        Jid ownBare = preferLid && _credentials.Lid is Jid lid
            ? lid.ToNonAd()
            : (_credentials.Pn?.ToNonAd() ?? _credentials.Lid!.Value.ToNonAd());
        ushort ownDeviceId = GetOwnDeviceId();
        Jid ownFull = ownBare with { Device = ownDeviceId };
        string ownFullStr = ownFull.ToString();

        var uniqueUsers = new List<Jid>();
        var seenUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Jid u in targetUsers.Append(ownBare))
        {
            Jid bare = u.ToNonAd();
            if (seenUsers.Add(bare.ToString()))
            {
                uniqueUsers.Add(bare);
            }
        }

        IReadOnlyDictionary<string, ResolvedDeviceList> deviceLists =
            await _usync.GetDeviceListsAsync(uniqueUsers, UsyncContext.Message, true, ct).ConfigureAwait(false);

        var targetDevices = new List<Jid>();
        foreach (Jid u in uniqueUsers)
        {
            if (deviceLists.TryGetValue(u.ToString(), out ResolvedDeviceList? list))
            {
                foreach (uint devId in list.DeviceIds)
                {
                    Jid d = u with { Device = (ushort)devId };
                    if (!string.Equals(d.ToString(), ownFullStr, StringComparison.OrdinalIgnoreCase))
                    {
                        targetDevices.Add(d);
                    }
                }
            }
            else if (!string.Equals(u.ToString(), ownBare.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                targetDevices.Add(u);
            }
        }

        SessionEnsureResult ensured = await _preKeyFetcher.EnsureSessionsAsync(targetDevices, reason: null, ct).ConfigureAwait(false);
        var rejectedSet = ensured.RejectedDevices
            .Where(r => r.Code == 406)
            .Select(r => r.DeviceJid.ToProtocolAddress())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        byte[] unpaddedBytes = message.ToByteArray();
        byte[] paddedDm = MessageFraming.PadMessageV2(unpaddedBytes);
        Message dsmMsg = MessageFraming.WrapDeviceSentMessage(message, groupBare.ToString());
        byte[] paddedDsm = MessageFraming.EncodeAndPadMessage(dsmMsg);

        var encryptedDevices = new List<(Jid DeviceJid, string EncType, byte[] Ciphertext)>(targetDevices.Count);
        bool anyPkmsg = false;

        foreach (Jid dev in targetDevices)
        {
            if (rejectedSet.Contains(dev.ToProtocolAddress()) || !ensured.Sessions.TryGetValue(dev, out SignalSession? session))
            {
                continue;
            }

            bool isOwnCompanion = string.Equals(dev.User, ownBare.User, StringComparison.OrdinalIgnoreCase);
            byte[] plaintext = isOwnCompanion ? paddedDsm : paddedDm;
            SignalEncryptedPayload enc = SignalCipher.Encrypt(session, plaintext, _credentials.IdentityKey, _credentials.RegistrationId);
            await _backend.Signal.SaveSessionAsync(dev.ToProtocolAddress(), session, ct).ConfigureAwait(false);

            if (enc.Type == SignalCiphertextType.PreKeyMessage)
            {
                anyPkmsg = true;
            }
            encryptedDevices.Add((dev, enc.WireType, enc.Bytes));
        }

        string stanzaType = MessageStanzas.ClassifyStanzaType(message);
        string? mediaType = MessageStanzas.ClassifyMediaType(message);
        bool hideDecryptFail = MessageStanzas.ShouldHideDecryptFail(message);
        IReadOnlyList<Node>? resolvedExtra = AppendInferredMetaIfAbsent(message, extraNodes);
        string? phash = targetDevices.Count > 0 ? MessageFraming.ComputeParticipantListHash(targetDevices) : null;

        Node stanza = MessageStanzas.BuildGroupDirectStanza(
            msgId,
            groupBare,
            stanzaType,
            encryptedDevices,
            mediaType,
            hideDecryptFail,
            phash,
            deviceIdentityBytes: anyPkmsg ? _credentials.AccountIdentityBytes : null,
            reportingToken: null,
            addressingMode: addressingMode,
            extraNodes: resolvedExtra);

        _retryCoordinator.CacheSentMessage(new CachedSentMessage(
            msgId,
            groupBare,
            stanzaType,
            mediaType,
            hideDecryptFail,
            unpaddedBytes,
            addressingMode,
            seenUsers));

        var (ackReceived, errCode, errText) = await SendWithOptionalAckWaitAsync(stanza, msgId, awaitServerAck, ct).ConfigureAwait(false);
        return new SendDirectGroupResult(msgId, ackReceived, encryptedDevices.Count, targetDevices.Count, errCode, errText);
    }

    public async Task<SendDirectGroupResult> SendGroupWithAckAsync(
        Jid groupJid,
        Message message,
        IReadOnlyList<Jid> participantUsers,
        string? messageId = null,
        string? addressingMode = null,
        CancellationToken ct = default)
    {
        string msgId = string.IsNullOrWhiteSpace(messageId) ? NextMessageId() : messageId;
        var ackTcs = new TaskCompletionSource<(string? ErrorCode, string? ErrorText)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ackWaiters.TryAdd(msgId, ackTcs))
        {
            throw new InvalidOperationException($"Duplicate pending message ACK waiter for ID '{msgId}'.");
        }

        try
        {
            SendMessageResult sent = await SendGroupMessageAsync(
                groupJid,
                message,
                participantUsers,
                messageId: msgId,
                addressingMode: addressingMode,
                ct: ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DefaultAckTimeout);
            try
            {
                var (errCode, errText) = await ackTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                return new SendDirectGroupResult(sent.MessageId, AckReceived: true, sent.TargetDeviceCount, sent.TargetDeviceCount, errCode, errText);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new SendDirectGroupResult(sent.MessageId, AckReceived: false, sent.TargetDeviceCount, sent.TargetDeviceCount);
            }
        }
        finally
        {
            _ackWaiters.TryRemove(msgId, out _);
        }
    }

    private async Task<(bool AckReceived, string? ErrorCode, string? ErrorText)> SendWithOptionalAckWaitAsync(
        Node stanza,
        string msgId,
        bool awaitServerAck,
        CancellationToken ct)
    {
        if (!awaitServerAck)
        {
            await _socket.SendNodeAsync(stanza, ct).ConfigureAwait(false);
            return (false, null, null);
        }

        var ackTcs = new TaskCompletionSource<(string? ErrorCode, string? ErrorText)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ackWaiters.TryAdd(msgId, ackTcs))
        {
            throw new InvalidOperationException($"Duplicate pending message ACK waiter for ID '{msgId}'.");
        }

        try
        {
            await _socket.SendNodeAsync(stanza, ct).ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DefaultAckTimeout);
            try
            {
                var (errCode, errText) = await ackTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                return (true, errCode, errText);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (false, null, null);
            }
        }
        finally
        {
            _ackWaiters.TryRemove(msgId, out _);
        }
    }

    private static IReadOnlyList<Node>? AppendInferredMetaIfAbsent(Message message, IReadOnlyList<Node>? extraNodes)
    {
        Node? inferred = MessageStanzas.InferMetaNode(message);
        if (inferred is null)
        {
            return extraNodes;
        }
        if (extraNodes is null || extraNodes.Count == 0)
        {
            return [inferred];
        }
        if (extraNodes.Any(n => string.Equals(n.Tag, "meta", StringComparison.Ordinal)))
        {
            return extraNodes;
        }
        var combined = new List<Node>(extraNodes.Count + 1);
        combined.AddRange(extraNodes);
        combined.Add(inferred);
        return combined;
    }

    private async Task PersistSentMetadataAsync(
        string msgId,
        string chatBare,
        string ownBare,
        Message msgToSend,
        byte[]? messageSecret32,
        CancellationToken ct)
    {
        long ts = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        byte[]? effectiveSecret = messageSecret32 is { Length: 32 }
            ? messageSecret32
            : (msgToSend.MessageContextInfo?.MessageSecret is { Length: 32 } ctxSec ? ctxSec.ToByteArray() : null);

        if (effectiveSecret is { Length: 32 })
        {
            await _backend.MsgSecrets.PutMsgSecretsAsync([(msgId, effectiveSecret, ts)], ct).ConfigureAwait(false);
        }

        MessageRecord record = MessageReceiver.ToMessageRecord(msgId, chatBare, ownBare, fromMe: true, (ulong)ts, msgToSend);
        await _backend.History.SaveMessageAsync(record, ct).ConfigureAwait(false);
    }

    private Jid GetOwnUserForAddressing(Jid recipientBare)
    {
        if ((recipientBare.IsLid() || recipientBare.Server == Server.Bot) && _credentials.Lid is Jid lid)
        {
            return lid.ToNonAd();
        }
        return _credentials.Pn?.ToNonAd() ?? _credentials.Lid!.Value.ToNonAd();
    }

    private ushort GetOwnDeviceId() =>
        _credentials.Pn?.Device is > 0 ? _credentials.Pn.Value.Device : _credentials.Lid?.Device ?? 0;
}
