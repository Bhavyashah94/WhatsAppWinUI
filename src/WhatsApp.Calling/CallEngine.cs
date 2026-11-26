using System.Security.Cryptography;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Calling;

public enum CallEnginePhase
{
    Idle,
    AllocatingRelay,
    RelayReady,
    MediaActive,
    Ended,
}

public abstract record CallEngineOutput;
public sealed record SendUdpOutput(byte[] Datagram, string TargetHost, ushort TargetPort) : CallEngineOutput;
public sealed record DecodedAudioOutput(AudioFrame Frame) : CallEngineOutput;
public sealed record DecodedVideoOutput(VideoFrame Frame) : CallEngineOutput;
public sealed record StateChangedOutput(CallEnginePhase Phase, string? Reason = null) : CallEngineOutput;
public sealed record RequestKeyframeOutput(uint PeerVideoSsrc) : CallEngineOutput;

public sealed record CallEngineConfig(
    string CallId,
    string SelfParticipantId,
    string PeerParticipantId,
    byte[] CallKey32,
    RelayEndpoint RelayEndpoint,
    byte[] RelayToken,
    byte[] IntegrityKey,
    byte[]? HbhKey30 = null,
    bool ForceRelayOnly = false,
    bool EnableSframe = false,
    bool VideoEnabled = false,
    int WarpMiTagLen = SrtpCipher.WarpMiTagLength,
    HbhSrtcpMode HbhMode = HbhSrtcpMode.Directional);

/// <summary>
/// Pure Sans-I/O VoIP relay &amp; media state machine matching <c>wacore/src/voip/engine.rs</c>.
/// Owns no socket, timer, or thread; callers feed clock ticks, UDP packets, and local media frames
/// and drain <see cref="PollOutputs"/>.
/// </summary>
public sealed class CallEngine
{
    public const long KeepaliveIntervalMs = 1_000;
    public const long RtcpIntervalMs = 1_500;
    public const long AllocateTimeoutMs = 10_000;
    public const long MinKeyframeIntervalMs = 1_000;

    private readonly CallEngineConfig _config;
    private readonly Queue<CallEngineOutput> _outputs = new();
    private readonly Func<byte[]> _txIdGenerator;

    private readonly SrtpKeys _e2eSendRtp;
    private readonly SrtpKeys _e2eRecvRtp;
    private readonly SrtpKeys _e2eSendRtcp;
    private readonly SrtpKeys _e2eRecvRtcp;
    private readonly SrtpKeys? _hbhDownlinkRtcp;
    private readonly SframeSession? _sframe;

    private readonly SendRocTracker _audioSendRoc = new();
    private readonly SendRocTracker _videoSendRoc = new();
    private readonly RecvRocTracker _audioRecvRoc = new();
    private readonly RecvRocTracker _videoRecvRoc = new();
    private readonly JitterBuffer _audioJitter = new();

    private readonly RtpStream _audioTxStream;
    private readonly RtpStream _videoTxStream;
    private readonly string _relayHost;
    private readonly ushort _relayPort;
    private readonly byte[] _relayXorEndpoint;

    private CallEnginePhase _phase = CallEnginePhase.Idle;
    private long _allocateDeadlineMs = long.MaxValue;
    private long _nextKeepaliveMs = long.MaxValue;
    private long _nextRtcpMs = long.MaxValue;
    private long _lastPliMs = -MinKeyframeIntervalMs;

    private uint _packetsSent;
    private uint _octetsSent;
    private uint _srtcpTxIndex = 1;
    private byte[]? _lastPingTxId;

    public CallEnginePhase Phase => _phase;
    public bool VideoEnabled { get; private set; }
    public bool Muted { get; private set; }
    public uint LocalAudioSsrc => _audioTxStream.Ssrc;
    public uint LocalVideoSsrc => _videoTxStream.Ssrc;
    public uint PeerAudioSsrc { get; }
    public uint PeerVideoSsrc { get; }
    public int JitterDepth => _audioJitter.Count;

    public CallEngine(CallEngineConfig config, Func<byte[]>? txIdGenerator = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.CallKey32.Length != CallCrypto.CallKeySize)
        {
            throw new ArgumentException($"CallKey32 must be {CallCrypto.CallKeySize} bytes.", nameof(config));
        }

        _txIdGenerator = txIdGenerator ?? (() => RandomNumberGenerator.GetBytes(12));

        string selfId = CallCrypto.FormatParticipantId(config.SelfParticipantId);
        string peerId = CallCrypto.FormatParticipantId(config.PeerParticipantId);

        _e2eSendRtp = SrtpCipher.DeriveE2eRtpKeys(config.CallKey32, selfId);
        _e2eRecvRtp = SrtpCipher.DeriveE2eRtpKeys(config.CallKey32, peerId);
        _e2eSendRtcp = SrtpCipher.DeriveE2eRtcpKeys(config.CallKey32, selfId);
        _e2eRecvRtcp = SrtpCipher.DeriveE2eRtcpKeys(config.CallKey32, peerId);

        if (config.HbhKey30 is { Length: SrtpCipher.HbhKeyLength } hbh)
        {
            _hbhDownlinkRtcp = SrtpCipher.DeriveHbhSrtcpKeysDownlink(hbh, config.HbhMode);
        }

        if (config.EnableSframe)
        {
            _sframe = SframeSession.TryCreate(config.CallKey32, selfId, peerId);
        }

        uint localAudioSsrc = CallCrypto.DeriveParticipantSsrc(config.CallId, selfId, CallCrypto.AudioSsrcSlotWord);
        uint localVideoSsrc = CallCrypto.DeriveVideoParticipantSsrc(config.CallId, selfId);
        PeerAudioSsrc = CallCrypto.DeriveParticipantSsrc(config.CallId, peerId, CallCrypto.AudioSsrcSlotWord);
        PeerVideoSsrc = CallCrypto.DeriveVideoParticipantSsrc(config.CallId, peerId);

        _audioTxStream = new RtpStream(localAudioSsrc);
        _videoTxStream = new RtpStream(localVideoSsrc, 6000, RtpRtcpCodec.PayloadTypeH264);
        VideoEnabled = config.VideoEnabled;

        RelayAddress primary = config.RelayEndpoint.Addresses.FirstOrDefault(a => !string.IsNullOrEmpty(a.Ipv4))
            ?? config.RelayEndpoint.Addresses.FirstOrDefault()
            ?? throw new ArgumentException("RelayEndpoint has no addresses.", nameof(config));
        _relayHost = primary.Ipv4 ?? primary.Ipv6 ?? throw new ArgumentException("RelayAddress has no host.", nameof(config));
        _relayPort = primary.Port;
        _relayXorEndpoint = StunRelayCodec.EncodeXorRelayEndpoint(_relayHost, _relayPort)
            ?? new byte[6];
    }

    public void Start(long nowMs)
    {
        if (_phase != CallEnginePhase.Idle)
        {
            return;
        }

        SetPhase(CallEnginePhase.AllocatingRelay);
        byte[] txId = NextTxId();
        byte[] alloc = StunRelayCodec.BuildWasmAllocateRequest(
            txId,
            _config.RelayToken,
            _relayXorEndpoint,
            _config.IntegrityKey,
            _config.CallId,
            CallCrypto.FormatParticipantId(_config.SelfParticipantId));

        _outputs.Enqueue(new SendUdpOutput(alloc, _relayHost, _relayPort));
        _allocateDeadlineMs = nowMs + AllocateTimeoutMs;
    }

    public void SetVideoEnabled(bool enabled) => VideoEnabled = enabled;
    public void SetMuted(bool muted) => Muted = muted;

    public void Close(string reason = "Call ended")
    {
        if (_phase == CallEnginePhase.Ended)
        {
            return;
        }
        SetPhase(CallEnginePhase.Ended, reason);
    }

    public long? NextTimeoutDeadlineMs()
    {
        if (_phase == CallEnginePhase.Ended)
        {
            return null;
        }
        long min = Math.Min(_allocateDeadlineMs, Math.Min(_nextKeepaliveMs, _nextRtcpMs));
        return min == long.MaxValue ? null : min;
    }

    public void HandleTimer(long nowMs)
    {
        if (_phase == CallEnginePhase.Ended)
        {
            return;
        }

        if (_phase == CallEnginePhase.AllocatingRelay && nowMs >= _allocateDeadlineMs)
        {
            SetPhase(CallEnginePhase.Ended, "Relay allocate timeout");
            return;
        }

        if (_phase is CallEnginePhase.RelayReady or CallEnginePhase.MediaActive)
        {
            if (nowMs >= _nextKeepaliveMs)
            {
                byte[] txId = NextTxId();
                _lastPingTxId = txId;
                byte[] ping = StunRelayCodec.BuildWhatsAppPing(txId);
                _outputs.Enqueue(new SendUdpOutput(ping, _relayHost, _relayPort));
                _nextKeepaliveMs = nowMs + KeepaliveIntervalMs;
            }

            if (nowMs >= _nextRtcpMs)
            {
                byte[] sr = RtpRtcpCodec.BuildSenderReport(
                    _audioTxStream.Ssrc,
                    new RtcpSenderStats(_packetsSent, _octetsSent, _audioTxStream.CurrentTimestamp),
                    (ulong)Math.Max(0, nowMs));
                byte[] srtcp = SrtpCipher.ProtectSrtcp(_e2eSendRtcp, _audioTxStream.Ssrc, _srtcpTxIndex++, sr);
                _outputs.Enqueue(new SendUdpOutput(srtcp, _relayHost, _relayPort));
                _nextRtcpMs = nowMs + RtcpIntervalMs;
            }
        }
    }

    public void SendAudioFrame(AudioFrame frame, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Muted || _phase is not (CallEnginePhase.RelayReady or CallEnginePhase.MediaActive))
        {
            return;
        }

        if (_phase == CallEnginePhase.RelayReady)
        {
            SetPhase(CallEnginePhase.MediaActive);
        }

        byte[] innerPayload = _sframe is not null
            ? _sframe.Encrypt(frame.EncodedPayload)
            : frame.EncodedPayload;

        uint? ext = frame.IsDtx ? RtpRtcpCodec.WhatsAppDtxExtensionWord : null;
        RtpHeader hdr = _audioTxStream.NextAudioPacket(extensionWord: ext);
        byte[] hdrBytes = RtpRtcpCodec.EncodeRtpHeader(hdr);
        byte[] protectedPkt = SrtpCipher.ProtectE2eRtpPacket(
            _e2eSendRtp,
            _audioSendRoc,
            hdr.Ssrc,
            hdr.SequenceNumber,
            hdrBytes,
            innerPayload,
            _config.WarpMiTagLen);

        _packetsSent++;
        _octetsSent += (uint)innerPayload.Length;
        _outputs.Enqueue(new SendUdpOutput(protectedPkt, _relayHost, _relayPort));
    }

    public void SendVideoFrame(VideoFrame frame, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!VideoEnabled || _phase is not (CallEnginePhase.RelayReady or CallEnginePhase.MediaActive))
        {
            return;
        }

        if (_phase == CallEnginePhase.RelayReady)
        {
            SetPhase(CallEnginePhase.MediaActive);
        }

        bool isIdr = frame.IsKeyframe || H264Packetizer.ContainsIdr(frame.AnnexBOrAccessUnit);
        IReadOnlyList<byte[]> nalus = H264Packetizer.PacketizeNalu(frame.AnnexBOrAccessUnit);
        for (int i = 0; i < nalus.Count; i++)
        {
            byte[] payload = _sframe is not null ? _sframe.Encrypt(nalus[i]) : nalus[i];
            bool lastPkt = i == nalus.Count - 1;
            RtpHeader hdr = _videoTxStream.NextVideoPacket(isIdr && i == 0, frame.Timestamp90k, marker: lastPkt);
            byte[] hdrBytes = RtpRtcpCodec.EncodeRtpHeader(hdr);
            byte[] protectedPkt = SrtpCipher.ProtectE2eRtpPacket(
                _e2eSendRtp,
                _videoSendRoc,
                hdr.Ssrc,
                hdr.SequenceNumber,
                hdrBytes,
                payload,
                _config.WarpMiTagLen);

            _packetsSent++;
            _octetsSent += (uint)payload.Length;
            _outputs.Enqueue(new SendUdpOutput(protectedPkt, _relayHost, _relayPort));
        }
    }

    public void HandleUdpPacket(ReadOnlySpan<byte> datagram, long nowMs)
    {
        if (_phase == CallEnginePhase.Ended || datagram.Length < 4)
        {
            return;
        }

        if (StunRelayCodec.IsStunPacket(datagram))
        {
            if (_phase == CallEnginePhase.AllocatingRelay && StunRelayCodec.IsAllocateOrBindingSuccess(datagram))
            {
                _allocateDeadlineMs = long.MaxValue;
                _nextKeepaliveMs = nowMs + KeepaliveIntervalMs;
                _nextRtcpMs = nowMs + RtcpIntervalMs;
                SetPhase(CallEnginePhase.RelayReady);
            }
            else if (StunRelayCodec.IsAllocateError(datagram))
            {
                SetPhase(CallEnginePhase.Ended, "Relay allocate error");
            }
            else if (_lastPingTxId is not null && StunRelayCodec.IsWhatsAppPong(datagram, _lastPingTxId))
            {
                _lastPingTxId = null;
            }
            return;
        }

        if (RtpRtcpCodec.IsRtcpPacket(datagram))
        {
            if (datagram.Length >= 8)
            {
                uint ssrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(datagram[4..8]);
                var decrypted = SrtpCipher.UnprotectSrtcp(_e2eRecvRtcp, ssrc, datagram)
                    ?? (_hbhDownlinkRtcp is not null ? SrtpCipher.UnprotectSrtcp(_hbhDownlinkRtcp, ssrc, datagram) : null);
                if (decrypted is { PlainRtcp: var plain })
                {
                    RtcpSummary? summary = RtpRtcpCodec.ParseRtcp(plain);
                    if (summary is not null && summary.Feedback.Any(f => f.PacketType == RtpRtcpCodec.RtcpPtPsFb && f.Fmt == 1))
                    {
                        _outputs.Enqueue(new RequestKeyframeOutput(PeerVideoSsrc));
                    }
                }
            }
            return;
        }

        if (RtpRtcpCodec.IsRtpPacket(datagram)
            && RtpRtcpCodec.GetRtpHeaderByteLength(datagram) is int hdrLen
            && RtpRtcpCodec.ParseRtpHeader(datagram) is { } hdr)
        {
            bool isVideo = hdr.PayloadType == RtpRtcpCodec.PayloadTypeH264;
            RecvRocTracker roc = isVideo ? _videoRecvRoc : _audioRecvRoc;

            byte[]? payload = SrtpCipher.UnprotectE2eRtpPacket(
                _e2eRecvRtp,
                roc,
                datagram,
                hdrLen,
                hdr.Ssrc,
                hdr.SequenceNumber,
                _config.WarpMiTagLen);
            if (payload is null)
            {
                if (isVideo && nowMs - _lastPliMs >= MinKeyframeIntervalMs)
                {
                    _lastPliMs = nowMs;
                    _outputs.Enqueue(new RequestKeyframeOutput(hdr.Ssrc));
                }
                return;
            }

            if (_sframe is not null)
            {
                SframeDecryptResult sf = _sframe.Decrypt(payload);
                if (sf.Status == SframeDecryptStatus.AuthFailed)
                {
                    return;
                }
                payload = sf.Data;
            }

            if (_phase == CallEnginePhase.RelayReady)
            {
                SetPhase(CallEnginePhase.MediaActive);
            }

            if (isVideo)
            {
                bool isIdr = H264Packetizer.ContainsIdr(payload);
                _outputs.Enqueue(new DecodedVideoOutput(new VideoFrame(payload, hdr.Timestamp, isIdr)));
            }
            else
            {
                _audioJitter.Push(hdr.SequenceNumber, payload);
                if (_audioJitter.TryPopNext(out _, out byte[] orderedPayload))
                {
                    _outputs.Enqueue(new DecodedAudioOutput(new AudioFrame(orderedPayload)));
                }
            }
        }
    }

    public IReadOnlyList<CallEngineOutput> PollOutputs()
    {
        if (_outputs.Count == 0)
        {
            return Array.Empty<CallEngineOutput>();
        }
        var items = new CallEngineOutput[_outputs.Count];
        int i = 0;
        while (_outputs.Count > 0)
        {
            items[i++] = _outputs.Dequeue();
        }
        return items;
    }

    private byte[] NextTxId()
    {
        byte[] id = _txIdGenerator();
        if (id.Length != 12)
        {
            throw new InvalidOperationException("TxIdGenerator must return exactly 12 bytes.");
        }
        return id;
    }

    private void SetPhase(CallEnginePhase phase, string? reason = null)
    {
        _phase = phase;
        _outputs.Enqueue(new StateChangedOutput(phase, reason));
    }
}
