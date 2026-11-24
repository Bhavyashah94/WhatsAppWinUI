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
    Task<byte[]> GetBytesAsync(Uri url, long maxBytes = 64 * 1024 * 1024, CancellationToken ct = default);
    Task<byte[]> PostBytesAsync(Uri url, ReadOnlyMemory<byte> body, string contentType = "application/octet-stream", CancellationToken ct = default);
}
