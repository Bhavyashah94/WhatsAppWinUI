using System.Security.Cryptography;
using System.Text;

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

public sealed record MediaUploadEncryptionResult(
    byte[] DataToUpload,
    byte[] MediaKey32,
    byte[] FileSha256,
    byte[] FileEncSha256,
    ulong FileLength,
    byte[]? StreamingSidecar);

/// <summary>
/// Unified HKDF-SHA256 + AES-256-CBC + HMAC-SHA256 cipher for WhatsApp MMS encrypted payloads
/// (History Sync chunks, AppState external blobs, Stickers, Images, Video, Audio, Documents),
/// including streaming sidecars and MMS CDN URL formatting verified against live <c>web.whatsapp.com</c>.
/// </summary>
public static class MediaCipher
{
    public const int MediaMacSize = 10;
    public const int AesBlockSize = 16;
    public const int MediaKeyLength = 32;
    public const int ExpandedKeyLength = 112;
    public const int SidecarChunkSize = 64 * 1024;
    public const int SidecarOverlapSize = 16;

    public static string ToMmsType(this WaMediaType mediaType) => mediaType switch
    {
        WaMediaType.Image => "image",
        WaMediaType.Sticker => "sticker",
        WaMediaType.Video => "video",
        WaMediaType.Gif => "gif",
        WaMediaType.Audio => "audio",
        WaMediaType.Ptt => "ptt",
        WaMediaType.Document => "document",
        WaMediaType.History => "md-msg-hist",
        WaMediaType.GroupHistory => "group-history",
        WaMediaType.AppState => "md-app-state",
        WaMediaType.StickerPack => "sticker-pack",
        WaMediaType.StickerPackThumbnail => "thumbnail-sticker-pack",
        WaMediaType.LinkThumbnail => "thumbnail-link",
        WaMediaType.ProductCatalogImage => "product-catalog-image",
        WaMediaType.MusicArtwork => "music-artwork",
        WaMediaType.NewsletterMusicArtwork => "newsletter-music-artwork",
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, "Unsupported WaMediaType")
    };

    public static string GetUploadPath(this WaMediaType mediaType) => mediaType switch
    {
        WaMediaType.Image or WaMediaType.Sticker => "/mms/image",
        WaMediaType.Video or WaMediaType.Gif => "/mms/video",
        WaMediaType.Audio or WaMediaType.Ptt => "/mms/audio",
        WaMediaType.Document => "/mms/document",
        WaMediaType.History => "/mms/md-msg-hist",
        WaMediaType.GroupHistory => "/mms/group-history",
        WaMediaType.AppState => "/mms/md-app-state",
        WaMediaType.StickerPack => "/mms/sticker-pack",
        WaMediaType.StickerPackThumbnail => "/mms/thumbnail-sticker-pack",
        WaMediaType.LinkThumbnail => "/mms/thumbnail-link",
        WaMediaType.ProductCatalogImage => "/product/image",
        WaMediaType.MusicArtwork => "/mms/music-artwork",
        WaMediaType.NewsletterMusicArtwork => "/mms/newsletter-music-artwork",
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, "Unsupported WaMediaType")
    };

    public static bool IsEncrypted(this WaMediaType mediaType) =>
        mediaType is not (WaMediaType.ProductCatalogImage or WaMediaType.NewsletterMusicArtwork);

    public static bool UsesSidecar(this WaMediaType mediaType) =>
        mediaType is WaMediaType.Audio or WaMediaType.Ptt or WaMediaType.Video or WaMediaType.Gif;

    public static string ToUrlSafeBase64NoPad(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static Uri BuildMmsUploadUrl(
        string hostname,
        WaMediaType mediaType,
        ReadOnlySpan<byte> fileEncSha256,
        string auth,
        ReadOnlySpan<byte> tokenBytes = default,
        bool resumeCheck = false,
        long? byteStart = null,
        long? byteEnd = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(auth);

        string hashB64 = ToUrlSafeBase64NoPad(fileEncSha256);
        string tokB64 = tokenBytes.IsEmpty ? hashB64 : ToUrlSafeBase64NoPad(tokenBytes);
        var sb = new StringBuilder();
        sb.Append("https://").Append(hostname.Trim()).Append(mediaType.GetUploadPath()).Append('/').Append(hashB64);
        sb.Append("?auth=").Append(Uri.EscapeDataString(auth));
        sb.Append("&token=").Append(Uri.EscapeDataString(tokB64));
        if (resumeCheck)
        {
            sb.Append("&resume=1");
        }
        if (byteStart is long start)
        {
            sb.Append("&bytestart=").Append(start);
        }
        if (byteEnd is long end)
        {
            sb.Append("&byteend=").Append(end);
        }
        return new Uri(sb.ToString(), UriKind.Absolute);
    }

    public static Uri BuildMmsDownloadUrl(
        string hostname,
        string directPath,
        string? auth = null,
        WaMediaType? mediaType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directPath);
        if (directPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(directPath, UriKind.Absolute);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        string cleanHost = hostname.Trim();
        string path = directPath.StartsWith('/') ? directPath : "/" + directPath;
        var sb = new StringBuilder();
        sb.Append("https://").Append(cleanHost).Append(path);
        char sep = path.Contains('?') ? '&' : '?';

        if (!string.IsNullOrEmpty(auth) && !path.Contains("auth=", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(sep).Append("auth=").Append(Uri.EscapeDataString(auth));
            sep = '&';
        }
        if (mediaType is WaMediaType mt && !path.Contains("mms-type=", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(sep).Append("mms-type=").Append(Uri.EscapeDataString(mt.ToMmsType()));
        }
        return new Uri(sb.ToString(), UriKind.Absolute);
    }

    public static MediaUploadEncryptionResult EncryptForUpload(
        ReadOnlySpan<byte> plaintext,
        WaMediaType mediaType,
        byte[]? mediaKeyOverride = null,
        bool? generateSidecar = null)
    {
        byte[] plainHash = SHA256.HashData(plaintext);
        if (!mediaType.IsEncrypted())
        {
            return new MediaUploadEncryptionResult(
                plaintext.ToArray(),
                Array.Empty<byte>(),
                plainHash,
                plainHash,
                (ulong)plaintext.Length,
                null);
        }

        byte[] mediaKey = mediaKeyOverride is { Length: MediaKeyLength }
            ? mediaKeyOverride.ToArray()
            : RandomNumberGenerator.GetBytes(MediaKeyLength);

        var (iv, _, macKey) = DeriveMediaKeys(mediaKey, mediaType);
        byte[] encBlob = EncryptBlob(plaintext, mediaKey, mediaType);
        byte[] encHash = SHA256.HashData(encBlob);

        bool wantSidecar = generateSidecar ?? mediaType.UsesSidecar();
        byte[]? sidecar = wantSidecar ? ComputeStreamingSidecar(encBlob, iv, macKey) : null;

        return new MediaUploadEncryptionResult(
            encBlob,
            mediaKey,
            plainHash,
            encHash,
            (ulong)plaintext.Length,
            sidecar);
    }

    public static byte[] ComputeStreamingSidecar(ReadOnlySpan<byte> encBlob, ReadOnlySpan<byte> iv16, ReadOnlySpan<byte> macKey32)
    {
        if (iv16.Length != SidecarOverlapSize || macKey32.Length != 32)
        {
            throw new ArgumentException("Sidecar requires 16-byte IV and 32-byte MAC key.");
        }

        byte[] macKey = macKey32.ToArray();
        Span<byte> overlap = stackalloc byte[SidecarOverlapSize];
        iv16.CopyTo(overlap);

        using var outMs = new MemoryStream();
        int offset = 0;
        while (offset < encBlob.Length)
        {
            int take = Math.Min(SidecarChunkSize, encBlob.Length - offset);
            ReadOnlySpan<byte> chunk = encBlob.Slice(offset, take);

            using (var hmac = new HMACSHA256(macKey))
            {
                hmac.TransformBlock(overlap.ToArray(), 0, SidecarOverlapSize, null, 0);
                hmac.TransformFinalBlock(chunk.ToArray(), 0, chunk.Length);
                outMs.Write(hmac.Hash!, 0, MediaMacSize);
            }

            if (take >= SidecarOverlapSize)
            {
                chunk[(take - SidecarOverlapSize)..].CopyTo(overlap);
            }
            else
            {
                int shift = SidecarOverlapSize - take;
                overlap[take..].CopyTo(overlap[..shift]);
                chunk.CopyTo(overlap[shift..]);
            }

            offset += take;
        }

        return outMs.ToArray();
    }

    public static byte[] DecryptPlaintext(ReadOnlySpan<byte> rawBytes, ReadOnlySpan<byte> expectedPlainSha256 = default)
    {
        if (!expectedPlainSha256.IsEmpty)
        {
            byte[] actual = SHA256.HashData(rawBytes);
            if (!CryptographicOperations.FixedTimeEquals(actual, expectedPlainSha256))
            {
                throw new CryptographicException("Plaintext media SHA-256 hash mismatch");
            }
        }
        return rawBytes.ToArray();
    }

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
