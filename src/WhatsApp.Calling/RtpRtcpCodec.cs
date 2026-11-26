using System.Buffers.Binary;

namespace WhatsApp.Calling;

public readonly record struct VideoRtpExtension(
    byte MediaFrameInfo,
    ushort InitialBandwidth,
    short ShortOffset,
    ushort TransportSequence)
{
    public void EncodeInto(Span<byte> dest12)
    {
        dest12[0] = 0x30;
        dest12[1] = MediaFrameInfo;
        dest12[2] = 0x51;
        BinaryPrimitives.WriteUInt16BigEndian(dest12[3..5], InitialBandwidth);
        dest12[5] = 0x61;
        BinaryPrimitives.WriteInt16BigEndian(dest12[6..8], ShortOffset);
        dest12[8] = 0x91;
        BinaryPrimitives.WriteUInt16BigEndian(dest12[9..11], TransportSequence);
        dest12[11] = 0x00;
    }
}

public sealed record RtpHeader(
    bool Marker,
    byte PayloadType,
    ushort SequenceNumber,
    uint Timestamp,
    uint Ssrc,
    uint? ExtensionWord = null,
    VideoRtpExtension? VideoExtension = null)
{
    public int ByteSize => VideoExtension.HasValue
        ? RtpRtcpCodec.VideoRtpHeaderSize
        : (ExtensionWord.HasValue ? RtpRtcpCodec.RtpHeaderDtxSize : RtpRtcpCodec.RtpHeaderSpeechSize);
}

public sealed record RtcpSenderStats(
    uint PacketsSent,
    uint OctetsSent,
    uint RtpTimestamp);

public sealed record RtcpFeedback(
    byte PacketType,
    byte Fmt,
    uint SenderSsrc,
    uint MediaSsrc,
    byte[] Fci);

public sealed record RtcpSummary(
    IReadOnlyList<byte> PacketTypes,
    uint SenderSsrc,
    IReadOnlyList<uint> ReferencedSsrcs,
    IReadOnlyList<RtcpFeedback> Feedback);

/// <summary>
/// WhatsApp RTP / WARP (<c>0xdebe</c> extension) &amp; RTCP (SR/SDES/PLI/compact 208/209) codecs
/// matching <c>wacore/src/voip/{rtp,rtcp}.rs</c>.
/// </summary>
public static class RtpRtcpCodec
{
    public const byte PayloadTypeOpus = 111;
    public const byte PayloadTypeWhatsAppAudio = 120;
    public const byte PayloadTypeMlow = PayloadTypeWhatsAppAudio;
    public const byte PayloadTypeMlowRed = 121;
    public const byte PayloadTypeH264 = 97;
    public const byte PayloadTypeAppData = 119;
    public const uint VideoClockRate = 90_000;
    public const ushort WhatsAppExtensionProfile = 0xDEBE;
    public const uint WhatsAppDtxExtensionWord = 0x3001_0000;
    public const uint WhatsAppPiggybackExtensionWord = 0x3001_0000;
    public const int RtpFixedHeaderLen = 12;
    public const int RtpHeaderSpeechSize = 16;
    public const int RtpHeaderDtxSize = 20;
    public const int VideoRtpHeaderSize = 28;
    public const byte VideoMediaFrameInfoIdr = 0x08;
    public const byte VideoMediaFrameInfoDelta = 0x00;

    public const byte RtcpPtSr = 200;
    public const byte RtcpPtRr = 201;
    public const byte RtcpPtSdes = 202;
    public const byte RtcpPtRtpFb = 205;
    public const byte RtcpPtPsFb = 206;
    public const byte RtcpPtCompact208 = 208;
    public const byte RtcpPtCompact209 = 209;
    public const int RtcpHeaderLen = 8;
    public const int WhatsAppRtcpCnameLen = 18;
    private const ulong NtpUnixOffsetSecs = 2_208_988_800UL;

    public static bool IsRtpPacket(ReadOnlySpan<byte> data) =>
        data.Length >= RtpFixedHeaderLen && ((data[0] >> 6) & 0x03) == 2 && (data[1] & 0x7F) is < 64 or > 95;

    public static bool IsRtcpPacket(ReadOnlySpan<byte> data) =>
        data.Length >= RtcpHeaderLen && ((data[0] >> 6) & 0x03) == 2 && data[1] is >= 192 and <= 223;

    public static int? GetRtpHeaderByteLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < RtpFixedHeaderLen || ((data[0] >> 6) & 0x03) != 2)
        {
            return null;
        }
        int cc = data[0] & 0x0F;
        int len = RtpFixedHeaderLen + cc * 4;
        if (data.Length < len) return null;

        if (((data[0] >> 4) & 1) == 1)
        {
            if (data.Length < len + 4) return null;
            int extWords = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(len + 2, 2));
            len += 4 + extWords * 4;
            if (data.Length < len) return null;
        }
        return len;
    }

    public static byte[] EncodeRtpHeader(RtpHeader h)
    {
        byte[] b = new byte[h.ByteSize];
        b[0] = 0x90; // V=2, P=0, X=1, CC=0
        b[1] = (byte)((h.Marker ? 0x80 : 0x00) | (h.PayloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2, 2), h.SequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4, 4), h.Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8, 4), h.Ssrc);

        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12, 2), WhatsAppExtensionProfile);
        if (h.VideoExtension is { } vx)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(14, 2), 3);
            vx.EncodeInto(b.AsSpan(16, 12));
        }
        else if (h.ExtensionWord is uint word)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(14, 2), 1);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16, 4), word);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(14, 2), 0);
        }
        return b;
    }

    public static RtpHeader? ParseRtpHeader(ReadOnlySpan<byte> data)
    {
        if (GetRtpHeaderByteLength(data) is not int headerLen || data.Length < headerLen)
        {
            return null;
        }
        bool marker = (data[1] & 0x80) != 0;
        byte pt = (byte)(data[1] & 0x7F);
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(data[2..4]);
        uint ts = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(data[8..12]);
        return new RtpHeader(marker, pt, seq, ts, ssrc);
    }

    public static byte[] BuildSenderReport(uint localSsrc, RtcpSenderStats stats, ulong nowMs)
    {
        byte[] buf = new byte[28];
        buf[0] = 0x80; // V=2, RC=0
        buf[1] = RtcpPtSr;
        buf[2] = 0x00;
        buf[3] = 0x06;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), localSsrc);

        uint ntpSec = unchecked((uint)((nowMs / 1000UL) + NtpUnixOffsetSecs));
        uint ntpFrac = unchecked((uint)(((nowMs % 1000UL) / 1000.0) * 4_294_967_296.0));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), ntpSec);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), ntpFrac);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16, 4), stats.RtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20, 4), stats.PacketsSent);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24, 4), stats.OctetsSent);
        return buf;
    }

    public static byte[] BuildCompact208(uint localSsrc, uint value)
    {
        byte[] buf = new byte[12];
        buf[0] = 0x81;
        buf[1] = RtcpPtCompact208;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), 2);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), localSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), value);
        return buf;
    }

    public static byte[] BuildCompact209(uint localSsrc)
    {
        byte[] buf = new byte[8];
        buf[0] = 0x81;
        buf[1] = RtcpPtCompact209;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), localSsrc);
        return buf;
    }

    public static byte[] BuildPictureLossIndication(uint senderSsrc, uint mediaSsrc)
    {
        byte[] buf = new byte[12];
        buf[0] = 0x81; // V=2, FMT=1 (PLI)
        buf[1] = RtcpPtPsFb;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), 2);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), mediaSsrc);
        return buf;
    }

    public static byte[] BuildSourceDescription(uint localSsrc, ReadOnlySpan<byte> cname18)
    {
        byte[] buf = new byte[32];
        buf[0] = 0x81;
        buf[1] = RtcpPtSdes;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), localSsrc);
        buf[8] = 1;
        buf[9] = WhatsAppRtcpCnameLen;
        cname18[..WhatsAppRtcpCnameLen].CopyTo(buf.AsSpan(10));
        return buf;
    }

    public static RtcpSummary? ParseRtcp(ReadOnlySpan<byte> data)
    {
        if (!IsRtcpPacket(data))
        {
            return null;
        }

        var packetTypes = new List<byte>();
        var refSsrcs = new HashSet<uint>();
        var feedback = new List<RtcpFeedback>();
        uint? senderSsrc = null;

        int offset = 0;
        while (offset + 4 <= data.Length)
        {
            if (((data[offset] >> 6) & 0x03) != 2) return null;
            int lengthWords = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
            int packetLen = (lengthWords + 1) * 4;
            if (offset + packetLen > data.Length || packetLen < RtcpHeaderLen) return null;

            ReadOnlySpan<byte> pkt = data.Slice(offset, packetLen);
            byte pt = pkt[1];
            byte rc = (byte)(pkt[0] & 0x1F);
            uint thisSender = BinaryPrimitives.ReadUInt32BigEndian(pkt[4..8]);
            senderSsrc ??= thisSender;
            packetTypes.Add(pt);

            if (pt is RtcpPtRtpFb or RtcpPtPsFb && pkt.Length >= 12)
            {
                byte fmt = (byte)(rc & 0x0F);
                uint mediaSsrc = BinaryPrimitives.ReadUInt32BigEndian(pkt[8..12]);
                if (mediaSsrc != 0) refSsrcs.Add(mediaSsrc);
                feedback.Add(new RtcpFeedback(pt, fmt, thisSender, mediaSsrc, pkt[12..].ToArray()));
            }
            else if (pt == RtcpPtCompact208 && pkt.Length >= 12)
            {
                refSsrcs.Add(BinaryPrimitives.ReadUInt32BigEndian(pkt[8..12]));
            }

            offset += packetLen;
        }

        return senderSsrc.HasValue
            ? new RtcpSummary(packetTypes, senderSsrc.Value, refSsrcs.OrderBy(x => x).ToArray(), feedback)
            : null;
    }
}

/// <summary>
/// Send-side RTP sequence/timestamp sequencer matching <c>wacore/src/voip/rtp.rs::RtpStream</c>.
/// </summary>
public sealed class RtpStream
{
    public uint Ssrc { get; }
    private ushort _seq = 1;
    private uint _timestamp;
    private uint? _lastSentTimestamp;
    private readonly uint _samplesPerPacket;
    private readonly byte _payloadType;

    public RtpStream(uint ssrc, uint samplesPerPacket = 960, byte payloadType = RtpRtcpCodec.PayloadTypeMlow)
    {
        Ssrc = ssrc;
        _samplesPerPacket = samplesPerPacket;
        _payloadType = payloadType;
    }

    public uint CurrentTimestamp => _lastSentTimestamp ?? _timestamp;

    public RtpHeader NextAudioPacket(bool marker = false, uint? extensionWord = null)
    {
        var header = new RtpHeader(marker, _payloadType, _seq, _timestamp, Ssrc, ExtensionWord: extensionWord);
        _seq++;
        _lastSentTimestamp = _timestamp;
        _timestamp += _samplesPerPacket;
        return header;
    }

    public RtpHeader NextVideoPacket(bool isKeyframe, uint timestamp90k, bool marker = true, ushort initialBandwidth = 0, short shortOffset = 0)
    {
        var ext = new VideoRtpExtension(
            isKeyframe ? RtpRtcpCodec.VideoMediaFrameInfoIdr : RtpRtcpCodec.VideoMediaFrameInfoDelta,
            initialBandwidth,
            shortOffset,
            _seq);
        var header = new RtpHeader(marker, RtpRtcpCodec.PayloadTypeH264, _seq, timestamp90k, Ssrc, VideoExtension: ext);
        _seq++;
        _lastSentTimestamp = timestamp90k;
        return header;
    }
}
