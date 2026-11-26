using System.Collections.Concurrent;
using System.Globalization;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Api;

/// <summary>
/// Runtime <c>&lt;call&gt;</c> signaling coordinator and call-privacy/log client.
/// Dispatches inbound <c>&lt;call&gt;</c> stanzas, awaits <c>&lt;ack class="call"&gt;</c>,
/// tracks synced <c>setting_relayAllCalls</c>, sends 1:1 / group / call-link / rekey stanzas,
/// and syncs <c>call_log</c> AppState mutations.
/// </summary>
public sealed class CallSignalingClient
{
    private readonly NoiseSessionSocket _socket;
    private readonly SyncdCoordinator? _syncd;
    private readonly DeviceCredentials _credentials;
    private readonly Func<WhatsAppEvent, ValueTask> _emitEvent;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Node>> _ackWaiters = new(StringComparer.Ordinal);
    private long _seq;
    private int _relayAllCallsEnabled;

    public CallSignalingClient(
        NoiseSessionSocket socket,
        DeviceCredentials credentials,
        Func<WhatsAppEvent, ValueTask> emitEvent,
        SyncdCoordinator? syncd = null,
        TimeProvider? timeProvider = null,
        bool initialRelayAllCalls = false)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _emitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
        _syncd = syncd;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _relayAllCallsEnabled = initialRelayAllCalls ? 1 : 0;
    }

    /// <summary>
    /// Current account relay-only preference (updated automatically from Syncd <c>PrivacySettingRelayAllCalls</c>).
    /// </summary>
    public bool RelayAllCallsEnabled => Volatile.Read(ref _relayAllCallsEnabled) != 0;

    internal void ApplyRelayAllCallsSetting(bool enabled) =>
        Volatile.Write(ref _relayAllCallsEnabled, enabled ? 1 : 0);

    public bool ResolveShouldForceRelay(bool? perCallOverride = null) =>
        perCallOverride ?? RelayAllCallsEnabled;

    public async Task<IncomingCallStanza?> HandleCallStanzaAsync(Node stanza, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stanza);
        IncomingCallStanza? parsed = CallStanzas.ParseIncomingCall(stanza);
        if (parsed is null)
        {
            return null;
        }

        await _emitEvent(new IncomingCallReceivedEvent(parsed, stanza)).ConfigureAwait(false);
        return parsed;
    }

    public string NextWrapperId() =>
        $"call-{Interlocked.Increment(ref _seq).ToString(CultureInfo.InvariantCulture)}";

    public Task SendOfferAsync(
        string callId,
        Jid to,
        IReadOnlyList<OfferDeviceKey> deviceKeys,
        Jid? callCreator = null,
        string? wrapperId = null,
        byte[]? privacyToken = null,
        byte[]? capability = null,
        bool multiDevice = false,
        bool video = false,
        IReadOnlyList<string>? audioRates = null,
        CancellationToken ct = default)
    {
        Jid creator = callCreator ?? (_credentials.Lid ?? _credentials.Pn)?.ToNonAd()
            ?? throw new InvalidOperationException("Cannot place call without local identity JID.");
        Node node = CallStanzas.BuildOffer(
            callId,
            to,
            creator,
            deviceKeys,
            wrapperId ?? NextWrapperId(),
            privacyToken,
            capability ?? (video ? CallStanzas.CapabilityVideoOffer : CallStanzas.CapabilityOffer),
            _credentials.AccountIdentityBytes,
            multiDevice,
            video,
            audioRates);
        return _socket.SendNodeAsync(node, ct);
    }

    public Task SendOfferAckReceiptAsync(IncomingCallStanza offerCall, Jid? ownAdJid = null, CancellationToken ct = default)
    {
        Node? receipt = CallStanzas.BuildOfferAckReceipt(offerCall, ownAdJid ?? _credentials.Lid ?? _credentials.Pn);
        return receipt is null ? Task.CompletedTask : _socket.SendNodeAsync(receipt, ct);
    }

    public Task SendTypedCallAckAsync(Node originalCall, string actionType, CancellationToken ct = default)
    {
        Node? ack = CallStanzas.BuildTypedCallAck(originalCall, actionType);
        return ack is null ? Task.CompletedTask : _socket.SendNodeAsync(ack, ct);
    }

    public Task SendPreAcceptAsync(
        string callId,
        Jid to,
        Jid callCreator,
        IReadOnlyList<string>? audioRates = null,
        bool video = false,
        byte[]? capability = null,
        string? wrapperId = null,
        CancellationToken ct = default)
    {
        Node node = CallStanzas.BuildPreAccept(
            callId,
            to,
            callCreator,
            wrapperId ?? NextWrapperId(),
            audioRates,
            video,
            capability);
        return _socket.SendNodeAsync(node, ct);
    }

    public Task SendAcceptAsync(
        string callId,
        Jid to,
        Jid callCreator,
        IReadOnlyList<string>? audioRates = null,
        byte[]? relayTe = null,
        byte[]? rte = null,
        byte[]? voipSettings = null,
        byte[]? capability = null,
        bool video = false,
        string? peerAbtestBucket = null,
        string? peerAbtestBucketIdList = null,
        string? wrapperId = null,
        CancellationToken ct = default)
    {
        Node node = CallStanzas.BuildAccept(
            callId,
            to,
            callCreator,
            wrapperId ?? NextWrapperId(),
            audioRates,
            relayTe,
            rte,
            voipSettings,
            capability,
            video,
            peerAbtestBucket,
            peerAbtestBucketIdList);
        return _socket.SendNodeAsync(node, ct);
    }

    public Task SendRejectAsync(
        string callId,
        Jid to,
        Jid callCreator,
        string? reason = null,
        string? wrapperId = null,
        CancellationToken ct = default)
    {
        Node node = CallStanzas.BuildReject(callId, to, callCreator, wrapperId ?? NextWrapperId(), reason);
        return _socket.SendNodeAsync(node, ct);
    }

    public Task SendTerminateAsync(
        string callId,
        Jid to,
        Jid callCreator,
        string? reason = null,
        string? wrapperId = null,
        CancellationToken ct = default)
    {
        Node node = CallStanzas.BuildTerminate(callId, to, callCreator, wrapperId ?? NextWrapperId(), reason);
        return _socket.SendNodeAsync(node, ct);
    }

    public Task SendTransportAsync(
        string callId,
        Jid to,
        Jid callCreator,
        string? p2pCandRound = null,
        string? transportMessageType = null,
        byte[]? relayTe = null,
        CancellationToken ct = default)
    {
        Node node = CallStanzas.BuildTransport(callId, to, callCreator, p2pCandRound, transportMessageType, relayTe);
        return _socket.SendNodeAsync(node, ct);
    }

    public Task SendRelayLatencyAsync(
        string callId,
        Jid to,
        Jid callCreator,
        IReadOnlyList<RelayLatencyEntry> entries,
        IReadOnlyList<Jid>? devices = null,
        CancellationToken ct = default)
    {
        Node node = CallStanzas.BuildRelayLatency(callId, to, callCreator, entries, devices);
        return _socket.SendNodeAsync(node, ct);
    }

    public Task SendMuteAsync(
        string callId,
        Jid to,
        Jid callCreator,
        bool muted,
        string? wrapperId = null,
        CancellationToken ct = default)
    {
        Node node = CallStanzas.BuildMuteV2(callId, to, callCreator, wrapperId ?? NextWrapperId(), muted);
        return _socket.SendNodeAsync(node, ct);
    }

    public Task SendVideoStateAsync(
        string callId,
        Jid to,
        Jid callCreator,
        CallVideoState state,
        string? dec = null,
        byte? deviceOrientation = null,
        string? wrapperId = null,
        CancellationToken ct = default)
    {
        Node node = CallStanzas.BuildVideoState(callId, to, wrapperId ?? NextWrapperId(), callCreator, state, dec, deviceOrientation);
        return _socket.SendNodeAsync(node, ct);
    }

    public Task SendHeartbeatAsync(string callId, Jid callCreator, string? wrapperId = null, CancellationToken ct = default)
    {
        Node node = CallStanzas.BuildHeartbeat(callId, callCreator, wrapperId ?? NextWrapperId());
        return _socket.SendNodeAsync(node, ct);
    }

    public bool TryResolveCallAck(Node ackNode)
    {
        ArgumentNullException.ThrowIfNull(ackNode);
        if (!string.Equals(ackNode.Tag, "ack", StringComparison.Ordinal)
            || !string.Equals(ackNode.GetAttr("class"), "call", StringComparison.Ordinal))
        {
            return false;
        }
        string? id = ackNode.GetAttr("id");
        if (string.IsNullOrEmpty(id) || !_ackWaiters.TryRemove(id, out var tcs))
        {
            return false;
        }
        return tcs.TrySetResult(ackNode);
    }

    public async Task<Node> SendAndAwaitCallAckAsync(string wrapperId, Node stanza, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(wrapperId);
        ArgumentNullException.ThrowIfNull(stanza);

        var tcs = new TaskCompletionSource<Node>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ackWaiters.TryAdd(wrapperId, tcs))
        {
            throw new InvalidOperationException($"Duplicate pending call ack waiter '{wrapperId}'.");
        }
        using var reg = ct.Register(() =>
        {
            if (_ackWaiters.TryRemove(wrapperId, out var pending))
            {
                pending.TrySetCanceled(ct);
            }
        });
        try
        {
            await _socket.SendNodeAsync(stanza, ct).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            _ackWaiters.TryRemove(wrapperId, out _);
            throw;
        }
    }

    public async Task<GroupCallUpdate?> SendInitialGroupOfferAsync(
        string callId,
        Jid callCreator,
        IReadOnlyList<GroupCallParticipant> participants,
        Jid? groupJid = null,
        uint audioRate = 16000,
        bool video = false,
        string? wrapperId = null,
        CancellationToken ct = default)
    {
        string id = wrapperId ?? NextWrapperId();
        Node node = GroupCallStanzas.BuildInitialGroupOffer(callId, id, callCreator, participants, groupJid, audioRate, video);
        Node ack = await SendAndAwaitCallAckAsync(id, node, ct).ConfigureAwait(false);
        return GroupCallStanzas.ParseInitialGroupCallAck(ack);
    }

    public Task SendGroupInviteOfferAsync(
        string callId,
        Jid to,
        Jid callCreator,
        IReadOnlyList<Jid> targetDevices,
        IReadOnlyList<GroupCallParticipant> participants,
        bool video = false,
        byte deviceOrientation = 0,
        string? wrapperId = null,
        CancellationToken ct = default)
    {
        string id = wrapperId ?? NextWrapperId();
        Node node = GroupCallStanzas.BuildGroupInviteOffer(callId, id, to, callCreator, targetDevices, participants, video, deviceOrientation);
        return _socket.SendNodeAsync(node, ct);
    }

    public Task SendActiveGroupPreAcceptAsync(string callId, Jid callCreator, bool video = false, string? wrapperId = null, CancellationToken ct = default) =>
        _socket.SendNodeAsync(GroupCallStanzas.BuildActiveGroupPreAccept(callId, callCreator, wrapperId ?? NextWrapperId(), video), ct);

    public Task SendActiveGroupAcceptAsync(string callId, Jid callCreator, bool video = false, string? wrapperId = null, CancellationToken ct = default) =>
        _socket.SendNodeAsync(GroupCallStanzas.BuildActiveGroupAccept(callId, callCreator, wrapperId ?? NextWrapperId(), video), ct);

    public Task SendGroupEncRekeyAsync(
        string callId,
        Jid to,
        Jid callCreator,
        uint transactionId,
        OfferDeviceKey deviceKey,
        byte[]? deviceIdentity = null,
        string? wrapperId = null,
        CancellationToken ct = default)
    {
        Node node = GroupCallStanzas.BuildGroupEncRekey(
            callId,
            wrapperId ?? NextWrapperId(),
            to,
            callCreator,
            transactionId,
            deviceKey,
            deviceIdentity ?? _credentials.AccountIdentityBytes);
        return _socket.SendNodeAsync(node, ct);
    }

    public Task SendGroupEncRekeyRetryReceiptAsync(
        string stanzaId,
        Jid peerJid,
        string callId,
        Jid callCreator,
        uint currentRetryCount = 0,
        CancellationToken ct = default)
    {
        Node node = GroupCallStanzas.BuildGroupEncRekeyRetryReceipt(
            stanzaId,
            peerJid,
            callId,
            callCreator,
            _credentials.RegistrationId,
            currentRetryCount);
        return _socket.SendNodeAsync(node, ct);
    }

    public async Task<CallLink> CreateCallLinkAsync(CallLinkMedia media, string? wrapperId = null, CancellationToken ct = default)
    {
        string id = wrapperId ?? NextWrapperId();
        Node ack = await SendAndAwaitCallAckAsync(id, GroupCallStanzas.BuildCallLinkCreate(media, id), ct).ConfigureAwait(false);
        return GroupCallStanzas.ParseCallLinkCreateAck(ack);
    }

    public async Task<CallLinkPreview> QueryCallLinkAsync(string token, CallLinkMedia media, string? wrapperId = null, CancellationToken ct = default)
    {
        string id = wrapperId ?? NextWrapperId();
        Node ack = await SendAndAwaitCallAckAsync(id, GroupCallStanzas.BuildCallLinkQuery(token, media, id), ct).ConfigureAwait(false);
        return GroupCallStanzas.ParseCallLinkQueryAck(ack);
    }

    public async Task<CallLinkJoin> JoinCallLinkAsync(string token, CallLinkMedia media, byte[]? capability = null, string? wrapperId = null, CancellationToken ct = default)
    {
        string id = wrapperId ?? NextWrapperId();
        Node ack = await SendAndAwaitCallAckAsync(id, GroupCallStanzas.BuildCallLinkJoin(token, media, id, capability), ct).ConfigureAwait(false);
        return GroupCallStanzas.ParseCallLinkJoinAck(ack, token);
    }

    public async Task ToggleWaitingRoomAsync(string callId, Jid creator, bool enabled, string? wrapperId = null, CancellationToken ct = default)
    {
        string id = wrapperId ?? NextWrapperId();
        _ = await SendAndAwaitCallAckAsync(id, GroupCallStanzas.BuildWaitingRoomToggle(callId, creator, enabled, id), ct).ConfigureAwait(false);
    }

    public Task SendWaitingRoomHeartbeatAsync(string callId, Jid creator, string? wrapperId = null, CancellationToken ct = default) =>
        _socket.SendNodeAsync(GroupCallStanzas.BuildWaitingRoomHeartbeat(callId, creator, wrapperId ?? NextWrapperId()), ct);

    public async Task AdmitWaitingRoomUserAsync(string callId, Jid creator, Jid user, string? wrapperId = null, CancellationToken ct = default)
    {
        string id = wrapperId ?? NextWrapperId();
        _ = await SendAndAwaitCallAckAsync(id, GroupCallStanzas.BuildWaitingRoomAdmit(callId, creator, user, id), ct).ConfigureAwait(false);
    }

    public async Task DenyWaitingRoomUserAsync(string callId, Jid creator, Jid user, string? wrapperId = null, CancellationToken ct = default)
    {
        string id = wrapperId ?? NextWrapperId();
        _ = await SendAndAwaitCallAckAsync(id, GroupCallStanzas.BuildWaitingRoomDeny(callId, creator, user, id), ct).ConfigureAwait(false);
    }

    public Task SetRaiseHandAsync(string callId, Jid to, Jid creator, bool raised, string? wrapperId = null, CancellationToken ct = default) =>
        _socket.SendNodeAsync(GroupCallStanzas.BuildRaiseHand(callId, to, creator, wrapperId ?? NextWrapperId(), raised), ct);

    public Task SetScreenShareAsync(string callId, Jid to, Jid creator, ScreenShareState state, uint? screenShareId = null, string? wrapperId = null, CancellationToken ct = default) =>
        _socket.SendNodeAsync(GroupCallStanzas.BuildScreenShare(callId, to, creator, wrapperId ?? NextWrapperId(), state, screenShareId), ct);

    public Task RecordCallLogAsync(
        Jid callCreatorJid,
        string callId,
        bool fromMe,
        CallLogRecord record,
        CancellationToken ct = default)
    {
        if (_syncd is null)
        {
            throw new InvalidOperationException("SyncdCoordinator is not initialized in this session.");
        }
        ArgumentNullException.ThrowIfNull(record);

        byte[] index = AppStateSchemas.CallLog.BuildIndex(
            callCreatorJid.ToNonAdString(),
            callId,
            fromMe ? "1" : "0");

        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            CallLogAction = new SyncActionValue.Types.CallLogAction
            {
                CallLogRecord = record.Clone()
            }
        };

        return _syncd.SendMutationAsync(
            AppStateSchemas.CallLog.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            index,
            value,
            ct: ct);
    }
}
