using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Api;

/// <summary>
/// Account security, device management, spam reporting, Signed PreKey rotation,
/// AppState collection resync, and Safety Number fingerprint verification client.
/// </summary>
public sealed class AccountSafetyClient
{
    private const uint MaxSignedPreKeyId = 16_777_215;

    private readonly IqClient _iqClient;
    private readonly IWhatsAppBackend _backend;
    private readonly DeviceCredentials _credentials;
    private readonly Func<WhatsAppEvent, ValueTask>? _emitEvent;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _rotationGate = new(1, 1);

    public AccountSafetyClient(
        IqClient iqClient,
        IWhatsAppBackend backend,
        DeviceCredentials credentials,
        Func<WhatsAppEvent, ValueTask>? emitEvent = null,
        TimeProvider? timeProvider = null)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _emitEvent = emitEvent;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RemoveCompanionDeviceAsync(Jid deviceJid, string reason = "user_initiated", CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = DeviceManagementIq.BuildRemoveCompanionDevice(iqId, deviceJid, reason);
        _ = await _iqClient.SendIqAsync(iq, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
    }

    public async Task<SpamReportResult> ReportMessageAsync(SpamReportRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TcToken is null && request.FromJid is Jid from && (from.IsPn() || from.IsLid()))
        {
            byte[]? stored = await _backend.Protocol.GetTcTokenAsync(from.ToNonAdString(), ct).ConfigureAwait(false);
            if (stored is { Length: > 0 })
            {
                request = request with { TcToken = stored };
            }
        }

        string iqId = _iqClient.NextRequestId();
        Node iq = SpamReportIq.BuildReportSpam(iqId, request);
        Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        return SpamReportIq.ParseReportSpamResponse(response);
    }

    /// <summary>
    /// Stages a fresh Signed PreKey in durable storage (<c>signed_prekeys</c>), uploads it via
    /// <c>xmlns="encrypt"</c> (<c>&lt;rotate&gt;&lt;skey/&gt;&lt;/rotate&gt;</c>), and promotes it
    /// on <see cref="DeviceCredentials"/> after server acceptance while retaining the previous key
    /// so in-flight <c>pkmsg</c> payloads remain decryptable.
    /// </summary>
    public async Task<uint> RotateSignedPreKeyAsync(CancellationToken ct = default)
    {
        await _rotationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            uint prevId = _credentials.SignedPreKeyId;
            uint nextId = prevId >= MaxSignedPreKeyId ? 1u : prevId + 1u;
            long nowSec = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

            // Retain the outgoing SPK in signed_prekeys before promoting the replacement.
            if (await _backend.Signal.GetSignedPreKeyAsync(prevId, ct).ConfigureAwait(false) is null)
            {
                byte[] prevRecord = PreKeyManager.EncodeSignedPreKeyRecord(
                    prevId,
                    _credentials.SignedPreKey,
                    _credentials.SignedPreKeySignature,
                    (ulong)nowSec);
                await _backend.Signal.SaveSignedPreKeyAsync(
                    new SignedPreKeyEntry(prevId, prevRecord, _credentials.SignedPreKeySignature, nowSec),
                    ct).ConfigureAwait(false);
            }

            GeneratedSignedPreKey nextSpk = PreKeyManager.CreateSignedPreKey(nextId, _credentials.IdentityKey, (ulong)nowSec);
            await _backend.Signal.SaveSignedPreKeyAsync(
                new SignedPreKeyEntry(nextSpk.Id, nextSpk.RecordBytes, nextSpk.Signature, nowSec),
                ct).ConfigureAwait(false);

            string iqId = _iqClient.NextRequestId();
            Node iq = PreKeyIq.BuildRotateSignedPreKey(iqId, nextSpk.Id, nextSpk.KeyPair.PublicKey, nextSpk.Signature);
            _ = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);

            _credentials.SignedPreKeyId = nextSpk.Id;
            _credentials.SignedPreKey = nextSpk.KeyPair;
            _credentials.SignedPreKeySignature = nextSpk.Signature;
            await _backend.Device.SaveAsync(_credentials, ct).ConfigureAwait(false);

            if (_emitEvent is not null)
            {
                await _emitEvent(new SignedPreKeyRotatedEvent(prevId, nextSpk.Id, nowSec)).ConfigureAwait(false);
            }

            return nextSpk.Id;
        }
        finally
        {
            _rotationGate.Release();
        }
    }

    public Task<IReadOnlyList<DecodedAppStateMutation>> ResyncAppStateCollectionAsync(
        SyncdCoordinator syncd,
        string collection,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(syncd);
        ArgumentException.ThrowIfNullOrEmpty(collection);
        return syncd.SyncCollectionAsync(collection, ct: ct);
    }

    public async Task<SafetyNumberFingerprint?> ComputeSafetyNumberAsync(Jid peerJid, CancellationToken ct = default)
    {
        Jid barePeer = peerJid.ToNonAd();
        byte[]? remoteIdentity = await _backend.Signal.GetIdentityAsync(barePeer.ToProtocolAddress(), ct).ConfigureAwait(false);
        if (remoteIdentity is not { Length: 32 })
        {
            return null;
        }

        string localId = (_credentials.Lid?.ToNonAd() ?? _credentials.Pn?.ToNonAd())?.ToString()
            ?? throw new InvalidOperationException("Local device JID is not configured.");
        return SafetyNumber.Compute(localId, _credentials.IdentityKey.PublicKey, barePeer.ToString(), remoteIdentity);
    }
}
