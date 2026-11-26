using System.Collections.Concurrent;
using System.Threading.Channels;
using WhatsApp.Api;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Storage.Sqlite;
using WhatsApp.Transport;

namespace WhatsApp.Cli;

public sealed record CliStatusSnapshot(
    string DatabasePath,
    int DeviceId,
    bool IsRegistered,
    string? PnJid,
    string? LidJid,
    bool HasServerStaticPublic,
    bool HasAppStateSyncKey,
    bool IsRunning,
    bool IsConnected,
    long CurrentGeneration,
    int ChatCount,
    int PersistedMessageCount,
    int HistoryBatchesReceived,
    int HistoryMessagesReceived,
    IReadOnlyDictionary<string, ulong> AppStateVersions);

/// <summary>
/// Headless session host bridging <see cref="ConnectionOrchestrator"/>, <see cref="IWhatsAppBackend"/>,
/// and CLI/REPL event waiters for Phase 11A–C verification.
/// </summary>
public sealed class CliRuntime : IAsyncDisposable
{
    private sealed record BufferedEvent(DateTimeOffset Timestamp, WhatsAppEvent Event);

    private readonly CliOptions _options;
    private readonly IWhatsAppBackend _backend;
    private readonly ITransportFactory _transportFactory;
    private readonly IHttpClient _httpClient;
    private readonly ConnectionOrchestratorOptions? _orchestratorOverrides;
    private readonly TextWriter _out;
    private readonly TimeProvider _timeProvider;
    private readonly object _waiterLock = new();
    private readonly List<Action<WhatsAppEvent>> _waiters = [];
    private readonly ConcurrentQueue<BufferedEvent> _recentEvents = new();
    private readonly CancellationTokenSource _pumpCts = new();

    private ConnectionOrchestrator? _orchestrator;
    private Task? _pumpTask;
    private int _isRunning;
    private int _isConnected;
    private int _historyBatches;
    private int _historyMessages;
    private int _disposed;

    public CliOptions Options => _options;
    public IWhatsAppBackend Backend => _backend;
    public ConnectionOrchestrator? Orchestrator => _orchestrator;
    public bool IsConnected => Volatile.Read(ref _isConnected) != 0;
    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;
    public int HistoryBatchesReceived => Volatile.Read(ref _historyBatches);
    public int HistoryMessagesReceived => Volatile.Read(ref _historyMessages);

    public CliRuntime(
        CliOptions options,
        IWhatsAppBackend? backend = null,
        ITransportFactory? transportFactory = null,
        IHttpClient? httpClient = null,
        ConnectionOrchestratorOptions? orchestratorOverrides = null,
        TextWriter? output = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _out = output ?? Console.Out;

        if (backend is not null)
        {
            _backend = backend;
        }
        else
        {
            string dbPath = options.ResolveDatabasePath();
            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _backend = new SqliteBackend(dbPath, options.DeviceId);
        }

        _transportFactory = transportFactory ?? new WebSocketTransportFactory();
        _httpClient = httpClient ?? new WhatsAppHttpClient();
        _orchestratorOverrides = orchestratorOverrides;
    }

    public static Task<CliRuntime> CreateAndInitializeAsync(
        CliOptions options,
        IWhatsAppBackend? backend = null,
        ITransportFactory? transportFactory = null,
        IHttpClient? httpClient = null,
        ConnectionOrchestratorOptions? orchestratorOverrides = null,
        TextWriter? output = null,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        var runtime = new CliRuntime(options, backend, transportFactory, httpClient, orchestratorOverrides, output, timeProvider);
        return runtime.InitializeAsync(ct);
    }

    private async Task<CliRuntime> InitializeAsync(CancellationToken ct)
    {
        if (_backend is SqliteBackend sqlite)
        {
            await sqlite.InitializeAsync(ct).ConfigureAwait(false);
        }
        DeviceCredentials creds = await _backend.Device.LoadOrCreateAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(_options.PushName) && creds.PushName != _options.PushName && !creds.IsRegistered)
        {
            creds.PushName = _options.PushName;
            await _backend.Device.SaveAsync(creds, ct).ConfigureAwait(false);
        }
        return this;
    }

    public async Task EnsureStartedAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_orchestrator is null)
        {
            ConnectionOrchestratorOptions opts = _orchestratorOverrides ?? new ConnectionOrchestratorOptions
            {
                Endpoints = [_options.Endpoint],
                HttpClient = _httpClient,
                TimeProvider = _timeProvider,
                AutoSendActiveIq = true,
                AutoUploadPreKeys = true,
                AutoSyncAppState = true,
                ExternalHandlers = _options.TraceStanzas ? BuildTraceHandlers() : null,
            };
            _orchestrator = new ConnectionOrchestrator(_backend, _transportFactory, opts);
            _pumpTask = Task.Run(() => PumpEventsAsync(_orchestrator.Events, _pumpCts.Token));
        }

        if (Interlocked.Exchange(ref _isRunning, 1) == 0)
        {
            await _orchestrator.StartAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        if (_orchestrator is not null && Interlocked.Exchange(ref _isRunning, 0) == 1)
        {
            await _orchestrator.StopAsync().ConfigureAwait(false);
            Volatile.Write(ref _isConnected, 0);
        }
    }

    public Task<TEvent> WaitForEventAsync<TEvent>(
        Func<TEvent, bool>? predicate = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
        where TEvent : WhatsAppEvent
    {
        var tcs = new TaskCompletionSource<TEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<WhatsAppEvent>? handler = null;
        handler = evt =>
        {
            if (evt is TEvent typed && (predicate is null || predicate(typed)))
            {
                lock (_waiterLock)
                {
                    _waiters.Remove(handler!);
                }
                tcs.TrySetResult(typed);
            }
        };

        lock (_waiterLock)
        {
            _waiters.Add(handler);
        }

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(effectiveTimeout);
        CancellationTokenRegistration reg = linkedCts.Token.Register(() =>
        {
            lock (_waiterLock)
            {
                _waiters.Remove(handler);
            }
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
            }
            else
            {
                tcs.TrySetException(new TimeoutException($"Timed out after {effectiveTimeout.TotalSeconds:0.#}s waiting for {typeof(TEvent).Name}."));
            }
        });

        _ = tcs.Task.ContinueWith(_ =>
        {
            reg.Dispose();
            linkedCts.Dispose();
        }, TaskScheduler.Default);

        return tcs.Task;
    }

    public IReadOnlyList<(DateTimeOffset Timestamp, WhatsAppEvent Event)> GetRecentEvents(int maxCount = 20)
    {
        BufferedEvent[] all = _recentEvents.ToArray();
        int skip = Math.Max(0, all.Length - Math.Max(1, maxCount));
        return all.Skip(skip).Select(x => (x.Timestamp, x.Event)).ToArray();
    }

    public async Task<CliStatusSnapshot> GetStatusAsync(CancellationToken ct = default)
    {
        DeviceCredentials? creds = await _backend.Device.LoadAsync(ct).ConfigureAwait(false);
        byte[]? syncKeyId = await _backend.AppSync.GetLatestSyncKeyIdAsync(ct).ConfigureAwait(false);
        IReadOnlyList<ChatSummary> chats = await _backend.History.GetChatsAsync(limit: 1000, ct: ct).ConfigureAwait(false);
        int persistedMsgs = await _backend.History.GetMessageCountAsync(ct: ct).ConfigureAwait(false);

        var versions = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (string col in new[] { "critical_block", "critical_unblock_low", "regular", "regular_high", "regular_low" })
        {
            (ulong ver, _) = await _backend.AppSync.GetCollectionStateAsync(col, ct).ConfigureAwait(false);
            versions[col] = ver;
        }

        return new CliStatusSnapshot(
            _backend is SqliteBackend sql ? sql.DatabasePath : ":memory:",
            _options.DeviceId,
            creds?.IsRegistered == true,
            creds?.Pn?.ToString(),
            creds?.Lid?.ToString(),
            creds?.ServerStaticPublic is { Length: 32 },
            syncKeyId is { Length: > 0 },
            IsRunning,
            IsConnected,
            _orchestrator?.CurrentGeneration ?? 0,
            chats.Count,
            persistedMsgs,
            HistoryBatchesReceived,
            HistoryMessagesReceived,
            versions);
    }

    private async Task PumpEventsAsync(ChannelReader<WhatsAppEvent> reader, CancellationToken ct)
    {
        try
        {
            await foreach (WhatsAppEvent evt in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case ConnectedEvent:
                        Volatile.Write(ref _isConnected, 1);
                        break;
                    case DisconnectedEvent:
                    case LoggedOutEvent:
                    case StreamReplacedEvent:
                        Volatile.Write(ref _isConnected, 0);
                        break;
                    case HistorySyncReceivedEvent hs:
                        Interlocked.Increment(ref _historyBatches);
                        Interlocked.Add(ref _historyMessages, hs.Batch.Conversations.Sum(c => c.Messages.Count));
                        break;
                }

                _recentEvents.Enqueue(new BufferedEvent(_timeProvider.GetUtcNow(), evt));
                while (_recentEvents.Count > 200 && _recentEvents.TryDequeue(out _)) { }

                Action<WhatsAppEvent>[] snapshot;
                lock (_waiterLock)
                {
                    snapshot = _waiters.ToArray();
                }
                foreach (Action<WhatsAppEvent> w in snapshot)
                {
                    w(evt);
                }

                if (!_options.QuietEvents)
                {
                    string line = _options.JsonEvents
                        ? CliEventFormatter.FormatJson(evt, _timeProvider.GetUtcNow())
                        : CliEventFormatter.FormatHuman(evt);
                    await _out.WriteLineAsync(line).ConfigureAwait(false);
                    await _out.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _pumpCts.Cancel();
        if (_orchestrator is not null)
        {
            await _orchestrator.DisposeAsync().ConfigureAwait(false);
        }
        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _pumpCts.Dispose();
        if (_httpClient is IDisposable dispHttp)
        {
            dispHttp.Dispose();
        }
        await _backend.DisposeAsync().ConfigureAwait(false);
    }

    private StanzaHandlers BuildTraceHandlers()
    {
        ValueTask TraceAsync(Node node)
        {
            string? id = node.GetAttr("id");
            string? type = node.GetAttr("type");
            string? from = node.GetAttr("from");
            string? offline = node.GetAttr("offline");
            string? senderLid = node.GetAttr("sender_lid");
            IReadOnlyList<Node> childNodes = node.Content is NodeContent.NodesContent nc ? nc.Nodes : Array.Empty<Node>();
            string children = string.Join(',', childNodes.Select(c =>
                c.Tag == "enc" ? $"enc({c.GetAttr("type") ?? "?"})" : c.Tag));
            string line = $"[TRACE] <{node.Tag} id={id ?? "-"} type={type ?? "-"} from={from ?? "-"} sender_lid={senderLid ?? "-"} offline={offline ?? "-"}> children=[{children}]";
            return new ValueTask(_out.WriteLineAsync(line));
        }

        return new StanzaHandlers
        {
            OnIqRequest = (n, _) => TraceAsync(n),
            OnIb = (n, _) => TraceAsync(n),
            OnNotification = (n, _) => TraceAsync(n),
            OnMessage = (n, _) => TraceAsync(n),
            OnReceipt = (n, _) => TraceAsync(n),
            OnAck = (n, _) => TraceAsync(n),
            OnCall = (n, _) => TraceAsync(n),
            OnUnhandled = (n, _) => TraceAsync(n),
        };
    }
}
