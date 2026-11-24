using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace WhatsApp.Protocol.Noise;

public readonly record struct KeyPair(byte[] PrivateKey, byte[] PublicKey);

public static class Curve25519
{
    private static readonly SecureRandom SecureRandom = new();

    public static KeyPair GenerateKeyPair()
    {
        var generator = new X25519KeyPairGenerator();
        generator.Init(new X25519KeyGenerationParameters(SecureRandom));

        var keyPair = generator.GenerateKeyPair();
        var priv = ((X25519PrivateKeyParameters)keyPair.Private).GetEncoded();
        var pub = ((X25519PublicKeyParameters)keyPair.Public).GetEncoded();

        return new KeyPair(priv, pub);
    }

    public static byte[] CalculateSharedSecret(byte[] privateKey, byte[] remotePublicKey)
    {
        if (privateKey.Length != 32)
        {
            throw new ArgumentException("Private key must be 32 bytes", nameof(privateKey));
        }
        if (remotePublicKey.Length != 32)
        {
            throw new ArgumentException("Remote public key must be 32 bytes", nameof(remotePublicKey));
        }

        var privParams = new X25519PrivateKeyParameters(privateKey, 0);
        var pubParams = new X25519PublicKeyParameters(remotePublicKey, 0);

        var agreement = new X25519Agreement();
        agreement.Init(privParams);

        byte[] secret = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(pubParams, secret, 0);
        return secret;
    }
}
