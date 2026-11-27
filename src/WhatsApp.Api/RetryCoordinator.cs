using System.Collections.Concurrent;
using System.Security.Cryptography;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Api;

public sealed record CachedSentMessage(
    string MessageId,
    Jid ToChatJid,
    string StanzaType,
    string? MediaType,
    bool HideDecryptFail,
    byte[] UnpaddedProtobufBytes,
    string? AddressingMode,
    IReadOnlySet<string> AllowedParticipantUsers);

/// <summary>
/// Manages Signal retry recovery for both outbound retry receipts (<c>&lt;receipt type="retry"&gt;</c>)
/// and inbound decryption failures (<c>MaxRetryCount = 5</c>, key-bundle inclusion at count &gt;= 2).
/// Matches <c>whatsapp-rust/src/retry.rs</c> and <c>WAWebSendRetryReceiptJob</c>.
/// </summary>
public sealed class RetryCoordinator
{
    public const int DefaultMaxCachedMessages = 512;

    private readonly NoiseSessionSocket _socket;
    private readonly IWhatsAppBackend _backend;
    private readonly PreKeyFetchClient _preKeyFetcher;
    private readonly ILidPnCache _lidPnCache;
    private readonly DeviceCredentials _credentials;
    private readonly Func<WhatsAppEvent, ValueTask> _emitEvent;
    private readonly int _maxCachedMessages;
    private readonly TimeProvider _timeProvider;

    private readonly object _cacheGate = new();
    private readonly Dictionary<string, LinkedListNode<(string Id, CachedSentMessage Entry)>> _outboundMap = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Id, CachedSentMessage Entry)> _outboundLru = new();
    private readonly ConcurrentDictionary<string, int> _inboundRetryCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _outboundRetryCounts = new(StringComparer.Ordinal);

    public RetryCoordinator(
        NoiseSessionSocket socket,
        IWhatsAppBackend backend,
        PreKeyFetchClient preKeyFetcher,
        ILidPnCache lidPnCache,
        DeviceCredentials credentials,
        Func<WhatsAppEvent, ValueTask> emitEvent,
        int maxCachedMessages = DefaultMaxCachedMessages,
        TimeProvider? timeProvider = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _preKeyFetcher = preKeyFetcher ?? throw new ArgumentNullException(nameof(preKeyFetcher));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _emitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
        _maxCachedMessages = Math.Max(16, maxCachedMessages);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void CacheSentMessage(CachedSentMessage entry)
    {
        CacheSentMessageInMemory(entry);
        _ = PersistOutboundRetryAsync(entry, CancellationToken.None);
    }

    public Task CacheSentMessageAsync(CachedSentMessage entry, CancellationToken ct = default)
    {
        CacheSentMessageInMemory(entry);
        return PersistOutboundRetryAsync(entry, ct);
    }

    private void CacheSentMessageInMemory(CachedSentMessage entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_cacheGate)
        {
            if (_outboundMap.TryGetValue(entry.MessageId, out var existing))
            {
                existing.Value = (entry.MessageId, entry);
                _outboundLru.Remove(existing);
                _outboundLru.AddFirst(existing);
                return;
            }

            var node = _outboundLru.AddFirst((entry.MessageId, entry));
            _outboundMap[entry.MessageId] = node;

            while (_outboundLru.Count > _maxCachedMessages && _outboundLru.Last is { } oldest)
            {
                _outboundLru.RemoveLast();
                _outboundMap.Remove(oldest.Value.Id);
            }
        }
    }

    private async Task PersistOutboundRetryAsync(CachedSentMessage entry, CancellationToken ct)
    {
        var record = new OutboundRetryEntry(
            entry.MessageId,
            entry.ToChatJid.ToNonAdString(),
            entry.StanzaType,
            entry.MediaType,
            entry.HideDecryptFail,
            entry.UnpaddedProtobufBytes,
            entry.AddressingMode,
            entry.AllowedParticipantUsers.ToArray(),
            _timeProvider.GetUtcNow().ToUnixTimeSeconds());
        await _backend.Protocol.SaveOutboundRetryAsync(record, Math.Max(1000, _maxCachedMessages), ct).ConfigureAwait(false);
    }

    public bool TryGetCachedSentMessage(string messageId, out CachedSentMessage? entry)
    {
        lock (_cacheGate)
        {
            if (_outboundMap.TryGetValue(messageId, out var node))
            {
                entry = node.Value.Entry;
                return true;
            }
        }

        entry = null;
        return false;
    }

    public async Task<CachedSentMessage?> GetOrLoadCachedSentMessageAsync(string messageId, CancellationToken ct = default)
    {
        if (TryGetCachedSentMessage(messageId, out CachedSentMessage? cached) && cached is not null)
        {
            return cached;
        }

        OutboundRetryEntry? stored = await _backend.Protocol.GetOutboundRetryAsync(messageId, ct).ConfigureAwait(false);
        if (stored is null || !Jid.TryParse(stored.ToChatJid, out Jid toChatJid))
        {
            return null;
        }

        var loaded = new CachedSentMessage(
            stored.MessageId,
            toChatJid,
            stored.StanzaType,
            stored.MediaType,
            stored.HideDecryptFail,
            stored.UnpaddedProtobufBytes,
            stored.AddressingMode,
            new HashSet<string>(stored.AllowedParticipantUsers, StringComparer.OrdinalIgnoreCase));
        CacheSentMessageInMemory(loaded);
        return loaded;
    }

    /// <summary>
    /// Increments the inbound decryption retry counter for <paramref name="chatJid"/>+<paramref name="messageId"/>,
    /// returning the new count and whether a retry receipt should still be sent (&lt;= 5).
    /// </summary>
    public (int RetryCount, bool ShouldSendRetry) RecordInboundDecryptFailure(string chatJid, string messageId)
    {
        string key = $"{chatJid}::{messageId}";
        int next = _inboundRetryCounts.AddOrUpdate(key, 1, static (_, prev) => prev >= RetryAndMediaRetryStanzas.MaxRetryCount + 1 ? prev : prev + 1);
        return (next, next <= RetryAndMediaRetryStanzas.MaxRetryCount);
    }

    public void ClearInboundRetry(string chatJid, string messageId) =>
        _inboundRetryCounts.TryRemove($"{chatJid}::{messageId}", out _);

    /// <summary>
    /// Builds a retry pre-key upload bundle (using an unconsumed stored pre-key or freshly generated one)
    /// when retry count reaches <see cref="RetryAndMediaRetryStanzas.MinRetryCountForKeys"/>.
    /// </summary>
    public async Task<RetryPreKeyUploadBundle> BuildRetryKeyBundleAsync(CancellationToken ct = default)
    {
        var first = await _backend.Signal.GetFirstPreKeyAsync(ct).ConfigureAwait(false);
        uint preKeyId;
        KeyPair preKeyPair;

        if (first is { } existing)
        {
            (preKeyId, preKeyPair) = PreKeyManager.DecodePreKeyRecord(existing.KeyPairBytes);
        }
        else
        {
            uint startId = _credentials.NextPreKeyId < 1 || _credentials.NextPreKeyId > PreKeyUploader.MaxPreKeyId
                ? 1u
                : _credentials.NextPreKeyId;
            GeneratedPreKey generated = PreKeyManager.GeneratePreKeys(startId, 1)[0];
            preKeyId = generated.Id;
            preKeyPair = generated.KeyPair;

            await _backend.Signal.SavePreKeysAsync([(generated.Id, generated.RecordBytes)], ct).ConfigureAwait(false);
            _credentials.NextPreKeyId = startId >= PreKeyUploader.MaxPreKeyId ? 1u : startId + 1u;
            await _backend.Device.SaveAsync(_credentials, ct).ConfigureAwait(false);
        }

        return new RetryPreKeyUploadBundle(
            _credentials.IdentityKey.PublicKey,
            preKeyId,
            preKeyPair.PublicKey,
            _credentials.SignedPreKeyId,
            _credentials.SignedPreKey.PublicKey,
            _credentials.SignedPreKeySignature,
            _credentials.AccountIdentityBytes);
    }

    /// <summary>
    /// Processes an incoming <c>&lt;receipt type="retry"&gt;</c>, verifying requester membership,
    /// handling registration ID mismatch &amp; bundled <c>&lt;keys&gt;</c> (or fetching fresh pre-keys),
    /// and resending the cached payload as a targeted <c>pkmsg</c>/<c>msg</c> (including <c>SKDM</c> for groups).
    /// </summary>
    public async Task<bool> HandleRetryReceiptAsync(ParsedReceiptStanza receipt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!string.Equals(receipt.Type, ReceiptStanza.TypeRetry, StringComparison.Ordinal))
        {
            return false;
        }

        CachedSentMessage? cached = await GetOrLoadCachedSentMessageAsync(receipt.MessageId, ct).ConfigureAwait(false);
        if (cached is null)
        {
            return true;
        }

        Jid requesterDevice = receipt.Participant ?? receipt.From;
        string retryKey = $"{receipt.MessageId}::{requesterDevice.ToProtocolAddress()}";
        int attempt = receipt.RetryCount ?? _outboundRetryCounts.AddOrUpdate(retryKey, 1, static (_, c) => c + 1);
        _outboundRetryCounts[retryKey] = attempt;

        if (attempt > RetryAndMediaRetryStanzas.MaxRetryCount)
        {
            return true;
        }

        // Security guard: verify requester is authorized (chat peer, own companion, or known group participant)
        if (!IsRequesterAuthorized(cached, requesterDevice))
        {
            return true;
        }

        // Validate registration ID against existing session; mismatch requires session reset + fresh X3DH.
        string requesterAddr = requesterDevice.ToProtocolAddress();
        SignalSession? existingSession = await _backend.Signal.GetSessionAsync(requesterAddr, ct).ConfigureAwait(false);
        if (existingSession is not null
            && receipt.RegistrationId is uint peerRegId
            && peerRegId != 0
            && existingSession.RemoteRegistrationId != 0
            && existingSession.RemoteRegistrationId != peerRegId)
        {
            await _backend.Signal.DeleteSessionAsync(requesterAddr, ct).ConfigureAwait(false);
            existingSession = null;
        }

        if (receipt.RetryKeyBundle is { } attachedBundle)
        {
            if (existingSession is not null)
            {
                await _backend.Signal.DeleteSessionAsync(requesterAddr, ct).ConfigureAwait(false);
            }
            existingSession = await _preKeyFetcher.EstablishFromBundleAsync(requesterDevice, attachedBundle, ct).ConfigureAwait(false);
        }
        else if (existingSession is null)
        {
            SessionEnsureResult ensured = await _preKeyFetcher.EnsureSessionsAsync([requesterDevice], reason: "retry", ct).ConfigureAwait(false);
            ensured.Sessions.TryGetValue(requesterDevice, out existingSession);
        }

        if (existingSession is null)
        {
            return true;
        }

        // Prepare plaintext to re-encrypt:
        // - For peer own-companion DM devices, wrap in DeviceSentMessage just like initial fanout
        // - For group chats, re-wrap with our SenderKeyDistributionMessage if present, otherwise original padded payload
        byte[] paddedPlaintext = await BuildResendPlaintextAsync(cached, requesterDevice, ct).ConfigureAwait(false);
        SignalEncryptedPayload encrypted = SignalCipher.Encrypt(
            existingSession,
            paddedPlaintext,
            _credentials.IdentityKey,
            _credentials.RegistrationId);

        await _backend.Signal.SaveSessionAsync(requesterAddr, existingSession, ct).ConfigureAwait(false);

        string encType = encrypted.Type == SignalCiphertextType.PreKeyMessage ? "pkmsg" : "msg";
        byte[]? devIdentity = encrypted.Type == SignalCiphertextType.PreKeyMessage ? _credentials.AccountIdentityBytes : null;

        Node resendStanza = MessageStanzas.BuildRetryResendStanza(
            cached.MessageId,
            cached.ToChatJid,
            requesterDevice,
            encType,
            encrypted.Bytes,
            cached.StanzaType,
            attempt,
            cached.MediaType,
            cached.HideDecryptFail,
            devIdentity,
            cached.AddressingMode);

        await _socket.SendNodeAsync(resendStanza, ct).ConfigureAwait(false);
        await _emitEvent(new MessageRetrySentEvent(
            cached.MessageId,
            requesterDevice.ToString(),
            attempt,
            WasGroupResend: cached.ToChatJid.IsGroup())).ConfigureAwait(false);

        return true;
    }

    private async Task<byte[]> BuildResendPlaintextAsync(
        CachedSentMessage cached,
        Jid requesterDevice,
        CancellationToken ct)
    {
        if (requesterDevice.IsBot())
        {
            Whatsapp.Message botMsg = Whatsapp.Message.Parser.ParseFrom(cached.UnpaddedProtobufBytes);
            if (botMsg.MessageContextInfo?.MessageSecret is { Length: 32 } sec)
            {
                botMsg.MessageContextInfo.BotMessageSecret = Google.Protobuf.ByteString.CopyFrom(
                    BotMessageCrypto.DeriveBaseBotKey(sec.Span));
                botMsg.MessageContextInfo.ClearMessageSecret();
            }
            if (botMsg.ProtocolMessage?.Key is { } pmKey)
            {
                pmKey.ClearRemoteJid();
                pmKey.ClearParticipant();
            }
            return MessageFraming.EncodeAndPadMessage(botMsg);
        }

        if (cached.ToChatJid.IsGroup())
        {
            string groupBare = cached.ToChatJid.ToNonAdString();
            string? ownAddr = GetOwnProtocolAddressForChat(cached.ToChatJid);
            if (ownAddr is not null)
            {
                byte[]? skBytes = await _backend.Signal.GetSenderKeyAsync(groupBare, ownAddr, ct).ConfigureAwait(false);
                if (skBytes is { Length: > 0 })
                {
                    SenderKeyRecord record = SignalStateCodec.DeserializeSenderKeyRecord(skBytes);
                    byte[] skdmBytes = SenderKeyCipher.CreateDistribution(record);
                    var skdmWrapper = new Whatsapp.Message
                    {
                        SenderKeyDistributionMessage = new Whatsapp.Message.Types.SenderKeyDistributionMessage
                        {
                            GroupId = groupBare,
                            AxolotlSenderKeyDistributionMessage = Google.Protobuf.ByteString.CopyFrom(skdmBytes)
                        }
                    };
                    // Merge SKDM inside original message payload so requester receives both SKDM & message in one retry resend
                    Whatsapp.Message merged = Whatsapp.Message.Parser.ParseFrom(cached.UnpaddedProtobufBytes);
                    merged.SenderKeyDistributionMessage = skdmWrapper.SenderKeyDistributionMessage;
                    return MessageFraming.EncodeAndPadMessage(merged);
                }
            }

            return MessageFraming.PadMessageV2(cached.UnpaddedProtobufBytes);
        }

        bool isOwnCompanion = IsOwnUser(requesterDevice.User);
        if (isOwnCompanion)
        {
            Whatsapp.Message inner = Whatsapp.Message.Parser.ParseFrom(cached.UnpaddedProtobufBytes);
            Whatsapp.Message wrapped = MessageFraming.WrapDeviceSentMessage(inner, cached.ToChatJid.ToNonAdString());
            return MessageFraming.EncodeAndPadMessage(wrapped);
        }

        return MessageFraming.PadMessageV2(cached.UnpaddedProtobufBytes);
    }

    private bool IsRequesterAuthorized(CachedSentMessage cached, Jid requesterDevice)
    {
        if (cached.AllowedParticipantUsers.Count == 0)
        {
            return true;
        }

        string user = requesterDevice.User;
        if (IsOwnUser(user) || cached.AllowedParticipantUsers.Contains(requesterDevice.ToNonAdString()))
        {
            return true;
        }

        string bare = requesterDevice.ToNonAdString();
        if (requesterDevice.IsLid() && _lidPnCache.TryGetPn(bare, out string? pn) && !string.IsNullOrEmpty(pn) && cached.AllowedParticipantUsers.Contains(pn))
        {
            return true;
        }
        if (requesterDevice.IsPn() && _lidPnCache.TryGetLid(bare, out string? lid) && !string.IsNullOrEmpty(lid) && cached.AllowedParticipantUsers.Contains(lid))
        {
            return true;
        }

        return false;
    }

    private string? GetOwnProtocolAddressForChat(Jid chatJid)
    {
        if (_credentials.Lid is Jid lid)
        {
            return lid.ToProtocolAddress();
        }
        return _credentials.Pn?.ToProtocolAddress();
    }

    private bool IsOwnUser(string user) =>
        (_credentials.Pn is Jid pn && string.Equals(pn.User, user, StringComparison.Ordinal))
        || (_credentials.Lid is Jid lid && string.Equals(lid.User, user, StringComparison.Ordinal));
}
