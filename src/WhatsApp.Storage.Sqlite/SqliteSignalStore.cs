using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Storage.Sqlite;

internal sealed class SqliteSignalStore : ISignalStore
{
    private readonly SqliteRunner _runner;
    private readonly int _deviceId;

    public SqliteSignalStore(SqliteRunner runner, int deviceId)
    {
        _runner = runner;
        _deviceId = deviceId;
    }

    public ValueTask<SignalSession?> GetSessionAsync(string address, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        return new(_runner.RunReadAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT record
                FROM signal_sessions
                WHERE address = $address AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$address", address);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            object? scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return scalar is byte[] blob ? SignalStateCodec.DeserializeSession(blob) : null;
        }, ct));
    }

    public ValueTask SaveSessionAsync(string address, SignalSession session, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        ArgumentNullException.ThrowIfNull(session);

        byte[] serialized = SignalStateCodec.SerializeSession(session);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO signal_sessions (address, device_id, record)
                VALUES ($address, $deviceId, $record)
                ON CONFLICT(address, device_id) DO UPDATE SET
                    record = excluded.record;
                """;
            cmd.Parameters.AddWithValue("$address", address);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$record", serialized);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask DeleteSessionAsync(string address, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM signal_sessions
                WHERE address = $address AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$address", address);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask<byte[]?> GetIdentityAsync(string address, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        return new(_runner.RunReadAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT identity_key
                FROM signal_identities
                WHERE address = $address AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$address", address);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            object? scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return scalar as byte[];
        }, ct));
    }

    public ValueTask SaveIdentityAsync(string address, byte[] identityKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        ArgumentNullException.ThrowIfNull(identityKey);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO signal_identities (address, device_id, identity_key)
                VALUES ($address, $deviceId, $identityKey)
                ON CONFLICT(address, device_id) DO UPDATE SET
                    identity_key = excluded.identity_key;
                """;
            cmd.Parameters.AddWithValue("$address", address);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$identityKey", identityKey);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask<byte[]?> GetPreKeyAsync(uint preKeyId, CancellationToken ct = default) =>
        new(_runner.RunReadAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT key_pair
                FROM prekeys
                WHERE id = $id AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$id", unchecked((long)preKeyId));
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            object? scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return scalar as byte[];
        }, ct));

    public ValueTask SavePreKeysAsync(IReadOnlyList<(uint Id, byte[] KeyPairBytes)> preKeys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preKeys);
        if (preKeys.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO prekeys (id, device_id, key_pair, uploaded)
                VALUES ($id, $deviceId, $keyPair, 0)
                ON CONFLICT(id, device_id) DO UPDATE SET
                    key_pair = excluded.key_pair;
                """;
            var idParam = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            var keyParam = cmd.Parameters.Add("$keyPair", Microsoft.Data.Sqlite.SqliteType.Blob);

            foreach (var (id, keyPairBytes) in preKeys)
            {
                idParam.Value = unchecked((long)id);
                keyParam.Value = keyPairBytes ?? throw new ArgumentException($"Null PreKey bytes for id {id}.", nameof(preKeys));
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, ct));
    }

    public ValueTask DeletePreKeyAsync(uint preKeyId, CancellationToken ct = default) =>
        new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM prekeys
                WHERE id = $id AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$id", unchecked((long)preKeyId));
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));

    public ValueTask<byte[]?> GetSenderKeyAsync(string groupJid, string senderAddress, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupJid);
        ArgumentException.ThrowIfNullOrEmpty(senderAddress);

        return new(_runner.RunReadAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT record
                FROM sender_keys
                WHERE group_jid = $groupJid AND sender_address = $senderAddress AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$groupJid", groupJid);
            cmd.Parameters.AddWithValue("$senderAddress", senderAddress);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            object? scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return scalar as byte[];
        }, ct));
    }

    public ValueTask SaveSenderKeyAsync(string groupJid, string senderAddress, byte[] recordBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupJid);
        ArgumentException.ThrowIfNullOrEmpty(senderAddress);
        ArgumentNullException.ThrowIfNull(recordBytes);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sender_keys (group_jid, sender_address, device_id, record)
                VALUES ($groupJid, $senderAddress, $deviceId, $record)
                ON CONFLICT(group_jid, sender_address, device_id) DO UPDATE SET
                    record = excluded.record;
                """;
            cmd.Parameters.AddWithValue("$groupJid", groupJid);
            cmd.Parameters.AddWithValue("$senderAddress", senderAddress);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$record", recordBytes);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }
}
