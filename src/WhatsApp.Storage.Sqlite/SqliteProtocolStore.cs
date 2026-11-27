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

    public ValueTask<int> IncrementInboundRetryCountAsync(string chatJid, string messageId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        return new(_runner.RunWriteTransactionAsync<int>(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO inbound_retry_counts (chat_jid, message_id, device_id, retry_count, updated_at)
                VALUES ($chatJid, $messageId, $deviceId, 1, $updatedAt)
                ON CONFLICT(chat_jid, message_id, device_id) DO UPDATE SET
                    retry_count = inbound_retry_counts.retry_count + 1,
                    updated_at = excluded.updated_at
                RETURNING retry_count;
                """;
            cmd.Parameters.AddWithValue("$chatJid", chatJid);
            cmd.Parameters.AddWithValue("$messageId", messageId);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            object? scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt32(scalar);
        }, ct));
    }

    public ValueTask ClearInboundRetryCountAsync(string chatJid, string messageId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM inbound_retry_counts
                WHERE chat_jid = $chatJid AND message_id = $messageId AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$chatJid", chatJid);
            cmd.Parameters.AddWithValue("$messageId", messageId);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask SaveOutboundRetryAsync(OutboundRetryEntry entry, int maxEntries = 1000, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(entry.MessageId);
        ArgumentException.ThrowIfNullOrEmpty(entry.ToChatJid);

        string allowedJson = JsonSerializer.Serialize(entry.AllowedParticipantUsers);
        int boundedMax = Math.Max(16, maxEntries);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO outbound_retry_cache (
                        message_id, device_id, to_chat_jid, stanza_type, media_type,
                        hide_decrypt_fail, unpadded_protobuf, addressing_mode,
                        allowed_participants_json, created_at)
                    VALUES (
                        $messageId, $deviceId, $toChatJid, $stanzaType, $mediaType,
                        $hideDecryptFail, $unpaddedProtobuf, $addressingMode,
                        $allowedJson, $createdAt)
                    ON CONFLICT(message_id, device_id) DO UPDATE SET
                        to_chat_jid = excluded.to_chat_jid,
                        stanza_type = excluded.stanza_type,
                        media_type = excluded.media_type,
                        hide_decrypt_fail = excluded.hide_decrypt_fail,
                        unpadded_protobuf = excluded.unpadded_protobuf,
                        addressing_mode = excluded.addressing_mode,
                        allowed_participants_json = excluded.allowed_participants_json,
                        created_at = excluded.created_at;
                    """;
                cmd.Parameters.AddWithValue("$messageId", entry.MessageId);
                cmd.Parameters.AddWithValue("$deviceId", _deviceId);
                cmd.Parameters.AddWithValue("$toChatJid", entry.ToChatJid);
                cmd.Parameters.AddWithValue("$stanzaType", entry.StanzaType);
                cmd.Parameters.AddWithValue("$mediaType", (object?)entry.MediaType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$hideDecryptFail", entry.HideDecryptFail ? 1 : 0);
                cmd.Parameters.AddWithValue("$unpaddedProtobuf", entry.UnpaddedProtobufBytes);
                cmd.Parameters.AddWithValue("$addressingMode", (object?)entry.AddressingMode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$allowedJson", allowedJson);
                cmd.Parameters.AddWithValue("$createdAt", entry.CreatedAtUnixSeconds);
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            using (var pruneCmd = conn.CreateCommand())
            {
                pruneCmd.CommandText = """
                    DELETE FROM outbound_retry_cache
                    WHERE device_id = $deviceId
                      AND message_id IN (
                          SELECT message_id FROM outbound_retry_cache
                          WHERE device_id = $deviceId
                          ORDER BY created_at DESC, rowid DESC
                          LIMIT -1 OFFSET $maxEntries
                      );
                    """;
                pruneCmd.Parameters.AddWithValue("$deviceId", _deviceId);
                pruneCmd.Parameters.AddWithValue("$maxEntries", boundedMax);
                await pruneCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, ct));
    }

    public ValueTask<OutboundRetryEntry?> GetOutboundRetryAsync(string messageId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        return new(_runner.RunReadAsync<OutboundRetryEntry?>(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT to_chat_jid, stanza_type, media_type, hide_decrypt_fail,
                       unpadded_protobuf, addressing_mode, allowed_participants_json, created_at
                FROM outbound_retry_cache
                WHERE message_id = $messageId AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$messageId", messageId);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            string toChatJid = reader.GetString(0);
            string stanzaType = reader.GetString(1);
            string? mediaType = reader.IsDBNull(2) ? null : reader.GetString(2);
            bool hideDecryptFail = reader.GetInt64(3) != 0;
            byte[] protobuf = (byte[])reader.GetValue(4);
            string? addressingMode = reader.IsDBNull(5) ? null : reader.GetString(5);
            string allowedJson = reader.GetString(6);
            long createdAt = reader.GetInt64(7);
            string[] allowed = JsonSerializer.Deserialize<string[]>(allowedJson) ?? Array.Empty<string>();

            return new OutboundRetryEntry(
                messageId,
                toChatJid,
                stanzaType,
                mediaType,
                hideDecryptFail,
                protobuf,
                addressingMode,
                allowed,
                createdAt);
        }, ct));
    }

    public ValueTask PruneExpiredOutboundRetriesAsync(long cutoffUnixSeconds, CancellationToken ct = default) =>
        new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM outbound_retry_cache
                WHERE device_id = $deviceId AND created_at < $cutoff;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$cutoff", cutoffUnixSeconds);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));

    internal static string NormalizePhoneJid(string pnJid) =>
        pnJid.EndsWith("@c.us", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(pnJid.AsSpan(0, pnJid.Length - "@c.us".Length), "@s.whatsapp.net")
            : pnJid;
}
