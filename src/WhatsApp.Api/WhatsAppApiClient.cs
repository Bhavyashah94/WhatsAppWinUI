using System.Threading.Channels;
using WhatsApp.Protocol.Abstractions;

namespace WhatsApp.Api;

/// <summary>
/// High-level headless WhatsApp Multi-Device API runtime host.
/// Orchestrates connection generations, Noise/Signal sessions, sync engines, and events with zero UI dependencies.
/// </summary>
public sealed class WhatsAppApiClient : IAsyncDisposable
{
    private readonly IWhatsAppBackend _backend;
    private readonly ITransportFactory _transportFactory;
    private readonly IHttpClient _httpClient;
    private readonly Channel<WhatsAppEvent> _events = Channel.CreateUnbounded<WhatsAppEvent>();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private NoiseSessionSocket? _activeSession;
    private long _generation;

    public WhatsAppApiClient(
        IWhatsAppBackend backend,
        ITransportFactory transportFactory,
        IHttpClient httpClient)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public ChannelReader<WhatsAppEvent> Events => _events.Reader;
    public long CurrentGeneration => Volatile.Read(ref _generation);
    public NoiseSessionSocket? ActiveSession => Volatile.Read(ref _activeSession);

    /// <summary>
    /// Opens a transport connection, completes the Noise handshake, installs a new <see cref="NoiseSessionSocket"/>
    /// with a bumped <see cref="CurrentGeneration"/>, and disposes any previous active session.
    /// </summary>
    internal async Task<NoiseSessionSocket> OpenSessionAsync(
        IReadOnlyList<Uri> endpoints,
        DeviceCredentials credentials,
        NoiseHandshakeOptions? handshakeOptions = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(credentials);

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_activeSession is { } previous)
            {
                _activeSession = null;
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            ITransport transport = await _transportFactory.ConnectAsync(endpoints, ct).ConfigureAwait(false);
            try
            {
                NoiseHandshakeResult handshake = await NoiseHandshakeRunner
                    .RunAsync(transport, credentials, handshakeOptions, ct)
                    .ConfigureAwait(false);

                long generation = Interlocked.Increment(ref _generation);
                var session = new NoiseSessionSocket(
                    generation,
                    transport,
                    handshake.Keys,
                    handshake.RemainingWireBytes);
                _activeSession = session;
                return session;
            }
            catch
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Disconnects the current active session, or if <paramref name="expectedGeneration"/> is provided,
    /// acts only when the active session matches that generation (ignoring stale reconnect/disconnect callbacks).
    /// </summary>
    public async Task<bool> DisconnectSessionAsync(long? expectedGeneration = null)
    {
        await _sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            NoiseSessionSocket? session = _activeSession;
            if (session is null)
            {
                return false;
            }

            if (expectedGeneration is long expected && session.Generation != expected)
            {
                return false;
            }

            _activeSession = null;
            await session.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectSessionAsync().ConfigureAwait(false);
        _sessionLock.Dispose();
        _events.Writer.TryComplete();
        await _backend.DisposeAsync().ConfigureAwait(false);
    }
}
