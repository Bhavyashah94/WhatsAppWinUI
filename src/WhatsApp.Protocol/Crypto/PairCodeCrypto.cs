using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Crypto;

public enum PairCodeRejectionCode
{
    BadRequest = 400,
    Forbidden = 403,
    RateOverlimit = 429,
    FeatureNotAvailable = 452,
    InternalServerError = 500
}

public sealed record ParsedPrimaryHello(
    byte[] WrappedPrimaryEphemeralPub,
    byte[] PrimaryIdentityPub,
    byte[] PairingRef);

public sealed record WrappedKeyBundleResult(
    byte[] WrappedBundle,
    byte[] AdvSecretKey);

/// <summary>
/// Pure cryptographic helpers and IQ/notification codecs for phone-number 8-character
/// companion linking (<c>link_code_companion_reg</c>).
/// Matches <c>wacore/src/pair_code.rs</c>, <c>wacore/src/companion_reg.rs</c>, and live WA Web <c>Alt/DeviceLinkingIq</c>.
/// </summary>
public static class PairCodeCrypto
{
    public const int SaltSizeBytes = 32;
    public const int CtrIvSizeBytes = 16;
    public const int WrappedEphemeralSizeBytes = 80;
    public const int Pbkdf2Iterations = 131_072; // 2^17 matching WA Web
    public const int BundleSaltSizeBytes = 32;
    public const int BundleGcmNonceSizeBytes = 12;
    public const int BundleGcmTagSizeBytes = 16;

    public static readonly TimeSpan DefaultCodeValidity = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan PrimaryHelloSuccessTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CompanionFinishIqTimeout = TimeSpan.FromSeconds(30);
    public const int MaxPrimaryHelloAttempts = 3;

    // Crockford Base32 omitting 0, I, O, U to avoid visual ambiguity.
    private const string CrockfordAlphabet = "123456789ABCDEFGHJKLMNPQRSTVWXYZ";
    private static ReadOnlySpan<byte> AdvSecretInfo => "adv_secret"u8;
    private static ReadOnlySpan<byte> BundleKeyInfo => "link_code_pairing_key_bundle_encryption_key"u8;

    public static string GenerateCode()
    {
        Span<byte> random5 = stackalloc byte[5];
        RandomNumberGenerator.Fill(random5);
        return EncodeCrockford(random5);
    }

    public static bool ValidateCode(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length != 8)
        {
            return false;
        }

        foreach (char c in code)
        {
            char upper = char.ToUpperInvariant(c);
            if (!CrockfordAlphabet.Contains(upper))
            {
                return false;
            }
        }
        return true;
    }

    public static string EncodeCrockford(ReadOnlySpan<byte> fiveBytes)
    {
        if (fiveBytes.Length != 5)
        {
            throw new ArgumentException("Pair code encoding requires exactly 5 bytes.", nameof(fiveBytes));
        }

        ulong acc = 0;
        for (int i = 0; i < 5; i++)
        {
            acc = (acc << 8) | fiveBytes[i];
        }

        Span<char> chars = stackalloc char[8];
        for (int i = 7; i >= 0; i--)
        {
            int idx = (int)((acc >> (i * 5)) & 0x1F);
            chars[7 - i] = CrockfordAlphabet[idx];
        }
        return new string(chars);
    }

    public static byte[] DeriveKey(string code, ReadOnlySpan<byte> salt32)
    {
        if (string.IsNullOrEmpty(code))
        {
            throw new ArgumentException("Pair code cannot be empty.", nameof(code));
        }
        if (salt32.Length != SaltSizeBytes)
        {
            throw new ArgumentException($"Salt must be {SaltSizeBytes} bytes.", nameof(salt32));
        }

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(code),
            salt32,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            32);
    }

    public static byte[] EncryptEphemeralPublicKey(
        ReadOnlySpan<byte> ephemeralPublic32,
        string pairCode,
        ReadOnlySpan<byte> salt32 = default,
        ReadOnlySpan<byte> iv16 = default)
    {
        if (ephemeralPublic32.Length != 32)
        {
            throw new ArgumentException("Ephemeral public key must be 32 bytes.", nameof(ephemeralPublic32));
        }

        byte[] salt = salt32.IsEmpty ? RandomNumberGenerator.GetBytes(SaltSizeBytes) : salt32.ToArray();
        byte[] iv = iv16.IsEmpty ? RandomNumberGenerator.GetBytes(CtrIvSizeBytes) : iv16.ToArray();
        if (salt.Length != SaltSizeBytes || iv.Length != CtrIvSizeBytes)
        {
            throw new ArgumentException("Salt must be 32 bytes and IV must be 16 bytes.");
        }

        byte[] derivedKey = DeriveKey(pairCode, salt);
        byte[] output = new byte[WrappedEphemeralSizeBytes];
        salt.CopyTo(output, 0);
        iv.CopyTo(output, SaltSizeBytes);
        ApplyAes256Ctr(derivedKey, iv, ephemeralPublic32, output.AsSpan(SaltSizeBytes + CtrIvSizeBytes, 32));
        return output;
    }

    public static byte[] DecryptPrimaryEphemeralPublicKey(ReadOnlySpan<byte> wrapped80, string pairCode)
    {
        if (wrapped80.Length != WrappedEphemeralSizeBytes)
        {
            throw new PairingException(400, "bad-request", $"Wrapped ephemeral data must be {WrappedEphemeralSizeBytes} bytes, got {wrapped80.Length}.");
        }

        ReadOnlySpan<byte> salt = wrapped80[..SaltSizeBytes];
        ReadOnlySpan<byte> iv = wrapped80.Slice(SaltSizeBytes, CtrIvSizeBytes);
        ReadOnlySpan<byte> ciphertext = wrapped80.Slice(SaltSizeBytes + CtrIvSizeBytes, 32);

        byte[] derivedKey = DeriveKey(pairCode, salt);
        byte[] plaintext = new byte[32];
        ApplyAes256Ctr(derivedKey, iv, ciphertext, plaintext);
        return plaintext;
    }

    public static WrappedKeyBundleResult PrepareKeyBundle(
        KeyPair companionEphemeral,
        ReadOnlySpan<byte> primaryEphemeralPublic32,
        ReadOnlySpan<byte> primaryIdentityPublic32,
        KeyPair companionIdentity,
        ReadOnlySpan<byte> randomBytes32 = default,
        ReadOnlySpan<byte> bundleSalt32 = default,
        ReadOnlySpan<byte> gcmNonce12 = default)
    {
        if (primaryEphemeralPublic32.Length != 32 || primaryIdentityPublic32.Length != 32)
        {
            throw new ArgumentException("Primary public keys must be 32 bytes.");
        }

        byte[] ephShared = Curve25519.CalculateSharedSecret(companionEphemeral.PrivateKey, primaryEphemeralPublic32.ToArray());
        byte[] idShared = Curve25519.CalculateSharedSecret(companionIdentity.PrivateKey, primaryIdentityPublic32.ToArray());

        byte[] rnd = randomBytes32.IsEmpty ? RandomNumberGenerator.GetBytes(32) : randomBytes32.ToArray();
        byte[] salt = bundleSalt32.IsEmpty ? RandomNumberGenerator.GetBytes(BundleSaltSizeBytes) : bundleSalt32.ToArray();
        byte[] nonce = gcmNonce12.IsEmpty ? RandomNumberGenerator.GetBytes(BundleGcmNonceSizeBytes) : gcmNonce12.ToArray();
        if (rnd.Length != 32 || salt.Length != BundleSaltSizeBytes || nonce.Length != BundleGcmNonceSizeBytes)
        {
            throw new ArgumentException("Invalid bundle random/salt/nonce sizes.");
        }

        byte[] combined = new byte[96];
        ephShared.CopyTo(combined, 0);
        idShared.CopyTo(combined, 32);
        rnd.CopyTo(combined, 64);

        byte[] advSecret = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, combined, advSecret, ReadOnlySpan<byte>.Empty, AdvSecretInfo);

        byte[] encKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ephShared, encKey, salt, BundleKeyInfo);

        byte[] plaintextBundle = new byte[96];
        companionIdentity.PublicKey.CopyTo(plaintextBundle, 0);
        primaryIdentityPublic32.CopyTo(plaintextBundle.AsSpan(32, 32));
        rnd.CopyTo(plaintextBundle, 64);

        byte[] wrapped = new byte[BundleSaltSizeBytes + BundleGcmNonceSizeBytes + plaintextBundle.Length + BundleGcmTagSizeBytes];
        salt.CopyTo(wrapped, 0);
        nonce.CopyTo(wrapped, BundleSaltSizeBytes);

        int ctOffset = BundleSaltSizeBytes + BundleGcmNonceSizeBytes;
        using var aesGcm = new AesGcm(encKey, BundleGcmTagSizeBytes);
        aesGcm.Encrypt(
            nonce,
            plaintextBundle,
            wrapped.AsSpan(ctOffset, plaintextBundle.Length),
            wrapped.AsSpan(ctOffset + plaintextBundle.Length, BundleGcmTagSizeBytes));

        return new WrappedKeyBundleResult(wrapped, advSecret);
    }

    public static (CompanionWebClientType PlatformId, string DisplayString) ResolveCompanionPlatform(
        DeviceProps? props = null,
        CompanionWebClientType? platformOverride = null,
        string? displayOsOverride = null)
    {
        CompanionWebClientType id = platformOverride ?? PairingCrypto.ClientTypeForProps(props);
        string rawOs = !string.IsNullOrWhiteSpace(displayOsOverride)
            ? displayOsOverride
            : (props?.Os ?? string.Empty);

        string canonicalOs = CanonicalizeOs(rawOs);
        string browser = id switch
        {
            CompanionWebClientType.Chrome => "Chrome",
            CompanionWebClientType.Edge => "Edge",
            CompanionWebClientType.Firefox => "Firefox",
            CompanionWebClientType.Ie => "IE",
            CompanionWebClientType.Opera => "Opera",
            CompanionWebClientType.Safari => "Safari",
            CompanionWebClientType.Electron => "Electron",
            CompanionWebClientType.Uwp => "UWP",
            _ => "Chrome"
        };

        return (id, $"{browser} ({canonicalOs})");
    }

    public static Node BuildCompanionHelloIq(
        string iqId,
        string normalizedPhoneNumber,
        byte[] noiseStaticPublic32,
        byte[] wrappedEphemeral80,
        CompanionWebClientType platformId,
        string platformDisplay,
        bool showPushNotification = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPhoneNumber);
        if (noiseStaticPublic32.Length != 32)
        {
            throw new ArgumentException("Noise static public key must be 32 bytes.", nameof(noiseStaticPublic32));
        }
        if (wrappedEphemeral80.Length != WrappedEphemeralSizeBytes)
        {
            throw new ArgumentException($"Wrapped ephemeral must be {WrappedEphemeralSizeBytes} bytes.", nameof(wrappedEphemeral80));
        }

        string platformIdStr = ((char)(byte)platformId).ToString();
        var regNode = new Node(
            "link_code_companion_reg",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["jid"] = Jid.Pn(normalizedPhoneNumber),
                ["stage"] = "companion_hello",
                ["should_show_push_notification"] = showPushNotification ? "true" : "false"
            },
            new Node[]
            {
                new("link_code_pairing_wrapped_companion_ephemeral_pub", wrappedEphemeral80),
                new("companion_server_auth_key_pub", noiseStaticPublic32),
                new("companion_platform_id", Encoding.UTF8.GetBytes(platformIdStr)),
                new("companion_platform_display", Encoding.UTF8.GetBytes(platformDisplay)),
                // Single 0x00 byte (not ASCII '0') matching WA Web Alt/DeviceLinkingIq.js.
                new("link_code_pairing_nonce", new byte[] { 0x00 })
            });

        return StanzaBuilder.Iq(iqId, "set", "md", StanzaBuilder.ServerPn, regNode);
    }

    public static byte[]? ParseCompanionHelloResponse(Node responseIq)
    {
        ArgumentNullException.ThrowIfNull(responseIq);
        Node? refNode = responseIq
            .GetChild("link_code_companion_reg")
            ?.GetChild("link_code_pairing_ref");

        return refNode?.Content is NodeContent.BytesContent bytes && bytes.Bytes.Length > 0
            ? bytes.Bytes
            : null;
    }

    public static bool TryParsePrimaryHelloNotification(Node notificationNode, out ParsedPrimaryHello? parsed)
    {
        ArgumentNullException.ThrowIfNull(notificationNode);
        parsed = null;

        Node? reg = notificationNode.GetChild("link_code_companion_reg");
        if (reg is null || !string.Equals(reg.GetAttr("stage"), "primary_hello", StringComparison.Ordinal))
        {
            return false;
        }

        if (reg.GetChild("link_code_pairing_wrapped_primary_ephemeral_pub")?.Content is not NodeContent.BytesContent eph
            || eph.Bytes.Length != WrappedEphemeralSizeBytes)
        {
            return false;
        }

        if (reg.GetChild("primary_identity_pub")?.Content is not NodeContent.BytesContent idPub
            || idPub.Bytes.Length != 32)
        {
            return false;
        }

        if (reg.GetChild("link_code_pairing_ref")?.Content is not NodeContent.BytesContent pairRef
            || pairRef.Bytes.Length == 0)
        {
            return false;
        }

        parsed = new ParsedPrimaryHello(eph.Bytes, idPub.Bytes, pairRef.Bytes);
        return true;
    }

    public static bool IsRefreshCodeNotification(Node notificationNode) =>
        notificationNode.GetChild("link_code_companion_reg") is { } reg
        && string.Equals(reg.GetAttr("stage"), "refresh_code", StringComparison.Ordinal);

    public static Node BuildCompanionFinishIq(
        string iqId,
        string normalizedPhoneNumber,
        byte[] wrappedKeyBundle,
        byte[] companionIdentityPublic32,
        byte[] pairingRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPhoneNumber);
        ArgumentNullException.ThrowIfNull(wrappedKeyBundle);
        if (companionIdentityPublic32.Length != 32)
        {
            throw new ArgumentException("Companion identity public key must be 32 bytes.", nameof(companionIdentityPublic32));
        }

        var regNode = new Node(
            "link_code_companion_reg",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["jid"] = Jid.Pn(normalizedPhoneNumber),
                ["stage"] = "companion_finish"
            },
            new Node[]
            {
                new("link_code_pairing_wrapped_key_bundle", wrappedKeyBundle),
                new("companion_identity_public", companionIdentityPublic32),
                new("link_code_pairing_ref", pairingRef)
            });

        return StanzaBuilder.Iq(iqId, "set", "md", StanzaBuilder.ServerPn, regNode);
    }

    public static PairCodeRejectionCode? ClassifyRejection(int? errorCode, string? errorText) =>
        (errorCode, errorText) switch
        {
            (400, "bad-request") => PairCodeRejectionCode.BadRequest,
            (403, "forbidden") => PairCodeRejectionCode.Forbidden,
            (429, "rate-overlimit") => PairCodeRejectionCode.RateOverlimit,
            (452, "feature-not-available") => PairCodeRejectionCode.FeatureNotAvailable,
            (500, "internal-server-error") => PairCodeRejectionCode.InternalServerError,
            _ => null
        };

    private static string CanonicalizeOs(string rawOs)
    {
        string os = rawOs.Trim().ToLowerInvariant();
        if (os.Contains("windows", StringComparison.Ordinal))
        {
            return "Windows";
        }
        if (os.Contains("mac", StringComparison.Ordinal) || os.Contains("osx", StringComparison.Ordinal) || os.Contains("darwin", StringComparison.Ordinal))
        {
            return "Mac OS";
        }
        if (os.Contains("android", StringComparison.Ordinal))
        {
            return "Android";
        }
        if (os.Contains("ipad", StringComparison.Ordinal) || os.Contains("iphone", StringComparison.Ordinal) || os.Contains("ios", StringComparison.Ordinal))
        {
            return "iOS";
        }
        return "Linux";
    }

    private static void ApplyAes256Ctr(
        ReadOnlySpan<byte> key32,
        ReadOnlySpan<byte> iv16,
        ReadOnlySpan<byte> input,
        Span<byte> output)
    {
        using var aes = Aes.Create();
        aes.Key = key32.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        Span<byte> counter = stackalloc byte[16];
        iv16.CopyTo(counter);
        Span<byte> keystream = stackalloc byte[16];

        int offset = 0;
        while (offset < input.Length)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);
            int take = Math.Min(16, input.Length - offset);
            for (int i = 0; i < take; i++)
            {
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
            }
            offset += take;
            IncrementBigEndianCounter(counter);
        }
    }

    private static void IncrementBigEndianCounter(Span<byte> counter)
    {
        ulong lo = BinaryPrimitives.ReadUInt64BigEndian(counter.Slice(8, 8));
        ulong nextLo = unchecked(lo + 1);
        BinaryPrimitives.WriteUInt64BigEndian(counter.Slice(8, 8), nextLo);
        if (nextLo == 0)
        {
            ulong hi = BinaryPrimitives.ReadUInt64BigEndian(counter.Slice(0, 8));
            BinaryPrimitives.WriteUInt64BigEndian(counter.Slice(0, 8), unchecked(hi + 1));
        }
    }
}
