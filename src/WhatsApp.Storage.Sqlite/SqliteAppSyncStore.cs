using Microsoft.Data.Sqlite;
using WhatsApp.Protocol.Abstractions;

namespace WhatsApp.Storage.Sqlite;

internal sealed class SqliteAppSyncStore : IAppSyncStore
{
    public const int LtHashLength = 128;

    private readonly SqliteRunner _runner;
    private readonly int _deviceId;

    public SqliteAppSyncStore(SqliteRunner runner, int deviceId)
    {
        _runner = runner;
        _deviceId = deviceId;
    }

    public ValueTask<byte[]?> GetSyncKeyAsync(byte[] keyId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        return new(_runner.RunReadAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT key_data
                FROM app_state_keys
                WHERE key_id = $keyId AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$keyId", keyId);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            object? scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return scalar as byte[];
        }, ct));
    }

    public ValueTask<byte[]?> GetLatestSyncKeyIdAsync(CancellationToken ct = default) =>
        new(_runner.RunReadAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT key_id
                FROM app_state_keys
                WHERE device_id = $deviceId AND length(key_data) = 32
                ORDER BY rowid DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            object? scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return scalar as byte[];
        }, ct));

    public ValueTask SaveSyncKeyAsync(byte[] keyId, byte[] keyData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        ArgumentNullException.ThrowIfNull(keyData);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO app_state_keys (key_id, device_id, key_data)
                VALUES ($keyId, $deviceId, $keyData)
                ON CONFLICT(key_id, device_id) DO UPDATE SET
                    key_data = excluded.key_data;
                """;
            cmd.Parameters.AddWithValue("$keyId", keyId);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$keyData", keyData);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask<(ulong Version, byte[] LtHash)> GetCollectionStateAsync(string collection, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(collection);

        return new(_runner.RunReadAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT version, lt_hash
                FROM app_state_versions
                WHERE collection = $collection AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$collection", collection);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return (0UL, new byte[LtHashLength]);
            }

            ulong version = unchecked((ulong)reader.GetInt64(0));
            byte[] ltHash = (byte[])reader.GetValue(1);
            return (version, ltHash);
        }, ct));
    }

    public ValueTask CommitSnapshotAsync(
        string collection,
        ulong newVersion,
        byte[] newLtHash,
        IReadOnlyList<(byte[] IndexMac, byte[] ValueMac)> macs,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(collection);
        ArgumentNullException.ThrowIfNull(newLtHash);
        ArgumentNullException.ThrowIfNull(macs);
        if (newLtHash.Length != LtHashLength)
        {
            throw new ArgumentException($"LTHash must be {LtHashLength} bytes.", nameof(newLtHash));
        }

        // Replace baseline atomically: wipe prior MACs before writing snapshot MACs and version.
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            await UpsertVersionAsync(conn, _deviceId, collection, newVersion, newLtHash, token).ConfigureAwait(false);

            using (var wipeCmd = conn.CreateCommand())
            {
                wipeCmd.CommandText = """
                    DELETE FROM app_state_mutation_macs
                    WHERE collection = $collection AND device_id = $deviceId;
                    """;
                wipeCmd.Parameters.AddWithValue("$collection", collection);
                wipeCmd.Parameters.AddWithValue("$deviceId", _deviceId);
                await wipeCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            if (macs.Count > 0)
            {
                await InsertMacsAsync(conn, _deviceId, collection, macs, token).ConfigureAwait(false);
            }
        }, ct));
    }

    public ValueTask CommitPatchAsync(
        string collection,
        ulong newVersion,
        byte[] newLtHash,
        IReadOnlyList<(byte[] IndexMac, byte[] ValueMac)> addedMacs,
        IReadOnlyList<byte[]> removedIndexMacs,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(collection);
        ArgumentNullException.ThrowIfNull(newLtHash);
        ArgumentNullException.ThrowIfNull(addedMacs);
        ArgumentNullException.ThrowIfNull(removedIndexMacs);
        if (newLtHash.Length != LtHashLength)
        {
            throw new ArgumentException($"LTHash must be {LtHashLength} bytes.", nameof(newLtHash));
        }

        // Single BEGIN IMMEDIATE transaction guarantees version, LTHash, deletions, and inserts commit atomically.
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            await UpsertVersionAsync(conn, _deviceId, collection, newVersion, newLtHash, token).ConfigureAwait(false);

            if (removedIndexMacs.Count > 0)
            {
                using var delCmd = conn.CreateCommand();
                delCmd.CommandText = """
                    DELETE FROM app_state_mutation_macs
                    WHERE collection = $collection AND device_id = $deviceId AND index_mac = $indexMac;
                    """;
                delCmd.Parameters.AddWithValue("$collection", collection);
                delCmd.Parameters.AddWithValue("$deviceId", _deviceId);
                var idxParam = delCmd.Parameters.Add("$indexMac", SqliteType.Blob);

                foreach (byte[] removed in removedIndexMacs)
                {
                    idxParam.Value = removed ?? throw new ArgumentException("Removed index MAC cannot be null.", nameof(removedIndexMacs));
                    await delCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            if (addedMacs.Count > 0)
            {
                await InsertMacsAsync(conn, _deviceId, collection, addedMacs, token).ConfigureAwait(false);
            }
        }, ct));
    }

    private static async Task UpsertVersionAsync(
        SqliteConnection conn,
        int deviceId,
        string collection,
        ulong newVersion,
        byte[] newLtHash,
        CancellationToken token)
    {
        using var verCmd = conn.CreateCommand();
        verCmd.CommandText = """
            INSERT INTO app_state_versions (collection, device_id, version, lt_hash)
            VALUES ($collection, $deviceId, $version, $ltHash)
            ON CONFLICT(collection, device_id) DO UPDATE SET
                version = excluded.version,
                lt_hash = excluded.lt_hash;
            """;
        verCmd.Parameters.AddWithValue("$collection", collection);
        verCmd.Parameters.AddWithValue("$deviceId", deviceId);
        verCmd.Parameters.AddWithValue("$version", unchecked((long)newVersion));
        verCmd.Parameters.AddWithValue("$ltHash", newLtHash);
        await verCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task InsertMacsAsync(
        SqliteConnection conn,
        int deviceId,
        string collection,
        IReadOnlyList<(byte[] IndexMac, byte[] ValueMac)> addedMacs,
        CancellationToken token)
    {
        using var addCmd = conn.CreateCommand();
        addCmd.CommandText = """
            INSERT INTO app_state_mutation_macs (collection, device_id, index_mac, value_mac)
            VALUES ($collection, $deviceId, $indexMac, $valueMac)
            ON CONFLICT(collection, device_id, index_mac) DO UPDATE SET
                value_mac = excluded.value_mac;
            """;
        addCmd.Parameters.AddWithValue("$collection", collection);
        addCmd.Parameters.AddWithValue("$deviceId", deviceId);
        var idxParam = addCmd.Parameters.Add("$indexMac", SqliteType.Blob);
        var valParam = addCmd.Parameters.Add("$valueMac", SqliteType.Blob);

        foreach (var (indexMac, valueMac) in addedMacs)
        {
            idxParam.Value = indexMac ?? throw new ArgumentException("Index MAC cannot be null.", nameof(addedMacs));
            valParam.Value = valueMac ?? throw new ArgumentException("Value MAC cannot be null.", nameof(addedMacs));
            await addCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    public ValueTask<byte[]?> GetMutationMacAsync(string collection, byte[] indexMac, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(collection);
        ArgumentNullException.ThrowIfNull(indexMac);

        return new(_runner.RunReadAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT value_mac
                FROM app_state_mutation_macs
                WHERE collection = $collection AND device_id = $deviceId AND index_mac = $indexMac;
                """;
            cmd.Parameters.AddWithValue("$collection", collection);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$indexMac", indexMac);

            object? scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return scalar as byte[];
        }, ct));
    }
}
