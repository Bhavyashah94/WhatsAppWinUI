using System.Collections.Concurrent;
using System.Net.Sockets;
using Whatsapp;
using WhatsApp.Calling;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Api;

public interface ICallUdpSocket : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> datagram, string host, ushort port, CancellationToken ct = default);
    ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default);
}

public sealed class UdpCallSocket : ICallUdpSocket
{
    private readonly UdpClient _udp = new();

    public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, string host, ushort port, CancellationToken ct = default) =>
        await _udp.SendAsync(datagram, host, port, ct).ConfigureAwait(false);

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default)
    {
        UdpReceiveResult res = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
        return res.Buffer;
    }

    public ValueTask DisposeAsync()
    {
        _udp.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed record ActiveCallSession(
    string CallId,
    Jid PeerJid,
    Jid CallCreator,
    bool IsVideo,
    bool ForceRelayOnly,
    CallEngine Engine);

public sealed record ActiveGroupCall(
    string CallId,
    Jid CallCreator,
    bool IsVideo,
    GroupCallState State,
    GroupMediaSession Media);

/// <summary>
/// Full VoIP call manager coordinating 1:1 &amp; group signaling (<see cref="CallSignalingClient"/>),
/// prekey/Signal <c>callKey</c> / <c>enc_rekey</c> epoch encryption, relay/UDP transport,
/// Sans-I/O <see cref="CallEngine"/> &amp; <see cref="GroupMediaSession"/>, call links/waiting rooms,
/// automatic <c>PrivacySettingRelayAllCalls</c> enforcement, and AppState <c>call_log</c> recording.
/// </summary>
public sealed class CallClient : IAsyncDisposable
{
    private sealed class ManagedCall : IAsyncDisposable
    {
        public required ActiveCallSession Session { get; init; }
        public required ICallUdpSocket Udp { get; init; }
        public required SemaphoreSlim Gate { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public Task? PumpTask { get; set; }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            if (PumpTask is not null)
            {
                try { await PumpTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            await Udp.DisposeAsync().ConfigureAwait(false);
            Gate.Dispose();
            Cts.Dispose();
        }
    }

    private readonly CallSignalingClient _signaling;
    private readonly UsyncClient _usync;
    private readonly PreKeyFetchClient _preKeyFetcher;
    private readonly IWhatsAppBackend _backend;
    private readonly ILidPnCache _lidPnCache;
    private readonly DeviceCredentials _credentials;
    private readonly Func<WhatsAppEvent, ValueTask> _emitEvent;
    private readonly Func<ICallUdpSocket> _udpFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, ManagedCall> _activeCalls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveGroupCall> _groupCalls = new(StringComparer.Ordinal);
    private int _disposed;

    public CallClient(
        CallSignalingClient signaling,
        UsyncClient usync,
        PreKeyFetchClient preKeyFetcher,
        IWhatsAppBackend backend,
        ILidPnCache lidPnCache,
        DeviceCredentials credentials,
        Func<WhatsAppEvent, ValueTask> emitEvent,
        Func<ICallUdpSocket>? udpFactory = null,
        TimeProvider? timeProvider = null)
    {
        _signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
        _usync = usync ?? throw new ArgumentNullException(nameof(usync));
        _preKeyFetcher = preKeyFetcher ?? throw new ArgumentNullException(nameof(preKeyFetcher));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _emitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
        _udpFactory = udpFactory ?? (() => new UdpCallSocket());
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryGetCall(string callId, out ActiveCallSession? session)
    {
        if (_activeCalls.TryGetValue(callId, out ManagedCall? m))
        {
            session = m.Session;
            return true;
        }
        session = null;
        return false;
    }

    /// <summary>
    /// Initiates a 1:1 audio or video call offer, encrypting a fresh 32-byte <c>callKey</c>
    /// for all resolved peer devices and optionally starting the relay media session immediately
    /// when caller relay allocation info is provided.
    /// </summary>
    public async Task<string> PlaceCallAsync(
        Jid peerJid,
        bool video = false,
        bool? forceRelayOnlyOverride = null,
        RelayData? relay = null,
        string? callId = null,
        bool enableSframe = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Jid peerBare = peerJid.ToNonAd();
        Jid ownCreator = (_credentials.Lid ?? _credentials.Pn)?.ToNonAd()
            ?? throw new InvalidOperationException("Cannot place call without local identity JID.");

        string resolvedCallId = string.IsNullOrWhiteSpace(callId)
            ? $"CALL-{Guid.NewGuid():N}".ToUpperInvariant()[..24]
            : callId;
        bool forceRelay = _signaling.ResolveShouldForceRelay(forceRelayOnlyOverride);

        IReadOnlyDictionary<string, ResolvedDeviceList> lists =
            await _usync.GetDeviceListsAsync([peerBare], UsyncContext.Interactive, allowCachedOnly: true, ct).ConfigureAwait(false);
        var devices = lists.TryGetValue(peerBare.ToString(), out ResolvedDeviceList? devList) && devList.DeviceIds.Count > 0
            ? devList.DeviceIds.Select(d => peerBare with { Device = (ushort)d }).ToArray()
            : [peerBare];

        SessionEnsureResult ensured = await _preKeyFetcher.EnsureSessionsAsync(devices, reason: null, ct).ConfigureAwait(false);
        byte[] callKey = CallCrypto.GenerateCallKey();
        var offerKeys = new List<OfferDeviceKey>(devices.Length);

        foreach (Jid dev in devices)
        {
            if (!ensured.Sessions.TryGetValue(dev, out SignalSession? session))
            {
                continue;
            }
            (string encType, byte[] ciphertext) = CallCrypto.EncryptOfferCallKey(
                callKey,
                session,
                _credentials.IdentityKey,
                _credentials.RegistrationId);
            await _backend.Signal.SaveSessionAsync(dev.ToProtocolAddress(), session, ct).ConfigureAwait(false);
            offerKeys.Add(new OfferDeviceKey(dev, ciphertext, encType));
        }

        if (offerKeys.Count == 0)
        {
            throw new InvalidOperationException($"No Signal session available for call peer '{peerBare}'.");
        }

        await _signaling.SendOfferAsync(
            resolvedCallId,
            peerBare,
            offerKeys,
            ownCreator,
            multiDevice: devices.Length > 1,
            video: video,
            ct: ct).ConfigureAwait(false);

        if (relay is not null)
        {
            await StartMediaSessionAsync(resolvedCallId, peerBare, ownCreator, callKey, relay, video, forceRelay, enableSframe, ct)
                .ConfigureAwait(false);
        }

        return resolvedCallId;
    }

    /// <summary>
    /// Decrypts the inbound Signal <c>&lt;offer&gt;</c> <c>callKey</c>, acknowledges receipt,
    /// sends <c>&lt;preaccept&gt;</c> and <c>&lt;accept&gt;</c>, and starts the Sans-I/O
    /// <see cref="CallEngine"/> + UDP relay loop.
    /// </summary>
    public async Task<ActiveCallSession> AcceptIncomingCallAsync(
        IncomingCallStanza offer,
        Node rawOfferStanza,
        RelayData? relayOverride = null,
        bool? forceRelayOnlyOverride = null,
        bool enableSframe = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(rawOfferStanza);
        if (offer.ActionKind != CallActionKind.Offer)
        {
            throw new ArgumentException($"Expected Offer stanza, got {offer.ActionKind}.", nameof(offer));
        }

        RelayData relay = relayOverride ?? offer.Relay
            ?? throw new InvalidOperationException("Call offer does not contain <relay> allocation.");

        OfferRecipientEnc? targetEnc = SelectOfferEncForOwnDevice(offer.OfferEncs);
        if (targetEnc is null)
        {
            throw new InvalidOperationException("Call offer has no <enc> addressed to this device.");
        }

        string senderAddr = offer.From.ToProtocolAddress();
        (byte[] callKey, _) = await DecryptSignalCallKeyAsync(senderAddr, targetEnc.Enc.EncType, targetEnc.Enc.Ciphertext, ct).ConfigureAwait(false);

        await _signaling.SendOfferAckReceiptAsync(offer, ct: ct).ConfigureAwait(false);
        await _signaling.SendTypedCallAckAsync(rawOfferStanza, "offer", ct).ConfigureAwait(false);
        await _signaling.SendPreAcceptAsync(offer.CallId, offer.From, offer.CallCreator, video: offer.IsVideo, ct: ct).ConfigureAwait(false);

        bool forceRelay = _signaling.ResolveShouldForceRelay(forceRelayOnlyOverride);
        ActiveCallSession session = await StartMediaSessionAsync(
            offer.CallId,
            offer.From,
            offer.CallCreator,
            callKey,
            relay,
            offer.IsVideo,
            forceRelay,
            enableSframe,
            ct).ConfigureAwait(false);

        byte[]? relayTe = relay.Endpoints.FirstOrDefault()?.Ipv4Te2Bytes;
        await _signaling.SendAcceptAsync(
            offer.CallId,
            offer.From,
            offer.CallCreator,
            relayTe: relayTe,
            capability: offer.IsVideo ? CallStanzas.CapabilityOffer : CallStanzas.CapabilityPreAccept,
            video: offer.IsVideo,
            peerAbtestBucket: offer.PeerAbtestBucket,
            peerAbtestBucketIdList: offer.PeerAbtestBucketIdList,
            ct: ct).ConfigureAwait(false);

        return session;
    }

    public async Task<ActiveCallSession> StartMediaSessionAsync(
        string callId,
        Jid peerJid,
        Jid callCreator,
        byte[] callKey32,
        RelayData relay,
        bool video,
        bool forceRelayOnly,
        bool enableSframe = false,
        CancellationToken ct = default)
    {
        RelayCandidate candidate = relay.Candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("RelayData contains no relay endpoints.");
        byte[] relayToken = candidate.Endpoint.TokenId < relay.RelayTokens.Count
            ? relay.RelayTokens[(int)candidate.Endpoint.TokenId]
            : Array.Empty<byte>();
        byte[] integrityKey = relay.RelayKey ?? relay.RelayKeyAscii ?? new byte[16];

        string selfId = (_credentials.Lid ?? _credentials.Pn)?.ToNonAdString()
            ?? throw new InvalidOperationException("No own JID configured.");
        string peerId = ResolveParticipantId(peerJid);

        var config = new CallEngineConfig(
            callId,
            selfId,
            peerId,
            callKey32,
            candidate.Endpoint,
            relayToken,
            integrityKey,
            relay.HbhKey,
            forceRelayOnly,
            enableSframe,
            video,
            (int)(relay.WarpMiTagLen ?? SrtpCipher.WarpMiTagLength));

        var engine = new CallEngine(config);
        ICallUdpSocket udp = _udpFactory();
        var session = new ActiveCallSession(callId, peerJid, callCreator, video, forceRelayOnly, engine);
        var managed = new ManagedCall
        {
            Session = session,
            Udp = udp,
            Gate = new SemaphoreSlim(1, 1),
            Cts = new CancellationTokenSource()
        };

        if (!_activeCalls.TryAdd(callId, managed))
        {
            await managed.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Call session '{callId}' is already active.");
        }

        await managed.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            engine.Start(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await DrainEngineOutputsAsync(managed, ct).ConfigureAwait(false);
        }
        finally
        {
            managed.Gate.Release();
        }

        managed.PumpTask = Task.Run(() => RunCallLoopAsync(managed), managed.Cts.Token);
        return session;
    }

    public async Task SendAudioFrameAsync(string callId, AudioFrame frame, CancellationToken ct = default)
    {
        if (!_activeCalls.TryGetValue(callId, out ManagedCall? managed))
        {
            throw new KeyNotFoundException($"No active call '{callId}'.");
        }

        await managed.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            managed.Session.Engine.SendAudioFrame(frame, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await DrainEngineOutputsAsync(managed, ct).ConfigureAwait(false);
        }
        finally
        {
            managed.Gate.Release();
        }
    }

    public async Task SendVideoFrameAsync(string callId, VideoFrame frame, CancellationToken ct = default)
    {
        if (!_activeCalls.TryGetValue(callId, out ManagedCall? managed))
        {
            throw new KeyNotFoundException($"No active call '{callId}'.");
        }

        await managed.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            managed.Session.Engine.SendVideoFrame(frame, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            await DrainEngineOutputsAsync(managed, ct).ConfigureAwait(false);
        }
        finally
        {
            managed.Gate.Release();
        }
    }

    public async Task SetMutedAsync(string callId, bool muted, CancellationToken ct = default)
    {
        if (!_activeCalls.TryGetValue(callId, out ManagedCall? managed))
        {
            throw new KeyNotFoundException($"No active call '{callId}'.");
        }
        managed.Session.Engine.SetMuted(muted);
        await _signaling.SendMuteAsync(callId, managed.Session.PeerJid, managed.Session.CallCreator, muted, ct: ct).ConfigureAwait(false);
    }

    public async Task EndCallAsync(
        string callId,
        string? reason = null,
        CallLogRecord.Types.CallResult result = CallLogRecord.Types.CallResult.Connected,
        long durationSeconds = 0,
        CancellationToken ct = default)
    {
        if (_activeCalls.TryRemove(callId, out ManagedCall? managed))
        {
            await managed.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                managed.Session.Engine.Close(reason ?? "Call terminated");
                await DrainEngineOutputsAsync(managed, ct).ConfigureAwait(false);
            }
            finally
            {
                managed.Gate.Release();
            }

            await _signaling.SendTerminateAsync(callId, managed.Session.PeerJid, managed.Session.CallCreator, reason, ct: ct).ConfigureAwait(false);
            bool fromMe = IsOwnCreator(managed.Session.CallCreator);
            if (_timeProvider is not null)
            {
                try
                {
                    await _signaling.RecordCallLogAsync(
                        managed.Session.CallCreator,
                        callId,
                        fromMe,
                        new CallLogRecord
                        {
                            CallId = callId,
                            CallCreatorJid = managed.Session.CallCreator.ToNonAdString(),
                            IsVideo = managed.Session.IsVideo,
                            Duration = durationSeconds,
                            StartTime = _timeProvider.GetUtcNow().ToUnixTimeSeconds(),
                            CallResult = result,
                            IsIncoming = fromMe // matches WA Web writer
                        },
                        ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // SyncdCoordinator absent in lightweight signaling-only tests
                }
            }

            await managed.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunCallLoopAsync(ManagedCall managed)
    {
        CancellationToken token = managed.Cts.Token;
        Task<byte[]?> recvTask = managed.Udp.ReceiveAsync(token).AsTask();

        while (!token.IsCancellationRequested && managed.Session.Engine.Phase != CallEnginePhase.Ended)
        {
            long? deadline;
            await managed.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                deadline = managed.Session.Engine.NextTimeoutDeadlineMs();
            }
            finally
            {
                managed.Gate.Release();
            }

            long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            int waitMs = deadline is long d ? (int)Math.Clamp(d - nowMs, 1, 250) : 250;

            Task timerTask = Task.Delay(TimeSpan.FromMilliseconds(waitMs), _timeProvider, token);
            Task completed = await Task.WhenAny(recvTask, timerTask).ConfigureAwait(false);

            await managed.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                long tickNow = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                if (completed == recvTask)
                {
                    byte[]? data = await recvTask.ConfigureAwait(false);
                    if (data is { Length: > 0 })
                    {
                        managed.Session.Engine.HandleUdpPacket(data, tickNow);
                    }
                    recvTask = managed.Udp.ReceiveAsync(token).AsTask();
                }
                managed.Session.Engine.HandleTimer(tickNow);
                await DrainEngineOutputsAsync(managed, token).ConfigureAwait(false);
            }
            finally
            {
                managed.Gate.Release();
            }
        }
    }

    private async Task DrainEngineOutputsAsync(ManagedCall managed, CancellationToken ct)
    {
        foreach (CallEngineOutput output in managed.Session.Engine.PollOutputs())
        {
            switch (output)
            {
                case SendUdpOutput udpOut:
                    await managed.Udp.SendAsync(udpOut.Datagram, udpOut.TargetHost, udpOut.TargetPort, ct).ConfigureAwait(false);
                    break;
                case StateChangedOutput st:
                    await _emitEvent(new CallMediaStateChangedEvent(managed.Session.CallId, st.Phase.ToString(), st.Reason)).ConfigureAwait(false);
                    break;
                case DecodedAudioOutput aud:
                    await _emitEvent(new CallAudioFrameReceivedEvent(managed.Session.CallId, aud.Frame.EncodedPayload, aud.Frame.SampleRate, aud.Frame.DurationMs)).ConfigureAwait(false);
                    break;
                case DecodedVideoOutput vid:
                    await _emitEvent(new CallVideoFrameReceivedEvent(managed.Session.CallId, vid.Frame.AnnexBOrAccessUnit, vid.Frame.Timestamp90k, vid.Frame.IsKeyframe)).ConfigureAwait(false);
                    break;
            }
        }
    }

    private OfferRecipientEnc? SelectOfferEncForOwnDevice(IReadOnlyList<OfferRecipientEnc>? encs)
    {
        if (encs is null || encs.Count == 0)
        {
            return null;
        }
        foreach (OfferRecipientEnc e in encs)
        {
            if (e.To is Jid toJid && ((_credentials.Pn is Jid pn && toJid.User == pn.User && toJid.Device == pn.Device)
                || (_credentials.Lid is Jid lid && toJid.User == lid.User && toJid.Device == lid.Device)))
            {
                return e;
            }
        }
        return encs.Count == 1 && encs[0].To is null ? encs[0] : null;
    }

    private string ResolveParticipantId(Jid jid)
    {
        string bare = jid.ToNonAdString();
        if (!jid.IsLid() && _lidPnCache.TryGetLid(bare, out string? lid) && !string.IsNullOrEmpty(lid))
        {
            return lid;
        }
        return bare;
    }

    private bool IsOwnCreator(Jid creator) =>
        (_credentials.Pn is Jid pn && string.Equals(pn.User, creator.User, StringComparison.Ordinal))
        || (_credentials.Lid is Jid lid && string.Equals(lid.User, creator.User, StringComparison.Ordinal));

    public bool TryGetGroupCall(string callId, out ActiveGroupCall? session) =>
        _groupCalls.TryGetValue(callId, out session);

    public async Task<ActiveGroupCall> StartGroupCallAsync(
        IReadOnlyList<Jid> peerJids,
        Jid? groupJid = null,
        bool video = false,
        string? callId = null,
        bool enableSframe = true,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Jid creator = (_credentials.Lid ?? _credentials.Pn)?.ToNonAd()
            ?? throw new InvalidOperationException("Cannot start group call without local identity JID.");
        Jid selfDev = _credentials.Lid ?? _credentials.Pn ?? creator;
        string resolvedCallId = string.IsNullOrWhiteSpace(callId)
            ? $"GRP-{Guid.NewGuid():N}".ToUpperInvariant()[..24]
            : callId;

        var participants = new List<GroupCallParticipant>(1 + peerJids.Count)
        {
            new(creator, [new GroupCallDevice(selfDev, CapabilityVersion: 1, Capability: video ? CallStanzas.CapabilityVideoOffer : CallStanzas.CapabilityOffer)], State: "connected")
        };
        foreach (Jid peer in peerJids)
        {
            Jid bare = peer.ToNonAd();
            participants.Add(new GroupCallParticipant(bare, [new GroupCallDevice(bare)]));
        }

        GroupCallUpdate? ackUpdate = await _signaling.SendInitialGroupOfferAsync(
            resolvedCallId, creator, participants, groupJid, video: video, ct: ct).ConfigureAwait(false);

        ActiveGroupCall grp = GetOrCreateGroupCall(resolvedCallId, creator, video, enableSframe);
        if (ackUpdate is not null)
        {
            grp.State.ApplyInitialOfferAck(ackUpdate);
            await _emitEvent(new GroupCallUpdatedEvent(ackUpdate)).ConfigureAwait(false);
        }
        return grp;
    }

    public async Task<ActiveGroupCall> AcceptGroupInviteAsync(
        IncomingCallStanza offer,
        Node rawOfferStanza,
        bool enableSframe = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(rawOfferStanza);

        await _signaling.SendOfferAckReceiptAsync(offer, ct: ct).ConfigureAwait(false);
        await _signaling.SendTypedCallAckAsync(rawOfferStanza, "offer", ct).ConfigureAwait(false);
        await _signaling.SendActiveGroupPreAcceptAsync(offer.CallId, offer.CallCreator, offer.IsVideo, ct: ct).ConfigureAwait(false);
        await _signaling.SendActiveGroupAcceptAsync(offer.CallId, offer.CallCreator, offer.IsVideo, ct: ct).ConfigureAwait(false);

        ActiveGroupCall grp = GetOrCreateGroupCall(offer.CallId, offer.CallCreator, offer.IsVideo, enableSframe);
        if (offer.GroupUpdate is { } snap)
        {
            if (grp.State.ApplyUpdate(snap) == GroupStateApply.Applied)
            {
                grp.Media.ApplyGroupUpdate(snap);
            }
            await _emitEvent(new GroupCallUpdatedEvent(snap)).ConfigureAwait(false);
        }
        return grp;
    }

    public async Task<byte[]> DistributeEpochKeyAsync(
        string callId,
        uint transactionId,
        IReadOnlyList<Jid> targetDevices,
        byte[]? epochKeyOverride = null,
        CancellationToken ct = default)
    {
        if (!_groupCalls.TryGetValue(callId, out ActiveGroupCall? grp))
        {
            throw new InvalidOperationException($"Unknown group call '{callId}'.");
        }
        byte[] epochKey = epochKeyOverride is { Length: CallCrypto.CallKeySize }
            ? (byte[])epochKeyOverride.Clone()
            : CallCrypto.GenerateCallKey();

        _ = grp.Media.ApplyRawEpoch(transactionId, epochKey);

        SessionEnsureResult ensured = await _preKeyFetcher.EnsureSessionsAsync(targetDevices, reason: null, ct).ConfigureAwait(false);
        foreach (Jid dev in targetDevices)
        {
            if (!ensured.Sessions.TryGetValue(dev, out SignalSession? session))
            {
                continue;
            }
            (string encType, byte[] ctBytes) = CallCrypto.EncryptOfferCallKey(
                epochKey, session, _credentials.IdentityKey, _credentials.RegistrationId);
            await _backend.Signal.SaveSessionAsync(dev.ToProtocolAddress(), session, ct).ConfigureAwait(false);
            await _signaling.SendGroupEncRekeyAsync(
                callId, dev, grp.CallCreator, transactionId, new OfferDeviceKey(dev, ctBytes, encType), ct: ct)
                .ConfigureAwait(false);
        }
        return epochKey;
    }

    public Task<CallLink> CreateCallLinkAsync(CallLinkMedia media, CancellationToken ct = default) =>
        _signaling.CreateCallLinkAsync(media, ct: ct);

    public Task<CallLinkPreview> QueryCallLinkAsync(string token, CallLinkMedia media, CancellationToken ct = default) =>
        _signaling.QueryCallLinkAsync(token, media, ct: ct);

    public async Task<CallLinkJoin> JoinCallLinkAsync(
        string token,
        CallLinkMedia media,
        bool enableSframe = true,
        CancellationToken ct = default)
    {
        CallLinkJoin join = await _signaling.JoinCallLinkAsync(token, media, ct: ct).ConfigureAwait(false);
        ActiveGroupCall grp = GetOrCreateGroupCall(join.CallId, join.CallCreator, media == CallLinkMedia.Video, enableSframe);
        if (join.WaitingRoom is { } wr)
        {
            grp.State.ApplyWaitingRoomUpdate(wr);
            await _emitEvent(new GroupCallWaitingRoomUpdatedEvent(wr)).ConfigureAwait(false);
        }
        if (join.Group is { } snapshot)
        {
            grp.State.ApplyInitialOfferAck(snapshot);
            grp.Media.ApplyGroupUpdate(snapshot);
            await _emitEvent(new GroupCallUpdatedEvent(snapshot)).ConfigureAwait(false);
        }
        return join;
    }

    public Task ToggleWaitingRoomAsync(string callId, Jid creator, bool enabled, CancellationToken ct = default) =>
        _signaling.ToggleWaitingRoomAsync(callId, creator, enabled, ct: ct);

    public Task SendWaitingRoomHeartbeatAsync(string callId, Jid creator, CancellationToken ct = default) =>
        _signaling.SendWaitingRoomHeartbeatAsync(callId, creator, ct: ct);

    public Task AdmitWaitingRoomUserAsync(string callId, Jid creator, Jid user, CancellationToken ct = default) =>
        _signaling.AdmitWaitingRoomUserAsync(callId, creator, user, ct: ct);

    public Task DenyWaitingRoomUserAsync(string callId, Jid creator, Jid user, CancellationToken ct = default) =>
        _signaling.DenyWaitingRoomUserAsync(callId, creator, user, ct: ct);

    public Task SetRaiseHandAsync(string callId, Jid creator, bool raised, CancellationToken ct = default) =>
        _signaling.SetRaiseHandAsync(callId, new Jid(callId, Server.Call), creator, raised, ct: ct);

    public Task SetScreenShareAsync(string callId, Jid creator, ScreenShareState state, uint? screenShareId = null, CancellationToken ct = default) =>
        _signaling.SetScreenShareAsync(callId, new Jid(callId, Server.Call), creator, state, screenShareId, ct: ct);

    public async Task ProcessIncomingCallStanzaAsync(IncomingCallStanza parsed, Node rawStanza, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(rawStanza);

        switch (parsed.ActionKind)
        {
            case CallActionKind.GroupUpdate when parsed.GroupUpdate is { } update:
            {
                ActiveGroupCall grp = GetOrCreateGroupCall(update.CallId, update.CallCreator, string.Equals(update.Media, "video", StringComparison.Ordinal), enableSframe: true);
                if (grp.State.ApplyUpdate(update) == GroupStateApply.Applied)
                {
                    grp.Media.ApplyGroupUpdate(update);
                }
                await _signaling.SendTypedCallAckAsync(rawStanza, "group_update", ct).ConfigureAwait(false);
                await _emitEvent(new GroupCallUpdatedEvent(update)).ConfigureAwait(false);
                break;
            }
            case CallActionKind.EncRekey when parsed.EncRekey is { } rekey:
            {
                ActiveGroupCall grp = GetOrCreateGroupCall(rekey.CallId, rekey.CallCreator, parsed.IsVideo, enableSframe: true);
                try
                {
                    string senderAddr = parsed.From.ToProtocolAddress();
                    (byte[] epochKey, _) = await DecryptSignalCallKeyAsync(senderAddr, rekey.EncryptionType, rekey.Ciphertext, ct).ConfigureAwait(false);
                    GroupEpochApply applied = grp.Media.ApplyRawEpoch(rekey.TransactionId, epochKey);
                    if (applied is GroupEpochApply.Conflicting or GroupEpochApply.Invalid)
                    {
                        throw new InvalidOperationException($"Epoch install rejected: {applied}.");
                    }
                    await _signaling.SendTypedCallAckAsync(rawStanza, "enc_rekey", ct).ConfigureAwait(false);
                    await _emitEvent(new GroupCallRekeyReceivedEvent(rekey.CallId, parsed.From.ToString(), rekey.TransactionId)).ConfigureAwait(false);
                }
                catch
                {
                    await _signaling.SendGroupEncRekeyRetryReceiptAsync(parsed.StanzaId, parsed.From, rekey.CallId, rekey.CallCreator, ct: ct).ConfigureAwait(false);
                }
                break;
            }
            case CallActionKind.WaitingRoomUpdate when parsed.WaitingRoom is { } room:
            {
                ActiveGroupCall grp = GetOrCreateGroupCall(room.CallId, room.CallCreator, room.Media == CallLinkMedia.Video, enableSframe: true);
                grp.State.ApplyWaitingRoomUpdate(room);
                await _signaling.SendTypedCallAckAsync(rawStanza, "waiting_room_update", ct).ConfigureAwait(false);
                await _emitEvent(new GroupCallWaitingRoomUpdatedEvent(room)).ConfigureAwait(false);
                break;
            }
            case CallActionKind.UserAction when parsed.RaisedHand is bool raised:
            {
                if (_groupCalls.TryGetValue(parsed.CallId, out ActiveGroupCall? grp))
                {
                    grp.State.ApplyRaiseHand(parsed.Participant ?? parsed.From, raised);
                }
                await _signaling.SendTypedCallAckAsync(rawStanza, "user_action", ct).ConfigureAwait(false);
                await _emitEvent(new GroupCallRaisedHandChangedEvent(parsed.CallId, (parsed.Participant ?? parsed.From).ToNonAdString(), raised)).ConfigureAwait(false);
                break;
            }
            case CallActionKind.ScreenShare when parsed.ScreenShare is { } share:
            {
                if (_groupCalls.TryGetValue(parsed.CallId, out ActiveGroupCall? grp))
                {
                    grp.State.ApplyScreenShare(parsed.Participant ?? parsed.From, share);
                }
                await _signaling.SendTypedCallAckAsync(rawStanza, "screen_share", ct).ConfigureAwait(false);
                await _emitEvent(new GroupCallScreenShareChangedEvent(parsed.CallId, (parsed.Participant ?? parsed.From).ToNonAdString(), share)).ConfigureAwait(false);
                break;
            }
        }
    }

    private ActiveGroupCall GetOrCreateGroupCall(string callId, Jid callCreator, bool isVideo, bool enableSframe) =>
        _groupCalls.GetOrAdd(callId, id =>
        {
            Jid selfDev = _credentials.Lid ?? _credentials.Pn ?? callCreator;
            return new ActiveGroupCall(
                id,
                callCreator,
                isVideo,
                new GroupCallState(id, callCreator),
                new GroupMediaSession(id, callCreator, selfDev, enableSframe: enableSframe));
        });

    private async Task<(byte[] CallKey, SignalSession UpdatedSession)> DecryptSignalCallKeyAsync(
        string senderAddr,
        string encType,
        byte[] ciphertext,
        CancellationToken ct)
    {
        SignalSession? existing = await _backend.Signal.GetSessionAsync(senderAddr, ct).ConfigureAwait(false);
        uint? preKeyId = null;
        uint signedPreKeyId = _credentials.SignedPreKeyId;
        if (string.Equals(encType, "pkmsg", StringComparison.Ordinal))
        {
            var pkmsg = WhatsApp.Protocol.Signal.PreKeySignalMessage.Parse(ciphertext);
            preKeyId = pkmsg.PreKeyId;
            signedPreKeyId = pkmsg.SignedPreKeyId;
        }
        var spkRecord = await _backend.Signal.GetSignedPreKeyAsync(signedPreKeyId, ct).ConfigureAwait(false);
        KeyPair spkPair = spkRecord is not null
            ? PreKeyManager.DecodeSignedPreKeyRecord(spkRecord.KeyPairBytes).KeyPair
            : _credentials.SignedPreKey;

        KeyPair? otpk = preKeyId is uint pkId && await _backend.Signal.GetPreKeyAsync(pkId, ct).ConfigureAwait(false) is { } otpkBytes
            ? PreKeyManager.DecodePreKeyRecord(otpkBytes).KeyPair
            : null;

        (byte[] callKey, SignalSession updatedSession) = CallCrypto.DecryptOfferCallKey(
            encType, ciphertext, _credentials.IdentityKey, spkPair, otpk, existing);

        await _backend.Signal.SaveSessionAsync(senderAddr, updatedSession, ct).ConfigureAwait(false);
        if (preKeyId is uint usedId)
        {
            await _backend.Signal.DeletePreKeyAsync(usedId, ct).ConfigureAwait(false);
        }
        return (callKey, updatedSession);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        foreach (string id in _activeCalls.Keys.ToArray())
        {
            if (_activeCalls.TryRemove(id, out ManagedCall? call))
            {
                await call.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
