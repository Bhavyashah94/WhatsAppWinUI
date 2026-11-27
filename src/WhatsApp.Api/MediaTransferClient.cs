using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Api;

public sealed record UploadedMediaResult(
    string Url,
    string DirectPath,
    byte[] MediaKey32,
    byte[] FileSha256,
    byte[] FileEncSha256,
    ulong FileLength,
    string? Handle,
    byte[]? StreamingSidecar,
    long UploadedFromOffset = 0);

/// <summary>
/// Multi-host MMS CDN client handling <c>w:m</c> <c>media_conn</c> caching &amp; token refresh,
/// encrypted &amp; plaintext uploads (with <c>&gt;= 1 MiB</c> <c>?resume=1</c> offset probing),
/// and verified downloads across primary/fallback CDN origins.
/// </summary>
public sealed class MediaTransferClient
{
    public const int ResumableMinBytes = 1_048_576; // 1 MiB verified against WAWebMmsClientUploadRetryResume

    private readonly IqClient _iqClient;
    private readonly IHttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _connLock = new(1, 1);
    private MediaConnInfo? _cachedMediaConn;

    public MediaTransferClient(
        IqClient iqClient,
        IHttpClient httpClient,
        TimeProvider? timeProvider = null)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<MediaConnInfo> GetMediaConnAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _connLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cachedMediaConn is { } existing && !existing.IsExpired(_timeProvider))
            {
                return existing;
            }

            string iqId = _iqClient.NextRequestId();
            Node resp = await _iqClient.SendIqAsync(MediaConnIq.BuildGetMediaConn(iqId), IqClient.DefaultTimeout, ct)
                .ConfigureAwait(false);

            MediaConnInfo parsed = MediaConnParser.ParseResponse(resp, _timeProvider);
            _cachedMediaConn = parsed;
            return parsed;
        }
        finally
        {
            _connLock.Release();
        }
    }

    public async Task<UploadedMediaResult> UploadAsync(
        ReadOnlyMemory<byte> plaintext,
        WaMediaType mediaType,
        bool checkResumeIfLarge = true,
        bool? generateSidecar = null,
        CancellationToken ct = default)
    {
        MediaUploadEncryptionResult enc = MediaCipher.EncryptForUpload(
            plaintext.Span,
            mediaType,
            mediaKeyOverride: null,
            generateSidecar: generateSidecar);

        return await ExecuteUploadWithFailoverAsync(
            mediaType,
            enc.MediaKey32,
            enc.FileSha256,
            enc.FileEncSha256,
            enc.FileLength,
            enc.StreamingSidecar,
            enc.DataToUpload.LongLength,
            checkResumeIfLarge,
            (uploadUrl, resumeOffset, token) =>
            {
                ReadOnlyMemory<byte> body = resumeOffset > 0
                    ? enc.DataToUpload.AsMemory((int)resumeOffset)
                    : enc.DataToUpload;
                return _httpClient.PostBytesAsync(uploadUrl, body, "application/octet-stream", token);
            },
            ct).ConfigureAwait(false);
    }

    public Task<UploadedMediaResult> UploadFileAsync(
        string filePath,
        WaMediaType mediaType,
        bool checkResumeIfLarge = true,
        bool? generateSidecar = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return UploadFileInternalAsync(filePath, mediaType, checkResumeIfLarge, generateSidecar, ct);
    }

    private async Task<UploadedMediaResult> UploadFileInternalAsync(
        string filePath,
        WaMediaType mediaType,
        bool checkResumeIfLarge,
        bool? generateSidecar,
        CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return await UploadFromStreamAsync(fs, mediaType, checkResumeIfLarge, generateSidecar, ct).ConfigureAwait(false);
    }

    public async Task<UploadedMediaResult> UploadFromStreamAsync(
        Stream plaintextStream,
        WaMediaType mediaType,
        bool checkResumeIfLarge = true,
        bool? generateSidecar = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintextStream);

        string tempEncPath = Path.Combine(Path.GetTempPath(), $"wa-up-{Guid.NewGuid():N}.enc");
        try
        {
            MediaStreamEncryptionResult enc;
            await using (var encWriteFs = new FileStream(tempEncPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                enc = await MediaCipher.EncryptForUploadAsync(
                    plaintextStream,
                    encWriteFs,
                    mediaType,
                    mediaKeyOverride: null,
                    generateSidecar: generateSidecar,
                    ct: ct).ConfigureAwait(false);
            }

            long encTotalLen = checked((long)enc.EncryptedLength);
            return await ExecuteUploadWithFailoverAsync(
                mediaType,
                enc.MediaKey32,
                enc.FileSha256,
                enc.FileEncSha256,
                enc.FileLength,
                enc.StreamingSidecar,
                encTotalLen,
                checkResumeIfLarge,
                async (uploadUrl, resumeOffset, token) =>
                {
                    await using var encReadFs = new FileStream(tempEncPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                    if (resumeOffset > 0)
                    {
                        encReadFs.Seek(resumeOffset, SeekOrigin.Begin);
                    }

                    return await _httpClient.PostStreamAsync(
                        uploadUrl,
                        encReadFs,
                        contentLength: encTotalLen - resumeOffset,
                        contentType: "application/octet-stream",
                        ct: token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(tempEncPath)) File.Delete(tempEncPath); } catch { }
        }
    }

    private async Task<UploadedMediaResult> ExecuteUploadWithFailoverAsync(
        WaMediaType mediaType,
        byte[] mediaKey32,
        byte[] fileSha256,
        byte[] fileEncSha256,
        ulong fileLength,
        byte[]? streamingSidecar,
        long encTotalLen,
        bool checkResumeIfLarge,
        Func<Uri, long, CancellationToken, Task<byte[]>> sendUploadAsync,
        CancellationToken ct)
    {
        Exception? lastError = null;
        for (int authAttempt = 0; authAttempt < 2; authAttempt++)
        {
            MediaConnInfo mediaConn = await GetMediaConnAsync(forceRefresh: authAttempt > 0, ct).ConfigureAwait(false);
            string[] hosts = mediaConn.GetOrderedUploadHosts()
                .DefaultIfEmpty(SyncdCoordinator.DefaultMediaHost)
                .ToArray();

            bool retryWithFreshAuth = false;
            foreach (string host in hosts)
            {
                if (authAttempt == 0 && mediaConn.IsExpired(_timeProvider))
                {
                    retryWithFreshAuth = true;
                    break;
                }

                try
                {
                    long resumeOffset = 0;
                    if (checkResumeIfLarge && encTotalLen >= ResumableMinBytes)
                    {
                        (long offset, (string Url, string DirectPath, string? Handle)? complete) =
                            await TryProbeResumeAsync(host, mediaType, fileEncSha256, mediaConn.Auth, fileEncSha256, ct)
                                .ConfigureAwait(false);
                        if (complete is { } alreadyDone)
                        {
                            return new UploadedMediaResult(
                                alreadyDone.Url,
                                alreadyDone.DirectPath,
                                mediaKey32,
                                fileSha256,
                                fileEncSha256,
                                fileLength,
                                alreadyDone.Handle,
                                streamingSidecar,
                                encTotalLen);
                        }
                        resumeOffset = Math.Clamp(offset, 0, encTotalLen);
                    }

                    Uri uploadUrl = MediaCipher.BuildMmsUploadUrl(
                        host,
                        mediaType,
                        fileEncSha256,
                        mediaConn.Auth,
                        fileEncSha256,
                        resumeCheck: false,
                        byteStart: resumeOffset > 0 ? resumeOffset : null,
                        byteEnd: resumeOffset > 0 ? encTotalLen - 1L : null);

                    byte[] respBytes = await sendUploadAsync(uploadUrl, resumeOffset, ct).ConfigureAwait(false);
                    (string url, string directPath, string? handle) = ParseUploadJsonResponse(respBytes);
                    return new UploadedMediaResult(
                        url,
                        directPath,
                        mediaKey32,
                        fileSha256,
                        fileEncSha256,
                        fileLength,
                        handle,
                        streamingSidecar,
                        resumeOffset);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (IsNonRetriableUploadError(ex))
                    {
                        throw new IOException($"MMS upload rejected by CDN host '{host}' (non-retriable): {ex.Message}", ex);
                    }
                    if (IsMediaAuthError(ex))
                    {
                        lastError = ex;
                        if (authAttempt == 0)
                        {
                            retryWithFreshAuth = true;
                            break;
                        }
                        throw new IOException($"MMS upload failed auth after media_conn refresh on '{host}': {ex.Message}", ex);
                    }
                    lastError = ex;
                }
            }

            if (!retryWithFreshAuth)
            {
                break;
            }
        }

        throw new IOException($"MMS upload failed across all CDN hosts: {lastError?.Message}", lastError);
    }

    private static bool IsMediaAuthError(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };

    private static bool IsNonRetriableUploadError(Exception ex)
    {
        if (ex is HttpRequestException hre && hre.StatusCode is HttpStatusCode status)
        {
            if (status is HttpStatusCode.RequestEntityTooLarge
                or HttpStatusCode.BadRequest
                or HttpStatusCode.UnsupportedMediaType
                or HttpStatusCode.UnprocessableEntity)
            {
                return true;
            }
        }

        return ex.Message.Contains("\"retriable\":false", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<byte[]> DownloadAsync(
        string directPath,
        WaMediaType mediaType,
        ReadOnlyMemory<byte> mediaKey = default,
        ReadOnlyMemory<byte> expectedFileSha256 = default,
        ReadOnlyMemory<byte> expectedFileEncSha256 = default,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directPath);

        if (directPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            byte[] directBytes = await _httpClient.GetBytesAsync(
                new Uri(directPath, UriKind.Absolute),
                maxBytes: IHttpClient.DefaultMaxStreamDownloadBytes,
                ct: ct).ConfigureAwait(false);
            return DecodeDownloadedBytes(directBytes, mediaType, mediaKey.Span, expectedFileSha256.Span, expectedFileEncSha256.Span);
        }

        MediaConnInfo mediaConn = await GetMediaConnAsync(forceRefresh: false, ct).ConfigureAwait(false);
        Exception? lastError = null;

        foreach (bool forceRefresh in new[] { false, true })
        {
            if (forceRefresh)
            {
                mediaConn = await GetMediaConnAsync(forceRefresh: true, ct).ConfigureAwait(false);
            }

            string[] hosts = mediaConn.GetOrderedHosts()
                .DefaultIfEmpty(SyncdCoordinator.DefaultMediaHost)
                .ToArray();

            foreach (string host in hosts)
            {
                try
                {
                    Uri url = MediaCipher.BuildMmsDownloadUrl(host, directPath, mediaConn.Auth, mediaType);
                    byte[] raw = await _httpClient.GetBytesAsync(
                        url,
                        maxBytes: IHttpClient.DefaultMaxStreamDownloadBytes,
                        ct: ct).ConfigureAwait(false);
                    return DecodeDownloadedBytes(raw, mediaType, mediaKey.Span, expectedFileSha256.Span, expectedFileEncSha256.Span);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                }
            }
        }

        throw new IOException($"MMS download failed for '{directPath}': {lastError?.Message}", lastError);
    }

    public async Task DownloadToStreamAsync(
        string directPath,
        Stream destination,
        WaMediaType mediaType,
        ReadOnlyMemory<byte> mediaKey = default,
        ReadOnlyMemory<byte> expectedFileSha256 = default,
        ReadOnlyMemory<byte> expectedFileEncSha256 = default,
        long maxBytes = IHttpClient.DefaultMaxStreamDownloadBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directPath);
        ArgumentNullException.ThrowIfNull(destination);

        string tempRawPath = Path.Combine(Path.GetTempPath(), $"wa-dl-{Guid.NewGuid():N}.enc");
        try
        {
            if (directPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await using (var rawWrite = new FileStream(tempRawPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    await _httpClient.GetToStreamAsync(new Uri(directPath, UriKind.Absolute), rawWrite, maxBytes, ct).ConfigureAwait(false);
                }
                await using var rawRead = new FileStream(tempRawPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                await MediaCipher.DecryptStreamAsync(rawRead, destination, mediaKey, mediaType, expectedFileEncSha256, expectedFileSha256, ct).ConfigureAwait(false);
                return;
            }

            MediaConnInfo mediaConn = await GetMediaConnAsync(forceRefresh: false, ct).ConfigureAwait(false);
            Exception? lastError = null;

            foreach (bool forceRefresh in new[] { false, true })
            {
                if (forceRefresh)
                {
                    mediaConn = await GetMediaConnAsync(forceRefresh: true, ct).ConfigureAwait(false);
                }

                string[] hosts = mediaConn.GetOrderedHosts()
                    .DefaultIfEmpty(SyncdCoordinator.DefaultMediaHost)
                    .ToArray();

                foreach (string host in hosts)
                {
                    try
                    {
                        Uri url = MediaCipher.BuildMmsDownloadUrl(host, directPath, mediaConn.Auth, mediaType);
                        await using (var rawWrite = new FileStream(tempRawPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                        {
                            await _httpClient.GetToStreamAsync(url, rawWrite, maxBytes, ct).ConfigureAwait(false);
                        }
                        await using var rawRead = new FileStream(tempRawPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                        await MediaCipher.DecryptStreamAsync(rawRead, destination, mediaKey, mediaType, expectedFileEncSha256, expectedFileSha256, ct).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        lastError = ex;
                    }
                }
            }

            throw new IOException($"MMS streaming download failed for '{directPath}': {lastError?.Message}", lastError);
        }
        finally
        {
            try { if (File.Exists(tempRawPath)) File.Delete(tempRawPath); } catch { }
        }
    }

    public async Task DownloadMessageMediaToFileAsync(
        WhatsApp.Protocol.HistorySync.MessageRecord record,
        string outputPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (string.IsNullOrWhiteSpace(record.DirectPath))
        {
            throw new InvalidOperationException($"Message '{record.Id}' has no DirectPath.");
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempOut = $"{outputPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            WaMediaType mediaType = ResolveMediaTypeForRecord(record);
            await using (var outFs = new FileStream(tempOut, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await DownloadToStreamAsync(
                    record.DirectPath,
                    outFs,
                    mediaType,
                    record.MediaKey ?? ReadOnlyMemory<byte>.Empty,
                    record.FileHash ?? ReadOnlyMemory<byte>.Empty,
                    record.FileEncHash ?? ReadOnlyMemory<byte>.Empty,
                    ct: ct).ConfigureAwait(false);
            }
            File.Move(tempOut, outputPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { }
            throw;
        }
    }

    public Task<byte[]> DownloadMessageMediaAsync(
        WhatsApp.Protocol.HistorySync.MessageRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.DirectPath))
        {
            throw new InvalidOperationException($"Message '{record.Id}' has no DirectPath.");
        }

        WaMediaType mediaType = ResolveMediaTypeForRecord(record);

        return DownloadAsync(
            record.DirectPath,
            mediaType,
            record.MediaKey ?? ReadOnlyMemory<byte>.Empty,
            record.FileHash ?? ReadOnlyMemory<byte>.Empty,
            record.FileEncHash ?? ReadOnlyMemory<byte>.Empty,
            ct);
    }

    private static WaMediaType ResolveMediaTypeForRecord(WhatsApp.Protocol.HistorySync.MessageRecord record)
    {
        bool isPlaintextNewsletter =
            record.RemoteJid.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase)
            || ((record.MediaKey is null || record.MediaKey.Length == 0)
                && !string.IsNullOrEmpty(record.DirectPath)
                && record.DirectPath.Contains("/newsletter", StringComparison.OrdinalIgnoreCase));

        return isPlaintextNewsletter
            ? record.Type switch
            {
                WhatsApp.Protocol.HistorySync.MessageType.Image => WaMediaType.NewsletterImage,
                WhatsApp.Protocol.HistorySync.MessageType.Sticker => WaMediaType.NewsletterSticker,
                WhatsApp.Protocol.HistorySync.MessageType.Video => WaMediaType.NewsletterVideo,
                WhatsApp.Protocol.HistorySync.MessageType.Audio => WaMediaType.NewsletterAudio,
                _ => WaMediaType.NewsletterDocument
            }
            : record.Type switch
            {
                WhatsApp.Protocol.HistorySync.MessageType.Image => WaMediaType.Image,
                WhatsApp.Protocol.HistorySync.MessageType.Sticker => WaMediaType.Sticker,
                WhatsApp.Protocol.HistorySync.MessageType.Video => WaMediaType.Video,
                WhatsApp.Protocol.HistorySync.MessageType.Audio => WaMediaType.Audio,
                _ => WaMediaType.Document
            };
    }

    private async Task<(long Offset, (string Url, string DirectPath, string? Handle)? Complete)> TryProbeResumeAsync(
        string host,
        WaMediaType mediaType,
        byte[] encHash,
        string auth,
        byte[] tokenBytes,
        CancellationToken ct)
    {
        try
        {
            Uri resumeUrl = MediaCipher.BuildMmsUploadUrl(host, mediaType, encHash, auth, tokenBytes, resumeCheck: true);
            byte[] jsonBytes = await _httpClient.PostBytesAsync(
                resumeUrl,
                ReadOnlyMemory<byte>.Empty,
                "application/octet-stream",
                ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(jsonBytes);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("resume", out JsonElement el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long off))
                {
                    return (off, null);
                }
                if (el.ValueKind == JsonValueKind.String)
                {
                    string? str = el.GetString();
                    if (string.Equals(str, "complete", StringComparison.OrdinalIgnoreCase))
                    {
                        string url = root.TryGetProperty("url", out JsonElement urlEl) ? (urlEl.GetString() ?? string.Empty) : string.Empty;
                        string dp = root.TryGetProperty("direct_path", out JsonElement dpEl) ? (dpEl.GetString() ?? string.Empty) : string.Empty;
                        string? handle = root.TryGetProperty("handle", out JsonElement hEl) ? hEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(dp) || Uri.TryCreate(url, UriKind.Absolute, out _))
                        {
                            if (string.IsNullOrWhiteSpace(dp) && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
                            {
                                dp = parsed.PathAndQuery;
                            }
                            return (0, (url, dp, handle));
                        }
                    }
                    else if (long.TryParse(str, out long parsedOff))
                    {
                        return (parsedOff, null);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: resume probe miss falls back to full offset 0 POST.
        }
        return (0, null);
    }

    private static byte[] DecodeDownloadedBytes(
        byte[] raw,
        WaMediaType mediaType,
        ReadOnlySpan<byte> mediaKey,
        ReadOnlySpan<byte> expectedPlainSha256,
        ReadOnlySpan<byte> expectedEncSha256) =>
        mediaType.IsEncrypted()
            ? MediaCipher.DecryptBlob(raw, mediaKey, mediaType, expectedEncSha256, expectedPlainSha256)
            : MediaCipher.DecryptPlaintext(raw, expectedPlainSha256);

    internal static (string Url, string DirectPath, string? Handle) ParseUploadJsonResponse(byte[] jsonBytes)
    {
        using JsonDocument doc = JsonDocument.Parse(jsonBytes);
        JsonElement root = doc.RootElement;
        string url = root.TryGetProperty("url", out JsonElement urlEl) ? (urlEl.GetString() ?? string.Empty) : string.Empty;
        string directPath = root.TryGetProperty("direct_path", out JsonElement dpEl) ? (dpEl.GetString() ?? string.Empty) : string.Empty;
        string? handle = root.TryGetProperty("handle", out JsonElement hEl) ? hEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(directPath) && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            directPath = parsed.PathAndQuery;
        }
        if (string.IsNullOrWhiteSpace(directPath))
        {
            throw new InvalidDataException("MMS upload response JSON missing 'direct_path' and 'url'.");
        }
        return (url, directPath, handle);
    }
}
