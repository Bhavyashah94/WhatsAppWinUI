using System.Security.Cryptography;
using System.Text;

namespace WhatsApp.Protocol.Crypto;

public sealed record SafetyNumberFingerprint(
    string DisplayableDigits,
    byte[] ScannableQrBytes)
{
    public bool Matches(ReadOnlySpan<byte> otherQrBytes) =>
        ScannableQrBytes.AsSpan().SequenceEqual(otherQrBytes);
}

/// <summary>
/// Signal Protocol safety number (numeric 60-digit + binary 52-byte QR fingerprint)
/// using 5,200 iterated SHA-512 rounds per party over version <c>0</c> and Curve25519 identity keys.
/// </summary>
public static class SafetyNumber
{
    public const int DefaultIterations = 5200;
    private const byte CurveDjbType = 0x05;

    public static SafetyNumberFingerprint Compute(
        string localStableId,
        ReadOnlySpan<byte> localIdentityPublic32,
        string remoteStableId,
        ReadOnlySpan<byte> remoteIdentityPublic32,
        int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(localStableId);
        ArgumentException.ThrowIfNullOrEmpty(remoteStableId);
        if (localIdentityPublic32.Length != 32)
        {
            throw new ArgumentException("Local identity public key must be 32 bytes.", nameof(localIdentityPublic32));
        }
        if (remoteIdentityPublic32.Length != 32)
        {
            throw new ArgumentException("Remote identity public key must be 32 bytes.", nameof(remoteIdentityPublic32));
        }
        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "Iterations must be positive.");
        }

        byte[] localDigest = ComputePartyDigest(localStableId, localIdentityPublic32, iterations);
        byte[] remoteDigest = ComputePartyDigest(remoteStableId, remoteIdentityPublic32, iterations);

        string localDigits = Encode30Digits(localDigest);
        string remoteDigits = Encode30Digits(remoteDigest);

        bool localFirst = string.CompareOrdinal(localDigits, remoteDigits) <= 0;
        string displayable = localFirst ? localDigits + remoteDigits : remoteDigits + localDigits;

        byte[] qr = new byte[52];
        qr[0] = 0x00;
        qr[1] = 0x00;
        Buffer.BlockCopy(localFirst ? localDigest : remoteDigest, 0, qr, 2, 25);
        Buffer.BlockCopy(localFirst ? remoteDigest : localDigest, 0, qr, 27, 25);

        return new SafetyNumberFingerprint(displayable, qr);
    }

    private static byte[] ComputePartyDigest(string stableId, ReadOnlySpan<byte> identityPub32, int iterations)
    {
        byte[] idBytes = Encoding.UTF8.GetBytes(stableId);
        byte[] prefixedKey = new byte[33];
        prefixedKey[0] = CurveDjbType;
        identityPub32.CopyTo(prefixedKey.AsSpan(1));

        byte[] digest = new byte[2 + prefixedKey.Length + idBytes.Length];
        digest[0] = 0x00;
        digest[1] = 0x00;
        Buffer.BlockCopy(prefixedKey, 0, digest, 2, prefixedKey.Length);
        Buffer.BlockCopy(idBytes, 0, digest, 2 + prefixedKey.Length, idBytes.Length);

        byte[] buffer = digest;
        Span<byte> combined = stackalloc byte[64 + prefixedKey.Length];
        for (int i = 0; i < iterations; i++)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            sha.AppendData(buffer);
            sha.AppendData(prefixedKey);
            buffer = sha.GetHashAndReset();
        }

        return buffer;
    }

    private static string Encode30Digits(ReadOnlySpan<byte> hash)
    {
        var sb = new StringBuilder(30);
        for (int offset = 0; offset <= 25; offset += 5)
        {
            ulong chunk = ((ulong)hash[offset] << 32)
                | ((ulong)hash[offset + 1] << 24)
                | ((ulong)hash[offset + 2] << 16)
                | ((ulong)hash[offset + 3] << 8)
                | hash[offset + 4];
            sb.Append((chunk % 100000UL).ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
