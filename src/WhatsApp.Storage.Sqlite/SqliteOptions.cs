namespace WhatsApp.Storage.Sqlite;

/// <summary>
/// Configuration options for <see cref="SqliteBackend"/>.
/// </summary>
public sealed class SqliteOptions
{
    public required string DatabasePath { get; init; }

    /// <summary>
    /// Logical account slot within a multi-account SQLite database (default: 1).
    /// </summary>
    public int DeviceId { get; init; } = 1;

    /// <summary>
    /// SQLite busy_timeout PRAGMA in milliseconds applied to each opened connection.
    /// </summary>
    public int BusyTimeoutMs { get; init; } = 5_000;

    /// <summary>
    /// Maximum retries on SQLITE_BUSY (5) or SQLITE_LOCKED (6) during transactions.
    /// </summary>
    public int MaxLockRetries { get; init; } = 8;

    /// <summary>
    /// Enables Write-Ahead Logging and synchronous=NORMAL for file-backed databases.
    /// </summary>
    public bool EnableWal { get; init; } = true;

    public static SqliteOptions ForPath(string databasePath, int deviceId = 1) =>
        new() { DatabasePath = databasePath, DeviceId = deviceId };
}
