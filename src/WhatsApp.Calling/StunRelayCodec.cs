using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WhatsApp.Calling;

public sealed record StunAttribute(ushort Type, byte[] Value);

/// <summary>
/// STUN / WhatsApp relay framing (RFC 5389 TLV encoder/parser with HMAC-SHA1 <c>MESSAGE-INTEGRITY</c>,
/// <c>FINGERPRINT</c>, WASM 0x4024 stream descriptors, 0x0016 XOR endpoint, and 0x0801/0x0802 WA ping/pong)
/// matching <c>wacore/src/voip/stun.rs</c>.
/// </summary>
public static class StunRelayCodec
{
    public const uint StunMagic = 0x2112_A442;
    public const uint StunFingerprintXor = 0x5354_554E;
    public const ushort StunXorPort = 0x2112;
    private static readonly byte[] StunXorAddr = [0x21, 0x12, 0xA4, 0x42];

    public const ushort MsgBindingRequest = 0x0001;
    public const ushort MsgAllocateRequest = 0x0003;
    public const ushort MsgBindingSuccess = 0x0101;
    public const ushort MsgBindingError = 0x0111;
    public const ushort MsgAllocateSuccess = 0x0103;
    public const ushort MsgAllocateError = 0x0113;
    public const ushort MsgWhatsAppPing = 0x0801;
    public const ushort MsgWhatsAppPong = 0x0802;

    public const ushort AttrMessageIntegrity = 0x0008;
    public const ushort AttrErrorCode = 0x0009;
    public const ushort AttrXorMappedAddress = 0x0020;
    public const ushort AttrWasmRelayEndpoint = 0x0016;
    public const ushort AttrRelayToken = 0x4000;
    public const ushort AttrReceiverSubscriptions = 0x4021;
    public const ushort AttrNativeSenderSub = 0x4023;
    public const ushort AttrStreamDescriptors = 0x4024;
    public const ushort AttrSenderSubscriptionsV2 = 0x4025;
    public const ushort AttrFingerprint = 0x8028;
    public const ushort AttrParticipantCount = 0x805A;

    private static readonly (uint StreamIndex, uint SubType, uint SlotWord)[] WasmStreamSlots =
    [
        (0, 0, 0),
        (0, 1, 1),
        (0, 2, 4),
        (1, 0, 2),
        (1, 1, 3),
        (1, 2, 5),
        (2, 0, 7),
        (2, 1, 8),
        (2, 2, 6),
    ];

    public static byte[] EncodeStunAttr(ushort attrType, ReadOnlySpan<byte> value)
    {
        int pad = (4 - (value.Length % 4)) % 4;
        byte[] buf = new byte[4 + value.Length + pad];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), attrType);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)value.Length);
        value.CopyTo(buf.AsSpan(4));
        return buf;
    }

    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFF_FFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB8_8320u : 0u);
            }
        }
        return ~crc;
    }

    public static byte[] EncodeStunRequest(
        ushort msgType,
        ReadOnlySpan<byte> transactionId12,
        ReadOnlySpan<byte> attrs,
        ReadOnlySpan<byte> integrityKey = default,
        bool includeFingerprint = false)
    {
        if (transactionId12.Length != 12)
        {
            throw new ArgumentException("STUN transaction ID must be 12 bytes.", nameof(transactionId12));
        }

        var body = new List<byte>(attrs.Length + 32);
        body.AddRange(attrs);

        if (!integrityKey.IsEmpty)
        {
            ushort msgLen = (ushort)(body.Count + 24);
            byte[] pseudo = new byte[20 + body.Count];
            WriteHeader(pseudo, msgType, msgLen, transactionId12);
            body.CopyTo(pseudo, 20);
            byte[] mi = HMACSHA1.HashData(integrityKey, pseudo);
            body.AddRange(EncodeStunAttr(AttrMessageIntegrity, mi));
        }

        if (includeFingerprint)
        {
            ushort msgLen = (ushort)(body.Count + 8);
            byte[] crcInput = new byte[20 + body.Count];
            WriteHeader(crcInput, msgType, msgLen, transactionId12);
            body.CopyTo(crcInput, 20);
            uint fp = ComputeCrc32(crcInput) ^ StunFingerprintXor;
            Span<byte> fpBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(fpBytes, fp);
            body.AddRange(EncodeStunAttr(AttrFingerprint, fpBytes));
        }

        byte[] output = new byte[20 + body.Count];
        WriteHeader(output, msgType, (ushort)body.Count, transactionId12);
        body.CopyTo(output, 20);
        return output;
    }

    public static byte[]? EncodeXorRelayEndpoint(string ipv4, ushort port)
    {
        string[] parts = ipv4.Split('.');
        if (parts.Length != 4) return null;

        byte[] buf = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), (ushort)(port ^ StunXorPort));
        for (int i = 0; i < 4; i++)
        {
            if (!byte.TryParse(parts[i], out byte oct)) return null;
            buf[2 + i] = (byte)(oct ^ StunXorAddr[i]);
        }
        return buf;
    }

    public static byte[] CreateNativeSenderSubscription(uint ssrc)
    {
        byte[] buf = new byte[5];
        buf[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1, 4), ssrc);
        return buf;
    }

    public static byte[] CreateWasmStreamDescriptors(string callId, string selfParticipantId)
    {
        uint[] ssrcs = CallCrypto.DeriveWasmRelayStreamSsrcs(callId, selfParticipantId);
        return CreateWasmStreamDescriptorsFromSsrcs(ssrcs);
    }

    public static byte[] CreateWasmStreamDescriptorsFromSsrcs(uint[] streamSsrcs, uint[]? hbhFecSsrcs = null)
    {
        var outBuf = new List<byte>();
        for (int i = 0; i < WasmStreamSlots.Length && i < streamSsrcs.Length; i++)
        {
            uint ssrc = streamSsrcs[i];
            if (ssrc == 0) continue;

            var (streamIndex, subType, _) = WasmStreamSlots[i];
            var desc = new List<byte>();
            if (streamIndex != 0)
            {
                WritePbTag(desc, 1, 0);
                WritePbVarint(desc, streamIndex);
            }
            if (subType != 0)
            {
                WritePbTag(desc, 2, 0);
                WritePbVarint(desc, subType);
            }
            WritePbTag(desc, 3, 0);
            WritePbVarint(desc, ssrc);
            WritePbLenDelim(outBuf, 1, desc);
        }
        if (hbhFecSsrcs is not null)
        {
            for (int index = 0; index < hbhFecSsrcs.Length; index++)
            {
                uint ssrc = hbhFecSsrcs[index];
                if (ssrc == 0) continue;
                var desc = new List<byte>();
                WritePbTag(desc, 1, 0);
                WritePbVarint(desc, (ulong)(index + 3));
                WritePbTag(desc, 2, 0);
                WritePbVarint(desc, 3);
                WritePbTag(desc, 3, 0);
                WritePbVarint(desc, ssrc);
                WritePbLenDelim(outBuf, 1, desc);
            }
        }
        return outBuf.ToArray();
    }

    public static byte[] CreateWasmGroupSenderSubscriptions(
        uint[] streamSsrcs9,
        uint appDataSsrc,
        IReadOnlyList<uint> participantPids)
    {
        if (streamSsrcs9.Length < 9)
        {
            throw new ArgumentException("Requires 9 WASM stream SSRCs.", nameof(streamSsrcs9));
        }
        uint[] pids = NormalizePids(participantPids);
        var outBuf = new List<byte>();
        outBuf.AddRange(CreateWasmSenderSubscription(streamSsrcs9.AsSpan(3, 3), pids, video: true));
        outBuf.AddRange(CreateWasmSenderSubscription(streamSsrcs9.AsSpan(6, 3), [], video: false));
        outBuf.AddRange(CreateWasmSenderSubscription(streamSsrcs9.AsSpan(0, 3), pids, video: false));
        outBuf.AddRange(CreateWasmSenderSubscription([appDataSsrc], pids, video: false));
        return outBuf.ToArray();
    }

    public static byte[] CreateWasmGroupReceiverSubscriptions(IReadOnlyList<uint> participantPids)
    {
        var outBuf = new List<byte>();
        foreach (uint pid in NormalizePids(participantPids))
        {
            var participant = new List<byte>();
            WritePbTag(participant, 1, 0);
            WritePbVarint(participant, pid);
            WritePbLenDelim(outBuf, 2, participant);
        }
        return outBuf.ToArray();
    }

    public static byte[] BuildWasmGroupAllocateRequest(
        ReadOnlySpan<byte> transactionId12,
        ReadOnlySpan<byte> relayToken,
        ReadOnlySpan<byte> endpointXor6,
        ReadOnlySpan<byte> integrityKey,
        uint[] streamSsrcs9,
        uint appDataSsrc,
        uint[]? hbhFecSsrcs,
        IReadOnlyList<uint> participantPids)
    {
        if (endpointXor6.Length != 6)
        {
            throw new ArgumentException("XOR endpoint must be 6 bytes.", nameof(endpointXor6));
        }

        uint[] pids = NormalizePids(participantPids);
        byte[] descriptors = CreateWasmStreamDescriptorsFromSsrcs(
            streamSsrcs9,
            pids.Length > 1 ? hbhFecSsrcs : null);

        byte[] endpointAttrVal = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(endpointAttrVal.AsSpan(0, 2), 1);
        endpointXor6.CopyTo(endpointAttrVal.AsSpan(2));

        var attrs = new List<byte>();
        attrs.AddRange(EncodeStunAttr(AttrRelayToken, relayToken));
        if (pids.Length > 0)
        {
            attrs.AddRange(EncodeStunAttr(
                AttrSenderSubscriptionsV2,
                CreateWasmGroupSenderSubscriptions(streamSsrcs9, appDataSsrc, pids)));
            attrs.AddRange(EncodeStunAttr(
                AttrReceiverSubscriptions,
                CreateWasmGroupReceiverSubscriptions(pids)));
        }
        attrs.AddRange(EncodeStunAttr(AttrStreamDescriptors, descriptors));
        if (pids.Length > 0)
        {
            var countBytes = new List<byte>();
            WritePbVarint(countBytes, (ulong)pids.Length);
            attrs.AddRange(EncodeStunAttr(AttrParticipantCount, countBytes.ToArray()));
        }
        attrs.AddRange(EncodeStunAttr(AttrWasmRelayEndpoint, endpointAttrVal));

        return EncodeStunRequest(MsgAllocateRequest, transactionId12, attrs.ToArray(), integrityKey, includeFingerprint: false);
    }

    private static byte[] CreateWasmSenderSubscription(ReadOnlySpan<uint> ssrcs, ReadOnlySpan<uint> pids, bool video)
    {
        var packedSsrcs = new List<byte>();
        foreach (uint ssrc in ssrcs)
        {
            if (ssrc != 0)
            {
                WritePbVarint(packedSsrcs, ssrc);
            }
        }
        var subscription = new List<byte>();
        WritePbLenDelim(subscription, 1, packedSsrcs);
        foreach (uint pid in pids)
        {
            var participant = new List<byte>();
            WritePbTag(participant, 1, 0);
            WritePbVarint(participant, pid);
            if (video)
            {
                WritePbTag(participant, 2, 0);
                WritePbVarint(participant, 1);
            }
            WritePbLenDelim(subscription, 2, participant);
        }
        var wrapper = new List<byte>();
        WritePbLenDelim(wrapper, 1, subscription);
        var outBuf = new List<byte>();
        WritePbLenDelim(outBuf, 1, wrapper);
        return outBuf.ToArray();
    }

    private static uint[] NormalizePids(IReadOnlyList<uint> participantPids)
    {
        if (participantPids.Count == 0) return Array.Empty<uint>();
        return participantPids.Distinct().OrderBy(static x => x).ToArray();
    }

    public static byte[] BuildWasmAllocateRequest(
        ReadOnlySpan<byte> transactionId12,
        ReadOnlySpan<byte> relayToken,
        ReadOnlySpan<byte> endpointXor6,
        ReadOnlySpan<byte> integrityKey,
        string callId,
        string selfParticipantId)
    {
        if (endpointXor6.Length != 6)
        {
            throw new ArgumentException("XOR endpoint must be 6 bytes.", nameof(endpointXor6));
        }

        byte[] endpointAttrVal = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(endpointAttrVal.AsSpan(0, 2), 1);
        endpointXor6.CopyTo(endpointAttrVal.AsSpan(2));

        var attrs = new List<byte>();
        attrs.AddRange(EncodeStunAttr(AttrRelayToken, relayToken));
        attrs.AddRange(EncodeStunAttr(AttrStreamDescriptors, CreateWasmStreamDescriptors(callId, selfParticipantId)));
        attrs.AddRange(EncodeStunAttr(AttrWasmRelayEndpoint, endpointAttrVal));

        return EncodeStunRequest(MsgAllocateRequest, transactionId12, attrs.ToArray(), integrityKey, includeFingerprint: false);
    }

    public static byte[] BuildWhatsAppPing(ReadOnlySpan<byte> transactionId12) =>
        EncodeStunRequest(MsgWhatsAppPing, transactionId12, ReadOnlySpan<byte>.Empty);

    public static bool IsStunPacket(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && (data[0] & 0xC0) == 0x00;

    public static ushort? GetMessageType(ReadOnlySpan<byte> data) =>
        data.Length >= 2 ? (ushort)(((data[0] & 0x3F) << 8) | data[1]) : null;

    public static bool IsCompleteStun(ReadOnlySpan<byte> data)
    {
        if (!IsStunPacket(data) || data.Length < 20 || BinaryPrimitives.ReadUInt32BigEndian(data[4..8]) != StunMagic)
        {
            return false;
        }
        ushort bodyLen = BinaryPrimitives.ReadUInt16BigEndian(data[2..4]);
        return (bodyLen % 4) == 0 && data.Length >= 20 + bodyLen;
    }

    public static bool IsAllocateOrBindingSuccess(ReadOnlySpan<byte> data) =>
        IsCompleteStun(data) && GetMessageType(data) is MsgAllocateSuccess or MsgBindingSuccess;

    public static bool IsAllocateError(ReadOnlySpan<byte> data) =>
        IsCompleteStun(data) && GetMessageType(data) == MsgAllocateError;

    public static bool IsWhatsAppPong(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expectedTxId = default)
    {
        if (!IsStunPacket(data) || GetMessageType(data) != MsgWhatsAppPong || data.Length < 20)
        {
            return false;
        }
        return expectedTxId.IsEmpty || data[8..20].SequenceEqual(expectedTxId);
    }

    public static IReadOnlyList<StunAttribute> ParseStunAttributes(ReadOnlySpan<byte> data)
    {
        if (!IsStunPacket(data) || data.Length < 20)
        {
            return Array.Empty<StunAttribute>();
        }

        var list = new List<StunAttribute>();
        int off = 20;
        while (off + 4 <= data.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(off, 2));
            ushort len = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(off + 2, 2));
            off += 4;
            if (off + len > data.Length)
            {
                break;
            }
            list.Add(new StunAttribute(type, data.Slice(off, len).ToArray()));
            off += len + ((4 - (len % 4)) % 4);
        }
        return list;
    }

    private static void WriteHeader(Span<byte> dest, ushort msgType, ushort bodyLen, ReadOnlySpan<byte> txId12)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dest[0..2], msgType);
        BinaryPrimitives.WriteUInt16BigEndian(dest[2..4], bodyLen);
        BinaryPrimitives.WriteUInt32BigEndian(dest[4..8], StunMagic);
        txId12.CopyTo(dest[8..20]);
    }

    private static void WritePbTag(List<byte> dest, int fieldNumber, int wireType) =>
        WritePbVarint(dest, (ulong)((fieldNumber << 3) | wireType));

    private static void WritePbVarint(List<byte> dest, ulong v)
    {
        while (v > 0x7F)
        {
            dest.Add((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }
        dest.Add((byte)(v & 0xFF));
    }

    private static void WritePbLenDelim(List<byte> dest, int fieldNumber, List<byte> bytes)
    {
        WritePbTag(dest, fieldNumber, 2);
        WritePbVarint(dest, (ulong)bytes.Count);
        dest.AddRange(bytes);
    }
}
