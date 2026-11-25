using System.Text.Json;
using Microsoft.Data.Sqlite;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Storage.Sqlite;

internal sealed class SqliteProtocolStore : IProtocolStore
{
    private readonly SqliteRunner _runner;
    private readonly int _deviceId;

    public SqliteProtocolStore(SqliteRunner runner, int deviceId)
    {
        _runner = runner;
        _deviceId = deviceId;
    }

    public ValueTask UpsertLidPnMappingsAsync(IReadOnlyList<LidPnMappingRecord> mappings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        if (mappings.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO lid_pn_mapping (lid, device_id, phone_number, learning_source, updated_at)
                VALUES ($lid, $deviceId, $phoneNumber, 'sync', $updatedAt)
                ON CONFLICT(lid, device_id) DO UPDATE SET
                    phone_number = excluded.phone_number,
                    updated_at = excluded.updated_at;
                """;
            var lidParam = cmd.Parameters.Add("$lid", SqliteType.Text);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            var pnParam = cmd.Parameters.Add("$phoneNumber", SqliteType.Text);
            cmd.Parameters.AddWithValue("$updatedAt", now);

            foreach (var m in mappings)
            {
                if (string.IsNullOrWhiteSpace(m.LidJid) || string.IsNullOrWhiteSpace(m.PnJid))
                {
                    continue;
                }

                lidParam.Value = m.LidJid;
                pnParam.Value = NormalizePhoneJid(m.PnJid);
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, ct));
    }

    public ValueTask<IReadOnlyList<LidPnMappingRecord>> LoadLidPnMappingsAsync(CancellationToken ct = default) =>
        new(_runner.RunReadAsync<IReadOnlyList<LidPnMappingRecord>>(async (conn, token) =>
        {
            var results = new List<LidPnMappingRecord>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT phone_number, lid
                FROM lid_pn_mapping
                WHERE device_id = $deviceId
                ORDER BY updated_at ASC, lid ASC;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(new LidPnMappingRecord(reader.GetString(0), reader.GetString(1)));
            }
            return results;
        }, ct));

    public ValueTask<IReadOnlyList<uint>> GetDeviceListAsync(string userJid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userJid);
        return new(_runner.RunReadAsync<IReadOnlyList<uint>>(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT devices_json
                FROM device_list_cache
                WHERE user_jid = $userJid AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$userJid", userJid);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            object? scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (scalar is not string json || string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<uint>();
            }

            return JsonSerializer.Deserialize<uint[]>(json) ?? Array.Empty<uint>();
        }, ct));
    }

    public ValueTask SaveDeviceListAsync(string userJid, IReadOnlyList<uint> deviceIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userJid);
        ArgumentNullException.ThrowIfNull(deviceIds);

        string json = JsonSerializer.Serialize(deviceIds);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO device_list_cache (user_jid, device_id, devices_json, updated_at)
                VALUES ($userJid, $deviceId, $devicesJson, $updatedAt)
                ON CONFLICT(user_jid, device_id) DO UPDATE SET
                    devices_json = excluded.devices_json,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$userJid", userJid);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$devicesJson", json);
            cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask DeleteDeviceListAsync(string userJid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userJid);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM device_list_cache
                WHERE user_jid = $userJid AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$userJid", userJid);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask StoreTcTokenAsync(string jid, byte[] token, long timestamp, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jid);
        ArgumentNullException.ThrowIfNull(token);

        return new(_runner.RunWriteTransactionAsync(async (conn, tokenCt) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tc_tokens (jid, device_id, token, token_timestamp, updated_at)
                VALUES ($jid, $deviceId, $token, $tokenTimestamp, $updatedAt)
                ON CONFLICT(jid, device_id) DO UPDATE SET
                    token = excluded.token,
                    token_timestamp = excluded.token_timestamp,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$jid", jid);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$token", token);
            cmd.Parameters.AddWithValue("$tokenTimestamp", timestamp);
            cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(tokenCt).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask<byte[]?> GetTcTokenAsync(string jid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jid);
        return new(_runner.RunReadAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT token
                FROM tc_tokens
                WHERE jid = $jid AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$jid", jid);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            object? scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return scalar as byte[];
        }, ct));
    }

    internal static string NormalizePhoneJid(string pnJid) =>
        pnJid.EndsWith("@c.us", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(pnJid.AsSpan(0, pnJid.Length - "@c.us".Length), "@s.whatsapp.net")
            : pnJid;
}
