using Microsoft.Data.Sqlite;
using WhatsApp.Protocol.Abstractions;

namespace WhatsApp.Storage.Sqlite;

/// <summary>
/// Scaffold entry point for the normalized WAL SQLite backend (full tables built in Phase 3).
/// </summary>
public sealed class SqliteBackend : IWhatsAppBackend
{
    public string DatabasePath { get; }

    public SqliteBackend(string databasePath)
    {
        DatabasePath = databasePath;
    }

    public IDeviceStore Device => throw new NotImplementedException("Phase 3");
    public ISignalStore Signal => throw new NotImplementedException("Phase 3");
    public IAppSyncStore AppSync => throw new NotImplementedException("Phase 3");
    public IProtocolStore Protocol => throw new NotImplementedException("Phase 3");
    public IMsgSecretStore MsgSecrets => throw new NotImplementedException("Phase 3");
    public IMessageHistoryStore History => throw new NotImplementedException("Phase 3");

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }
}
