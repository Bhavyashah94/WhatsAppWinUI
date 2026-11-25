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

    public ValueTask DeleteSessionsByUserAsync(string user, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(user);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            // Matches bare user (`user@server`) and device-suffixed (`user:device@server`) or Signal `user.0` forms.
            cmd.CommandText = """
                DELETE FROM signal_sessions
                WHERE device_id = $deviceId
                  AND (address LIKE $colonPrefix OR address LIKE $dotPrefix OR address LIKE $atPrefix);
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$colonPrefix", $"{user}:%@%");
            cmd.Parameters.AddWithValue("$dotPrefix", $"{user}.%");
            cmd.Parameters.AddWithValue("$atPrefix", $"{user}@%");
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

    public ValueTask DeleteIdentityAsync(string address, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM signal_identities
                WHERE address = $address AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$address", address);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
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

    public ValueTask<(uint Id, byte[] KeyPairBytes)?> GetFirstPreKeyAsync(CancellationToken ct = default) =>
        new(_runner.RunReadAsync<(uint Id, byte[] KeyPairBytes)?>(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, key_pair
                FROM prekeys
                WHERE device_id = $deviceId
                ORDER BY id ASC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }
            return (unchecked((uint)reader.GetInt64(0)), (byte[])reader.GetValue(1));
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

    public ValueTask<SignedPreKeyEntry?> GetSignedPreKeyAsync(uint signedPreKeyId, CancellationToken ct = default) =>
        new(_runner.RunReadAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT key_pair, signature, created_at
                FROM signed_prekeys
                WHERE id = $id AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$id", unchecked((long)signedPreKeyId));
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            return new SignedPreKeyEntry(
                signedPreKeyId,
                (byte[])reader.GetValue(0),
                (byte[])reader.GetValue(1),
                reader.GetInt64(2));
        }, ct));

    public ValueTask SaveSignedPreKeyAsync(SignedPreKeyEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.KeyPairBytes);
        ArgumentNullException.ThrowIfNull(entry.Signature);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO signed_prekeys (id, device_id, key_pair, signature, created_at)
                VALUES ($id, $deviceId, $keyPair, $signature, $createdAt)
                ON CONFLICT(id, device_id) DO UPDATE SET
                    key_pair = excluded.key_pair,
                    signature = excluded.signature,
                    created_at = excluded.created_at;
                """;
            cmd.Parameters.AddWithValue("$id", unchecked((long)entry.Id));
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$keyPair", entry.KeyPairBytes);
            cmd.Parameters.AddWithValue("$signature", entry.Signature);
            cmd.Parameters.AddWithValue("$createdAt", entry.CreatedAtUnixSeconds);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask DeleteSignedPreKeyAsync(uint signedPreKeyId, CancellationToken ct = default) =>
        new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM signed_prekeys
                WHERE id = $id AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$id", unchecked((long)signedPreKeyId));
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

    public ValueTask DeleteSenderKeyAsync(string groupJid, string senderAddress, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupJid);
        ArgumentException.ThrowIfNullOrEmpty(senderAddress);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM sender_keys
                WHERE group_jid = $groupJid AND sender_address = $senderAddress AND device_id = $deviceId;
                DELETE FROM sender_key_devices
                WHERE group_jid = $groupJid AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$groupJid", groupJid);
            cmd.Parameters.AddWithValue("$senderAddress", senderAddress);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask<IReadOnlySet<string>> GetSenderKeyDistributedDevicesAsync(string groupJid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupJid);
        return new(_runner.RunReadAsync<IReadOnlySet<string>>(async (conn, token) =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT device_jid
                FROM sender_key_devices
                WHERE group_jid = $groupJid AND device_id = $deviceId AND has_key = 1;
                """;
            cmd.Parameters.AddWithValue("$groupJid", groupJid);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                set.Add(reader.GetString(0));
            }
            return set;
        }, ct));
    }

    public ValueTask MarkSenderKeyDistributedAsync(string groupJid, IReadOnlyList<string> deviceJids, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupJid);
        ArgumentNullException.ThrowIfNull(deviceJids);
        if (deviceJids.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sender_key_devices (group_jid, device_jid, device_id, has_key, updated_at)
                VALUES ($groupJid, $deviceJid, $deviceId, 1, $updatedAt)
                ON CONFLICT(group_jid, device_jid, device_id) DO UPDATE SET
                    has_key = 1,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$groupJid", groupJid);
            var devParam = cmd.Parameters.Add("$deviceJid", Microsoft.Data.Sqlite.SqliteType.Text);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$updatedAt", now);

            foreach (string d in deviceJids)
            {
                if (!string.IsNullOrWhiteSpace(d))
                {
                    devParam.Value = d;
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }, ct));
    }

    public ValueTask ClearSenderKeyDistributedAsync(string groupJid, string? userPrefix = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupJid);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            if (string.IsNullOrEmpty(userPrefix))
            {
                cmd.CommandText = """
                    DELETE FROM sender_key_devices
                    WHERE group_jid = $groupJid AND device_id = $deviceId;
                    """;
            }
            else
            {
                cmd.CommandText = """
                    DELETE FROM sender_key_devices
                    WHERE group_jid = $groupJid AND device_id = $deviceId
                      AND (device_jid LIKE $colonPrefix OR device_jid LIKE $atPrefix);
                    """;
                cmd.Parameters.AddWithValue("$colonPrefix", $"{userPrefix}:%");
                cmd.Parameters.AddWithValue("$atPrefix", $"{userPrefix}@%");
            }
            cmd.Parameters.AddWithValue("$groupJid", groupJid);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }
}
