namespace WhatsApp.Protocol.Abstractions;

/// <summary>
/// Raw binary duplex transport abstraction (WebSocket or in-memory test pipe).
/// Knows nothing about Noise, Protobuf, or SQLite.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    bool IsConnected { get; }
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
}

/// <summary>
/// Creates and races connections (supports Happy Eyeballs multi-endpoint dial).
/// </summary>
public interface ITransportFactory
{
    Task<ITransport> ConnectAsync(IReadOnlyList<Uri> endpoints, CancellationToken ct = default);
}

/// <summary>
/// Pure HTTP client abstraction for encrypted MMS CDN downloads/uploads and MEX GraphQL calls.
/// </summary>
public interface IHttpClient
{
    public const long DefaultMaxGetBytes = 64L * 1024 * 1024;
    public const long DefaultMaxStreamDownloadBytes = 2L * 1024 * 1024 * 1024 + 64L * 1024 * 1024;

    Task<byte[]> GetBytesAsync(Uri url, long maxBytes = DefaultMaxGetBytes, CancellationToken ct = default);
    Task<byte[]> PostBytesAsync(Uri url, ReadOnlyMemory<byte> body, string contentType = "application/octet-stream", CancellationToken ct = default);

    async Task GetToStreamAsync(Uri url, Stream destination, long maxBytes = DefaultMaxStreamDownloadBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        byte[] bytes = await GetBytesAsync(url, maxBytes, ct).ConfigureAwait(false);
        await destination.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    async Task<byte[]> PostStreamAsync(
        Uri url,
        Stream body,
        long? contentLength = null,
        string contentType = "application/octet-stream",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var ms = contentLength is > 0 and <= int.MaxValue ? new MemoryStream((int)contentLength) : new MemoryStream();
        await body.CopyToAsync(ms, ct).ConfigureAwait(false);
        return await PostBytesAsync(url, ms.ToArray(), contentType, ct).ConfigureAwait(false);
    }
}
