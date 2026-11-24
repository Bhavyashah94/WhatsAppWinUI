using Whatsapp;

namespace WhatsApp.Protocol.Noise;

public sealed record VerifiedServerCertChain(
    byte[] IntermediateKey,
    ulong IntermediateNotBefore,
    ulong IntermediateNotAfter,
    byte[] LeafKey,
    ulong LeafNotBefore,
    ulong LeafNotAfter);

/// <summary>
/// Verifies WhatsApp Noise server certificate chains against WhatsApp's pinned root key using XEdDSA.
/// Mirrors <c>wacore-noise</c> <c>NoiseHandshake::verify_server_cert</c>.
/// </summary>
public static class NoiseCertVerifier
{
    public const uint RootIssuerSerial = 0;

    /// <summary>
    /// WhatsApp pinned Curve25519 root public key for Noise server certificate verification.
    /// </summary>
    public static ReadOnlySpan<byte> RootPublicKey =>
    [
        0x14, 0x23, 0x75, 0x57, 0x4d, 0x0a, 0x58, 0x71, 0x66, 0xaa, 0xe7, 0x1e, 0xbe, 0x51, 0x64, 0x37,
        0xc4, 0xa2, 0x8b, 0x73, 0xe3, 0x69, 0x5c, 0x6c, 0xe1, 0xf7, 0xf9, 0x54, 0x5d, 0xa8, 0xee, 0x6b
    ];

    public static VerifiedServerCertChain VerifyServerCert(ReadOnlySpan<byte> certChainBytes, ReadOnlySpan<byte> serverStaticPublicKey) =>
        VerifyServerCertWithRoot(certChainBytes, serverStaticPublicKey, RootPublicKey);

    internal static VerifiedServerCertChain VerifyServerCertWithRoot(
        ReadOnlySpan<byte> certChainBytes,
        ReadOnlySpan<byte> serverStaticPublicKey,
        ReadOnlySpan<byte> rootPublicKey)
    {
        if (rootPublicKey.Length != 32)
        {
            throw new ArgumentException("Root public key must be 32 bytes.", nameof(rootPublicKey));
        }
        if (serverStaticPublicKey.Length != 32)
        {
            throw new ArgumentException("Server static key must be 32 bytes.", nameof(serverStaticPublicKey));
        }

        CertChain chain = CertChain.Parser.ParseFrom(certChainBytes);
        var intermediate = chain.Intermediate ?? throw new InvalidDataException("Missing intermediate Noise certificate.");
        var leaf = chain.Leaf ?? throw new InvalidDataException("Missing leaf Noise certificate.");

        if (!intermediate.HasDetails || !intermediate.HasSignature)
        {
            throw new InvalidDataException("Intermediate certificate is missing details or signature.");
        }

        byte[] intermediateDetailsBytes = intermediate.Details.ToByteArray();
        var intermediateDetails = CertChain.Types.NoiseCertificate.Types.Details.Parser.ParseFrom(intermediateDetailsBytes);
        if (intermediateDetails.IssuerSerial != RootIssuerSerial)
        {
            throw new InvalidDataException(
                $"Unexpected intermediate certificate issuer serial {intermediateDetails.IssuerSerial}, expected {RootIssuerSerial}.");
        }

        byte[] intermediateKey = intermediateDetails.Key?.ToByteArray() ?? [];
        if (intermediateKey.Length != 32)
        {
            throw new InvalidDataException($"Intermediate certificate key length {intermediateKey.Length} != 32.");
        }

        if (!XEdDSA.Verify(rootPublicKey.ToArray(), intermediateDetailsBytes, intermediate.Signature.ToByteArray()))
        {
            throw new InvalidDataException("Intermediate Noise certificate failed XEdDSA verification against root key.");
        }

        if (!leaf.HasDetails || !leaf.HasSignature)
        {
            throw new InvalidDataException("Leaf certificate is missing details or signature.");
        }

        byte[] leafDetailsBytes = leaf.Details.ToByteArray();
        var leafDetails = CertChain.Types.NoiseCertificate.Types.Details.Parser.ParseFrom(leafDetailsBytes);
        if (leafDetails.IssuerSerial != intermediateDetails.Serial)
        {
            throw new InvalidDataException(
                $"Leaf issuer serial {leafDetails.IssuerSerial} does not match intermediate serial {intermediateDetails.Serial}.");
        }

        byte[] leafKey = leafDetails.Key?.ToByteArray() ?? [];
        if (leafKey.Length != 32 || !leafKey.AsSpan().SequenceEqual(serverStaticPublicKey))
        {
            throw new InvalidDataException("Leaf certificate key does not match decrypted server static key.");
        }

        if (!XEdDSA.Verify(intermediateKey, leafDetailsBytes, leaf.Signature.ToByteArray()))
        {
            throw new InvalidDataException("Leaf Noise certificate failed XEdDSA verification against intermediate key.");
        }

        return new VerifiedServerCertChain(
            intermediateKey,
            intermediateDetails.NotBefore,
            intermediateDetails.NotAfter,
            leafKey,
            leafDetails.NotBefore,
            leafDetails.NotAfter);
    }
}
