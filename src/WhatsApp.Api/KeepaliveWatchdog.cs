using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Api;

public sealed class KeepaliveOptions
{
    public TimeSpan IntervalMin { get; init; } = ConnectionHealth.KeepAliveIntervalMin;
    public TimeSpan IntervalMax { get; init; } = ConnectionHealth.KeepAliveIntervalMax;
    public TimeSpan ResponseDeadline { get; init; } = ConnectionHealth.KeepAliveResponseDeadline;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// Monotonic keepalive ping loop (<c>xmlns="w:p"</c>) and dead-socket watchdog scoped to a single
/// connection generation.
/// </summary>
public sealed class KeepaliveWatchdog : IAsyncDisposable
{
    private readonly long _generation;
    private readonly Func<CancellationToken, Task<Node>> _sendPingAsync;
    private readonly Func<long, Task> _onReconnectRequestedAsync;
    private readonly Func<bool> _isConnected;
    private readonly KeepaliveOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    private long _lastDataReceivedTicks;
    private long _firstSendSinceRecvTicks;
    private int _disposed;

    public KeepaliveWatchdog(
        long generation,
        Func<CancellationToken, Task<Node>> sendPingAsync,
        Func<long, Task> onReconnectRequestedAsync,
        Func<bool> isConnected,
        KeepaliveOptions? options = null)
    {
        _generation = generation;
        _sendPingAsync = sendPingAsync ?? throw new ArgumentNullException(nameof(sendPingAsync));
        _onReconnectRequestedAsync = onReconnectRequestedAsync ?? throw new ArgumentNullException(nameof(onReconnectRequestedAsync));
        _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
        _options = options ?? new KeepaliveOptions();

        long now = _options.TimeProvider.GetTimestamp();
        Volatile.Write(ref _lastDataReceivedTicks, now);
        Volatile.Write(ref _firstSendSinceRecvTicks, 0);

        _loopTask = Task.Run(RunLoopAsync);
    }

    public KeepaliveWatchdog(
        long generation,
        IqClient iqClient,
        Func<long, Task> onReconnectRequestedAsync,
        Func<bool> isConnected,
        KeepaliveOptions? options = null)
        : this(
            generation,
            ct =>
            {
                string id = iqClient.NextRequestId();
                return iqClient.SendIqAsync(PingIq.BuildPing(id), ConnectionHealth.KeepAliveResponseDeadline, ct);
            },
            onReconnectRequestedAsync,
            isConnected,
            options)
    {
        ArgumentNullException.ThrowIfNull(iqClient);
    }

    public void RecordDataReceived()
    {
        long now = _options.TimeProvider.GetTimestamp();
        Volatile.Write(ref _lastDataReceivedTicks, now);
        Volatile.Write(ref _firstSendSinceRecvTicks, 0);
    }

    public void RecordDataSent()
    {
        long now = _options.TimeProvider.GetTimestamp();
        Interlocked.CompareExchange(ref _firstSendSinceRecvTicks, now, 0);
    }

    private async Task RunLoopAsync()
    {
        var health = new ConnectionHealth();
        CancellationToken ct = _cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TimeSpan interval = PickInterval(_options.IntervalMin, _options.IntervalMax);
                await Task.Delay(interval, _options.TimeProvider, ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested || !_isConnected())
                {
                    return;
                }

                long now = _options.TimeProvider.GetTimestamp();
                long? lastRecv = ReadNullableTicks(ref _lastDataReceivedTicks);
                long? firstSend = ReadNullableTicks(ref _firstSendSinceRecvTicks);

                HealthAction preAction = health.BeforeProbe(_options.TimeProvider, now, lastRecv, firstSend);
                if (preAction == HealthAction.Skip)
                {
                    continue;
                }
                if (preAction == HealthAction.Reconnect)
                {
                    await _onReconnectRequestedAsync(_generation).ConfigureAwait(false);
                    return;
                }
                if (preAction == HealthAction.Stop)
                {
                    return;
                }

                long? recvBefore = ReadNullableTicks(ref _lastDataReceivedTicks);
                KeepaliveProbeOutcome outcome = await ProbeAsync(ct).ConfigureAwait(false);

                long afterNow = _options.TimeProvider.GetTimestamp();
                long? recvAfter = ReadNullableTicks(ref _lastDataReceivedTicks);
                long? firstSendAfter = ReadNullableTicks(ref _firstSendSinceRecvTicks);

                HealthAction postAction = health.ProbeCompleted(
                    outcome,
                    _options.TimeProvider,
                    afterNow,
                    recvBefore,
                    recvAfter,
                    firstSendAfter);

                if (postAction == HealthAction.Stop)
                {
                    return;
                }
                if (postAction == HealthAction.Reconnect)
                {
                    await _onReconnectRequestedAsync(_generation).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task<KeepaliveProbeOutcome> ProbeAsync(CancellationToken ct)
    {
        RecordDataSent();
        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(_options.ResponseDeadline);
            Node response = await _sendPingAsync(deadlineCts.Token).ConfigureAwait(false);
            if (response.Tag == "iq" && string.Equals(response.GetAttr("type"), "result", StringComparison.Ordinal))
            {
                RecordDataReceived();
                return KeepaliveProbeOutcome.Ok;
            }
            return KeepaliveProbeOutcome.TransientFailure;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return KeepaliveProbeOutcome.FatalFailure;
        }
        catch (TimeoutException)
        {
            return KeepaliveProbeOutcome.TransientFailure;
        }
        catch (OperationCanceledException)
        {
            // Local ResponseDeadline expired.
            return KeepaliveProbeOutcome.TransientFailure;
        }
        catch (IqException ex) when (ex.ErrorCode is not null)
        {
            // Server replied with an IQ error: socket is alive, treat as transient health failure.
            RecordDataReceived();
            return KeepaliveProbeOutcome.TransientFailure;
        }
        catch (IqException)
        {
            // Socket/cancellation transport teardown inside IqClient.
            return KeepaliveProbeOutcome.FatalFailure;
        }
        catch (ObjectDisposedException)
        {
            return KeepaliveProbeOutcome.FatalFailure;
        }
        catch (Exception) when (!_isConnected())
        {
            return KeepaliveProbeOutcome.FatalFailure;
        }
        catch
        {
            return KeepaliveProbeOutcome.TransientFailure;
        }
    }

    private static long? ReadNullableTicks(ref long location)
    {
        long val = Volatile.Read(ref location);
        return val > 0 ? val : null;
    }

    private static TimeSpan PickInterval(TimeSpan min, TimeSpan max)
    {
        if (max <= min)
        {
            return min;
        }
        long minMs = Math.Max(1, (long)min.TotalMilliseconds);
        long maxMs = Math.Max(minMs, (long)max.TotalMilliseconds);
        long chosen = minMs == maxMs ? minMs : Random.Shared.NextInt64(minMs, maxMs + 1);
        return TimeSpan.FromMilliseconds(chosen);
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
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}
