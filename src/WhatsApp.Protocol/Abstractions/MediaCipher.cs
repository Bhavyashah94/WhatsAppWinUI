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
    NewsletterImage,
    NewsletterVideo,
    NewsletterAudio,
    NewsletterPtt,
    NewsletterDocument,
    NewsletterSticker,
    NewsletterGif,
    NewsletterLinkThumbnail,
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

public sealed record MediaStreamEncryptionResult(
    byte[] MediaKey32,
    byte[] FileSha256,
    byte[] FileEncSha256,
    ulong FileLength,
    ulong EncryptedLength,
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
        WaMediaType.NewsletterImage => "newsletter-image",
        WaMediaType.NewsletterVideo => "newsletter-video",
        WaMediaType.NewsletterAudio => "newsletter-audio",
        WaMediaType.NewsletterPtt => "newsletter-ptt",
        WaMediaType.NewsletterDocument => "newsletter-document",
        WaMediaType.NewsletterSticker => "newsletter-sticker",
        WaMediaType.NewsletterGif => "newsletter-gif",
        WaMediaType.NewsletterLinkThumbnail => "newsletter-thumbnail-link",
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
        WaMediaType.NewsletterImage or WaMediaType.NewsletterSticker => "/mms/newsletter-image",
        WaMediaType.NewsletterVideo or WaMediaType.NewsletterGif => "/mms/newsletter-video",
        WaMediaType.NewsletterAudio or WaMediaType.NewsletterPtt => "/mms/newsletter-audio",
        WaMediaType.NewsletterDocument => "/mms/newsletter-document",
        WaMediaType.NewsletterLinkThumbnail => "/mms/newsletter-thumbnail-link",
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, "Unsupported WaMediaType")
    };

    public static bool IsEncrypted(this WaMediaType mediaType) =>
        mediaType is not (
            WaMediaType.ProductCatalogImage
            or WaMediaType.NewsletterMusicArtwork
            or WaMediaType.NewsletterImage
            or WaMediaType.NewsletterVideo
            or WaMediaType.NewsletterAudio
            or WaMediaType.NewsletterPtt
            or WaMediaType.NewsletterDocument
            or WaMediaType.NewsletterSticker
            or WaMediaType.NewsletterGif
            or WaMediaType.NewsletterLinkThumbnail);

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

    public static async Task<MediaStreamEncryptionResult> EncryptForUploadAsync(
        Stream plaintextInput,
        Stream encryptedOutput,
        WaMediaType mediaType,
        byte[]? mediaKeyOverride = null,
        bool? generateSidecar = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintextInput);
        ArgumentNullException.ThrowIfNull(encryptedOutput);

        const int ioBufferSize = 65536;
        using var plainSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (!mediaType.IsEncrypted())
        {
            byte[] rawBuf = new byte[ioBufferSize];
            ulong rawLen = 0;
            int rawRead;
            while ((rawRead = await plaintextInput.ReadAsync(rawBuf, ct).ConfigureAwait(false)) > 0)
            {
                plainSha.AppendData(rawBuf, 0, rawRead);
                await encryptedOutput.WriteAsync(rawBuf.AsMemory(0, rawRead), ct).ConfigureAwait(false);
                rawLen += (ulong)rawRead;
            }

            byte[] rawHash = plainSha.GetHashAndReset();
            return new MediaStreamEncryptionResult(Array.Empty<byte>(), rawHash, rawHash, rawLen, rawLen, null);
        }

        byte[] mediaKey = mediaKeyOverride is { Length: MediaKeyLength }
            ? mediaKeyOverride.ToArray()
            : RandomNumberGenerator.GetBytes(MediaKeyLength);

        var (iv, cipherKey, macKey) = DeriveMediaKeys(mediaKey, mediaType);
        bool wantSidecar = generateSidecar ?? mediaType.UsesSidecar();
        var sidecar = wantSidecar ? new StreamingSidecarAccumulator(iv, macKey) : null;

        using var encSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);
        hmac.AppendData(iv);

        using var aes = Aes.Create();
        aes.Key = cipherKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using ICryptoTransform encryptor = aes.CreateEncryptor();

        byte[] plainBuf = new byte[AesBlockSize + ioBufferSize];
        byte[] cipherBuf = new byte[AesBlockSize + ioBufferSize];
        int pendingPlain = 0;
        ulong plainLen = 0;
        ulong encLen = 0;

        while (true)
        {
            int read = await plaintextInput.ReadAsync(plainBuf.AsMemory(pendingPlain, ioBufferSize), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            plainSha.AppendData(plainBuf, pendingPlain, read);
            plainLen += (ulong)read;
            int total = pendingPlain + read;
            int aligned = (total / AesBlockSize) * AesBlockSize;
            int rem = total - aligned;

            if (aligned > 0)
            {
                int cipherWritten = encryptor.TransformBlock(plainBuf, 0, aligned, cipherBuf, 0);
                if (cipherWritten > 0)
                {
                    await encryptedOutput.WriteAsync(cipherBuf.AsMemory(0, cipherWritten), ct).ConfigureAwait(false);
                    encSha.AppendData(cipherBuf, 0, cipherWritten);
                    hmac.AppendData(cipherBuf, 0, cipherWritten);
                    sidecar?.Append(cipherBuf.AsSpan(0, cipherWritten));
                    encLen += (ulong)cipherWritten;
                }
            }

            if (rem > 0)
            {
                Buffer.BlockCopy(plainBuf, aligned, plainBuf, 0, rem);
            }
            pendingPlain = rem;
        }

        byte[] finalCipher = encryptor.TransformFinalBlock(plainBuf, 0, pendingPlain);
        if (finalCipher.Length > 0)
        {
            await encryptedOutput.WriteAsync(finalCipher, ct).ConfigureAwait(false);
            encSha.AppendData(finalCipher);
            hmac.AppendData(finalCipher);
            sidecar?.Append(finalCipher);
            encLen += (ulong)finalCipher.Length;
        }

        byte[] fullMac = hmac.GetHashAndReset();
        byte[] mac10 = fullMac[..MediaMacSize];
        await encryptedOutput.WriteAsync(mac10, ct).ConfigureAwait(false);
        encSha.AppendData(mac10);
        sidecar?.Append(mac10);
        encLen += MediaMacSize;

        return new MediaStreamEncryptionResult(
            mediaKey,
            plainSha.GetHashAndReset(),
            encSha.GetHashAndReset(),
            plainLen,
            encLen,
            sidecar?.Finish());
    }

    public static async Task DecryptStreamAsync(
        Stream encryptedInput,
        Stream plaintextOutput,
        ReadOnlyMemory<byte> mediaKey,
        WaMediaType mediaType,
        ReadOnlyMemory<byte> expectedEncSha256 = default,
        ReadOnlyMemory<byte> expectedPlainSha256 = default,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(encryptedInput);
        ArgumentNullException.ThrowIfNull(plaintextOutput);

        if (!mediaType.IsEncrypted())
        {
            await DecryptPlaintextStreamAsync(encryptedInput, plaintextOutput, expectedPlainSha256, ct).ConfigureAwait(false);
            return;
        }

        const int ioBufferSize = 65536;
        const int keepTail = MediaMacSize + AesBlockSize;
        var (iv, cipherKey, macKey) = DeriveMediaKeys(mediaKey.Span, mediaType);

        using var encSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var plainSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, macKey);
        hmac.AppendData(iv);

        using var aes = Aes.Create();
        aes.Key = cipherKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using ICryptoTransform decryptor = aes.CreateDecryptor();

        byte[] inBuf = new byte[keepTail + ioBufferSize];
        byte[] plainBuf = new byte[keepTail + ioBufferSize];
        int buffered = 0;

        while (true)
        {
            int read = await encryptedInput.ReadAsync(inBuf.AsMemory(buffered, ioBufferSize), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffered += read;
            if (buffered > keepTail)
            {
                int safeCipher = ((buffered - MediaMacSize - 1) / AesBlockSize) * AesBlockSize;
                if (safeCipher > 0)
                {
                    encSha.AppendData(inBuf, 0, safeCipher);
                    hmac.AppendData(inBuf, 0, safeCipher);
                    int plainWritten = decryptor.TransformBlock(inBuf, 0, safeCipher, plainBuf, 0);
                    if (plainWritten > 0)
                    {
                        plainSha.AppendData(plainBuf, 0, plainWritten);
                        await plaintextOutput.WriteAsync(plainBuf.AsMemory(0, plainWritten), ct).ConfigureAwait(false);
                    }
                    int rem = buffered - safeCipher;
                    Buffer.BlockCopy(inBuf, safeCipher, inBuf, 0, rem);
                    buffered = rem;
                }
            }
        }

        if (buffered < AesBlockSize + MediaMacSize)
        {
            throw new ArgumentException($"Encrypted blob is too short: {buffered} trailing bytes", nameof(encryptedInput));
        }

        int finalCipherLen = buffered - MediaMacSize;
        if (finalCipherLen <= 0 || finalCipherLen % AesBlockSize != 0)
        {
            throw new CryptographicException("Ciphertext length must be a non-zero multiple of AES block size");
        }

        encSha.AppendData(inBuf, 0, buffered);
        if (!expectedEncSha256.IsEmpty)
        {
            byte[] actualEncHash = encSha.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualEncHash, expectedEncSha256.Span))
            {
                throw new CryptographicException("Encrypted blob SHA-256 hash mismatch");
            }
        }

        hmac.AppendData(inBuf, 0, finalCipherLen);
        byte[] computedMac = hmac.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(computedMac.AsSpan(0, MediaMacSize), inBuf.AsSpan(finalCipherLen, MediaMacSize)))
        {
            throw new CryptographicException("Media blob HMAC verification failed");
        }

        byte[] finalPlain = decryptor.TransformFinalBlock(inBuf, 0, finalCipherLen);
        if (finalPlain.Length > 0)
        {
            plainSha.AppendData(finalPlain);
            await plaintextOutput.WriteAsync(finalPlain, ct).ConfigureAwait(false);
        }

        if (!expectedPlainSha256.IsEmpty)
        {
            byte[] actualPlainHash = plainSha.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualPlainHash, expectedPlainSha256.Span))
            {
                throw new CryptographicException("Decrypted plaintext SHA-256 hash mismatch");
            }
        }
    }

    private static async Task DecryptPlaintextStreamAsync(
        Stream rawInput,
        Stream plaintextOutput,
        ReadOnlyMemory<byte> expectedPlainSha256,
        CancellationToken ct)
    {
        using var plainSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buf = new byte[65536];
        int read;
        while ((read = await rawInput.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            plainSha.AppendData(buf, 0, read);
            await plaintextOutput.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        if (!expectedPlainSha256.IsEmpty)
        {
            byte[] actual = plainSha.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actual, expectedPlainSha256.Span))
            {
                throw new CryptographicException("Plaintext media SHA-256 hash mismatch");
            }
        }
    }

    private sealed class StreamingSidecarAccumulator
    {
        private readonly byte[] _macKey;
        private readonly byte[] _overlap = new byte[SidecarOverlapSize];
        private readonly byte[] _chunk = new byte[SidecarChunkSize];
        private int _chunkLen;
        private readonly MemoryStream _out = new();

        public StreamingSidecarAccumulator(ReadOnlySpan<byte> iv16, ReadOnlySpan<byte> macKey32)
        {
            iv16.CopyTo(_overlap);
            _macKey = macKey32.ToArray();
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            int pos = 0;
            while (pos < data.Length)
            {
                int copy = Math.Min(SidecarChunkSize - _chunkLen, data.Length - pos);
                data.Slice(pos, copy).CopyTo(_chunk.AsSpan(_chunkLen));
                _chunkLen += copy;
                pos += copy;

                if (_chunkLen == SidecarChunkSize)
                {
                    EmitChunk(_chunkLen);
                    _chunkLen = 0;
                }
            }
        }

        public byte[] Finish()
        {
            if (_chunkLen > 0)
            {
                EmitChunk(_chunkLen);
                _chunkLen = 0;
            }
            return _out.ToArray();
        }

        private void EmitChunk(int len)
        {
            using var hmac = new HMACSHA256(_macKey);
            hmac.TransformBlock(_overlap, 0, SidecarOverlapSize, null, 0);
            hmac.TransformFinalBlock(_chunk, 0, len);
            _out.Write(hmac.Hash!, 0, MediaMacSize);

            if (len >= SidecarOverlapSize)
            {
                Buffer.BlockCopy(_chunk, len - SidecarOverlapSize, _overlap, 0, SidecarOverlapSize);
            }
            else
            {
                int shift = SidecarOverlapSize - len;
                Buffer.BlockCopy(_overlap, len, _overlap, 0, shift);
                Buffer.BlockCopy(_chunk, 0, _overlap, shift, len);
            }
        }
    }
}
