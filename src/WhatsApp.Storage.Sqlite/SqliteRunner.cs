using Microsoft.Data.Sqlite;

namespace WhatsApp.Storage.Sqlite;

/// <summary>
/// Thread-safe WAL SQLite connection, migration gate, and BEGIN IMMEDIATE retry runner.
/// </summary>
internal sealed class SqliteRunner
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private readonly SqliteOptions _options;
    private readonly string _connectionString;
    private readonly bool _isMemoryDatabase;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private SqliteConnection? _memoryAnchorConnection;
    private volatile bool _initialized;

    public SqliteRunner(SqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            throw new ArgumentException("Database path cannot be empty.", nameof(options));
        }

        _options = options;
        _isMemoryDatabase = options.DatabasePath.Equals(":memory:", StringComparison.OrdinalIgnoreCase);

        if (!_isMemoryDatabase)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        var builder = _isMemoryDatabase
            ? new SqliteConnectionStringBuilder
            {
                DataSource = $"whatsapp_mem_{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
                Pooling = false
            }
            : new SqliteConnectionStringBuilder
            {
                DataSource = options.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                Cache = SqliteCacheMode.Private
            };
        _connectionString = builder.ToString();
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            if (_isMemoryDatabase && _memoryAnchorConnection is null)
            {
                _memoryAnchorConnection = await OpenConfiguredConnectionAsync(configureJournalMode: false, ct).ConfigureAwait(false);
            }

            await using var conn = await OpenConfiguredConnectionAsync(configureJournalMode: true, ct).ConfigureAwait(false);
            await SqliteMigrations.ApplyPendingAsync(conn, ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task<T> RunReadAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        return await ExecuteWithRetryAsync(async token =>
        {
            await using var conn = await OpenConfiguredConnectionAsync(configureJournalMode: false, token).ConfigureAwait(false);
            return await action(conn, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public Task RunWriteTransactionAsync(Func<SqliteConnection, CancellationToken, Task> action, CancellationToken ct = default) =>
        RunWriteTransactionAsync<bool>(async (conn, token) =>
        {
            await action(conn, token).ConfigureAwait(false);
            return true;
        }, ct);

    public async Task<T> RunWriteTransactionAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        return await ExecuteWithRetryAsync(async token =>
        {
            await using var conn = await OpenConfiguredConnectionAsync(configureJournalMode: false, token).ConfigureAwait(false);
            using (var beginCmd = conn.CreateCommand())
            {
                // Acquiring the reserved write lock up front eliminates deferred read->write deadlocks.
                beginCmd.CommandText = "BEGIN IMMEDIATE;";
                await beginCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            try
            {
                T result = await action(conn, token).ConfigureAwait(false);

                using var commitCmd = conn.CreateCommand();
                commitCmd.CommandText = "COMMIT;";
                await commitCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return result;
            }
            catch
            {
                try
                {
                    using var rollbackCmd = conn.CreateCommand();
                    rollbackCmd.CommandText = "ROLLBACK;";
                    await rollbackCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve original exception when rollback also fails (e.g. closed connection).
                }
                throw;
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConfiguredConnectionAsync(bool configureJournalMode, CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var pragmaCmd = conn.CreateCommand();
            if (configureJournalMode && _options.EnableWal && !_isMemoryDatabase)
            {
                pragmaCmd.CommandText = $"""
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;
                    PRAGMA foreign_keys = ON;
                    PRAGMA busy_timeout = {_options.BusyTimeoutMs};
                    """;
            }
            else
            {
                pragmaCmd.CommandText = $"""
                    PRAGMA foreign_keys = ON;
                    PRAGMA busy_timeout = {_options.BusyTimeoutMs};
                    """;
            }
            await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        int maxAttempts = Math.Max(1, _options.MaxLockRetries + 1);
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsRetriableLockError(ex) && attempt < maxAttempts - 1)
            {
                int delayMs = Math.Min(200, 5 * (1 << Math.Min(attempt, 5)));
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Unreachable retry exhaustion.");
    }

    internal static bool IsRetriableLockError(SqliteException ex) =>
        ex.SqliteErrorCode == SqliteBusy ||
        ex.SqliteErrorCode == SqliteLocked ||
        ex.Message.Contains("locked", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("busy", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _memoryAnchorConnection?.Dispose();
        _memoryAnchorConnection = null;
        SqliteConnection.ClearAllPools();
        _initGate.Dispose();
    }
}
