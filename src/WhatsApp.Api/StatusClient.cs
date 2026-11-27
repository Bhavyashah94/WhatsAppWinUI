using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Api;

/// <summary>
/// High-level client for WhatsApp Statuses / Stories (<c>status@broadcast</c> and
/// <c>GroupStatusMessageV2</c>): status text/image/video/music posts, revokes,
/// pairwise author reactions, group status updates, and AppState status privacy/mute state.
/// </summary>
public sealed class StatusClient
{
    private readonly NoiseSessionSocket _socket;
    private readonly UsyncClient _usync;
    private readonly PreKeyFetchClient _preKeyFetcher;
    private readonly MessageSender _messageSender;
    private readonly IWhatsAppBackend _backend;
    private readonly ILidPnCache _lidPnCache;
    private readonly DeviceCredentials _credentials;
    private readonly RetryCoordinator _retryCoordinator;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, bool> _mutedUsers = new(StringComparer.OrdinalIgnoreCase);

    private SyncActionValue.Types.StatusPrivacyAction? _audiencePrivacy;

    public StatusClient(
        NoiseSessionSocket socket,
        UsyncClient usync,
        PreKeyFetchClient preKeyFetcher,
        MessageSender messageSender,
        IWhatsAppBackend backend,
        ILidPnCache lidPnCache,
        DeviceCredentials credentials,
        RetryCoordinator retryCoordinator,
        TimeProvider? timeProvider = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _usync = usync ?? throw new ArgumentNullException(nameof(usync));
        _preKeyFetcher = preKeyFetcher ?? throw new ArgumentNullException(nameof(preKeyFetcher));
        _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _retryCoordinator = retryCoordinator ?? throw new ArgumentNullException(nameof(retryCoordinator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SyncActionValue.Types.StatusPrivacyAction? AudiencePrivacy => Volatile.Read(ref _audiencePrivacy);

    public bool IsUserMuted(Jid userJid) =>
        _mutedUsers.TryGetValue(userJid.ToNonAdString(), out bool muted) && muted;

    public void ApplySyncedPrivacy(SyncActionValue.Types.StatusPrivacyAction privacy)
    {
        ArgumentNullException.ThrowIfNull(privacy);
        Volatile.Write(ref _audiencePrivacy, privacy.Clone());
    }

    public void ApplySyncedUserMute(string userJid, bool muted)
    {
        if (!string.IsNullOrWhiteSpace(userJid))
        {
            _mutedUsers[userJid] = muted;
        }
    }

    public Task<SendMessageResult> SendTextStatusAsync(
        string text,
        IReadOnlyList<Jid> recipients,
        uint backgroundArgb = 0xFF1E6E4F,
        Message.Types.ExtendedTextMessage.Types.FontType font = Message.Types.ExtendedTextMessage.Types.FontType.System,
        StatusSendOptions? options = null,
        CancellationToken ct = default)
    {
        Message msg = StatusStanzas.BuildTextStatus(text, backgroundArgb, font);
        return SendStatusMessageAsync(msg, recipients, options, ct);
    }

    public Task<SendMessageResult> SendImageStatusAsync(
        UploadedMediaResult upload,
        IReadOnlyList<Jid> recipients,
        string mimetype = "image/jpeg",
        byte[]? jpegThumbnail = null,
        string? caption = null,
        StatusSendOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        long ts = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        Message msg = StatusStanzas.BuildImageStatus(
            upload.DirectPath,
            mimetype,
            upload.FileSha256,
            upload.FileEncSha256,
            upload.MediaKey32,
            upload.FileLength,
            ts,
            jpegThumbnail,
            caption);
        return SendStatusMessageAsync(msg, recipients, options, ct);
    }

    public Task<SendMessageResult> SendVideoStatusAsync(
        UploadedMediaResult upload,
        uint durationSeconds,
        IReadOnlyList<Jid> recipients,
        string mimetype = "video/mp4",
        byte[]? jpegThumbnail = null,
        string? caption = null,
        StatusSendOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        long ts = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        Message msg = StatusStanzas.BuildVideoStatus(
            upload.DirectPath,
            mimetype,
            upload.FileSha256,
            upload.FileEncSha256,
            upload.MediaKey32,
            upload.FileLength,
            ts,
            durationSeconds,
            jpegThumbnail,
            caption);
        return SendStatusMessageAsync(msg, recipients, options, ct);
    }

    public Task<SendMessageResult> SendMusicStatusAsync(
        string songTitle,
        string artistName,
        IReadOnlyList<Jid> recipients,
        string? songUri = null,
        string? artworkUri = null,
        StatusSendOptions? options = null,
        CancellationToken ct = default)
    {
        Message msg = StatusStanzas.BuildMusicStatus(songTitle, artistName, songUri, artworkUri);
        return SendStatusMessageAsync(msg, recipients, options, ct);
    }

    public Task<SendMessageResult> RevokeStatusAsync(
        string targetMessageId,
        IReadOnlyList<Jid> recipients,
        StatusSendOptions? options = null,
        CancellationToken ct = default)
    {
        if (options?.StanzaIdOverride is { Length: > 0 } stanzaId
            && string.Equals(stanzaId, targetMessageId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Revoke stanza ID must differ from the target status message ID.", nameof(options));
        }

        Message revoke = StatusStanzas.BuildStatusRevoke(targetMessageId);
        return SendStatusMessageAsync(revoke, recipients, options, ct);
    }

    public Task<SendMessageResult> SendStatusReactionAsync(
        MessageKey statusKey,
        string emojiText,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statusKey);
        if (!statusKey.HasParticipant || !Jid.TryParse(statusKey.Participant, out Jid author) || !(author.IsPn() || author.IsLid()))
        {
            throw new ArgumentException("Status reaction requires statusKey.Participant set to status author JID.", nameof(statusKey));
        }

        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        Message rx = RichMessageBuilders.BuildReaction(statusKey, emojiText, nowMs);
        return _messageSender.SendDmAsync(author.ToNonAd(), rx, ct: ct);
    }

    public Task<SendMessageResult> SendGroupStatusV2Async(
        Jid groupJid,
        Message innerMessage,
        IReadOnlyList<Jid> groupParticipants,
        string? messageId = null,
        CancellationToken ct = default)
    {
        if (!groupJid.IsGroup())
        {
            throw new ArgumentException($"Group status requires a @g.us JID, got '{groupJid}'.", nameof(groupJid));
        }

        Message wrapped = StatusStanzas.BuildGroupStatusV2(innerMessage);
        return _messageSender.SendGroupMessageAsync(groupJid, wrapped, groupParticipants, messageId, ct: ct);
    }

    public async Task SetUserStatusMutedAsync(
        SyncdCoordinator syncd,
        Jid userJid,
        bool muted,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(syncd);
        byte[] idxJson = AppStateSchemas.UserStatusMute.BuildIndex(userJid.ToNonAdString());
        var val = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            UserStatusMuteAction = new SyncActionValue.Types.UserStatusMuteAction { Muted = muted }
        };
        await syncd.SendMutationAsync(
            AppStateSchemas.UserStatusMute.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            idxJson,
            val,
            actionVersion: AppStateSchemas.UserStatusMute.Version,
            ct: ct).ConfigureAwait(false);
        ApplySyncedUserMute(userJid.ToNonAdString(), muted);
    }

    public async Task<SendMessageResult> SendStatusMessageAsync(
        Message message,
        IReadOnlyList<Jid> recipients,
        StatusSendOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Count == 0)
        {
            throw new ArgumentException("Cannot send status with no recipients.", nameof(recipients));
        }

        StatusSendOptions opts = options ?? new StatusSendOptions();
        if (_credentials.Lid is not Jid ownLid)
        {
            throw new InvalidOperationException("Cannot send status: device has no LID assigned.");
        }

        string statusChatStr = StatusStanzas.StatusBroadcastJid.ToString();
        string msgId = opts.StanzaIdOverride ?? opts.MessageIdOverride ?? _messageSender.NextMessageId();

        var resolvedUsers = new List<Jid>(recipients.Count);
        foreach (Jid raw in recipients)
        {
            if (!(raw.IsPn() || raw.IsLid()))
            {
                throw new ArgumentException($"Status recipient '{raw}' must be a user JID (PN or LID).");
            }
            if (raw.IsPn() && _lidPnCache.TryGetLid(raw.ToNonAdString(), out string? cachedLid) && !string.IsNullOrEmpty(cachedLid) && Jid.TryParse(cachedLid, out Jid parsedLid))
            {
                resolvedUsers.Add(parsedLid.ToNonAd());
            }
            else
            {
                resolvedUsers.Add(raw.ToNonAd());
            }
        }

        Jid ownLidBare = ownLid.ToNonAd();
        IReadOnlyList<Jid> audience = StatusStanzas.AssembleStatusParticipants(resolvedUsers, ownLidBare);

        ushort ownDevId = _credentials.Lid?.Device is > 0 ? _credentials.Lid.Value.Device : (_credentials.Pn?.Device ?? 0);
        Jid ownFull = ownLidBare with { Device = ownDevId };
        string ownSenderAddr = ownFull.ToProtocolAddress();

        IReadOnlyDictionary<string, ResolvedDeviceList> deviceLists =
            await _usync.GetDeviceListsAsync(audience, UsyncContext.Message, allowCachedOnly: true, ct).ConfigureAwait(false);

        var allDevices = new List<Jid>();
        foreach (Jid user in audience)
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

        byte[]? skBytes = await _backend.Signal.GetSenderKeyAsync(statusChatStr, ownSenderAddr, ct).ConfigureAwait(false);
        SenderKeyRecord senderRecord = skBytes is { Length: > 0 }
            ? SignalStateCodec.DeserializeSenderKeyRecord(skBytes)
            : new SenderKeyRecord();

        byte[] skdmBytes = SenderKeyCipher.CreateDistribution(senderRecord);
        IReadOnlySet<string> distributed = await _backend.Signal.GetSenderKeyDistributedDevicesAsync(statusChatStr, ct).ConfigureAwait(false);

        string ownFullStr = ownFull.ToString();
        Jid[] needSkdm = allDevices
            .Where(d => !string.Equals(d.ToString(), ownFullStr, StringComparison.OrdinalIgnoreCase)
                && !distributed.Contains(d.ToString()))
            .ToArray();

        var skdmTargets = new List<(Jid DeviceJid, string EncType, byte[] SkdmCiphertext)>(needSkdm.Length);
        var newlyDistributed = new List<string>(needSkdm.Length);
        bool anyPkmsg = false;

        if (needSkdm.Length > 0)
        {
            SessionEnsureResult ensured = await _preKeyFetcher.EnsureSessionsAsync(needSkdm, reason: null, ct).ConfigureAwait(false);
            var rejected = ensured.RejectedDevices
                .Where(r => r.Code == 406)
                .Select(r => r.DeviceJid.ToProtocolAddress())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            byte[] paddedSkdm = MessageFraming.EncodeAndPadSkdmWrapper(statusChatStr, skdmBytes);

            foreach (Jid targetDev in needSkdm)
            {
                if (rejected.Contains(targetDev.ToProtocolAddress()) || !ensured.Sessions.TryGetValue(targetDev, out SignalSession? session))
                {
                    continue;
                }

                SignalEncryptedPayload enc = SignalCipher.Encrypt(session, paddedSkdm, _credentials.IdentityKey, _credentials.RegistrationId);
                await _backend.Signal.SaveSessionAsync(targetDev.ToProtocolAddress(), session, ct).ConfigureAwait(false);

                if (enc.Type == SignalCiphertextType.PreKeyMessage)
                {
                    anyPkmsg = true;
                }
                skdmTargets.Add((targetDev, enc.WireType, enc.Bytes));
                newlyDistributed.Add(targetDev.ToString());
            }
        }

        byte[] unpaddedBytes = message.ToByteArray();
        byte[] paddedPayload = MessageFraming.PadMessageV2(unpaddedBytes);
        byte[] skmsgCiphertext = SenderKeyCipher.Encrypt(senderRecord, paddedPayload);

        await _backend.Signal.SaveSenderKeyAsync(
            statusChatStr,
            ownSenderAddr,
            SignalStateCodec.SerializeSenderKeyRecord(senderRecord),
            ct).ConfigureAwait(false);

        Node stanza = StatusStanzas.BuildStatusBroadcastStanza(
            msgId,
            audience,
            message,
            skmsgCiphertext,
            skdmTargets,
            opts.Privacy,
            opts.AddressingMode,
            deviceIdentityBytes: anyPkmsg ? _credentials.AccountIdentityBytes : null);

        var allowedUsers = audience.Select(u => u.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _retryCoordinator.CacheSentMessageAsync(new CachedSentMessage(
            msgId,
            StatusStanzas.StatusBroadcastJid,
            MessageStanzas.ClassifyStanzaType(message),
            MessageStanzas.ClassifyMediaType(message),
            MessageStanzas.ShouldHideDecryptFail(message),
            unpaddedBytes,
            opts.AddressingMode,
            allowedUsers), ct).ConfigureAwait(false);

        await _socket.SendNodeAsync(stanza, ct).ConfigureAwait(false);

        if (newlyDistributed.Count > 0)
        {
            await _backend.Signal.MarkSenderKeyDistributedAsync(statusChatStr, newlyDistributed, ct).ConfigureAwait(false);
        }

        return new SendMessageResult(msgId, statusChatStr, allDevices.Count, Phash: null);
    }
}
