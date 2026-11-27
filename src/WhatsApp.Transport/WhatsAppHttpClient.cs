using System.Net.Http.Headers;
using WhatsApp.Protocol.Abstractions;

namespace WhatsApp.Transport;

/// <summary>
/// Pure pooled HTTP transport for MMS CDN blobs and MEX calls (zero crypto/proto dependencies).
/// </summary>
public sealed class WhatsAppHttpClient : IHttpClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public WhatsAppHttpClient(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _http = httpClient;
            _ownsClient = false;
        }
        else
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = System.Net.DecompressionMethods.None
            })
            {
                Timeout = TimeSpan.FromMinutes(60)
            };
            _ownsClient = true;
        }
    }

    public async Task<byte[]> GetBytesAsync(Uri url, long maxBytes = IHttpClient.DefaultMaxGetBytes, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await GetToStreamAsync(url, ms, maxBytes, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    public async Task GetToStreamAsync(
        Uri url,
        Stream destination,
        long maxBytes = IHttpClient.DefaultMaxStreamDownloadBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(resp, ct).ConfigureAwait(false);

        if (resp.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidOperationException($"HTTP response Content-Length ({resp.Content.Headers.ContentLength}) exceeds maxBytes ({maxBytes}).");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException($"HTTP response stream exceeded maxBytes ({maxBytes}).");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    public async Task<byte[]> PostBytesAsync(
        Uri url,
        ReadOnlyMemory<byte> body,
        string contentType = "application/octet-stream",
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Origin", "https://web.whatsapp.com");
        using var content = new ReadOnlyMemoryContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        req.Content = content;
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<byte[]> PostStreamAsync(
        Uri url,
        Stream body,
        long? contentLength = null,
        string contentType = "application/octet-stream",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Origin", "https://web.whatsapp.com");
        using var content = new NonDisposingStreamContent(body, 65536);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (contentLength is long len and >= 0)
        {
            content.Headers.ContentLength = len;
        }
        req.Content = content;
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(resp, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        string bodySnippet = string.Empty;
        try
        {
            string raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                bodySnippet = raw.Length > 2048 ? raw[..2048] : raw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ignore secondary failures reading error payload.
        }

        string message = string.IsNullOrEmpty(bodySnippet)
            ? $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})"
            : $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}) body={bodySnippet}";
        throw new HttpRequestException(message, null, resp.StatusCode);
    }

    private sealed class NonDisposingStreamContent(Stream inner, int bufferSize) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            inner.CopyToAsync(stream, bufferSize);

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken) =>
            inner.CopyToAsync(stream, bufferSize, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            if (Headers.ContentLength is long explicitLen)
            {
                length = explicitLen;
                return true;
            }
            if (inner.CanSeek)
            {
                length = inner.Length - inner.Position;
                return true;
            }
            length = 0;
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
