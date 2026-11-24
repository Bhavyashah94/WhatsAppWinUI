using System.Security.Cryptography;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Signal;

public sealed record GeneratedPreKey(uint Id, KeyPair KeyPair, byte[] RecordBytes);

public sealed record GeneratedSignedPreKey(
    uint Id,
    KeyPair KeyPair,
    byte[] Signature,
    ulong TimestampSeconds,
    byte[] RecordBytes);

/// <summary>
/// Manages Signal one-time pre-keys, signed pre-key generation/rotation, record codecs,
/// and SHA-1 <c>digestKey</c> validation matching <c>WAWebDigestKeyJob</c> and <c>wacore/src/prekeys.rs</c>.
/// </summary>
public static class PreKeyManager
{
    public const int DefaultUploadCountTarget = 812;
    public const int DefaultLowPreKeyThreshold = 5;

    public static bool NeedsReplenishment(int serverPreKeyCount, int minThreshold = DefaultLowPreKeyThreshold) =>
        serverPreKeyCount < minThreshold;

    public static IReadOnlyList<GeneratedPreKey> GeneratePreKeys(uint startId, int count = 50)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var batch = new List<GeneratedPreKey>(count);
        for (int i = 0; i < count; i++)
        {
            uint id = checked(startId + (uint)i);
            KeyPair kp = Curve25519.GenerateKeyPair();
            byte[] recordBytes = EncodePreKeyRecord(id, kp);
            batch.Add(new GeneratedPreKey(id, kp, recordBytes));
        }
        return batch;
    }

    public static GeneratedSignedPreKey CreateSignedPreKey(
        uint id,
        KeyPair identityKey,
        ulong? timestampSeconds = null)
    {
        KeyPair kp = Curve25519.GenerateKeyPair();
        byte[] signature = XEdDSA.Sign(identityKey.PrivateKey, PreKeySignalMessage.TagKey(kp.PublicKey));
        ulong ts = timestampSeconds ?? (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] recordBytes = EncodeSignedPreKeyRecord(id, kp, signature, ts);
        return new GeneratedSignedPreKey(id, kp, signature, ts, recordBytes);
    }

    public static IReadOnlyList<PreKeyPairBytes> ToUploadItems(IEnumerable<GeneratedPreKey> preKeys)
    {
        ArgumentNullException.ThrowIfNull(preKeys);
        return preKeys.Select(pk => new PreKeyPairBytes(pk.Id, pk.KeyPair.PublicKey)).ToArray();
    }

    public static byte[] EncodePreKeyRecord(uint id, KeyPair keyPair)
    {
        var proto = new PreKeyRecordStructure
        {
            Id = id,
            PublicKey = ByteString.CopyFrom(keyPair.PublicKey),
            PrivateKey = ByteString.CopyFrom(keyPair.PrivateKey)
        };
        return proto.ToByteArray();
    }

    public static (uint Id, KeyPair KeyPair) DecodePreKeyRecord(ReadOnlySpan<byte> recordBytes)
    {
        var proto = PreKeyRecordStructure.Parser.ParseFrom(recordBytes);
        if (!proto.HasPublicKey || !proto.HasPrivateKey)
        {
            throw new InvalidDataException("PreKeyRecordStructure is missing public or private key.");
        }

        byte[] pub = PreKeySignalMessage.StripKeyPrefix(proto.PublicKey.ToByteArray());
        byte[] priv = proto.PrivateKey.ToByteArray();
        return (proto.Id, new KeyPair(priv, pub));
    }

    public static byte[]? TryExtractPreKeyPublicKey(ReadOnlySpan<byte> recordBytes)
    {
        try
        {
            var proto = PreKeyRecordStructure.Parser.ParseFrom(recordBytes);
            if (!proto.HasPublicKey) return null;
            return PreKeySignalMessage.StripKeyPrefix(proto.PublicKey.ToByteArray());
        }
        catch
        {
            return null;
        }
    }

    public static byte[] EncodeSignedPreKeyRecord(
        uint id,
        KeyPair keyPair,
        ReadOnlySpan<byte> signature,
        ulong timestampSeconds)
    {
        if (signature.Length != 64)
        {
            throw new ArgumentException("Signed pre-key signature must be 64 bytes.", nameof(signature));
        }

        var proto = new SignedPreKeyRecordStructure
        {
            Id = id,
            PublicKey = ByteString.CopyFrom(keyPair.PublicKey),
            PrivateKey = ByteString.CopyFrom(keyPair.PrivateKey),
            Signature = ByteString.CopyFrom(signature),
            Timestamp = timestampSeconds
        };
        return proto.ToByteArray();
    }

    public static (uint Id, KeyPair KeyPair, byte[] Signature, ulong Timestamp) DecodeSignedPreKeyRecord(
        ReadOnlySpan<byte> recordBytes)
    {
        var proto = SignedPreKeyRecordStructure.Parser.ParseFrom(recordBytes);
        if (!proto.HasPublicKey || !proto.HasPrivateKey || !proto.HasSignature)
        {
            throw new InvalidDataException("SignedPreKeyRecordStructure is missing required fields.");
        }

        byte[] pub = PreKeySignalMessage.StripKeyPrefix(proto.PublicKey.ToByteArray());
        byte[] priv = proto.PrivateKey.ToByteArray();
        return (proto.Id, new KeyPair(priv, pub), proto.Signature.ToByteArray(), proto.Timestamp);
    }

    /// <summary>
    /// Computes the 20-byte SHA-1 bundle digest matching WA Web <c>validateLocalKeyBundle</c> and
    /// <c>wacore::prekeys::compute_key_bundle_digest</c>:
    /// <c>SHA1(identityPublicKey || signedPreKeyPublic || signedPreKeySignature || preKey1 || ...)</c>.
    /// </summary>
    public static byte[] ComputeKeyBundleDigest(
        ReadOnlySpan<byte> identityPublicKey,
        ReadOnlySpan<byte> signedPreKeyPublic,
        ReadOnlySpan<byte> signedPreKeySignature,
        IEnumerable<byte[]> preKeyPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(preKeyPublicKeys);

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(identityPublicKey);
        sha1.AppendData(signedPreKeyPublic);
        sha1.AppendData(signedPreKeySignature);
        foreach (byte[] preKeyPub in preKeyPublicKeys)
        {
            sha1.AppendData(preKeyPub);
        }
        return sha1.GetHashAndReset();
    }
}
