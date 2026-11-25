using System.Security.Cryptography;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Crypto;

/// <summary>
/// Trailing single-character client type suffix in WhatsApp companion QR payloads
/// (matches WhatsApp Web <c>CompanionWebClientType</c> and <c>wacore/src/companion_reg.rs</c>).
/// </summary>
public enum CompanionWebClientType : byte
{
    Chrome = (byte)'1',
    Edge = (byte)'2',
    Firefox = (byte)'3',
    Ie = (byte)'4',
    Opera = (byte)'5',
    Safari = (byte)'6',
    Electron = (byte)'7',
    Uwp = (byte)'8',
    OtherWebClient = (byte)'9',
    AndroidTablet = (byte)'d',
    AndroidPhone = (byte)'e',
    AndroidAmbiguous = (byte)'f',
}

/// <summary>
/// Parsed components of a WhatsApp companion QR code payload.
/// </summary>
public sealed record ParsedQrCode(
    string PairingRef,
    byte[] NoisePublicKey,
    byte[] IdentityPublicKey);

/// <summary>
/// Cryptographic or structural failure while verifying an ADV pairing payload,
/// paired with the XMPP IQ error <see cref="Code"/> and <see cref="ErrorText"/> expected by the server.
/// </summary>
public sealed class PairingException : Exception
{
    public int Code { get; }
    public string ErrorText { get; }

    public PairingException(int code, string errorText, string? message = null, Exception? innerException = null)
        : base(message ?? $"Pairing failed ({code} {errorText})", innerException)
    {
        Code = code;
        ErrorText = errorText;
    }
}

/// <summary>
/// QR payload encoding/parsing and ADV cryptographic verification/signing for multi-device pairing.
/// Mirrors <c>wacore/src/adv.rs</c> and <c>wacore/src/pair.rs</c>.
/// </summary>
public static class PairingCrypto
{
    public const string NativeCameraDeepLinkPrefix = "https://wa.me/settings/linked_devices#";

    private static ReadOnlySpan<byte> PrefixAccountSignature => [0x06, 0x00];
    private static ReadOnlySpan<byte> PrefixDeviceSignature => [0x06, 0x01];
    private static ReadOnlySpan<byte> PrefixKeyIndexListAccountSignature => [0x06, 0x02];
    private static ReadOnlySpan<byte> PrefixHostedAccountSignature => [0x06, 0x05];
    private static ReadOnlySpan<byte> PrefixHostedDeviceSignature => [0x06, 0x06];

    /// <summary>
    /// Maps <see cref="DeviceProps"/> platform metadata to its QR wire client type byte.
    /// </summary>
    public static CompanionWebClientType ClientTypeForProps(DeviceProps? props)
    {
        if (props is null || !props.HasPlatformType)
        {
            return CompanionWebClientType.OtherWebClient;
        }

        return props.PlatformType switch
        {
            DeviceProps.Types.PlatformType.Chrome => CompanionWebClientType.Chrome,
            DeviceProps.Types.PlatformType.AndroidPhone => CompanionWebClientType.Chrome,
            DeviceProps.Types.PlatformType.AndroidTablet => CompanionWebClientType.Chrome,
            DeviceProps.Types.PlatformType.AndroidAmbiguous => CompanionWebClientType.Chrome,
            DeviceProps.Types.PlatformType.Firefox => CompanionWebClientType.Firefox,
            DeviceProps.Types.PlatformType.Ie => CompanionWebClientType.Ie,
            DeviceProps.Types.PlatformType.Opera => CompanionWebClientType.Opera,
            DeviceProps.Types.PlatformType.Safari => CompanionWebClientType.Safari,
            DeviceProps.Types.PlatformType.Edge => CompanionWebClientType.Edge,
            DeviceProps.Types.PlatformType.Desktop => CompanionWebClientType.Electron,
            DeviceProps.Types.PlatformType.Uwp => CompanionWebClientType.Uwp,
            _ => CompanionWebClientType.OtherWebClient,
        };
    }

    /// <summary>
    /// Formats a 5-field WhatsApp Web companion QR payload:
    /// <c>{ref},{base64(noisePub)},{base64(identityPub)},{base64(advSecret)},{clientType}</c>.
    /// </summary>
    public static string MakeQrData(
        ReadOnlySpan<byte> noisePublicKey,
        ReadOnlySpan<byte> identityPublicKey,
        ReadOnlySpan<byte> advSecretKey,
        string pairingRef,
        CompanionWebClientType clientType = CompanionWebClientType.Chrome)
    {
        ArgumentException.ThrowIfNullOrEmpty(pairingRef);
        ValidateKeyLength(noisePublicKey, nameof(noisePublicKey));
        ValidateKeyLength(identityPublicKey, nameof(identityPublicKey));
        ValidateKeyLength(advSecretKey, nameof(advSecretKey));

        return string.Create(
            null,
            $"{pairingRef},{Convert.ToBase64String(noisePublicKey)},{Convert.ToBase64String(identityPublicKey)},{Convert.ToBase64String(advSecretKey)},{(char)clientType}");
    }

    /// <summary>
    /// Formats a 5-field companion QR payload directly from persisted <see cref="DeviceCredentials"/>.
    /// </summary>
    public static string MakeQrData(
        DeviceCredentials credentials,
        string pairingRef,
        CompanionWebClientType clientType = CompanionWebClientType.Chrome)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return MakeQrData(
            credentials.NoiseKey.PublicKey,
            credentials.IdentityKey.PublicKey,
            credentials.AdvSecretKey,
            pairingRef,
            clientType);
    }

    /// <summary>
    /// Parses a companion QR payload (accepting 4-field legacy, 5-field modern,
    /// <c>https://wa.me/settings/linked_devices#</c> deep link prefix, and trailing FAQ URLs).
    /// </summary>
    public static ParsedQrCode ParseQrData(string qrCode)
    {
        ArgumentNullException.ThrowIfNull(qrCode);

        string body = qrCode.StartsWith(NativeCameraDeepLinkPrefix, StringComparison.Ordinal)
            ? qrCode[NativeCameraDeepLinkPrefix.Length..]
            : qrCode;

        string[] parts = body.Split(',');
        if (parts.Length < 4)
        {
            throw new FormatException(
                $"Invalid QR code format: expected at least 4 comma-separated fields, got {parts.Length}");
        }

        string pairingRef = parts[0];
        if (string.IsNullOrEmpty(pairingRef)
            || string.IsNullOrEmpty(parts[1])
            || string.IsNullOrEmpty(parts[2])
            || string.IsNullOrEmpty(parts[3])
            || parts.Skip(4).Any(string.IsNullOrEmpty))
        {
            throw new FormatException(
                "Invalid QR code format: all comma-separated fields must be non-empty");
        }

        byte[] noisePub;
        byte[] identityPub;
        try
        {
            noisePub = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Invalid QR noise public key base64", ex);
        }

        try
        {
            identityPub = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Invalid QR identity public key base64", ex);
        }

        if (noisePub.Length != 32)
        {
            throw new FormatException($"Invalid noise public key length: expected 32 bytes, got {noisePub.Length}");
        }
        if (identityPub.Length != 32)
        {
            throw new FormatException($"Invalid identity public key length: expected 32 bytes, got {identityPub.Length}");
        }

        return new ParsedQrCode(pairingRef, noisePub, identityPub);
    }

    /// <summary>
    /// Computes the monotonic <c>LidMigrated</c> update according to <c>PairUtils::lid_migrated_update</c>:
    /// <list type="bullet">
    /// <item>Primary reported migrated (<paramref name="reportedMigrated"/> == true) -> always <c>true</c>.</item>
    /// <item>Different account paired without <c>isChatDbLidMigrated=true</c> -> reset to <c>false</c>.</item>
    /// <item>Same-account relink without flag -> preserve existing stored value (<c>null</c> means unchanged).</item>
    /// </list>
    /// </summary>
    public static bool? ComputeLidMigratedUpdate(bool reportedMigrated, bool accountChanged)
    {
        if (reportedMigrated)
        {
            return true;
        }

        return accountChanged ? false : null;
    }

    /// <summary>
    /// Verifies an incoming <see cref="ADVSignedDeviceIdentityHMAC"/> payload from <c>&lt;pair-success&gt;</c>
    /// (first HMAC with <c>AdvSecretKey</c>, then <c>AccountSignature</c> with XEdDSA),
    /// signs <c>DeviceSignature</c> with <see cref="DeviceCredentials.IdentityKey"/> while preserving
    /// <c>ADVSignedDeviceIdentity.Details</c> byte-for-byte, and returns the serialized
    /// <see cref="ADVSignedDeviceIdentity"/> plus the primary-assigned <c>keyIndex</c>.
    /// </summary>
    public static (byte[] SelfSignedIdentityBytes, uint KeyIndex) VerifyAndSignDeviceIdentity(
        DeviceCredentials credentials,
        ReadOnlySpan<byte> hmacContainerBytes)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (credentials.AdvSecretKey.Length != 32)
        {
            throw new ArgumentException("AdvSecretKey must be 32 bytes.", nameof(credentials));
        }
        if (credentials.IdentityKey.PublicKey.Length != 32 || credentials.IdentityKey.PrivateKey.Length != 32)
        {
            throw new ArgumentException("IdentityKey must contain 32-byte public and private keys.", nameof(credentials));
        }

        ADVSignedDeviceIdentityHMAC container;
        try
        {
            container = ADVSignedDeviceIdentityHMAC.Parser.ParseFrom(hmacContainerBytes);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new PairingException(500, "internal-error", "Failed to decode ADVSignedDeviceIdentityHMAC", ex);
        }

        if (!container.HasDetails)
        {
            throw new PairingException(500, "internal-error", "ADVSignedDeviceIdentityHMAC missing details");
        }

        // Authenticate container HMAC before decoding inner protobufs.
        ReadOnlySpan<byte> containerDetails = container.Details.Span;
        byte[] expectedHmac = ComputeAdvContainerHmac(
            credentials.AdvSecretKey,
            container.HasAccountType && container.AccountType == ADVEncryptionType.Hosted,
            containerDetails);

        ReadOnlySpan<byte> receivedHmac = container.HasHmac ? container.Hmac.Span : ReadOnlySpan<byte>.Empty;
        if (receivedHmac.Length != 32 || !CryptographicOperations.FixedTimeEquals(expectedHmac, receivedHmac))
        {
            throw new PairingException(401, "hmac-mismatch", "ADV pairing HMAC verification failed");
        }

        ADVSignedDeviceIdentity signedIdentity;
        try
        {
            signedIdentity = ADVSignedDeviceIdentity.Parser.ParseFrom(containerDetails);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new PairingException(500, "internal-error", "Failed to decode ADVSignedDeviceIdentity", ex);
        }

        if (!signedIdentity.HasDetails)
        {
            throw new PairingException(500, "internal-error", "ADVSignedDeviceIdentity missing details");
        }

        ReadOnlySpan<byte> innerDetails = signedIdentity.Details.Span;
        ADVDeviceIdentity deviceIdentity;
        try
        {
            deviceIdentity = ADVDeviceIdentity.Parser.ParseFrom(innerDetails);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new PairingException(500, "internal-error", "Failed to decode ADVDeviceIdentity", ex);
        }

        if (!deviceIdentity.HasKeyIndex)
        {
            throw new PairingException(500, "internal-error", "ADVDeviceIdentity missing key_index");
        }

        byte[] accountSigKey = signedIdentity.HasAccountSignatureKey
            ? signedIdentity.AccountSignatureKey.ToByteArray()
            : [];
        if (accountSigKey.Length != 32)
        {
            throw new PairingException(401, "invalid-key", $"Invalid accountSignatureKey length: {accountSigKey.Length}");
        }

        byte[] accountSig = signedIdentity.HasAccountSignature
            ? signedIdentity.AccountSignature.ToByteArray()
            : [];
        if (accountSig.Length != 64)
        {
            throw new PairingException(401, "signature-mismatch", "Invalid or missing accountSignature");
        }

        bool isHostedDevice = deviceIdentity.HasDeviceType && deviceIdentity.DeviceType == ADVEncryptionType.Hosted;
        ReadOnlySpan<byte> accountPrefix = isHostedDevice
            ? PrefixHostedAccountSignature
            : PrefixAccountSignature;

        byte[] verifyMsg = ConcatSegments(accountPrefix, innerDetails, credentials.IdentityKey.PublicKey);
        if (!XEdDSA.Verify(accountSigKey, verifyMsg, accountSig))
        {
            throw new PairingException(401, "signature-mismatch", "ADV accountSignature verification failed");
        }

        byte[] signMsg = ConcatSegments(
            PrefixDeviceSignature,
            innerDetails,
            credentials.IdentityKey.PublicKey,
            accountSigKey);
        byte[] deviceSig = XEdDSA.Sign(credentials.IdentityKey.PrivateKey, signMsg);

        signedIdentity.DeviceSignature = ByteString.CopyFrom(deviceSig);
        return (signedIdentity.ToByteArray(), deviceIdentity.KeyIndex);
    }

    /// <summary>
    /// Validates both <c>accountSignature</c> and <c>deviceSignature</c> on a self-signed
    /// <see cref="ADVSignedDeviceIdentity"/> blob against a companion Curve25519 identity public key.
    /// Matches <c>wacore::adv::validate_adv_with_identity_key</c>: the account prefix follows
    /// <c>ADVDeviceIdentity.deviceType</c>, while the device prefix follows whether the target JID
    /// itself is a hosted device (<c>[6, 6]</c> when <paramref name="isHostedDeviceJid"/> is true,
    /// otherwise <c>[6, 1]</c>).
    /// </summary>
    public static bool ValidateSignedDeviceIdentity(
        ReadOnlySpan<byte> signedIdentityBytes,
        ReadOnlySpan<byte> companionIdentityPublicKey32,
        ReadOnlySpan<byte> expectedAccountIdentityPublicKey32 = default,
        bool isHostedDeviceJid = false)
    {
        if (companionIdentityPublicKey32.Length != 32)
        {
            return false;
        }

        ADVSignedDeviceIdentity signed;
        ADVDeviceIdentity device;
        try
        {
            signed = ADVSignedDeviceIdentity.Parser.ParseFrom(signedIdentityBytes);
            if (!signed.HasDetails
                || !signed.HasAccountSignature
                || !signed.HasDeviceSignature)
            {
                return false;
            }
            device = ADVDeviceIdentity.Parser.ParseFrom(signed.Details.Span);
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }

        byte[] accountKey = signed.HasAccountSignatureKey && signed.AccountSignatureKey.Length > 0
            ? signed.AccountSignatureKey.ToByteArray()
            : expectedAccountIdentityPublicKey32.ToArray();
        byte[] accountSig = signed.AccountSignature.ToByteArray();
        byte[] deviceSig = signed.DeviceSignature.ToByteArray();
        if (accountKey.Length != 32 || accountSig.Length != 64 || deviceSig.Length != 64)
        {
            return false;
        }

        if (!expectedAccountIdentityPublicKey32.IsEmpty &&
            !CryptographicOperations.FixedTimeEquals(accountKey, expectedAccountIdentityPublicKey32))
        {
            return false;
        }

        bool isHostedDeviceType = device.HasDeviceType && device.DeviceType == ADVEncryptionType.Hosted;
        ReadOnlySpan<byte> details = signed.Details.Span;
        byte[] accountMsg = ConcatSegments(
            isHostedDeviceType ? PrefixHostedAccountSignature : PrefixAccountSignature,
            details,
            companionIdentityPublicKey32);
        if (!XEdDSA.Verify(accountKey, accountMsg, accountSig))
        {
            return false;
        }

        byte[] deviceMsg = ConcatSegments(
            isHostedDeviceJid ? PrefixHostedDeviceSignature : PrefixDeviceSignature,
            details,
            companionIdentityPublicKey32,
            accountKey);
        return XEdDSA.Verify(companionIdentityPublicKey32.ToArray(), deviceMsg, deviceSig);
    }

    public sealed record DecodedKeyIndexList(
        uint RawId,
        ulong Timestamp,
        uint CurrentIndex,
        IReadOnlyList<uint> ValidIndexes,
        ADVEncryptionType AccountType);

    /// <summary>
    /// Decodes <see cref="ADVSignedKeyIndexList"/> without verifying its signature.
    /// Network callers must prefer <see cref="VerifyKeyIndexList"/> or <see cref="VerifyHostedKeyIndexList"/>.
    /// </summary>
    public static DecodedKeyIndexList? DecodeKeyIndexList(ReadOnlySpan<byte> signedKeyIndexBytes)
    {
        if (signedKeyIndexBytes.IsEmpty)
        {
            return null;
        }

        try
        {
            ADVSignedKeyIndexList signed = ADVSignedKeyIndexList.Parser.ParseFrom(signedKeyIndexBytes);
            if (!signed.HasDetails || signed.Details.IsEmpty)
            {
                return null;
            }

            ADVKeyIndexList keyIndex = ADVKeyIndexList.Parser.ParseFrom(signed.Details);
            if (!keyIndex.HasRawId || !keyIndex.HasTimestamp)
            {
                return null;
            }

            return new DecodedKeyIndexList(
                keyIndex.RawId,
                keyIndex.Timestamp,
                keyIndex.HasCurrentIndex ? keyIndex.CurrentIndex : 0u,
                keyIndex.ValidIndexes.ToArray(),
                keyIndex.HasAccountType ? keyIndex.AccountType : ADVEncryptionType.E2Ee);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    /// <summary>
    /// Authenticates <see cref="ADVSignedKeyIndexList"/> against a trusted primary account identity key.
    /// Matches live <c>WAWebAdvKeyIndexSignatureVerify</c> (<c>[0x06, 0x02] || details</c>).
    /// </summary>
    public static DecodedKeyIndexList? VerifyKeyIndexList(
        ReadOnlySpan<byte> signedKeyIndexBytes,
        ReadOnlySpan<byte> accountIdentityPublicKey32)
    {
        if (signedKeyIndexBytes.IsEmpty || accountIdentityPublicKey32.Length != 32)
        {
            return null;
        }

        try
        {
            ADVSignedKeyIndexList signed = ADVSignedKeyIndexList.Parser.ParseFrom(signedKeyIndexBytes);
            if (!signed.HasDetails || signed.Details.IsEmpty || !signed.HasAccountSignature || signed.AccountSignature.Length != 64)
            {
                return null;
            }

            byte[] message = ConcatSegments(PrefixKeyIndexListAccountSignature, signed.Details.Span, ReadOnlySpan<byte>.Empty);
            if (!XEdDSA.Verify(accountIdentityPublicKey32.ToArray(), message, signed.AccountSignature.ToByteArray()))
            {
                return null;
            }

            return DecodeKeyIndexList(signedKeyIndexBytes);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    /// <summary>
    /// Authenticates a hosted/coex <see cref="ADVSignedKeyIndexList"/> using its embedded
    /// <see cref="ADVSignedKeyIndexList.AccountSignatureKey"/> (only use for hosted branches).
    /// </summary>
    public static DecodedKeyIndexList? VerifyHostedKeyIndexList(
        ReadOnlySpan<byte> signedKeyIndexBytes,
        out byte[]? envelopeAccountIdentityPublicKey32)
    {
        envelopeAccountIdentityPublicKey32 = null;
        if (signedKeyIndexBytes.IsEmpty)
        {
            return null;
        }

        try
        {
            ADVSignedKeyIndexList signed = ADVSignedKeyIndexList.Parser.ParseFrom(signedKeyIndexBytes);
            if (!signed.HasAccountSignatureKey || signed.AccountSignatureKey.Length != 32)
            {
                return null;
            }

            byte[] accountPub = signed.AccountSignatureKey.ToByteArray();
            DecodedKeyIndexList? decoded = VerifyKeyIndexList(signedKeyIndexBytes, accountPub);
            if (decoded is null)
            {
                return null;
            }

            envelopeAccountIdentityPublicKey32 = accountPub;
            return decoded;
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates an XEdDSA-signed <see cref="ADVSignedKeyIndexList"/> blob for tests and tooling.
    /// </summary>
    public static byte[] SignKeyIndexList(
        ReadOnlySpan<byte> accountIdentityPrivateKey32,
        ADVKeyIndexList list,
        ReadOnlySpan<byte> accountIdentityPublicKey32 = default)
    {
        ValidateKeyLength(accountIdentityPrivateKey32, nameof(accountIdentityPrivateKey32));
        ArgumentNullException.ThrowIfNull(list);

        byte[] details = list.ToByteArray();
        byte[] message = ConcatSegments(PrefixKeyIndexListAccountSignature, details, ReadOnlySpan<byte>.Empty);
        byte[] signature = XEdDSA.Sign(accountIdentityPrivateKey32.ToArray(), message);

        var signed = new ADVSignedKeyIndexList
        {
            Details = ByteString.CopyFrom(details),
            AccountSignature = ByteString.CopyFrom(signature),
        };
        if (accountIdentityPublicKey32.Length == 32)
        {
            signed.AccountSignatureKey = ByteString.CopyFrom(accountIdentityPublicKey32);
        }

        return signed.ToByteArray();
    }

    internal static byte[] ComputeAdvContainerHmac(
        byte[] advSecretKey,
        bool isHostedAccount,
        ReadOnlySpan<byte> signedDetailsBytes)
    {
        using var hmac = new HMACSHA256(advSecretKey);
        if (isHostedAccount)
        {
            byte[] prefix = [PrefixHostedAccountSignature[0], PrefixHostedAccountSignature[1]];
            hmac.TransformBlock(prefix, 0, prefix.Length, null, 0);
        }

        byte[] detailsCopy = signedDetailsBytes.ToArray();
        hmac.TransformFinalBlock(detailsCopy, 0, detailsCopy.Length);
        return hmac.Hash!;
    }

    private static byte[] ConcatSegments(
        ReadOnlySpan<byte> a,
        ReadOnlySpan<byte> b,
        ReadOnlySpan<byte> c,
        ReadOnlySpan<byte> d = default)
    {
        byte[] buffer = new byte[a.Length + b.Length + c.Length + d.Length];
        int offset = 0;
        a.CopyTo(buffer.AsSpan(offset));
        offset += a.Length;
        b.CopyTo(buffer.AsSpan(offset));
        offset += b.Length;
        c.CopyTo(buffer.AsSpan(offset));
        offset += c.Length;
        if (!d.IsEmpty)
        {
            d.CopyTo(buffer.AsSpan(offset));
        }
        return buffer;
    }

    private static void ValidateKeyLength(ReadOnlySpan<byte> key, string paramName)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("Key must be exactly 32 bytes.", paramName);
        }
    }
}
