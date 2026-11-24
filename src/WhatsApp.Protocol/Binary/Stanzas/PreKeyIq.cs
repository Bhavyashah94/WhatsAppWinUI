using System.Globalization;

namespace WhatsApp.Protocol.Binary.Stanzas;

public readonly record struct PreKeyPairBytes(uint Id, byte[] PublicKey);

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
}
