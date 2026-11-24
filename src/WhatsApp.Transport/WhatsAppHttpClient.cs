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
            });
            _ownsClient = true;
        }
    }

    public async Task<byte[]> GetBytesAsync(Uri url, long maxBytes = 64 * 1024 * 1024, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        if (resp.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidOperationException($"HTTP response Content-Length ({resp.Content.Headers.ContentLength}) exceeds maxBytes ({maxBytes}).");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[16384];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException($"HTTP response stream exceeded maxBytes ({maxBytes}).");
            }
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    public async Task<byte[]> PostBytesAsync(
        Uri url,
        ReadOnlyMemory<byte> body,
        string contentType = "application/octet-stream",
        CancellationToken ct = default)
    {
        using var content = new ReadOnlyMemoryContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
