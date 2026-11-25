using WhatsApp.Protocol.Abstractions;

namespace WhatsApp.Storage.Sqlite;

/// <summary>
/// Normalized WAL SQLite backend implementing <see cref="IWhatsAppBackend"/>.
/// </summary>
public sealed class SqliteBackend : IWhatsAppBackend
{
    public SqliteOptions Options { get; }
    public string DatabasePath => Options.DatabasePath;
    public int DeviceId => Options.DeviceId;

    internal SqliteRunner Runner { get; }
    private readonly SqliteDeviceStore _device;
    private readonly SqliteSignalStore _signal;
    private readonly SqliteAppSyncStore _appSync;
    private readonly SqliteProtocolStore _protocol;
    private readonly SqliteMsgSecretStore _msgSecrets;
    private readonly SqliteHistoryStore _history;

    public SqliteBackend(string databasePath, int deviceId = 1)
        : this(SqliteOptions.ForPath(databasePath, deviceId))
    {
    }

    public SqliteBackend(SqliteOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Runner = new SqliteRunner(options);
        _device = new SqliteDeviceStore(Runner, options.DeviceId);
        _signal = new SqliteSignalStore(Runner, options.DeviceId);
        _appSync = new SqliteAppSyncStore(Runner, options.DeviceId);
        _protocol = new SqliteProtocolStore(Runner, options.DeviceId);
        _msgSecrets = new SqliteMsgSecretStore(Runner, options.DeviceId);
        _history = new SqliteHistoryStore(Runner, options.DeviceId);
    }

    /// <summary>
    /// Eagerly opens the database, configures WAL PRAGMAs, and applies pending schema migrations.
    /// Store operations also call this lazily if not initialized explicitly.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default) => Runner.EnsureInitializedAsync(ct);

    public IDeviceStore Device => _device;
    public ISignalStore Signal => _signal;
    public IAppSyncStore AppSync => _appSync;
    public IProtocolStore Protocol => _protocol;
    public IMsgSecretStore MsgSecrets => _msgSecrets;
    public IMessageHistoryStore History => _history;

    public ValueTask DisposeAsync()
    {
        Runner.Dispose();
        return ValueTask.CompletedTask;
    }
}
