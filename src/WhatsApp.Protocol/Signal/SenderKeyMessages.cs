using System.Security.Cryptography;
using Google.Protobuf;

namespace WhatsApp.Protocol.Signal;

/// <summary>
/// Wire codec for Signal <c>SenderKeyDistributionMessage</c> (<c>0x33 || proto(id, iteration, chainKey, signingKey)</c>).
/// </summary>
public sealed class SenderKeyDistributionMessage
{
    public byte Version { get; set; } = 3;
    public uint Id { get; set; }
    public uint Iteration { get; set; }
    public byte[] ChainKey { get; set; } = [];
    public byte[] SigningKey { get; set; } = [];

    public static SenderKeyDistributionMessage Parse(byte[] data)
    {
        if (data is null || data.Length < 2)
        {
            throw new ArgumentException("SenderKeyDistributionMessage is too short.", nameof(data));
        }

        byte version = (byte)(data[0] >> 4);
        if (version != 3)
        {
            throw new NotSupportedException($"Unsupported SenderKeyDistributionMessage version: {version}");
        }

        var msg = new SenderKeyDistributionMessage { Version = version };
        var cis = new CodedInputStream(data, 1, data.Length - 1);
        uint tag;
        while ((tag = cis.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1:
                    msg.Id = cis.ReadUInt32();
                    break;
                case 2:
                    msg.Iteration = cis.ReadUInt32();
                    break;
                case 3:
                    msg.ChainKey = cis.ReadBytes().ToByteArray();
                    break;
                case 4:
                    msg.SigningKey = PreKeySignalMessage.StripKeyPrefix(cis.ReadBytes().ToByteArray());
                    break;
                default:
                    cis.SkipLastField();
                    break;
            }
        }

        if (msg.ChainKey.Length != 32 || msg.SigningKey.Length != 32)
        {
            throw new InvalidDataException("Invalid SenderKeyDistributionMessage key lengths.");
        }

        return msg;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)((Version << 4) | 3));
        var cos = new CodedOutputStream(ms);

        cos.WriteTag(1, WireFormat.WireType.Varint);
        cos.WriteUInt32(Id);

        cos.WriteTag(2, WireFormat.WireType.Varint);
        cos.WriteUInt32(Iteration);

        cos.WriteTag(3, WireFormat.WireType.LengthDelimited);
        cos.WriteBytes(ByteString.CopyFrom(ChainKey));

        cos.WriteTag(4, WireFormat.WireType.LengthDelimited);
        cos.WriteBytes(ByteString.CopyFrom(PreKeySignalMessage.TagKey(SigningKey)));

        cos.Flush();
        return ms.ToArray();
    }
}

/// <summary>
/// Wire codec for Signal group <c>SenderKeyMessage</c>
/// (<c>0x33 || proto(id, iteration, ciphertext) || signature[64]</c>).
/// </summary>
public sealed class SenderKeyMessage
{
    public const int SignatureLength = 64;

    public byte Version { get; set; } = 3;
    public uint Id { get; set; }
    public uint Iteration { get; set; }
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Signature { get; set; } = [];
    public byte[] RawBytesWithoutSignature { get; set; } = [];

    public static SenderKeyMessage Parse(byte[] data)
    {
        if (data is null || data.Length < 1 + SignatureLength)
        {
            throw new ArgumentException("SenderKeyMessage too short to contain header and 64-byte signature.", nameof(data));
        }

        byte version = (byte)(data[0] >> 4);
        if (version != 3)
        {
            throw new NotSupportedException($"Unsupported SenderKeyMessage version: {version}");
        }

        int payloadLen = data.Length - SignatureLength;
        byte[] rawBody = new byte[payloadLen];
        Buffer.BlockCopy(data, 0, rawBody, 0, payloadLen);

        byte[] signature = new byte[SignatureLength];
        Buffer.BlockCopy(data, payloadLen, signature, 0, SignatureLength);

        var msg = new SenderKeyMessage
        {
            Version = version,
            Signature = signature,
            RawBytesWithoutSignature = rawBody
        };

        var cis = new CodedInputStream(data, 1, payloadLen - 1);
        uint tag;
        while ((tag = cis.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1:
                    msg.Id = cis.ReadUInt32();
                    break;
                case 2:
                    msg.Iteration = cis.ReadUInt32();
                    break;
                case 3:
                    msg.Ciphertext = cis.ReadBytes().ToByteArray();
                    break;
                default:
                    cis.SkipLastField();
                    break;
            }
        }

        return msg;
    }

    public byte[] SerializeWithoutSignature()
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)((Version << 4) | 3));
        var cos = new CodedOutputStream(ms);

        cos.WriteTag(1, WireFormat.WireType.Varint);
        cos.WriteUInt32(Id);

        cos.WriteTag(2, WireFormat.WireType.Varint);
        cos.WriteUInt32(Iteration);

        cos.WriteTag(3, WireFormat.WireType.LengthDelimited);
        cos.WriteBytes(ByteString.CopyFrom(Ciphertext));

        cos.Flush();
        return ms.ToArray();
    }

    public byte[] Serialize(byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Length != SignatureLength)
        {
            throw new ArgumentException($"SenderKeyMessage signature must be {SignatureLength} bytes.", nameof(signature));
        }

        byte[] body = SerializeWithoutSignature();
        byte[] wire = new byte[body.Length + SignatureLength];
        body.CopyTo(wire, 0);
        signature.CopyTo(wire.AsSpan(body.Length));
        return wire;
    }
}

public sealed class SenderKeyState
{
    public const int MaxSkippedMessageKeys = 2000;

    public uint Id { get; set; }
    public uint Iteration { get; set; }
    public byte[] ChainKey { get; set; } = [];
    public byte[] SigningPublicKey { get; set; } = [];
    public byte[]? SigningPrivateKey { get; set; }
    public Dictionary<uint, byte[]> SkippedMessageSeeds { get; } = [];
    public Queue<uint> SkippedOrder { get; } = new();

    internal void StoreSkippedSeeds(IReadOnlyList<(uint Iteration, byte[] Seed)> seeds)
    {
        if (seeds.Count == 0) return;

        foreach (var (iter, seed) in seeds)
        {
            if (!SkippedMessageSeeds.ContainsKey(iter))
            {
                SkippedMessageSeeds[iter] = seed;
                SkippedOrder.Enqueue(iter);
            }
        }

        while (SkippedMessageSeeds.Count > MaxSkippedMessageKeys && SkippedOrder.TryDequeue(out uint oldest))
        {
            if (SkippedMessageSeeds.Remove(oldest, out byte[]? evicted))
            {
                CryptographicOperations.ZeroMemory(evicted);
            }
        }
    }
}

public sealed class SenderKeyRecord
{
    public const int MaxStates = 5;

    public List<SenderKeyState> States { get; } = [];

    internal SenderKeyState? FindState(uint id) => States.FirstOrDefault(s => s.Id == id);
}
