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

        byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
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
                    long resumeOffset = 0;
                    if (checkResumeIfLarge && enc.DataToUpload.Length >= ResumableMinBytes)
                    {
                        resumeOffset = await TryProbeResumeOffsetAsync(host, mediaType, enc.FileEncSha256, mediaConn.Auth, tokenBytes, ct)
                            .ConfigureAwait(false);
                        resumeOffset = Math.Clamp(resumeOffset, 0, enc.DataToUpload.Length);
                    }

                    ReadOnlyMemory<byte> body = resumeOffset > 0
                        ? enc.DataToUpload.AsMemory((int)resumeOffset)
                        : enc.DataToUpload;

                    Uri uploadUrl = MediaCipher.BuildMmsUploadUrl(
                        host,
                        mediaType,
                        enc.FileEncSha256,
                        mediaConn.Auth,
                        tokenBytes,
                        resumeCheck: false,
                        byteStart: resumeOffset > 0 ? resumeOffset : null,
                        byteEnd: resumeOffset > 0 ? enc.DataToUpload.Length - 1L : null);

                    byte[] respBytes = await _httpClient.PostBytesAsync(uploadUrl, body, "application/octet-stream", ct)
                        .ConfigureAwait(false);

                    (string url, string directPath, string? handle) = ParseUploadJsonResponse(respBytes);
                    return new UploadedMediaResult(
                        url,
                        directPath,
                        enc.MediaKey32,
                        enc.FileSha256,
                        enc.FileEncSha256,
                        enc.FileLength,
                        handle,
                        enc.StreamingSidecar,
                        resumeOffset);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                }
            }
        }

        throw new IOException($"MMS upload failed across all CDN hosts: {lastError?.Message}", lastError);
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
            byte[] directBytes = await _httpClient.GetBytesAsync(new Uri(directPath, UriKind.Absolute), ct: ct)
                .ConfigureAwait(false);
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
                    byte[] raw = await _httpClient.GetBytesAsync(url, ct: ct).ConfigureAwait(false);
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

    public Task<byte[]> DownloadMessageMediaAsync(
        WhatsApp.Protocol.HistorySync.MessageRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.DirectPath))
        {
            throw new InvalidOperationException($"Message '{record.Id}' has no DirectPath.");
        }

        WaMediaType mediaType = record.Type switch
        {
            WhatsApp.Protocol.HistorySync.MessageType.Image => WaMediaType.Image,
            WhatsApp.Protocol.HistorySync.MessageType.Sticker => WaMediaType.Sticker,
            WhatsApp.Protocol.HistorySync.MessageType.Video => WaMediaType.Video,
            WhatsApp.Protocol.HistorySync.MessageType.Audio => WaMediaType.Audio,
            _ => WaMediaType.Document
        };

        return DownloadAsync(
            record.DirectPath,
            mediaType,
            record.MediaKey ?? ReadOnlyMemory<byte>.Empty,
            record.FileHash ?? ReadOnlyMemory<byte>.Empty,
            record.FileEncHash ?? ReadOnlyMemory<byte>.Empty,
            ct);
    }

    private async Task<long> TryProbeResumeOffsetAsync(
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
            byte[] jsonBytes = await _httpClient.GetBytesAsync(resumeUrl, maxBytes: 64 * 1024, ct: ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(jsonBytes);
            if (doc.RootElement.TryGetProperty("resume", out JsonElement el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long off))
                {
                    return off;
                }
                if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out long parsedOff))
                {
                    return parsedOff;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: resume probe miss falls back to full offset 0 POST.
        }
        return 0;
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
