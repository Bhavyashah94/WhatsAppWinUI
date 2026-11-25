using WhatsApp.Protocol.Binary;

namespace WhatsApp.Api;

/// <summary>
/// Pluggable handlers invoked by <see cref="StanzaRouter"/> for inbound WhatsApp binary nodes.
/// </summary>
public sealed class StanzaHandlers
{
    public Func<Node, CancellationToken, ValueTask>? OnIqRequest { get; init; }
    public Func<Node, CancellationToken, ValueTask>? OnIb { get; init; }
    public Func<Node, CancellationToken, ValueTask>? OnSuccess { get; init; }
    public Func<Node, CancellationToken, ValueTask>? OnFailure { get; init; }
    public Func<Node, CancellationToken, ValueTask>? OnStreamError { get; init; }
    public Func<Node, CancellationToken, ValueTask>? OnAck { get; init; }
    public Func<Node, CancellationToken, ValueTask>? OnReceipt { get; init; }
    public Func<Node, CancellationToken, ValueTask>? OnNotification { get; init; }
    public Func<Node, CancellationToken, ValueTask>? OnMessage { get; init; }
    public Func<Node, CancellationToken, ValueTask>? OnUnhandled { get; init; }
    public Action<long, Exception?>? OnClosed { get; init; }
}

/// <summary>
/// Generation-scoped inbound stanza dispatcher reading from <see cref="NoiseSessionSocket.InboundNodes"/>.
/// Routes IQ responses to <see cref="IqClient"/> before invoking handler callbacks and cancels
/// all in-flight IQs when the session stream closes.
/// </summary>
public sealed class StanzaRouter : IAsyncDisposable
{
    private readonly NoiseSessionSocket _socket;
    private readonly IqClient _iqClient;
    private readonly StanzaHandlers _handlers;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;
    private int _disposed;

    public StanzaRouter(NoiseSessionSocket socket, IqClient iqClient, StanzaHandlers? handlers = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _handlers = handlers ?? new StanzaHandlers();

        _pumpTask = Task.Run(PumpLoopAsync);
    }

    public long Generation => _socket.Generation;
    public Task Completion => _pumpTask;

    private async Task PumpLoopAsync()
    {
        Exception? fault = null;
        try
        {
            await foreach (Node node in _socket.InboundNodes.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await DispatchAsync(node, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Clean router shutdown.
        }
        catch (Exception ex)
        {
            fault = ex;
        }
        finally
        {
            _iqClient.CancelAll(fault ?? new OperationCanceledException($"StanzaRouter generation {Generation} closed."));
            _handlers.OnClosed?.Invoke(Generation, fault);
        }
    }

    internal async ValueTask DispatchAsync(Node node, CancellationToken ct)
    {
        switch (node.Tag)
        {
            case "iq":
                if (_iqClient.TryResolve(node))
                {
                    return;
                }
                if (_handlers.OnIqRequest is { } onIq)
                {
                    await onIq(node, ct).ConfigureAwait(false);
                    return;
                }
                break;

            case "ib":
                if (_handlers.OnIb is { } onIb)
                {
                    await onIb(node, ct).ConfigureAwait(false);
                    return;
                }
                break;

            case "success":
                if (_handlers.OnSuccess is { } onSuccess)
                {
                    await onSuccess(node, ct).ConfigureAwait(false);
                    return;
                }
                break;

            case "failure":
                if (_handlers.OnFailure is { } onFailure)
                {
                    await onFailure(node, ct).ConfigureAwait(false);
                    return;
                }
                break;

            case "stream:error":
                if (_handlers.OnStreamError is { } onStreamError)
                {
                    await onStreamError(node, ct).ConfigureAwait(false);
                    return;
                }
                break;

            case "ack":
                if (_handlers.OnAck is { } onAck)
                {
                    await onAck(node, ct).ConfigureAwait(false);
                    return;
                }
                break;

            case "receipt":
                if (_handlers.OnReceipt is { } onReceipt)
                {
                    await onReceipt(node, ct).ConfigureAwait(false);
                    return;
                }
                break;

            case "notification":
                if (_handlers.OnNotification is { } onNotification)
                {
                    await onNotification(node, ct).ConfigureAwait(false);
                    return;
                }
                break;

            case "message":
                if (_handlers.OnMessage is { } onMessage)
                {
                    await onMessage(node, ct).ConfigureAwait(false);
                    return;
                }
                break;
        }

        if (_handlers.OnUnhandled is { } onUnhandled)
        {
            await onUnhandled(node, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}
