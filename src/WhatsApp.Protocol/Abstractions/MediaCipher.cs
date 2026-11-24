using System.Security.Cryptography;

namespace WhatsApp.Protocol.Crypto;

/// <summary>
/// WhatsApp MMS media types matching reference/whatsapp-rust wacore::download::MediaType.
/// </summary>
public enum WaMediaType
{
    Image,
    Video,
    Audio,
    Document,
    History,
    GroupHistory,
    AppState,
    Sticker,
    StickerPack,
    StickerPackThumbnail,
    LinkThumbnail,
    ProductCatalogImage,
    MusicArtwork,
    NewsletterMusicArtwork,
    Ptt,
    Gif
}

/// <summary>
/// Unified HKDF-SHA256 + AES-256-CBC + HMAC-SHA256 cipher for WhatsApp MMS encrypted payloads
/// (History Sync chunks, AppState external blobs, Stickers, Images, Video, Audio, Documents).
/// </summary>
public static class MediaCipher
{
    public const int MediaMacSize = 10;
    public const int AesBlockSize = 16;
    public const int MediaKeyLength = 32;
    public const int ExpandedKeyLength = 112;

    public static ReadOnlySpan<byte> GetAppInfo(WaMediaType mediaType) => mediaType switch
    {
        WaMediaType.Image or WaMediaType.Sticker or WaMediaType.ProductCatalogImage => "WhatsApp Image Keys"u8,
        WaMediaType.Video or WaMediaType.Gif => "WhatsApp Video Keys"u8,
        WaMediaType.Audio or WaMediaType.Ptt => "WhatsApp Audio Keys"u8,
        WaMediaType.Document => "WhatsApp Document Keys"u8,
        WaMediaType.History => "WhatsApp History Keys"u8,
        WaMediaType.GroupHistory => "Group History"u8,
        WaMediaType.AppState => "WhatsApp App State Keys"u8,
        WaMediaType.StickerPack => "WhatsApp Sticker Pack Keys"u8,
        WaMediaType.StickerPackThumbnail => "WhatsApp Sticker Pack Thumbnail Keys"u8,
        WaMediaType.LinkThumbnail => "WhatsApp Link Thumbnail Keys"u8,
        WaMediaType.MusicArtwork or WaMediaType.NewsletterMusicArtwork => "WhatsApp Music Artwork Keys"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, "Unsupported WaMediaType")
    };

    public static (byte[] Iv, byte[] CipherKey, byte[] MacKey) DeriveMediaKeys(
        ReadOnlySpan<byte> mediaKey,
        WaMediaType mediaType)
        => DeriveMediaKeys(mediaKey, GetAppInfo(mediaType));

    public static (byte[] Iv, byte[] CipherKey, byte[] MacKey) DeriveMediaKeys(
        ReadOnlySpan<byte> mediaKey,
        ReadOnlySpan<byte> info)
    {
        if (mediaKey.Length != MediaKeyLength)
        {
            throw new ArgumentException($"MediaKey must be exactly {MediaKeyLength} bytes", nameof(mediaKey));
        }

        byte[] expanded = new byte[ExpandedKeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, mediaKey, expanded, salt: default, info: info);

        return (expanded[..16], expanded[16..48], expanded[48..80]);
    }

    public static byte[] DecryptBlob(
        ReadOnlySpan<byte> encBlob,
        ReadOnlySpan<byte> mediaKey,
        WaMediaType mediaType,
        ReadOnlySpan<byte> expectedEncSha256 = default,
        ReadOnlySpan<byte> expectedPlainSha256 = default)
        => DecryptBlob(encBlob, mediaKey, GetAppInfo(mediaType), expectedEncSha256, expectedPlainSha256);

    public static byte[] DecryptBlob(
        ReadOnlySpan<byte> encBlob,
        ReadOnlySpan<byte> mediaKey,
        ReadOnlySpan<byte> info,
        ReadOnlySpan<byte> expectedEncSha256 = default,
        ReadOnlySpan<byte> expectedPlainSha256 = default)
    {
        if (encBlob.Length < AesBlockSize + MediaMacSize)
        {
            throw new ArgumentException($"Encrypted blob is too short: {encBlob.Length} bytes", nameof(encBlob));
        }

        if (!expectedEncSha256.IsEmpty)
        {
            byte[] actualEncHash = SHA256.HashData(encBlob);
            if (!CryptographicOperations.FixedTimeEquals(actualEncHash, expectedEncSha256))
            {
                throw new CryptographicException("Encrypted blob SHA-256 hash mismatch");
            }
        }

        ReadOnlySpan<byte> ciphertext = encBlob[..^MediaMacSize];
        ReadOnlySpan<byte> trailingMac = encBlob[^MediaMacSize..];

        var (iv, cipherKey, macKey) = DeriveMediaKeys(mediaKey, info);

        using (var hmac = new HMACSHA256(macKey))
        {
            hmac.TransformBlock(iv, 0, iv.Length, null, 0);
            hmac.TransformFinalBlock(ciphertext.ToArray(), 0, ciphertext.Length);
            byte[] fullMac = hmac.Hash!;

            if (!CryptographicOperations.FixedTimeEquals(fullMac.AsSpan(0, MediaMacSize), trailingMac))
            {
                throw new CryptographicException("Media blob HMAC verification failed");
            }
        }

        if (ciphertext.IsEmpty || ciphertext.Length % AesBlockSize != 0)
        {
            throw new CryptographicException("Ciphertext length must be a non-zero multiple of AES block size");
        }

        using var aes = Aes.Create();
        aes.Key = cipherKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        byte[] plaintext = decryptor.TransformFinalBlock(ciphertext.ToArray(), 0, ciphertext.Length);

        if (!expectedPlainSha256.IsEmpty)
        {
            byte[] actualPlainHash = SHA256.HashData(plaintext);
            if (!CryptographicOperations.FixedTimeEquals(actualPlainHash, expectedPlainSha256))
            {
                throw new CryptographicException("Decrypted plaintext SHA-256 hash mismatch");
            }
        }

        return plaintext;
    }

    public static byte[] EncryptBlob(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> mediaKey,
        WaMediaType mediaType)
        => EncryptBlob(plaintext, mediaKey, GetAppInfo(mediaType));

    public static byte[] EncryptBlob(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> mediaKey,
        ReadOnlySpan<byte> info)
    {
        var (iv, cipherKey, macKey) = DeriveMediaKeys(mediaKey, info);

        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Key = cipherKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            ciphertext = encryptor.TransformFinalBlock(plaintext.ToArray(), 0, plaintext.Length);
        }

        byte[] result = new byte[ciphertext.Length + MediaMacSize];
        ciphertext.CopyTo(result, 0);

        using (var hmac = new HMACSHA256(macKey))
        {
            hmac.TransformBlock(iv, 0, iv.Length, null, 0);
            hmac.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            byte[] fullMac = hmac.Hash!;
            fullMac.AsSpan(0, MediaMacSize).CopyTo(result.AsSpan(ciphertext.Length));
        }

        return result;
    }
}
