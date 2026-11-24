using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using WhatsApp.Protocol.Abstractions;

namespace WhatsApp.Transport;

public enum TransportDisconnectKind
{
    None = 0,
    LocalDisconnect = 1,
    ServerClose = 2,
    StreamEnded = 3,
    ReadError = 4
}

public sealed record TransportDisconnectReason(
    TransportDisconnectKind Kind,
    WebSocketCloseStatus? CloseStatus = null,
    string? Description = null,
    string? ErrorMessage = null);

/// <summary>
/// Pooled <see cref="Pipe"/>-backed duplex WebSocket transport matching <c>tokio-transport</c> semantics.
/// Serializes outbound WebSocket writes, pumps binary RFC 6455 fragments into <see cref="Reader"/>,
/// and records structured disconnect reasons.
/// </summary>
public sealed class WebSocketTransport : ITransport
{
    private const int DefaultMinimumReadSize = 4096;
    private const int DefaultPauseWriterThreshold = 512 * 1024;
    private const int DefaultResumeWriterThreshold = 256 * 1024;

    private readonly WebSocket _ws;
    private readonly Pipe _pipe;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pumpTask;
    private int _closed;
    private int _disposed;
    private TransportDisconnectReason? _disconnectReason;

    internal WebSocketTransport(WebSocket ws, PipeOptions? pipeOptions = null)
    {
        _ws = ws ?? throw new ArgumentNullException(nameof(ws));
        _pipe = new Pipe(pipeOptions ?? new PipeOptions(
            pauseWriterThreshold: DefaultPauseWriterThreshold,
            resumeWriterThreshold: DefaultResumeWriterThreshold,
            useSynchronizationContext: false));
        _pumpTask = Task.Run(PumpReceiveLoopAsync);
    }

    public bool IsConnected =>
        Volatile.Read(ref _closed) == 0
        && Volatile.Read(ref _disposed) == 0
        && _ws.State == WebSocketState.Open;

    public TransportDisconnectReason? DisconnectReason => Volatile.Read(ref _disconnectReason);

    /// <summary>
    /// Exposes the underlying <see cref="PipeReader"/> fed by the WebSocket receive pump.
    /// </summary>
    public PipeReader Reader => _pipe.Reader;

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _closed) != 0 || _ws.State != WebSocketState.Open)
        {
            throw new WebSocketException(WebSocketError.InvalidState, "WebSocket transport is closed.");
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _closed) != 0 || _ws.State != WebSocketState.Open)
            {
                throw new WebSocketException(WebSocketError.InvalidState, "WebSocket transport is closed.");
            }

            await _ws.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            ReadResult result = await _pipe.Reader.ReadAsync(ct).ConfigureAwait(false);
            ReadOnlySequence<byte> readable = result.Buffer;

            if (!readable.IsEmpty)
            {
                int toCopy = (int)Math.Min(buffer.Length, readable.Length);
                ReadOnlySequence<byte> slice = readable.Slice(0, toCopy);
                slice.CopyTo(buffer.Span);
                _pipe.Reader.AdvanceTo(slice.End);
                return toCopy;
            }

            _pipe.Reader.AdvanceTo(readable.End);
            if (result.IsCompleted)
            {
                return 0;
            }
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        Interlocked.CompareExchange(
            ref _disconnectReason,
            new TransportDisconnectReason(TransportDisconnectKind.LocalDisconnect),
            null);

        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            closeCts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await _sendLock.WaitAsync(closeCts.Token).ConfigureAwait(false);
                try
                {
                    if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch
            {
                // Best-effort close handshake during disconnect.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _closed, 1);
        await _pumpCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore expected cancellation or socket teardown exceptions on dispose.
        }

        // Wait briefly for any active SendAsync holding _sendLock before disposing the socket/semaphore.
        await _sendLock.WaitAsync().ConfigureAwait(false);
        _sendLock.Release();

        await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
        await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
        _pumpCts.Dispose();
        _sendLock.Dispose();
        _ws.Dispose();
    }

    private async Task PumpReceiveLoopAsync()
    {
        Exception? completionError = null;
        try
        {
            while (!_pumpCts.IsCancellationRequested && _ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                Memory<byte> memory = _pipe.Writer.GetMemory(DefaultMinimumReadSize);
                ValueWebSocketReceiveResult result = await _ws.ReceiveAsync(memory, _pumpCts.Token).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Interlocked.Exchange(ref _closed, 1);
                    Interlocked.CompareExchange(
                        ref _disconnectReason,
                        new TransportDisconnectReason(
                            TransportDisconnectKind.ServerClose,
                            _ws.CloseStatus,
                            _ws.CloseStatusDescription),
                        null);
                    break;
                }

                // Ignore non-binary frames cleanly so stray text frames never corrupt the Noise stream.
                if (result.MessageType != WebSocketMessageType.Binary || result.Count <= 0)
                {
                    continue;
                }

                _pipe.Writer.Advance(result.Count);
                FlushResult flush = await _pipe.Writer.FlushAsync(_pumpCts.Token).ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled)
                {
                    break;
                }
            }

            if (_disconnectReason is null && !_pumpCts.IsCancellationRequested)
            {
                Interlocked.CompareExchange(
                    ref _disconnectReason,
                    new TransportDisconnectReason(TransportDisconnectKind.StreamEnded),
                    null);
            }
        }
        catch (OperationCanceledException) when (_pumpCts.IsCancellationRequested)
        {
            Interlocked.CompareExchange(
                ref _disconnectReason,
                new TransportDisconnectReason(TransportDisconnectKind.LocalDisconnect),
                null);
        }
        catch (WebSocketException ex) when (Volatile.Read(ref _closed) != 0 || ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            Interlocked.Exchange(ref _closed, 1);
            Interlocked.CompareExchange(
                ref _disconnectReason,
                new TransportDisconnectReason(TransportDisconnectKind.StreamEnded, ErrorMessage: ex.Message),
                null);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _closed, 1);
            Interlocked.CompareExchange(
                ref _disconnectReason,
                new TransportDisconnectReason(TransportDisconnectKind.ReadError, ErrorMessage: ex.Message),
                null);
            completionError = ex;
        }
        finally
        {
            await _pipe.Writer.CompleteAsync(completionError).ConfigureAwait(false);
        }
    }
}

public sealed class WebSocketTransportFactory : ITransportFactory
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36";

    private readonly Func<Uri, CancellationToken, Task<ITransport>> _dialer;
    private readonly TimeSpan _staggerDelay;

    public WebSocketTransportFactory()
        : this(DialClientWebSocketAsync, TimeSpan.Zero)
    {
    }

    internal WebSocketTransportFactory(
        Func<Uri, CancellationToken, Task<ITransport>> dialer,
        TimeSpan staggerDelay = default)
    {
        _dialer = dialer ?? throw new ArgumentNullException(nameof(dialer));
        _staggerDelay = staggerDelay < TimeSpan.Zero ? TimeSpan.Zero : staggerDelay;
    }

    public async Task<ITransport> ConnectAsync(IReadOnlyList<Uri> endpoints, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Count == 0)
        {
            throw new ArgumentException("At least one endpoint URI is required.", nameof(endpoints));
        }

        var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var primaryFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new List<Task<ITransport>>(endpoints.Count);

        for (int i = 0; i < endpoints.Count; i++)
        {
            Uri uri = endpoints[i];
            int index = i;
            tasks.Add(DialWithStaggerAsync(uri, index, primaryFailed, raceCts.Token));
        }

        var errors = new List<Exception>();
        while (tasks.Count > 0)
        {
            Task<ITransport> finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);
            try
            {
                ITransport winner = await finished.ConfigureAwait(false);
                await raceCts.CancelAsync().ConfigureAwait(false);
                _ = CleanupLosersAsync(tasks, raceCts);
                return winner;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await raceCts.CancelAsync().ConfigureAwait(false);
                _ = CleanupLosersAsync(tasks, raceCts);
                throw;
            }
            catch (Exception ex)
            {
                primaryFailed.TrySetResult();
                errors.Add(ex);
            }
        }

        raceCts.Dispose();
        if (errors.Count == 1)
        {
            throw errors[0];
        }
        throw new AggregateException("Failed to connect to any WhatsApp endpoint.", errors);
    }

    private async Task<ITransport> DialWithStaggerAsync(
        Uri uri,
        int index,
        TaskCompletionSource primaryFailed,
        CancellationToken ct)
    {
        if (index > 0 && _staggerDelay > TimeSpan.Zero)
        {
            TimeSpan delay = TimeSpan.FromTicks(_staggerDelay.Ticks * index);
            await Task.WhenAny(Task.Delay(delay, ct), primaryFailed.Task).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        return await _dialer(uri, ct).ConfigureAwait(false);
    }

    private static async Task CleanupLosersAsync(
        List<Task<ITransport>> remainingTasks,
        CancellationTokenSource raceCts)
    {
        try
        {
            foreach (Task<ITransport> task in remainingTasks)
            {
                try
                {
                    ITransport loser = await task.ConfigureAwait(false);
                    await loser.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Suppress expected cancellation or dial errors from losing endpoints.
                }
            }
        }
        finally
        {
            raceCts.Dispose();
        }
    }

    private static async Task<ITransport> DialClientWebSocketAsync(Uri uri, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", WhatsAppEndpointBuilder.DefaultOrigin);
        ws.Options.SetRequestHeader("User-Agent", UserAgent);
        try
        {
            await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            return new WebSocketTransport(ws);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }
}
