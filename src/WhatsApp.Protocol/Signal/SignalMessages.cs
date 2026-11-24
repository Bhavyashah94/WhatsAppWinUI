using Google.Protobuf;

namespace WhatsApp.Protocol.Signal;

/// <summary>
/// WhatsApp PreKeySignalMessage wire format.
/// Prefix: (version << 4) | 3  (typically 0x33 for v3)
/// Protobuf fields:
/// 1: uint32 pre_key_id (optional)
/// 2: bytes base_key (32 or 33 bytes with 0x05 prefix)
/// 3: bytes identity_key (32 or 33 bytes with 0x05 prefix)
/// 4: bytes message (inner SignalMessage bytes)
/// 5: uint32 registration_id
/// 6: uint32 signed_pre_key_id
/// </summary>
public sealed class PreKeySignalMessage
{
    public byte Version { get; set; } = 3;
    public uint? PreKeyId { get; set; }
    public byte[] BaseKey { get; set; } = Array.Empty<byte>();
    public byte[] IdentityKey { get; set; } = Array.Empty<byte>();
    public byte[] MessageBytes { get; set; } = Array.Empty<byte>();
    public uint RegistrationId { get; set; }
    public uint SignedPreKeyId { get; set; }

    public static PreKeySignalMessage Parse(byte[] data)
    {
        if (data.Length < 1)
            throw new ArgumentException("PreKeySignalMessage is empty", nameof(data));

        byte versionByte = data[0];
        byte version = (byte)(versionByte >> 4);
        if (version != 3 && version != 4)
            throw new NotSupportedException($"Unsupported Signal protocol version: {version}");

        var msg = new PreKeySignalMessage { Version = version };
        var cis = new CodedInputStream(data, 1, data.Length - 1);
        uint tag;
        while ((tag = cis.ReadTag()) != 0)
        {
            int fieldNumber = WireFormat.GetTagFieldNumber(tag);
            switch (fieldNumber)
            {
                case 1:
                    msg.PreKeyId = cis.ReadUInt32();
                    break;
                case 2:
                    msg.BaseKey = StripKeyPrefix(cis.ReadBytes().ToByteArray());
                    break;
                case 3:
                    msg.IdentityKey = StripKeyPrefix(cis.ReadBytes().ToByteArray());
                    break;
                case 4:
                    msg.MessageBytes = cis.ReadBytes().ToByteArray();
                    break;
                case 5:
                    msg.RegistrationId = cis.ReadUInt32();
                    break;
                case 6:
                    msg.SignedPreKeyId = cis.ReadUInt32();
                    break;
                default:
                    cis.SkipLastField();
                    break;
            }
        }
        return msg;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)((Version << 4) | 3));
        var cos = new CodedOutputStream(ms);

        if (PreKeyId.HasValue)
        {
            cos.WriteTag(1, WireFormat.WireType.Varint);
            cos.WriteUInt32(PreKeyId.Value);
        }

        cos.WriteTag(2, WireFormat.WireType.LengthDelimited);
        cos.WriteBytes(ByteString.CopyFrom(TagKey(BaseKey)));

        cos.WriteTag(3, WireFormat.WireType.LengthDelimited);
        cos.WriteBytes(ByteString.CopyFrom(TagKey(IdentityKey)));

        cos.WriteTag(4, WireFormat.WireType.LengthDelimited);
        cos.WriteBytes(ByteString.CopyFrom(MessageBytes));

        cos.WriteTag(5, WireFormat.WireType.Varint);
        cos.WriteUInt32(RegistrationId);

        cos.WriteTag(6, WireFormat.WireType.Varint);
        cos.WriteUInt32(SignedPreKeyId);

        cos.Flush();
        return ms.ToArray();
    }

    public static byte[] StripKeyPrefix(byte[] key)
    {
        if (key.Length == 33 && key[0] == 5)
        {
            var clean = new byte[32];
            Buffer.BlockCopy(key, 1, clean, 0, 32);
            return clean;
        }
        return key;
    }

    public static byte[] TagKey(byte[] key)
    {
        if (key.Length == 32)
        {
            var tagged = new byte[33];
            tagged[0] = 5;
            Buffer.BlockCopy(key, 0, tagged, 1, 32);
            return tagged;
        }
        return key;
    }
}

/// <summary>
/// WhatsApp SignalMessage wire format.
/// Prefix: (version << 4) | 3  (typically 0x33 for v3)
/// Protobuf fields:
/// 1: bytes ratchet_key (32 or 33 bytes with 0x05 prefix)
/// 2: uint32 counter
/// 3: uint32 previous_counter
/// 4: bytes ciphertext (AES-256-CBC)
/// Trailing 8 bytes: HMAC-SHA256 MAC authentication tag
/// </summary>
public sealed class SignalMessage
{
    public byte Version { get; set; } = 3;
    public byte[] RatchetKey { get; set; } = Array.Empty<byte>();
    public uint Counter { get; set; }
    public uint PreviousCounter { get; set; }
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    public byte[] Mac { get; set; } = Array.Empty<byte>();
    public byte[] RawBytesWithoutMac { get; set; } = Array.Empty<byte>();

    public static SignalMessage Parse(byte[] data)
    {
        if (data.Length < 9)
            throw new ArgumentException("SignalMessage too short (minimum 9 bytes for header + MAC)", nameof(data));

        byte versionByte = data[0];
        byte version = (byte)(versionByte >> 4);
        if (version != 3 && version != 4)
            throw new NotSupportedException($"Unsupported Signal protocol version: {version}");

        int payloadLen = data.Length - 8;
        var rawWithoutMac = new byte[payloadLen];
        Buffer.BlockCopy(data, 0, rawWithoutMac, 0, payloadLen);

        var mac = new byte[8];
        Buffer.BlockCopy(data, payloadLen, mac, 0, 8);

        var msg = new SignalMessage
        {
            Version = version,
            Mac = mac,
            RawBytesWithoutMac = rawWithoutMac
        };

        var cis = new CodedInputStream(data, 1, payloadLen - 1);
        uint tag;
        while ((tag = cis.ReadTag()) != 0)
        {
            int fieldNumber = WireFormat.GetTagFieldNumber(tag);
            switch (fieldNumber)
            {
                case 1:
                    msg.RatchetKey = PreKeySignalMessage.StripKeyPrefix(cis.ReadBytes().ToByteArray());
                    break;
                case 2:
                    msg.Counter = cis.ReadUInt32();
                    break;
                case 3:
                    msg.PreviousCounter = cis.ReadUInt32();
                    break;
                case 4:
                    msg.Ciphertext = cis.ReadBytes().ToByteArray();
                    break;
                default:
                    cis.SkipLastField();
                    break;
            }
        }
        return msg;
    }

    public byte[] SerializeWithoutMac()
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)((Version << 4) | 3));
        var cos = new CodedOutputStream(ms);

        cos.WriteTag(1, WireFormat.WireType.LengthDelimited);
        cos.WriteBytes(ByteString.CopyFrom(PreKeySignalMessage.TagKey(RatchetKey)));

        cos.WriteTag(2, WireFormat.WireType.Varint);
        cos.WriteUInt32(Counter);

        cos.WriteTag(3, WireFormat.WireType.Varint);
        cos.WriteUInt32(PreviousCounter);

        cos.WriteTag(4, WireFormat.WireType.LengthDelimited);
        cos.WriteBytes(ByteString.CopyFrom(Ciphertext));

        cos.Flush();
        return ms.ToArray();
    }

    public byte[] Serialize(byte[] mac)
    {
        byte[] body = SerializeWithoutMac();
        byte[] output = new byte[body.Length + 8];
        Buffer.BlockCopy(body, 0, output, 0, body.Length);
        Buffer.BlockCopy(mac, 0, output, body.Length, 8);
        return output;
    }
}
