using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Api;

public sealed class OfflineBatchOptions
{
    public int BatchSize { get; init; } = OfflineBatchIb.DefaultBatchSize;
    public TimeSpan RequestDebounce { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// Client-paced offline backlog controller matching live <c>WAWebOfflineHandler</c>
/// and <c>whatsapp-rust/src/client/offline_resume.rs</c>.
/// Sends <c>&lt;ib&gt;&lt;offline_batch count="200"/&gt;&lt;/ib&gt;</c> after receiving
/// <c>&lt;ib&gt;&lt;offline_preview count="N"/&gt;&lt;/ib&gt;</c> and drives subsequent
/// batches until <c>&lt;ib&gt;&lt;offline/&gt;&lt;/ib&gt;</c> completes or the 60s inactivity watchdog fires.
/// </summary>
public sealed class OfflineBatchCoordinator : IAsyncDisposable
{
    private readonly long _generation;
    private readonly Func<Node, CancellationToken, ValueTask> _sendNodeAsync;
    private readonly Func<WhatsAppEvent, ValueTask> _emitEventAsync;
    private readonly OfflineBatchOptions _options;

    private readonly object _gate = new();
    private readonly TaskCompletionSource _offlineCompletedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _disposeCts = new();

    private bool _armed;
    private bool _completed;
    private bool _prevBatchInflight;
    private int _unbooked;
    private int _outstanding;
    private int _processedCount;
    private int _admittedCount;
    private bool _deferred;
    private long _stanzaActivity;
    private int _disposed;

    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _watchdogCts;

    public OfflineBatchCoordinator(
        long generation,
        Func<Node, CancellationToken, ValueTask> sendNodeAsync,
        Func<WhatsAppEvent, ValueTask> emitEventAsync,
        OfflineBatchOptions? options = null)
    {
        _generation = generation;
        _sendNodeAsync = sendNodeAsync ?? throw new ArgumentNullException(nameof(sendNodeAsync));
        _emitEventAsync = emitEventAsync ?? throw new ArgumentNullException(nameof(emitEventAsync));
        _options = options ?? new OfflineBatchOptions();
        if (_options.BatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BatchSize must be positive.");
        }
    }

    public long Generation => _generation;

    public bool IsArmed
    {
        get { lock (_gate) return _armed; }
    }

    public bool IsCompleted
    {
        get { lock (_gate) return _completed; }
    }

    public Task Completion => _offlineCompletedTcs.Task;

    /// <summary>
    /// Arms offline batch pacing when <c>&lt;ib&gt;&lt;offline_preview count="N"/&gt;</c> arrives.
    /// When <paramref name="preview"/>.Total &gt; 0, emits the initial <c>offline_batch</c> stanza.
    /// </summary>
    public async Task OnOfflinePreviewAsync(OfflinePreviewInfo preview, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        await _emitEventAsync(new OfflineSyncPreviewEvent(
            preview.Total,
            preview.AppDataChanges,
            preview.Messages,
            preview.Notifications,
            preview.Receipts,
            preview.Calls,
            preview.Statuses)).ConfigureAwait(false);

        if (preview.Total <= 0)
        {
            return;
        }

        CancellationTokenSource? watchdogToStart = null;
        lock (_gate)
        {
            if (_completed || _disposeCts.IsCancellationRequested)
            {
                return;
            }

            // Book the first batch immediately; WAWebOfflineHandler waits for first arrival
            // of that batch before issuing the debounced continuation request.
            int firstBook = Math.Min(_options.BatchSize, preview.Total);
            _unbooked = Math.Max(0, preview.Total - firstBook);
            _outstanding = firstBook;
            _prevBatchInflight = true;
            _armed = true;
            _stanzaActivity++;

            _watchdogCts?.Cancel();
            _watchdogCts?.Dispose();
            _watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            watchdogToStart = _watchdogCts;
        }

        if (watchdogToStart is not null)
        {
            _ = RunInactivityWatchdogAsync(watchdogToStart.Token);
        }

        await _sendNodeAsync(OfflineBatchIb.BuildOfflineBatch(_options.BatchSize), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Notifies the coordinator that an offline stanza (<c>offline="..."</c>) arrived on the wire.
    /// Releases a caller guard whose <see cref="Dispose"/> (or <see cref="CompleteStanza"/>) marks
    /// local lane processing finished.
    /// </summary>
    public OfflineStanzaLease? TrackOfflineStanza(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.GetAttr("offline") is null)
        {
            return null;
        }

        bool sendNow = false;
        bool scheduleDebounce = false;

        lock (_gate)
        {
            if (!_armed || _completed || _disposeCts.IsCancellationRequested)
            {
                return null;
            }

            _stanzaActivity++;
            _admittedCount++;

            if (_prevBatchInflight && _unbooked > 0)
            {
                _prevBatchInflight = false;
                if (_outstanding <= _options.BatchSize)
                {
                    if (_options.RequestDebounce <= TimeSpan.Zero)
                    {
                        sendNow = TryReserveNextBatchLocked();
                    }
                    else
                    {
                        scheduleDebounce = true;
                    }
                }
                else
                {
                    _deferred = true;
                }
            }
        }

        if (sendNow)
        {
            _ = DispatchNextBatchSafeAsync();
        }
        else if (scheduleDebounce)
        {
            ScheduleDebouncedBatchRequest();
        }

        return new OfflineStanzaLease(this);
    }

    internal void ReleaseStanzaLease()
    {
        bool sendNow = false;

        lock (_gate)
        {
            if (_admittedCount > 0)
            {
                _admittedCount--;
            }
            if (_outstanding > 0)
            {
                _outstanding--;
            }
            _processedCount++;
            _stanzaActivity++;

            if (_armed && !_completed && _deferred && _outstanding <= _options.BatchSize && _unbooked > 0)
            {
                _deferred = false;
                sendNow = TryReserveNextBatchLocked();
            }
        }

        if (sendNow)
        {
            _ = DispatchNextBatchSafeAsync();
        }
    }

    /// <summary>
    /// Handles <c>&lt;ib&gt;&lt;offline count="..."/&gt;</c> completion from the server.
    /// </summary>
    public async Task OnOfflineCompletedAsync(int count)
    {
        await CompleteInternalAsync(count, timedOut: false).ConfigureAwait(false);
    }

    private void ScheduleDebouncedBatchRequest()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (!_armed || _completed || _disposeCts.IsCancellationRequested)
            {
                return;
            }

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            cts = _debounceCts;
        }

        _ = RunDebouncedBatchAsync(cts);
    }

    private async Task RunDebouncedBatchAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_options.RequestDebounce, _options.TimeProvider, cts.Token).ConfigureAwait(false);
            bool shouldSend;
            lock (_gate)
            {
                if (!_armed || _completed || cts.IsCancellationRequested)
                {
                    return;
                }
                shouldSend = TryReserveNextBatchLocked();
            }

            if (shouldSend)
            {
                await DispatchNextBatchSafeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool TryReserveNextBatchLocked()
    {
        if (_unbooked <= 0)
        {
            return false;
        }

        int next = Math.Min(_options.BatchSize, _unbooked);
        _unbooked -= next;
        _outstanding += next;
        _prevBatchInflight = true;
        return true;
    }

    private async Task DispatchNextBatchSafeAsync()
    {
        try
        {
            await _sendNodeAsync(OfflineBatchIb.BuildOfflineBatch(_options.BatchSize), _disposeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Transport send failure is handled by NoiseSessionSocket / ConnectionOrchestrator.
        }
    }

    private async Task RunInactivityWatchdogAsync(CancellationToken token)
    {
        long seen;
        lock (_gate)
        {
            seen = _stanzaActivity;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_options.InactivityTimeout, _options.TimeProvider, token).ConfigureAwait(false);

                int processedSnapshot;
                lock (_gate)
                {
                    if (_completed || !_armed || token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (_stanzaActivity != seen)
                    {
                        seen = _stanzaActivity;
                        continue;
                    }

                    if (_admittedCount > 0 || _deferred)
                    {
                        // Own backpressure is still active; give lanes more time.
                        continue;
                    }

                    processedSnapshot = _processedCount;
                }

                await CompleteInternalAsync(processedSnapshot, timedOut: true).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CompleteInternalAsync(int count, bool timedOut)
    {
        CancellationTokenSource? debounceCts;
        CancellationTokenSource? watchdogCts;

        lock (_gate)
        {
            if (_completed)
            {
                return;
            }
            _completed = true;
            _armed = false;
            debounceCts = _debounceCts;
            _debounceCts = null;
            watchdogCts = _watchdogCts;
            _watchdogCts = null;
        }

        debounceCts?.Cancel();
        debounceCts?.Dispose();
        watchdogCts?.Cancel();
        watchdogCts?.Dispose();

        _offlineCompletedTcs.TrySetResult();
        await _emitEventAsync(new OfflineSyncCompletedEvent(count, timedOut)).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        CancellationTokenSource? debounceCts;
        CancellationTokenSource? watchdogCts;

        lock (_gate)
        {
            _armed = false;
            debounceCts = _debounceCts;
            _debounceCts = null;
            watchdogCts = _watchdogCts;
            _watchdogCts = null;
        }

        _disposeCts.Cancel();
        debounceCts?.Cancel();
        debounceCts?.Dispose();
        watchdogCts?.Cancel();
        watchdogCts?.Dispose();
        _disposeCts.Dispose();
        _offlineCompletedTcs.TrySetCanceled();
        return ValueTask.CompletedTask;
    }

    public sealed class OfflineStanzaLease : IDisposable
    {
        private readonly OfflineBatchCoordinator _owner;
        private int _released;

        internal OfflineStanzaLease(OfflineBatchCoordinator owner) => _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.ReleaseStanzaLease();
            }
        }
    }
}
