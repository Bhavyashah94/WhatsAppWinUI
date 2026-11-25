using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Api;

/// <summary>
/// Generation-scoped <c>&lt;notification&gt;</c> router implementing live WA Web and
/// <c>whatsapp-rust/src/handlers/notification/*</c> state mutations, signal cleanup,
/// sender-key rotation, device list caching, and typed <see cref="WhatsAppEvent"/> dispatch.
/// </summary>
public sealed class NotificationRouter
{
    private readonly IWhatsAppBackend _backend;
    private readonly ILidPnCache _lidPnCache;
    private readonly DeviceCredentials _credentials;
    private readonly NoiseSessionSocket _socket;
    private readonly IqClient _iqClient;
    private readonly SyncdCoordinator? _syncd;
    private readonly MessageReceiver? _messageReceiver;
    private readonly Func<WhatsAppEvent, ValueTask> _emitEvent;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _preKeyUploadGate = new(1, 1);

    public NotificationRouter(
        IWhatsAppBackend backend,
        ILidPnCache lidPnCache,
        DeviceCredentials credentials,
        NoiseSessionSocket socket,
        IqClient iqClient,
        SyncdCoordinator? syncd,
        Func<WhatsAppEvent, ValueTask> emitEvent,
        TimeProvider? timeProvider = null,
        MessageReceiver? messageReceiver = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _syncd = syncd;
        _messageReceiver = messageReceiver;
        _emitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task HandleAsync(
        Node notification,
        bool autoSyncAppState = true,
        bool sendAck = true,
        CancellationToken sessionToken = default,
        CancellationToken handlerToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        string? type = notification.GetAttr("type");
        long ts = NotificationStanzas.ParseTimestampSeconds(notification, _timeProvider);

        switch (type)
        {
            case "server_sync":
                await HandleServerSyncAsync(notification, autoSyncAppState, sessionToken, handlerToken).ConfigureAwait(false);
                break;
            case "privacy_token":
                await HandlePrivacyTokenAsync(notification, ts, handlerToken).ConfigureAwait(false);
                break;
            case "encrypt":
                await HandleEncryptAsync(notification, sessionToken, handlerToken).ConfigureAwait(false);
                break;
            case "devices":
                await HandleDevicesAsync(notification, handlerToken).ConfigureAwait(false);
                break;
            case "w:gp2":
                await HandleGroupAsync(notification, handlerToken).ConfigureAwait(false);
                break;
            case "contacts":
                await HandleContactsAsync(notification, handlerToken).ConfigureAwait(false);
                break;
            case "account_sync":
                await HandleAccountSyncAsync(notification, handlerToken).ConfigureAwait(false);
                break;
            case "picture":
                await HandlePictureAsync(notification, handlerToken).ConfigureAwait(false);
                break;
            case "disappearing_mode":
                await HandleDisappearingModeAsync(notification).ConfigureAwait(false);
                break;
            case "business":
                await HandleBusinessAsync(notification).ConfigureAwait(false);
                break;
            case "status":
                await HandleStatusAsync(notification, ts).ConfigureAwait(false);
                break;
            case "mex":
                await HandleMexAsync(notification).ConfigureAwait(false);
                break;
            case "mediaretry" when _messageReceiver is not null:
                await _messageReceiver.HandleMediaRetryNotificationAsync(notification, mediaKey32: null, handlerToken).ConfigureAwait(false);
                break;
        }

        if (sendAck
            && notification.GetAttr("id") is { Length: > 0 } notifId
            && notification.GetAttr("from") is { Length: > 0 } fromStr
            && Jid.TryParse(fromStr, out Jid fromJid))
        {
            await _socket.SendNodeAsync(AckStanza.Build(notifId, "notification", fromJid, type), handlerToken).ConfigureAwait(false);
        }
    }

    private async Task HandleServerSyncAsync(
        Node notification,
        bool autoSyncAppState,
        CancellationToken sessionToken,
        CancellationToken handlerToken)
    {
        if (_syncd is null || !autoSyncAppState)
        {
            return;
        }

        var staleCollections = new List<string>();
        foreach (Node col in notification.GetChildren("collection"))
        {
            string? name = col.GetAttr("name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            ulong serverVersion = ulong.TryParse(col.GetAttr("version"), out ulong sv) ? sv : 0;
            var (localVer, _) = await _backend.AppSync.GetCollectionStateAsync(name, handlerToken).ConfigureAwait(false);
            if (serverVersion == 0 || localVer < serverVersion)
            {
                staleCollections.Add(name);
            }
        }

        if (staleCollections.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!sessionToken.IsCancellationRequested)
                    {
                        await _syncd.SyncCollectionsAsync(staleCollections, ct: sessionToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Next server_sync or dirty bit retries.
                }
            }, sessionToken);
        }
    }

    private async Task HandlePrivacyTokenAsync(Node notification, long ts, CancellationToken ct)
    {
        string? senderAttr = notification.GetAttr("sender_lid") ?? notification.GetAttr("from");
        if (!string.IsNullOrEmpty(senderAttr) && Jid.TryParse(senderAttr, out Jid senderJid))
        {
            IReadOnlyList<ParsedTcToken> tokens = TcTokenProtocol.ParseTokensResponse(notification);
            foreach (ParsedTcToken tok in tokens)
            {
                await _backend.Protocol.StoreTcTokenAsync(senderJid.ToNonAdString(), tok.Token, tok.Timestamp, ct).ConfigureAwait(false);
            }

            if (tokens.Count > 0)
            {
                await _emitEvent(new PrivacyTokenNotificationEvent(senderJid.ToNonAdString(), tokens.Count, ts)).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleEncryptAsync(Node notification, CancellationToken sessionToken, CancellationToken handlerToken)
    {
        ParsedEncryptNotification? parsed = NotificationStanzas.ParseEncryptNotification(notification, _timeProvider);
        if (parsed is null)
        {
            return;
        }

        switch (parsed.Kind)
        {
            case EncryptNotificationKind.IdentityChange:
                await HandleIdentityChangeAsync(parsed, handlerToken).ConfigureAwait(false);
                break;

            case EncryptNotificationKind.PreKeyLow when parsed.HasCount:
                _ = Task.Run(async () =>
                {
                    if (!await _preKeyUploadGate.WaitAsync(0, sessionToken).ConfigureAwait(false))
                    {
                        return;
                    }
                    try
                    {
                        await PreKeyUploader.EnsureUploadedAsync(_iqClient, _backend, _credentials, force: true, ct: sessionToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Retry on next login or prekey_low push.
                    }
                    finally
                    {
                        _preKeyUploadGate.Release();
                    }
                }, sessionToken);
                break;
        }
    }

    private async Task HandleIdentityChangeAsync(ParsedEncryptNotification parsed, CancellationToken ct)
    {
        if (parsed.From.Device != 0 || IsSelfUser(parsed.From.User))
        {
            return;
        }

        if (parsed.LidJid is Jid lidJid && parsed.From.IsPn())
        {
            await LearnMappingAsync(parsed.From.ToNonAdString(), lidJid.ToNonAdString(), LidPnLearningSource.DeviceNotification, parsed.TimestampSeconds, ct)
                .ConfigureAwait(false);
        }

        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            parsed.From.ToNonAd().ToProtocolAddress(),
            parsed.From.ToNonAdString()
        };
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { parsed.From.User };

        if (parsed.LidJid is Jid explicitLid)
        {
            addresses.Add(explicitLid.ToNonAd().ToProtocolAddress());
            addresses.Add(explicitLid.ToNonAdString());
            users.Add(explicitLid.User);
        }

        string fromBare = parsed.From.ToNonAdString();
        if (parsed.From.IsPn() && _lidPnCache.TryGetLid(fromBare, out string? mappedLid) && !string.IsNullOrEmpty(mappedLid) && Jid.TryParse(mappedLid, out Jid ml))
        {
            addresses.Add(ml.ToProtocolAddress());
            addresses.Add(ml.ToNonAdString());
            users.Add(ml.User);
        }
        else if (parsed.From.IsLid() && _lidPnCache.TryGetPn(fromBare, out string? mappedPn) && !string.IsNullOrEmpty(mappedPn) && Jid.TryParse(mappedPn, out Jid mp))
        {
            addresses.Add(mp.ToProtocolAddress());
            addresses.Add(mp.ToNonAdString());
            users.Add(mp.User);
        }

        foreach (string user in users)
        {
            await _backend.Signal.DeleteSessionsByUserAsync(user, ct).ConfigureAwait(false);
        }

        foreach (string addr in addresses)
        {
            await _backend.Signal.DeleteIdentityAsync(addr, ct).ConfigureAwait(false);
            await _backend.Protocol.DeleteDeviceListAsync(addr, ct).ConfigureAwait(false);
        }

        await RotateOwnSenderKeyAsync("status@broadcast", ct).ConfigureAwait(false);

        await _emitEvent(new IdentityChangeEvent(
            parsed.From.ToNonAdString(),
            parsed.LidJid?.ToNonAdString(),
            Implicit: false,
            parsed.TimestampSeconds)).ConfigureAwait(false);
    }

    private async Task HandleDevicesAsync(Node notification, CancellationToken ct)
    {
        ParsedDevicesNotification? parsed = NotificationStanzas.ParseDevicesNotification(notification, _timeProvider);
        if (parsed is null)
        {
            return;
        }

        foreach (DeviceNotificationElement d in parsed.Devices)
        {
            if (d.DeviceJid.IsPn() && d.LidDeviceJid is Jid l)
            {
                await LearnMappingAsync(d.DeviceJid.ToNonAdString(), l.ToNonAdString(), LidPnLearningSource.DeviceNotification, parsed.TimestampSeconds, ct)
                    .ConfigureAwait(false);
            }
        }

        string userBare = parsed.From.ToNonAdString();
        switch (parsed.Operation)
        {
            case DeviceNotificationOp.Update when !string.IsNullOrEmpty(parsed.ContactHash):
                IReadOnlyList<string> matched = _lidPnCache.LookupByContactHash(parsed.ContactHash);
                foreach (string m in matched)
                {
                    await _backend.Protocol.DeleteDeviceListAsync(m, ct).ConfigureAwait(false);
                }
                if (matched.Count == 0)
                {
                    await _backend.Protocol.DeleteDeviceListAsync(userBare, ct).ConfigureAwait(false);
                }
                await _emitEvent(new DeviceNotificationEvent(
                    matched.Count > 0 ? matched[0] : userBare,
                    "update",
                    Array.Empty<uint>(),
                    parsed.ContactHash,
                    parsed.TimestampSeconds)).ConfigureAwait(false);
                break;

            case DeviceNotificationOp.Remove:
                var existingRem = await _backend.Protocol.GetDeviceListAsync(userBare, ct).ConfigureAwait(false);
                var removeSet = parsed.Devices.Select(d => d.DeviceId).Where(id => id != 0).ToHashSet();
                var updatedRem = existingRem.Where(id => id == 0 || !removeSet.Contains(id)).ToList();
                if (!updatedRem.Contains(0u))
                {
                    updatedRem.Insert(0, 0u);
                }
                await _backend.Protocol.SaveDeviceListAsync(userBare, updatedRem, ct).ConfigureAwait(false);
                foreach (DeviceNotificationElement dev in parsed.Devices.Where(d => d.DeviceId != 0))
                {
                    await _backend.Signal.DeleteSessionAsync(dev.DeviceJid.ToProtocolAddress(), ct).ConfigureAwait(false);
                }
                await _emitEvent(new DeviceNotificationEvent(
                    userBare,
                    "remove",
                    updatedRem,
                    null,
                    parsed.TimestampSeconds)).ConfigureAwait(false);
                break;

            case DeviceNotificationOp.Add:
                var existingAdd = (await _backend.Protocol.GetDeviceListAsync(userBare, ct).ConfigureAwait(false)).ToHashSet();
                existingAdd.Add(0u);

                bool addAuthorized = true;
                if (parsed.SignedKeyIndexBytes is { Length: > 0 } signedBytes)
                {
                    byte[]? primaryId = await _backend.Signal.GetIdentityAsync(parsed.From.ToNonAd().ToProtocolAddress(), ct).ConfigureAwait(false);
                    if (primaryId is { Length: >= 32 })
                    {
                        byte[] norm32 = primaryId.Length == 33 && primaryId[0] == 0x05 ? primaryId[1..] : primaryId[..32];
                        PairingCrypto.DecodedKeyIndexList? decoded = PairingCrypto.VerifyKeyIndexList(signedBytes, norm32);
                        addAuthorized = decoded is not null && (long)decoded.Timestamp == parsed.KeyIndexTimestampSeconds;
                        if (decoded is not null && addAuthorized)
                        {
                            foreach (DeviceNotificationElement d in parsed.Devices)
                            {
                                if (d.DeviceId == 0 || (d.KeyIndex is uint ki && (decoded.ValidIndexes.Contains(ki) || ki > decoded.CurrentIndex)))
                                {
                                    existingAdd.Add(d.DeviceId);
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (DeviceNotificationElement d in parsed.Devices)
                        {
                            existingAdd.Add(d.DeviceId);
                        }
                    }
                }

                if (addAuthorized)
                {
                    uint[] nextList = existingAdd.OrderBy(id => id).ToArray();
                    await _backend.Protocol.SaveDeviceListAsync(userBare, nextList, ct).ConfigureAwait(false);
                    await _emitEvent(new DeviceNotificationEvent(
                        userBare,
                        "add",
                        nextList,
                        null,
                        parsed.TimestampSeconds)).ConfigureAwait(false);
                }
                break;
        }
    }

    private async Task HandleGroupAsync(Node notification, CancellationToken ct)
    {
        ParsedGroupNotification? parsed = NotificationStanzas.ParseGroupNotification(notification, _timeProvider);
        if (parsed is null)
        {
            return;
        }

        if (parsed.IsDirtyNotification)
        {
            foreach (Jid dirtyGroup in parsed.DirtyGroupJids)
            {
                await _emitEvent(new GroupNotificationEvent(
                    dirtyGroup.ToNonAdString(),
                    "groups_dirty",
                    null,
                    Array.Empty<string>(),
                    null,
                    parsed.TimestampSeconds,
                    IsDirtyNotification: true)).ConfigureAwait(false);
            }
            return;
        }

        for (int i = 0; i < parsed.ParticipantJids.Count && i < parsed.ParticipantPnJids.Count; i++)
        {
            Jid pJid = parsed.ParticipantJids[i];
            Jid pnJid = parsed.ParticipantPnJids[i];
            if (pJid.IsLid() && pnJid.IsPn())
            {
                await LearnMappingAsync(pnJid.ToNonAdString(), pJid.ToNonAdString(), LidPnLearningSource.PeerJid, parsed.TimestampSeconds, ct)
                    .ConfigureAwait(false);
            }
        }

        if (parsed.RotateSenderKeyRequired == true)
        {
            await RotateOwnSenderKeyAsync(parsed.From.ToNonAdString(), ct).ConfigureAwait(false);
        }

        await _emitEvent(new GroupNotificationEvent(
            parsed.From.ToNonAdString(),
            parsed.Action,
            parsed.ActorJid?.ToNonAdString(),
            parsed.ParticipantJids.Select(p => p.ToNonAdString()).ToArray(),
            parsed.Subject,
            parsed.TimestampSeconds)).ConfigureAwait(false);
    }

    private async Task HandleContactsAsync(Node notification, CancellationToken ct)
    {
        ParsedContactsNotification? parsed = NotificationStanzas.ParseContactsNotification(notification, _timeProvider);
        if (parsed is null)
        {
            return;
        }

        if (parsed.Action == "modify" && parsed.OldPnJid is Jid oldPn && parsed.NewPnJid is Jid newPn)
        {
            if (parsed.OldLidJid is Jid oldLid)
            {
                await LearnMappingAsync(oldPn.ToNonAdString(), oldLid.ToNonAdString(), LidPnLearningSource.ContactsNotification, parsed.TimestampSeconds, ct)
                    .ConfigureAwait(false);
            }
            if (parsed.NewLidJid is Jid newLid)
            {
                await LearnMappingAsync(newPn.ToNonAdString(), newLid.ToNonAdString(), LidPnLearningSource.ContactsNotification, parsed.TimestampSeconds, ct)
                    .ConfigureAwait(false);
            }
        }

        await _emitEvent(new ContactNotificationEvent(
            parsed.Action,
            parsed.OldPnJid?.ToNonAdString(),
            parsed.NewPnJid?.ToNonAdString(),
            parsed.OldLidJid?.ToNonAdString(),
            parsed.NewLidJid?.ToNonAdString(),
            parsed.ContactHash,
            parsed.TimestampSeconds)).ConfigureAwait(false);
    }

    private async Task HandleAccountSyncAsync(Node notification, CancellationToken ct)
    {
        ParsedAccountSyncNotification? parsed = NotificationStanzas.ParseAccountSyncNotification(notification, _timeProvider);
        if (parsed is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(parsed.PushName) && !string.Equals(_credentials.PushName, parsed.PushName, StringComparison.Ordinal))
        {
            _credentials.PushName = parsed.PushName;
            await _backend.Device.SaveAsync(_credentials, ct).ConfigureAwait(false);
        }

        if (parsed.Devices is { } devNotif)
        {
            string ownBare = _credentials.Lid?.ToNonAdString() ?? _credentials.Pn?.ToNonAdString() ?? parsed.From.ToNonAdString();
            uint[] ids = devNotif.Devices.Select(d => d.DeviceId).Append(0u).Distinct().OrderBy(x => x).ToArray();
            await _backend.Protocol.SaveDeviceListAsync(ownBare, ids, ct).ConfigureAwait(false);
            await _emitEvent(new DeviceNotificationEvent(ownBare, "account_sync", ids, null, parsed.TimestampSeconds)).ConfigureAwait(false);
        }

        if (parsed.DisappearingMode is { IsActionOnly: false } dm)
        {
            await _emitEvent(new DisappearingModeUpdateEvent(
                dm.From.ToNonAdString(),
                dm.DurationSeconds,
                dm.SettingTimestampSeconds,
                dm.EphemeralityDisabled)).ConfigureAwait(false);
        }
    }

    private async Task HandlePictureAsync(Node notification, CancellationToken ct)
    {
        ParsedPictureNotification? parsed = NotificationStanzas.ParsePictureNotification(notification, _timeProvider);
        if (parsed is null)
        {
            return;
        }

        string targetStr = parsed.TargetJid.ToNonAdString();
        if (!string.IsNullOrEmpty(parsed.ContactHash) && parsed.TargetJid == parsed.From)
        {
            IReadOnlyList<string> matches = _lidPnCache.LookupByContactHash(parsed.ContactHash);
            if (matches.Count > 0)
            {
                targetStr = matches[0];
            }
        }

        await _emitEvent(new PictureUpdateEvent(
            targetStr,
            parsed.AuthorJid?.ToNonAdString(),
            parsed.PictureId,
            parsed.ContactHash,
            parsed.Removed,
            parsed.TimestampSeconds)).ConfigureAwait(false);
        await Task.CompletedTask;
    }

    private async Task HandleDisappearingModeAsync(Node notification)
    {
        ParsedDisappearingModeNotification? parsed = NotificationStanzas.ParseDisappearingModeNotification(notification);
        if (parsed is null || parsed.IsActionOnly)
        {
            return;
        }

        await _emitEvent(new DisappearingModeUpdateEvent(
            parsed.From.ToNonAdString(),
            parsed.DurationSeconds,
            parsed.SettingTimestampSeconds,
            parsed.EphemeralityDisabled)).ConfigureAwait(false);
    }

    private async Task HandleBusinessAsync(Node notification)
    {
        ParsedBusinessNotification? parsed = NotificationStanzas.ParseBusinessNotification(notification, _timeProvider);
        if (parsed is null)
        {
            return;
        }

        await _emitEvent(new BusinessUpdateEvent(
            parsed.From.ToNonAdString(),
            parsed.UpdateType,
            parsed.TargetJid?.ToNonAdString(),
            parsed.VerifiedName,
            parsed.ContactHash,
            parsed.TimestampSeconds)).ConfigureAwait(false);
    }

    private async Task HandleStatusAsync(Node notification, long ts)
    {
        if (notification.GetAttr("from") is not { Length: > 0 } fromStr
            || !Jid.TryParse(fromStr, out Jid fromJid))
        {
            return;
        }

        string? text = NotificationStanzas.ParseStatusAboutText(notification);
        if (text is null)
        {
            return;
        }

        await _emitEvent(new UserAboutUpdateEvent(fromJid.ToNonAdString(), text, ts)).ConfigureAwait(false);
    }

    private async Task HandleMexAsync(Node notification)
    {
        ParsedMexNotification? parsed = NotificationStanzas.ParseMexNotification(notification, _timeProvider);
        if (parsed is null)
        {
            return;
        }

        await _emitEvent(new MexNotificationEvent(parsed.Operation, parsed.RawPayloadJson, parsed.TimestampSeconds)).ConfigureAwait(false);
    }

    private async Task RotateOwnSenderKeyAsync(string groupJid, CancellationToken ct)
    {
        if (_credentials.Pn is Jid ownPn)
        {
            await _backend.Signal.DeleteSenderKeyAsync(groupJid, ownPn.ToProtocolAddress(), ct).ConfigureAwait(false);
        }
        if (_credentials.Lid is Jid ownLid)
        {
            await _backend.Signal.DeleteSenderKeyAsync(groupJid, ownLid.ToProtocolAddress(), ct).ConfigureAwait(false);
        }
    }

    private bool IsSelfUser(string user) =>
        (_credentials.Pn is Jid pn && string.Equals(pn.User, user, StringComparison.Ordinal))
        || (_credentials.Lid is Jid lid && string.Equals(lid.User, user, StringComparison.Ordinal));

    private async Task LearnMappingAsync(string pnJid, string lidJid, LidPnLearningSource source, long ts, CancellationToken ct)
    {
        _lidPnCache.Learn(pnJid, lidJid, source, ts);
        await _backend.Protocol.UpsertLidPnMappingsAsync([new LidPnMappingRecord(pnJid, lidJid)], ct).ConfigureAwait(false);
    }
}
