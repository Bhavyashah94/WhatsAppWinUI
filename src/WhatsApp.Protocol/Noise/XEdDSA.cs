using System.Numerics;
using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace WhatsApp.Protocol.Noise;

/// <summary>
/// Implements XEdDSA (XEd25519) signatures and Montgomery/Edwards key conversions
/// per the Signal specification (https://signal.org/docs/specifications/xeddsa/)
/// and reference/whatsapp-rust/wacore/libsignal/src/curve/xeddsa.rs.
/// </summary>
public static class XEdDSA
{
    // 2^255 - 19
    public static readonly BigInteger P = (BigInteger.One << 255) - 19;

    // Curve order L = 2^252 + 27742317777372353535851937790883648493
    public static readonly BigInteger L = BigInteger.Parse("7237005577332262213973186563042994240857116359379907606001950938285454250989");

    // d = -121665 / 121666 mod P
    public static readonly BigInteger D = BigInteger.Parse("37095705934669439343138083508754565189542113879843219016388785533085940283555");

    // Base point B = (Bx, By) on Edwards25519 (-x^2 + y^2 = 1 + d*x^2*y^2)
    public static readonly BigInteger Bx = BigInteger.Parse("15112221349535400772501151409588531511454012693041857206046113283949847762202");
    public static readonly BigInteger By = BigInteger.Parse("46316835694926478169428394003475163141307993866256225615783033603165251855960");

    private readonly record struct ProjectivePoint(BigInteger X, BigInteger Y, BigInteger Z)
    {
        public static readonly ProjectivePoint Identity = new(BigInteger.Zero, BigInteger.One, BigInteger.One);
        public static readonly ProjectivePoint BasePoint = new(Bx, By, BigInteger.One);
    }

    /// <summary>
    /// Computes modular inverse using Fermat's Little Theorem: a^(p-2) mod p.
    /// </summary>
    public static BigInteger ModInverse(BigInteger a, BigInteger m)
    {
        return BigInteger.ModPow(a, m - 2, m);
    }

    private static BigInteger Mod(BigInteger a, BigInteger m)
    {
        var r = a % m;
        return r < 0 ? r + m : r;
    }

    /// <summary>
    /// Complete addition law on Edwards25519 (-x^2 + y^2 = 1 + d*x^2*y^2).
    /// </summary>
    private static ProjectivePoint PointAdd(ProjectivePoint p1, ProjectivePoint p2)
    {
        if (p1.X == BigInteger.Zero && p1.Y == p1.Z) return p2;
        if (p2.X == BigInteger.Zero && p2.Y == p2.Z) return p1;

        BigInteger a = Mod(p1.Z * p2.Z, P);
        BigInteger b = Mod(a * a, P);
        BigInteger c = Mod(p1.X * p2.X, P);
        BigInteger d = Mod(p1.Y * p2.Y, P);
        BigInteger e = Mod(D * Mod(c * d, P), P);
        BigInteger f = Mod(b - e, P);
        BigInteger g = Mod(b + e, P);

        BigInteger x1PlusY1 = Mod(p1.X + p1.Y, P);
        BigInteger x2PlusY2 = Mod(p2.X + p2.Y, P);
        BigInteger term = Mod(Mod(x1PlusY1 * x2PlusY2, P) - c - d, P);

        BigInteger x3 = Mod(Mod(a * f, P) * term, P);
        BigInteger y3 = Mod(Mod(a * g, P) * Mod(d + c, P), P);
        BigInteger z3 = Mod(f * g, P);

        return new ProjectivePoint(x3, y3, z3);
    }

    /// <summary>
    /// Scalar multiplication using double-and-add algorithm.
    /// </summary>
    private static ProjectivePoint ScalarMult(BigInteger k, ProjectivePoint p)
    {
        var result = ProjectivePoint.Identity;
        var addend = p;

        while (k > BigInteger.Zero)
        {
            if ((k & BigInteger.One) == BigInteger.One)
            {
                result = PointAdd(result, addend);
            }
            addend = PointAdd(addend, addend);
            k >>= 1;
        }

        return result;
    }

    /// <summary>
    /// Compresses an Edwards point into 32-byte Ed25519 standard format.
    /// </summary>
    private static byte[] Compress(ProjectivePoint p)
    {
        BigInteger zInv = ModInverse(p.Z, P);
        BigInteger x = Mod(p.X * zInv, P);
        BigInteger y = Mod(p.Y * zInv, P);

        byte[] bytes = new byte[32];
        byte[] yBytes = y.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(yBytes, bytes, Math.Min(yBytes.Length, 32));

        if (!x.IsEven)
        {
            bytes[31] |= 0x80;
        }

        return bytes;
    }

    /// <summary>
    /// Signs a message with a Curve25519 (X25519) private key using XEd25519.
    /// </summary>
    public static byte[] Sign(byte[] privateKey, byte[] message, byte[]? randomness = null)
    {
        if (privateKey.Length != 32)
        {
            throw new ArgumentException("Private key must be 32 bytes", nameof(privateKey));
        }

        byte[] rand = randomness ?? RandomNumberGenerator.GetBytes(64);
        if (rand.Length != 64)
        {
            throw new ArgumentException("Randomness must be 64 bytes", nameof(randomness));
        }

        // 1. Clamp private key scalar
        byte[] clamped = (byte[])privateKey.Clone();
        clamped[0] &= 248;
        clamped[31] = (byte)((clamped[31] & 127) | 64);

        BigInteger scalar = Mod(new BigInteger(clamped, isUnsigned: true, isBigEndian: false), L);

        // 2. Base point multiplication A = scalar * B
        var point = ScalarMult(scalar, ProjectivePoint.BasePoint);
        byte[] publicBytes = Compress(point);

        int sign = (publicBytes[31] >> 7) & 1;
        BigInteger a = sign == 1 ? Mod(L - scalar, L) : scalar;
        publicBytes[31] &= 0x7F; // clear sign bit

        // 3. Domain separation and nonce generation
        byte[] domain = new byte[32];
        Array.Fill(domain, (byte)0xFF);
        domain[0] = 0xFE;

        byte[] aBytes = new byte[32];
        byte[] aRaw = a.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(aRaw, aBytes, Math.Min(aRaw.Length, 32));

        using var sha512 = SHA512.Create();
        sha512.TransformBlock(domain, 0, domain.Length, null, 0);
        sha512.TransformBlock(aBytes, 0, aBytes.Length, null, 0);
        sha512.TransformBlock(message, 0, message.Length, null, 0);
        sha512.TransformFinalBlock(rand, 0, rand.Length);
        byte[] h1 = sha512.Hash!;

        BigInteger nonce = Mod(new BigInteger(h1, isUnsigned: true, isBigEndian: false), L);

        // 4. Compute R = nonce * B
        var rPoint = ScalarMult(nonce, ProjectivePoint.BasePoint);
        byte[] rBytes = Compress(rPoint);

        // 5. Challenge h = SHA512(rBytes || publicBytes || message) mod L
        using var sha512Challenge = SHA512.Create();
        sha512Challenge.TransformBlock(rBytes, 0, rBytes.Length, null, 0);
        sha512Challenge.TransformBlock(publicBytes, 0, publicBytes.Length, null, 0);
        sha512Challenge.TransformFinalBlock(message, 0, message.Length);
        byte[] h2 = sha512Challenge.Hash!;

        BigInteger challenge = Mod(new BigInteger(h2, isUnsigned: true, isBigEndian: false), L);

        // 6. S = (nonce + challenge * a) mod L
        BigInteger s = Mod(nonce + challenge * a, L);
        byte[] sBytes = new byte[32];
        byte[] sRaw = s.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(sRaw, sBytes, Math.Min(sRaw.Length, 32));

        byte[] signature = new byte[64];
        Buffer.BlockCopy(rBytes, 0, signature, 0, 32);
        Buffer.BlockCopy(sBytes, 0, signature, 32, 32);
        return signature;
    }

    /// <summary>
    /// Checks if an Edwards point is of small order (order dividing 8).
    /// </summary>
    private static bool IsSmallOrder(ProjectivePoint p)
    {
        var p2 = PointAdd(p, p);
        var p4 = PointAdd(p2, p2);
        var p8 = PointAdd(p4, p4);
        return p8.X == BigInteger.Zero && Mod(p8.Y, P) == Mod(p8.Z, P);
    }

    /// <summary>
    /// Converts a Curve25519 (Montgomery) public key to an Ed25519 compressed public key point,
    /// given the sign bit from the signature.
    /// </summary>
    public static byte[]? MontgomeryToEdwardsCompressed(byte[] montgomeryPublicKey, int signBit)
    {
        if (montgomeryPublicKey.Length != 32)
        {
            return null;
        }

        BigInteger u = new BigInteger(montgomeryPublicKey, isUnsigned: true, isBigEndian: false);

        // Canonical Montgomery check: 0 < u < P - 1 (reject 0, 1, and twist u = P - 1)
        if (u <= BigInteger.Zero || u >= P || u == BigInteger.One || u == P - 1)
        {
            return null;
        }

        // y = (u - 1) / (u + 1) mod P
        BigInteger yn = Mod(u - 1, P);
        BigInteger yd = Mod(u + 1, P);
        BigInteger y = Mod(yn * ModInverse(yd, P), P);

        // Check if point is on curve: x^2 = (y^2 - 1) / (d*y^2 + 1) mod P
        BigInteger y2 = Mod(y * y, P);
        BigInteger num = Mod(y2 - 1, P);
        BigInteger den = Mod(D * y2 + 1, P);
        BigInteger x2 = Mod(num * ModInverse(den, P), P);

        if (x2 == BigInteger.Zero)
        {
            var p0 = new ProjectivePoint(BigInteger.Zero, y, BigInteger.One);
            if (IsSmallOrder(p0)) return null;
            return Compress(p0);
        }

        // Euler's criterion: x2^((P-1)/2) mod P == 1
        BigInteger legendre = BigInteger.ModPow(x2, (P - 1) / 2, P);
        if (legendre != BigInteger.One)
        {
            return null; // u is on the twist!
        }

        // Square root in F_p where P = 2^255 - 19 = 5 mod 8
        BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x, P) != x2)
        {
            BigInteger sqrtMinus1 = BigInteger.ModPow(2, (P - 1) / 4, P);
            x = Mod(x * sqrtMinus1, P);
        }

        int currentSign = x.IsEven ? 0 : 1;
        if (currentSign != (signBit & 1))
        {
            x = Mod(P - x, P);
        }

        var point = new ProjectivePoint(x, y, BigInteger.One);
        if (IsSmallOrder(point))
        {
            return null;
        }

        byte[] edBytes = new byte[32];
        byte[] yBytes = y.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(yBytes, edBytes, Math.Min(yBytes.Length, 32));

        if ((signBit & 1) != 0)
        {
            edBytes[31] |= 0x80;
        }
        else
        {
            edBytes[31] &= 0x7F;
        }

        return edBytes;
    }

    /// <summary>
    /// Verifies an XEd25519 signature over a message using a Curve25519 (Montgomery) public key.
    /// Supports bit 511 transported Edwards sign bit per Signal specification and whatsapp-rust.
    /// </summary>
    public static bool Verify(byte[] montgomeryPublicKey, byte[] message, byte[] signature)
    {
        if (montgomeryPublicKey.Length != 32 || signature.Length != 64)
        {
            return false;
        }

        // Check canonical Montgomery range
        BigInteger u = new BigInteger(montgomeryPublicKey, isUnsigned: true, isBigEndian: false);
        if (u <= BigInteger.Zero || u >= P)
        {
            return false;
        }

        int signBit = signature[63] >> 7;
        byte[]? edwardsPub = MontgomeryToEdwardsCompressed(montgomeryPublicKey, signBit);
        if (edwardsPub == null)
        {
            return false;
        }

        // Clean signature: RFC 8032 requires s < L with bit 255 (signature byte 63 high bit) = 0
        byte[] cleanSig = (byte[])signature.Clone();
        cleanSig[63] &= 0x7F; // clear transported Edwards sign bit

        // Verify s < 2^253 per XEdDSA spec
        if ((cleanSig[63] & 0xE0) != 0)
        {
            return false;
        }

        // XEdDSA allows s < 2^253 < 2L; reduce modulo L for RFC 8032 verification
        BigInteger s = new BigInteger(cleanSig.AsSpan(32, 32), isUnsigned: true, isBigEndian: false);
        if (s >= L)
        {
            s %= L;
            byte[] sBytes = s.ToByteArray(isUnsigned: true, isBigEndian: false);
            Array.Clear(cleanSig, 32, 32);
            Array.Copy(sBytes, 0, cleanSig, 32, Math.Min(sBytes.Length, 32));
        }

        try
        {
            return Ed25519.Verify(cleanSig, 0, edwardsPub, 0, message, 0, message.Length);
        }
        catch
        {
            return false;
        }
    }
}
