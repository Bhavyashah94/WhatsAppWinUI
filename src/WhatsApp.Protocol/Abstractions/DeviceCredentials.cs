using System.Security.Cryptography;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Abstractions;

/// <summary>
/// Persistent cryptographic identity and session routing credentials for a linked WhatsApp device.
/// Lives in the protocol core so transport and storage adapters never depend on each other.
/// </summary>
public sealed class DeviceCredentials
{
    public KeyPair NoiseKey { get; set; }
    public KeyPair IdentityKey { get; set; }
    public KeyPair SignedPreKey { get; set; }
    public byte[] SignedPreKeySignature { get; set; } = [];
    public uint SignedPreKeyId { get; set; } = 1;
    public uint NextPreKeyId { get; set; } = 1;
    public uint RegistrationId { get; set; }
    public byte[] AdvSecretKey { get; set; } = [];

    public Jid? Pn { get; set; }
    public Jid? Lid { get; set; }
    public bool LidMigrated { get; set; }
    public string PushName { get; set; } = "WhatsApp Desktop";
    public byte[]? ServerStaticPublic { get; set; }
    public byte[]? EdgeRoutingInfo { get; set; }

    public bool IsRegistered => Pn != null;

    public static DeviceCredentials CreateNew(string pushName = "WhatsApp Desktop")
    {
        var noiseKey = Curve25519.GenerateKeyPair();
        var identityKey = Curve25519.GenerateKeyPair();
        var signedPreKey = Curve25519.GenerateKeyPair();

        byte[] preKeyToSign = new byte[33];
        preKeyToSign[0] = 5; // Curve25519 key type prefix
        Buffer.BlockCopy(signedPreKey.PublicKey, 0, preKeyToSign, 1, 32);

        byte[] signature = XEdDSA.Sign(identityKey.PrivateKey, preKeyToSign);

        return new DeviceCredentials
        {
            NoiseKey = noiseKey,
            IdentityKey = identityKey,
            SignedPreKey = signedPreKey,
            SignedPreKeySignature = signature,
            SignedPreKeyId = 1,
            RegistrationId = (uint)RandomNumberGenerator.GetInt32(1, 16380),
            AdvSecretKey = RandomNumberGenerator.GetBytes(32),
            PushName = pushName
        };
    }
}
