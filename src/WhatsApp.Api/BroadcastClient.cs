using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Api;

/// <summary>
/// Runtime client for numeric user Broadcast Lists (<c>{id}@broadcast</c>, <c>xmlns="w:b"</c>):
/// list discovery and encrypted SenderKey + bare-participant broadcast fan-out.
/// </summary>
public sealed class BroadcastClient
{
    private readonly IqClient _iqClient;
    private readonly NoiseSessionSocket _socket;
    private readonly UsyncClient _usync;
    private readonly PreKeyFetchClient _preKeyFetcher;
    private readonly MessageSender _messageSender;
    private readonly IWhatsAppBackend _backend;
    private readonly ILidPnCache _lidPnCache;
    private readonly DeviceCredentials _credentials;
    private readonly RetryCoordinator _retryCoordinator;

    public BroadcastClient(
        IqClient iqClient,
        NoiseSessionSocket socket,
        UsyncClient usync,
        PreKeyFetchClient preKeyFetcher,
        MessageSender messageSender,
        IWhatsAppBackend backend,
        ILidPnCache lidPnCache,
        DeviceCredentials credentials,
        RetryCoordinator retryCoordinator)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _usync = usync ?? throw new ArgumentNullException(nameof(usync));
        _preKeyFetcher = preKeyFetcher ?? throw new ArgumentNullException(nameof(preKeyFetcher));
        _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _retryCoordinator = retryCoordinator ?? throw new ArgumentNullException(nameof(retryCoordinator));
    }

    public async Task<IReadOnlyList<BroadcastListInfo>> GetBroadcastListsAsync(CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = BroadcastIqs.BuildGetBroadcastLists(iqId);
        Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        return BroadcastIqs.ParseGetBroadcastListsResponse(response);
    }

    public Task<SendMessageResult> SendBroadcastTextAsync(
        Jid broadcastListJid,
        string text,
        IReadOnlyList<Jid> recipients,
        string? messageId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return SendBroadcastMessageAsync(
            broadcastListJid,
            new Message { ExtendedTextMessage = new Message.Types.ExtendedTextMessage { Text = text } },
            recipients,
            messageId,
            ct);
    }

    public async Task<SendMessageResult> SendBroadcastMessageAsync(
        Jid broadcastListJid,
        Message message,
        IReadOnlyList<Jid> recipients,
        string? messageId = null,
        CancellationToken ct = default)
    {
        if (!BroadcastIqs.IsUserBroadcastJid(broadcastListJid))
        {
            throw new ArgumentException($"Expected user broadcast list JID, got '{broadcastListJid}'.", nameof(broadcastListJid));
        }
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Count == 0)
        {
            throw new ArgumentException("Broadcast send requires at least one recipient.", nameof(recipients));
        }
        if (_credentials.Lid is not Jid ownLid)
        {
            throw new InvalidOperationException("Cannot send broadcast without local LID.");
        }

        string msgId = string.IsNullOrWhiteSpace(messageId) ? _messageSender.NextMessageId() : messageId;
        string listStr = broadcastListJid.ToNonAdString();

        var resolvedUsers = new List<Jid>(recipients.Count + 1);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Jid raw in recipients)
        {
            Jid bare = raw.ToNonAd();
            if (bare.IsPn() && _lidPnCache.TryGetLid(bare.ToString(), out string? cachedLid) && !string.IsNullOrEmpty(cachedLid) && Jid.TryParse(cachedLid, out Jid parsedLid))
            {
                bare = parsedLid.ToNonAd();
            }
            if (seen.Add(bare.User))
            {
                resolvedUsers.Add(bare);
            }
        }
        Jid ownBare = ownLid.ToNonAd();
        if (seen.Add(ownBare.User))
        {
            resolvedUsers.Add(ownBare);
        }

        ushort ownDevId = _credentials.Lid?.Device is > 0 ? _credentials.Lid.Value.Device : (_credentials.Pn?.Device ?? 0);
        Jid ownFull = ownBare with { Device = ownDevId };
        string ownAddr = ownFull.ToProtocolAddress();

        IReadOnlyDictionary<string, ResolvedDeviceList> deviceLists =
            await _usync.GetDeviceListsAsync(resolvedUsers, UsyncContext.Message, allowCachedOnly: true, ct).ConfigureAwait(false);

        var allDevices = new List<Jid>();
        foreach (Jid user in resolvedUsers)
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

        byte[]? skBytes = await _backend.Signal.GetSenderKeyAsync(listStr, ownAddr, ct).ConfigureAwait(false);
        SenderKeyRecord senderRecord = skBytes is { Length: > 0 }
            ? SignalStateCodec.DeserializeSenderKeyRecord(skBytes)
            : new SenderKeyRecord();

        byte[] skdmBytes = SenderKeyCipher.CreateDistribution(senderRecord);
        IReadOnlySet<string> distributed = await _backend.Signal.GetSenderKeyDistributedDevicesAsync(listStr, ct).ConfigureAwait(false);

        string ownFullStr = ownFull.ToString();
        Jid[] needSkdm = allDevices
            .Where(d => !string.Equals(d.ToString(), ownFullStr, StringComparison.OrdinalIgnoreCase) && !distributed.Contains(d.ToString()))
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

            byte[] paddedSkdm = MessageFraming.EncodeAndPadSkdmWrapper(listStr, skdmBytes);

            foreach (Jid targetDev in needSkdm)
            {
                if (rejected.Contains(targetDev.ToProtocolAddress()) || !ensured.Sessions.TryGetValue(targetDev, out SignalSession? session))
                {
                    continue;
                }

                SignalEncryptedPayload enc = SignalCipher.Encrypt(session, paddedSkdm, _credentials.IdentityKey, _credentials.RegistrationId);
                await _backend.Signal.SaveSessionAsync(targetDev.ToProtocolAddress(), session, ct).ConfigureAwait(false);
                if (enc.Type == SignalCiphertextType.PreKeyMessage) anyPkmsg = true;
                skdmTargets.Add((targetDev, enc.WireType, enc.Bytes));
                newlyDistributed.Add(targetDev.ToString());
            }
        }

        byte[] unpaddedBytes = message.ToByteArray();
        byte[] skmsgCiphertext = SenderKeyCipher.Encrypt(senderRecord, MessageFraming.PadMessageV2(unpaddedBytes));
        await _backend.Signal.SaveSenderKeyAsync(listStr, ownAddr, SignalStateCodec.SerializeSenderKeyRecord(senderRecord), ct).ConfigureAwait(false);

        Node stanza = BroadcastIqs.BuildBroadcastMessageStanza(
            msgId,
            broadcastListJid.ToNonAd(),
            resolvedUsers,
            message,
            skmsgCiphertext,
            skdmTargets,
            addressingMode: "lid",
            deviceIdentityBytes: anyPkmsg ? _credentials.AccountIdentityBytes : null);

        var allowed = resolvedUsers.Select(u => u.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _retryCoordinator.CacheSentMessageAsync(new CachedSentMessage(
            msgId,
            broadcastListJid.ToNonAd(),
            MessageStanzas.ClassifyStanzaType(message),
            MessageStanzas.ClassifyMediaType(message),
            MessageStanzas.ShouldHideDecryptFail(message),
            unpaddedBytes,
            "lid",
            allowed), ct).ConfigureAwait(false);

        await _socket.SendNodeAsync(stanza, ct).ConfigureAwait(false);

        if (newlyDistributed.Count > 0)
        {
            await _backend.Signal.MarkSenderKeyDistributedAsync(listStr, newlyDistributed, ct).ConfigureAwait(false);
        }

        return new SendMessageResult(msgId, listStr, allDevices.Count, Phash: null);
    }
}
