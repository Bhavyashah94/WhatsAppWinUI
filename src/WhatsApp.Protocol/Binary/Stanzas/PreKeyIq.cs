using System.Buffers.Binary;
using System.Globalization;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Protocol.Binary.Stanzas;

public readonly record struct PreKeyPairBytes(uint Id, byte[] PublicKey);

public sealed record RejectedPreKeyDevice(Jid DeviceJid, int Code);

public sealed record PreKeyFetchOutcome(
    IReadOnlyDictionary<Jid, PreKeyBundle> Bundles,
    IReadOnlyList<RejectedPreKeyDevice> RejectedDevices);

/// <summary>
/// Signal pre-key count, digest, fetch, and upload IQ builders (<c>xmlns="encrypt"</c>).
/// </summary>
public static class PreKeyIq
{
    public const string Xmlns = "encrypt";
    public const byte DjbKeyType = 5;

    public static Node BuildPreKeyCount(string iqId) =>
        StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn, new Node("count"));

    public static int ParsePreKeyCount(Node response)
    {
        var countNode = response.GetChild("count")
            ?? throw new InvalidDataException("Missing <count> child in pre-key count response.");

        string? raw = countNode.GetAttr("value");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? count : 0;
    }

    public static Node BuildDigestKeyBundle(string iqId) =>
        StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn, new Node("digest"));

    public static Node BuildFetchPreKeys(string iqId, IReadOnlyList<Jid> jids, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(jids);
        if (jids.Count == 0)
        {
            throw new ArgumentException("At least one JID is required for pre-key fetch.", nameof(jids));
        }

        var users = new Node[jids.Count];
        for (int i = 0; i < jids.Count; i++)
        {
            var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["jid"] = jids[i]
            };
            if (!string.IsNullOrWhiteSpace(reason))
            {
                attrs["reason"] = reason;
            }
            users[i] = new Node("user", attrs);
        }

        return StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn, new Node("key", users));
    }

    public static Node BuildUploadPreKeys(
        string iqId,
        uint registrationId,
        ReadOnlySpan<byte> identityPublicKey,
        uint signedPreKeyId,
        ReadOnlySpan<byte> signedPreKeyPublic,
        ReadOnlySpan<byte> signedPreKeySignature,
        IReadOnlyList<PreKeyPairBytes> oneTimePreKeys)
    {
        ArgumentNullException.ThrowIfNull(oneTimePreKeys);
        if (identityPublicKey.Length != 32)
            throw new ArgumentException("Identity public key must be 32 bytes.", nameof(identityPublicKey));
        if (signedPreKeyPublic.Length != 32)
            throw new ArgumentException("Signed pre-key public key must be 32 bytes.", nameof(signedPreKeyPublic));
        if (signedPreKeySignature.Length != 64)
            throw new ArgumentException("Signed pre-key signature must be 64 bytes.", nameof(signedPreKeySignature));

        var keyNodes = new Node[oneTimePreKeys.Count];
        for (int i = 0; i < oneTimePreKeys.Count; i++)
        {
            var pk = oneTimePreKeys[i];
            if (pk.PublicKey is null || pk.PublicKey.Length != 32)
            {
                throw new ArgumentException($"Pre-key {pk.Id} public key must be 32 bytes.", nameof(oneTimePreKeys));
            }

            keyNodes[i] = new Node("key", new Node[]
            {
                new("id", StanzaBuilder.EncodeKeyId24(pk.Id)),
                new("value", pk.PublicKey)
            });
        }

        var signedPreKeyNode = new Node("skey", new Node[]
        {
            new("id", StanzaBuilder.EncodeKeyId24(signedPreKeyId)),
            new("value", signedPreKeyPublic.ToArray()),
            new("signature", signedPreKeySignature.ToArray())
        });

        return StanzaBuilder.Iq(
            iqId,
            "set",
            Xmlns,
            StanzaBuilder.ServerPn,
            new Node("registration", StanzaBuilder.EncodeUInt32BigEndian(registrationId)),
            new Node("type", new[] { DjbKeyType }),
            new Node("identity", identityPublicKey.ToArray()),
            new Node("list", keyNodes),
            signedPreKeyNode);
    }

    public static Node BuildRotateSignedPreKey(
        string iqId,
        uint signedPreKeyId,
        ReadOnlySpan<byte> signedPreKeyPublic,
        ReadOnlySpan<byte> signedPreKeySignature)
    {
        if (signedPreKeyPublic.Length != 32)
            throw new ArgumentException("Signed pre-key public key must be 32 bytes.", nameof(signedPreKeyPublic));
        if (signedPreKeySignature.Length != 64)
            throw new ArgumentException("Signed pre-key signature must be 64 bytes.", nameof(signedPreKeySignature));

        var signedPreKeyNode = new Node("skey", new Node[]
        {
            new("id", StanzaBuilder.EncodeKeyId24(signedPreKeyId)),
            new("value", signedPreKeyPublic.ToArray()),
            new("signature", signedPreKeySignature.ToArray())
        });

        return StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerPn, new Node("rotate", new[] { signedPreKeyNode }));
    }

    public static PreKeyFetchOutcome ParsePreKeysResponse(
        Node responseIq,
        IReadOnlyDictionary<string, byte[]>? accountIdentities = null)
    {
        ArgumentNullException.ThrowIfNull(responseIq);
        Node listNode = responseIq.GetChild("list")
            ?? throw new InvalidDataException("Missing <list> child in pre-key fetch response.");

        var primaryIdentities = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (Node uNode in listNode.GetChildren("user"))
        {
            if (uNode.GetAttr("jid") is not { Length: > 0 } jidStr || !Jid.TryParse(jidStr, out Jid jid) || jid.Device != 0)
            {
                continue;
            }
            if (TryParseSingleBundle(jid, uNode, expectedAccountIdentity: null, out PreKeyBundle? primaryBundle)
                && primaryBundle is not null
                && XEdDSA.Verify(primaryBundle.IdentityKey, PreKeySignalMessage.TagKey(primaryBundle.SignedPreKeyPublic), primaryBundle.SignedPreKeySignature))
            {
                primaryIdentities[jid.ToNonAdString()] = primaryBundle.IdentityKey;
            }
        }

        var bundles = new Dictionary<Jid, PreKeyBundle>();
        var rejected = new List<RejectedPreKeyDevice>();

        foreach (Node userNode in listNode.GetChildren("user"))
        {
            if (userNode.GetAttr("jid") is not { Length: > 0 } jidStr || !Jid.TryParse(jidStr, out Jid devJid))
            {
                continue;
            }

            if (userNode.GetChild("error") is { } errNode)
            {
                if (int.TryParse(errNode.GetAttr("code"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                {
                    rejected.Add(new RejectedPreKeyDevice(devJid, code));
                }
                continue;
            }

            byte[]? expectedPrimary = null;
            if (devJid.Device != 0)
            {
                string bare = devJid.ToNonAdString();
                if (!(accountIdentities is not null && accountIdentities.TryGetValue(bare, out expectedPrimary)))
                {
                    primaryIdentities.TryGetValue(bare, out expectedPrimary);
                }
            }

            if (TryParseSingleBundle(devJid, userNode, expectedPrimary, out PreKeyBundle? bundle) && bundle is not null)
            {
                bundles[devJid] = bundle;
            }
        }

        return new PreKeyFetchOutcome(bundles, rejected);
    }

    public static bool TryParseSingleBundle(
        Jid deviceJid,
        Node bundleContainer,
        byte[]? expectedAccountIdentity,
        out PreKeyBundle? bundle)
    {
        bundle = null;
        if (bundleContainer.GetChild("registration")?.Content is not NodeContent.BytesContent regBc
            || !TryReadBigEndianUInt32(regBc.Bytes, out uint regId))
        {
            return false;
        }

        Node keysNode = bundleContainer.GetChild("keys") ?? bundleContainer;
        if (keysNode.GetChild("identity")?.Content is not NodeContent.BytesContent idBc)
        {
            return false;
        }

        byte[] identityPub32 = NormalizeCurvePublicKey32(idBc.Bytes);
        if (identityPub32.Length != 32)
        {
            return false;
        }

        Node? skeyNode = keysNode.GetChild("skey");
        if (skeyNode is null
            || skeyNode.GetChild("id")?.Content is not NodeContent.BytesContent skeyIdBc
            || !TryReadKeyId24OrString(skeyIdBc.Bytes, out uint spkId)
            || skeyNode.GetChild("value")?.Content is not NodeContent.BytesContent skeyValBc
            || skeyNode.GetChild("signature")?.Content is not NodeContent.BytesContent skeySigBc
            || skeySigBc.Bytes.Length != 64)
        {
            return false;
        }

        byte[] spkPub32 = NormalizeCurvePublicKey32(skeyValBc.Bytes);
        if (spkPub32.Length != 32)
        {
            return false;
        }

        uint? otpId = null;
        byte[]? otpPub32 = null;
        if (keysNode.GetChild("key") is { } otpNode
            && otpNode.GetChild("id")?.Content is NodeContent.BytesContent otpIdBc
            && TryReadKeyId24OrString(otpIdBc.Bytes, out uint parsedOtpId)
            && otpNode.GetChild("value")?.Content is NodeContent.BytesContent otpValBc)
        {
            byte[] normalizedOtp = NormalizeCurvePublicKey32(otpValBc.Bytes);
            if (normalizedOtp.Length == 32)
            {
                otpId = parsedOtpId;
                otpPub32 = normalizedOtp;
            }
        }

        // Companion devices (device != 0) MUST validate <device-identity> ADV certificate.
        if (deviceJid.Device != 0)
        {
            byte[]? devIdentityBytes = (bundleContainer.GetChild("device-identity")?.Content as NodeContent.BytesContent)?.Bytes
                ?? (keysNode.GetChild("device-identity")?.Content as NodeContent.BytesContent)?.Bytes;
            if (devIdentityBytes is not { Length: > 0 }
                || !PairingCrypto.ValidateSignedDeviceIdentity(
                    devIdentityBytes,
                    identityPub32,
                    expectedAccountIdentity ?? ReadOnlySpan<byte>.Empty,
                    deviceJid.IsHosted()))
            {
                return false;
            }
        }

        bundle = new PreKeyBundle(
            regId,
            deviceJid.Device,
            spkId,
            spkPub32,
            skeySigBc.Bytes,
            identityPub32,
            otpId,
            otpPub32);
        return true;
    }

    internal static byte[] NormalizeCurvePublicKey32(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 33 && bytes[0] == DjbKeyType ? bytes[1..].ToArray()
        : bytes.Length == 32 ? bytes.ToArray()
        : Array.Empty<byte>();

    internal static bool TryReadBigEndianUInt32(ReadOnlySpan<byte> bytes, out uint value)
    {
        if (bytes.Length == 4)
        {
            value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            return true;
        }
        if (bytes.Length is >= 1 and <= 3)
        {
            Span<byte> buf = stackalloc byte[4];
            bytes.CopyTo(buf[(4 - bytes.Length)..]);
            value = BinaryPrimitives.ReadUInt32BigEndian(buf);
            return true;
        }
        value = 0;
        return false;
    }

    internal static bool TryReadKeyId24OrString(ReadOnlySpan<byte> bytes, out uint value)
    {
        if (bytes.Length == 3)
        {
            value = ((uint)bytes[0] << 16) | ((uint)bytes[1] << 8) | bytes[2];
            return true;
        }
        if (bytes.Length == 4)
        {
            value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            return true;
        }
        value = 0;
        return false;
    }
}
