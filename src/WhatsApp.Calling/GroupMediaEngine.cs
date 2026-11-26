using System.Security.Cryptography;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Calling;

public enum GroupStateApply
{
    Applied,
    Stale,
    IdentityMismatch,
    InvalidSnapshot,
}

public enum GroupEpochApply
{
    Installed,
    Buffered,
    Stale,
    Duplicate,
    Conflicting,
    Invalid,
}

public sealed record ParticipantAudioOutput(
    string ParticipantId,
    Jid UserJid,
    Jid DeviceJid,
    uint? Pid,
    RtpHeader Header,
    AudioFrame Frame);

public sealed record ParticipantVideoOutput(
    string ParticipantId,
    Jid UserJid,
    Jid DeviceJid,
    uint? Pid,
    RtpHeader Header,
    VideoFrame Frame);

/// <summary>
/// Transaction-ordered authoritative group-call state machine matching <c>wacore/src/voip_control/group.rs</c>.
/// Tracks roster, relay refreshes, waiting room snapshots, raised hands, screen shares, and owed initial rekeys.
/// </summary>
public sealed class GroupCallState
{
    private uint? _waitingRoomTx;
    private readonly HashSet<Jid> _raisedHands = [];
    private readonly Dictionary<Jid, ScreenShare> _screenShares = new();

    public string CallId { get; }
    public Jid CallCreator { get; }
    public GroupCallUpdate? Snapshot { get; private set; }
    public WaitingRoom? WaitingRoom { get; private set; }
    public IReadOnlySet<Jid> RaisedHands => _raisedHands;
    public IReadOnlyDictionary<Jid, ScreenShare> ScreenShares => _screenShares;
    public bool OwedRekey { get; private set; }

    public GroupCallState(string callId, Jid callCreator)
    {
        if (string.IsNullOrWhiteSpace(callId)) throw new ArgumentException("callId required", nameof(callId));
        CallId = callId;
        CallCreator = callCreator;
    }

    public GroupStateApply ApplyUpdate(GroupCallUpdate update) =>
        ApplySnapshot(update, initialOfferAck: false);

    public GroupStateApply ApplyInitialOfferAck(GroupCallUpdate update) =>
        ApplySnapshot(update, initialOfferAck: true);

    public GroupStateApply ApplyWaitingRoomUpdate(WaitingRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (!string.Equals(room.CallId, CallId, StringComparison.Ordinal) || room.CallCreator != CallCreator)
        {
            return GroupStateApply.IdentityMismatch;
        }
        if (room.TransactionId is uint nextTx && _waitingRoomTx is uint prevTx && nextTx <= prevTx)
        {
            return GroupStateApply.Stale;
        }
        if (room.TransactionId is uint tx)
        {
            _waitingRoomTx = tx;
        }
        WaitingRoom = room;
        return GroupStateApply.Applied;
    }

    public void ApplyRaiseHand(Jid participantJid, bool raised)
    {
        Jid bare = participantJid.ToNonAd();
        if (raised) _raisedHands.Add(bare);
        else _raisedHands.Remove(bare);
    }

    public void ApplyScreenShare(Jid participantJid, ScreenShare share)
    {
        Jid bare = participantJid.ToNonAd();
        if (share.State == ScreenShareState.Started)
        {
            _screenShares[bare] = share;
        }
        else
        {
            _screenShares.Remove(bare);
        }
    }

    public bool ConsumeOwedRekey()
    {
        if (!OwedRekey) return false;
        OwedRekey = false;
        return true;
    }

    private GroupStateApply ApplySnapshot(GroupCallUpdate update, bool initialOfferAck)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!string.Equals(update.CallId, CallId, StringComparison.Ordinal) || update.CallCreator != CallCreator)
        {
            return GroupStateApply.IdentityMismatch;
        }

        if (Snapshot?.GroupJid is Jid currentGroup)
        {
            if (update.GroupJid is Jid incomingGroup && incomingGroup != currentGroup)
            {
                return GroupStateApply.IdentityMismatch;
            }
            if (update.GroupJid is null)
            {
                update = update with { GroupJid = currentGroup };
            }
        }

        bool useInitialRelaxation = initialOfferAck && Snapshot is null;
        if (initialOfferAck && Snapshot is not null && (update.TransactionId == 0 || update.ConnectedLimit == 0))
        {
            return GroupStateApply.Stale;
        }

        if (!IsValidSnapshot(update, useInitialRelaxation))
        {
            return GroupStateApply.InvalidSnapshot;
        }

        if (Snapshot is { } current && update.TransactionId <= current.TransactionId)
        {
            bool advancesRelay = update.Relay is { TransactionId: uint nextRelayTx }
                && (current.Relay?.TransactionId is not uint prevRelayTx || nextRelayTx > prevRelayTx);
            if (advancesRelay)
            {
                Snapshot = current with { Relay = update.Relay };
                return GroupStateApply.Applied;
            }
            return GroupStateApply.Stale;
        }

        if (Snapshot is { } prev)
        {
            update = InheritMissingPidsAndPreserveRelay(update, prev);
        }

        Snapshot = update;
        if (update.RekeyRequested)
        {
            OwedRekey = true;
        }

        var activeBare = update.Participants.Select(p => p.Jid.ToNonAd()).ToHashSet();
        _raisedHands.RemoveWhere(j => !activeBare.Contains(j));
        foreach (Jid key in _screenShares.Keys.Where(j => !activeBare.Contains(j)).ToList())
        {
            _screenShares.Remove(key);
        }

        return GroupStateApply.Applied;
    }

    private static GroupCallUpdate InheritMissingPidsAndPreserveRelay(GroupCallUpdate next, GroupCallUpdate prev)
    {
        var knownPids = new Dictionary<Jid, uint>();
        foreach (GroupCallParticipant p in prev.Participants)
        {
            foreach (GroupCallDevice d in p.Devices)
            {
                if (d.Pid is uint pid)
                {
                    knownPids[d.Jid] = pid;
                }
            }
        }

        var mergedParticipants = new List<GroupCallParticipant>(next.Participants.Count);
        foreach (GroupCallParticipant p in next.Participants)
        {
            bool changed = false;
            var devs = new List<GroupCallDevice>(p.Devices.Count);
            foreach (GroupCallDevice d in p.Devices)
            {
                if (d.Pid is null && knownPids.TryGetValue(d.Jid, out uint inherited))
                {
                    devs.Add(d with { Pid = inherited });
                    changed = true;
                }
                else
                {
                    devs.Add(d);
                }
            }
            mergedParticipants.Add(changed ? p with { Devices = devs } : p);
        }

        GroupCallRelay? relay = next.Relay ?? prev.Relay;
        return next with { Participants = mergedParticipants, Relay = relay };
    }

    private static bool IsValidSnapshot(GroupCallUpdate update, bool initialOfferAck)
    {
        if (update.Participants.Count == 0 || update.Participants.Count > GroupCallStanzas.GroupCallMaxParticipants)
        {
            return false;
        }
        if (!initialOfferAck && (update.TransactionId == 0 || update.ConnectedLimit == 0))
        {
            return false;
        }
        return update.Participants.All(p => !string.IsNullOrEmpty(p.Jid.User) && p.Devices.Count > 0);
    }
}

/// <summary>
/// Per-participant SFU group media cipher &amp; SSRC router with transaction-ordered
/// shared keygen-v2 epochs (<c>enc_rekey</c>) matching <c>wacore/src/voip/group_media.rs</c>.
/// </summary>
public sealed class GroupMediaSession
{
    public const int MaxBufferedEpochs = 8;

    private sealed class ParticipantReceiver
    {
        public string ParticipantId { get; }
        public Jid UserJid { get; set; }
        public Jid DeviceJid { get; set; }
        public uint? Pid { get; set; }
        public uint AudioSsrc { get; }
        public uint PrimaryVideoSsrc { get; }
        public uint ScreenShareVideoSsrc { get; }
        public uint AppDataSsrc { get; }
        public bool VideoEnabled { get; set; }
        public SrtpKeys? RtpKeys { get; set; }
        public SrtpKeys? RtcpKeys { get; set; }
        public SframeSession? Sframe { get; set; }
        public RecvRocTracker AudioRoc { get; } = new();
        public RecvRocTracker VideoRoc { get; } = new();
        public RecvRocTracker ScreenShareRoc { get; } = new();
        public JitterBuffer AudioJitter { get; } = new();

        public ParticipantReceiver(
            string callId,
            string participantId,
            Jid userJid,
            Jid deviceJid,
            uint? pid,
            bool videoEnabled)
        {
            ParticipantId = participantId;
            UserJid = userJid;
            DeviceJid = deviceJid;
            Pid = pid;
            VideoEnabled = videoEnabled;
            AudioSsrc = CallCrypto.DeriveParticipantSsrc(callId, participantId, CallCrypto.AudioSsrcSlotWord);
            PrimaryVideoSsrc = CallCrypto.DeriveVideoParticipantSsrc(callId, participantId);
            ScreenShareVideoSsrc = CallCrypto.DeriveParticipantSsrc(callId, participantId, CallCrypto.HbhFecTxSsrcSlotWord);
            AppDataSsrc = CallCrypto.DeriveParticipantSsrc(callId, participantId, CallCrypto.AppDataSsrcSlotWord);
        }

        public void InstallKeys(byte[] epochKey32, string selfParticipantId, bool enableSframe)
        {
            RtpKeys = SrtpCipher.DeriveE2eRtpKeys(epochKey32, ParticipantId);
            RtcpKeys = SrtpCipher.DeriveE2eRtcpKeys(epochKey32, ParticipantId);
            Sframe = enableSframe ? SframeSession.TryCreate(epochKey32, selfParticipantId, ParticipantId) : null;
        }
    }

    private readonly string _callId;
    private readonly Jid _callCreator;
    private readonly string _selfParticipantId;
    private readonly Jid _selfDevice;
    private readonly int _warpMiTagLen;
    private readonly bool _enableSframe;

    private readonly Dictionary<string, ParticipantReceiver> _receivers = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _audioRoutes = new();
    private readonly Dictionary<uint, string> _videoRoutes = new();
    private readonly SortedDictionary<uint, byte[]> _pendingEpochs = new();

    private (uint Tx, byte[] Key)? _installedEpoch;
    private SrtpKeys? _txRtpKeys;
    private SrtpKeys? _txRtcpKeys;
    private readonly Dictionary<string, SframeSession> _txSframePerPeer = new(StringComparer.Ordinal);
    private readonly SendRocTracker _txAudioRoc = new();
    private readonly SendRocTracker _txVideoRoc = new();
    private readonly SendRocTracker _txScreenShareRoc = new();
    private readonly RtpStream _txAudioStream;
    private readonly RtpStream _txVideoStream;
    private readonly RtpStream _txScreenShareStream;

    public uint? RosterTransaction { get; private set; }
    public uint? InstalledEpochTransaction => _installedEpoch?.Tx;
    public uint LocalAudioSsrc => _txAudioStream.Ssrc;
    public uint LocalVideoSsrc => _txVideoStream.Ssrc;
    public uint LocalScreenShareSsrc => _txScreenShareStream.Ssrc;
    public IReadOnlyCollection<string> ActiveParticipantIds => _receivers.Keys;

    public GroupMediaSession(
        string callId,
        Jid callCreator,
        Jid selfDeviceLid,
        int warpMiTagLen = SrtpCipher.WarpMiTagLength,
        bool enableSframe = false)
    {
        if (string.IsNullOrWhiteSpace(callId)) throw new ArgumentException("callId required", nameof(callId));
        if (warpMiTagLen is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(warpMiTagLen));

        _callId = callId;
        _callCreator = callCreator;
        _selfDevice = selfDeviceLid;
        _selfParticipantId = CallCrypto.FormatParticipantId(selfDeviceLid.ToString());
        _warpMiTagLen = warpMiTagLen;
        _enableSframe = enableSframe;

        _txAudioStream = new RtpStream(CallCrypto.DeriveParticipantSsrc(callId, _selfParticipantId, CallCrypto.AudioSsrcSlotWord));
        _txVideoStream = new RtpStream(CallCrypto.DeriveVideoParticipantSsrc(callId, _selfParticipantId), 6000, RtpRtcpCodec.PayloadTypeH264);
        _txScreenShareStream = new RtpStream(CallCrypto.DeriveParticipantSsrc(callId, _selfParticipantId, CallCrypto.HbhFecTxSsrcSlotWord), 6000, RtpRtcpCodec.PayloadTypeH264);
    }

    public GroupStateApply ApplyGroupUpdate(GroupCallUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!string.Equals(update.CallId, _callId, StringComparison.Ordinal) || update.CallCreator != _callCreator)
        {
            return GroupStateApply.IdentityMismatch;
        }
        if (RosterTransaction is uint currentTx && update.TransactionId <= currentTx)
        {
            return GroupStateApply.Stale;
        }

        bool isVideoCall = string.Equals(update.Media, "video", StringComparison.Ordinal);
        var next = new Dictionary<string, ParticipantReceiver>(StringComparer.Ordinal);

        foreach (GroupCallParticipant p in update.Participants)
        {
            if (!p.IsConnected) continue;
            foreach (GroupCallDevice dev in p.Devices)
            {
                if (dev.Jid == _selfDevice) continue;
                string pidStr = CallCrypto.FormatParticipantId(dev.Jid.ToString());

                ParticipantReceiver rec;
                if (_receivers.TryGetValue(pidStr, out ParticipantReceiver? existing) && existing.Pid == dev.Pid)
                {
                    rec = existing;
                    rec.UserJid = p.Jid;
                    rec.DeviceJid = dev.Jid;
                    rec.VideoEnabled = isVideoCall;
                }
                else
                {
                    rec = new ParticipantReceiver(_callId, pidStr, p.Jid, dev.Jid, dev.Pid, isVideoCall);
                    if (_installedEpoch is { Key: var epochKey })
                    {
                        rec.InstallKeys(epochKey, _selfParticipantId, _enableSframe);
                    }
                }
                next[pidStr] = rec;
            }
        }

        _receivers.Clear();
        foreach (var kv in next)
        {
            _receivers[kv.Key] = kv.Value;
        }

        RosterTransaction = update.TransactionId;
        InstallBestPendingEpoch();
        RebuildRoutes();
        return GroupStateApply.Applied;
    }

    public GroupEpochApply ApplyRawEpoch(uint transactionId, ReadOnlySpan<byte> rawEpoch32)
    {
        if (transactionId == 0 || rawEpoch32.Length != CallCrypto.CallKeySize)
        {
            return GroupEpochApply.Invalid;
        }
        if (_installedEpoch is { Tx: var installedTx, Key: var installedKey })
        {
            if (transactionId < installedTx)
            {
                return GroupEpochApply.Stale;
            }
            if (transactionId == installedTx)
            {
                return CryptographicOperations.FixedTimeEquals(installedKey, rawEpoch32)
                    ? GroupEpochApply.Duplicate
                    : GroupEpochApply.Conflicting;
            }
        }
        if (_pendingEpochs.TryGetValue(transactionId, out byte[]? pendingKey))
        {
            return CryptographicOperations.FixedTimeEquals(pendingKey, rawEpoch32)
                ? GroupEpochApply.Duplicate
                : GroupEpochApply.Conflicting;
        }

        if (RosterTransaction is not uint rosterTx || transactionId > rosterTx)
        {
            _pendingEpochs[transactionId] = rawEpoch32.ToArray();
            if (_pendingEpochs.Count > MaxBufferedEpochs)
            {
                uint farthest = _pendingEpochs.Keys.Last();
                _pendingEpochs.Remove(farthest);
            }
            return GroupEpochApply.Buffered;
        }

        InstallEpoch(transactionId, rawEpoch32.ToArray());
        return GroupEpochApply.Installed;
    }

    public IReadOnlyList<uint> GetActivePids() =>
        _receivers.Values
            .Where(r => r.Pid.HasValue)
            .Select(r => r.Pid!.Value)
            .Distinct()
            .OrderBy(static p => p)
            .ToArray();

    public byte[]? ProtectAudioPacket(AudioFrame frame)
    {
        if (_txRtpKeys is null) return null;

        byte[] payload = frame.EncodedPayload;
        if (_enableSframe && _txSframePerPeer.Count > 0)
        {
            // Group SFrame encrypts using own participant key (identical cipher output per sender)
            SframeSession sender = _txSframePerPeer.Values.First();
            payload = sender.Encrypt(payload);
        }

        uint? ext = frame.IsDtx ? RtpRtcpCodec.WhatsAppDtxExtensionWord : null;
        RtpHeader hdr = _txAudioStream.NextAudioPacket(extensionWord: ext);
        byte[] header = RtpRtcpCodec.EncodeRtpHeader(hdr);
        return SrtpCipher.ProtectE2eRtpPacket(_txRtpKeys, _txAudioRoc, hdr.Ssrc, hdr.SequenceNumber, header, payload, _warpMiTagLen);
    }

    public byte[]? ProtectVideoPacket(VideoFrame frame, bool screenShare = false)
    {
        if (_txRtpKeys is null) return null;

        RtpStream stream = screenShare ? _txScreenShareStream : _txVideoStream;
        SendRocTracker roc = screenShare ? _txScreenShareRoc : _txVideoRoc;

        byte[] payload = frame.AnnexBOrAccessUnit;
        if (_enableSframe && _txSframePerPeer.Count > 0)
        {
            payload = _txSframePerPeer.Values.First().Encrypt(payload);
        }

        RtpHeader hdr = stream.NextVideoPacket(frame.IsKeyframe, frame.Timestamp90k, marker: true);
        byte[] header = RtpRtcpCodec.EncodeRtpHeader(hdr);
        return SrtpCipher.ProtectE2eRtpPacket(_txRtpKeys, roc, hdr.Ssrc, hdr.SequenceNumber, header, payload, _warpMiTagLen);
    }

    public ParticipantAudioOutput? UnprotectAudio(ReadOnlySpan<byte> packet)
    {
        if (RtpRtcpCodec.ParseRtpHeader(packet) is not { } header) return null;
        if (!_audioRoutes.TryGetValue(header.Ssrc, out string? pidStr) || !_receivers.TryGetValue(pidStr, out ParticipantReceiver? rec))
        {
            return null;
        }
        if (rec.RtpKeys is null) return null;
        int hdrLen = RtpRtcpCodec.GetRtpHeaderByteLength(packet) ?? 0;
        if (hdrLen == 0) return null;

        byte[]? plain = SrtpCipher.UnprotectE2eRtpPacket(rec.RtpKeys, rec.AudioRoc, packet, hdrLen, header.Ssrc, header.SequenceNumber, _warpMiTagLen);
        if (plain is null) return null;

        if (_enableSframe && rec.Sframe is not null)
        {
            SframeDecryptResult sf = rec.Sframe.Decrypt(plain);
            if (sf.Status == SframeDecryptStatus.AuthFailed) return null;
            plain = sf.Data;
        }

        rec.AudioJitter.Push(header.SequenceNumber, plain);
        if (!rec.AudioJitter.TryPopNext(out _, out byte[] popped)) return null;

        return new ParticipantAudioOutput(rec.ParticipantId, rec.UserJid, rec.DeviceJid, rec.Pid, header, new AudioFrame(popped));
    }

    public ParticipantVideoOutput? UnprotectVideo(ReadOnlySpan<byte> packet)
    {
        if (RtpRtcpCodec.ParseRtpHeader(packet) is not { } header) return null;
        if (!_videoRoutes.TryGetValue(header.Ssrc, out string? pidStr) || !_receivers.TryGetValue(pidStr, out ParticipantReceiver? rec))
        {
            return null;
        }
        if (rec.RtpKeys is null || !rec.VideoEnabled) return null;
        int hdrLen = RtpRtcpCodec.GetRtpHeaderByteLength(packet) ?? 0;
        if (hdrLen == 0) return null;

        RecvRocTracker roc = header.Ssrc == rec.ScreenShareVideoSsrc ? rec.ScreenShareRoc : rec.VideoRoc;
        byte[]? plain = SrtpCipher.UnprotectE2eRtpPacket(rec.RtpKeys, roc, packet, hdrLen, header.Ssrc, header.SequenceNumber, _warpMiTagLen);
        if (plain is null) return null;

        if (_enableSframe && rec.Sframe is not null)
        {
            SframeDecryptResult sf = rec.Sframe.Decrypt(plain);
            if (sf.Status == SframeDecryptStatus.AuthFailed) return null;
            plain = sf.Data;
        }

        return new ParticipantVideoOutput(
            rec.ParticipantId,
            rec.UserJid,
            rec.DeviceJid,
            rec.Pid,
            header,
            new VideoFrame(plain, header.Timestamp, header.Marker));
    }

    private void InstallBestPendingEpoch()
    {
        if (RosterTransaction is not uint rosterTx || _pendingEpochs.Count == 0)
        {
            return;
        }

        uint? bestTx = null;
        foreach (uint tx in _pendingEpochs.Keys)
        {
            if (tx <= rosterTx)
            {
                bestTx = tx;
            }
        }
        if (bestTx is not uint targetTx)
        {
            return;
        }

        byte[] key = _pendingEpochs[targetTx];
        foreach (uint obsolete in _pendingEpochs.Keys.Where(k => k <= targetTx).ToList())
        {
            _pendingEpochs.Remove(obsolete);
        }
        InstallEpoch(targetTx, key);
    }

    private void InstallEpoch(uint transactionId, byte[] epochKey32)
    {
        _installedEpoch = (transactionId, epochKey32);
        _txRtpKeys = SrtpCipher.DeriveE2eRtpKeys(epochKey32, _selfParticipantId);
        _txRtcpKeys = SrtpCipher.DeriveE2eRtcpKeys(epochKey32, _selfParticipantId);
        _txSframePerPeer.Clear();

        foreach (ParticipantReceiver rec in _receivers.Values)
        {
            rec.InstallKeys(epochKey32, _selfParticipantId, _enableSframe);
            if (_enableSframe && SframeSession.TryCreate(epochKey32, _selfParticipantId, rec.ParticipantId) is { } sf)
            {
                _txSframePerPeer[rec.ParticipantId] = sf;
            }
        }
        if (_enableSframe && _txSframePerPeer.Count == 0 && SframeSession.TryCreate(epochKey32, _selfParticipantId, _selfParticipantId) is { } selfSf)
        {
            _txSframePerPeer[_selfParticipantId] = selfSf;
        }
    }

    private void RebuildRoutes()
    {
        _audioRoutes.Clear();
        _videoRoutes.Clear();
        foreach (ParticipantReceiver rec in _receivers.Values)
        {
            _audioRoutes[rec.AudioSsrc] = rec.ParticipantId;
            _videoRoutes[rec.PrimaryVideoSsrc] = rec.ParticipantId;
            _videoRoutes[rec.ScreenShareVideoSsrc] = rec.ParticipantId;
        }
    }
}
