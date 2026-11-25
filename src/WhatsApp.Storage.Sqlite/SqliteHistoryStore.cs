using Microsoft.Data.Sqlite;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Storage.Sqlite;

internal sealed class SqliteHistoryStore : IMessageHistoryStore
{
    private const string HistoryAction = "history";

    private readonly SqliteRunner _runner;
    private readonly int _deviceId;

    public SqliteHistoryStore(SqliteRunner runner, int deviceId)
    {
        _runner = runner;
        _deviceId = deviceId;
    }

    public ValueTask UpsertBatchAsync(HistorySyncBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (batch.LidMappings.Count > 0)
            {
                await UpsertLidMappingsInternalAsync(conn, batch.LidMappings, now, token).ConfigureAwait(false);
            }

            if (batch.Pushnames.Count > 0)
            {
                await UpsertPushnamesInternalAsync(conn, batch.Pushnames, now, token).ConfigureAwait(false);
            }

            if (batch.InlineContacts.Count > 0)
            {
                await UpsertInlineContactsInternalAsync(conn, batch.InlineContacts, now, token).ConfigureAwait(false);
            }

            if (batch.Conversations.Count > 0)
            {
                await UpsertConversationsInternalAsync(conn, batch.Conversations, now, token).ConfigureAwait(false);
            }
        }, ct));
    }

    public ValueTask SaveMessageAsync(MessageRecord message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(message.Id);
        ArgumentException.ThrowIfNullOrEmpty(message.RemoteJid);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using (var chatCmd = conn.CreateCommand())
            {
                chatCmd.CommandText = """
                    INSERT INTO chats (id, device_id, name, unread_count, last_msg_timestamp, archived, updated_at)
                    VALUES ($id, $deviceId, NULL, 0, $lastTs, 0, $updatedAt)
                    ON CONFLICT(id, device_id) DO UPDATE SET
                        last_msg_timestamp = MAX(chats.last_msg_timestamp, excluded.last_msg_timestamp),
                        updated_at = excluded.updated_at;
                    """;
                chatCmd.Parameters.AddWithValue("$id", message.RemoteJid);
                chatCmd.Parameters.AddWithValue("$deviceId", _deviceId);
                chatCmd.Parameters.AddWithValue("$lastTs", unchecked((long)message.Timestamp));
                chatCmd.Parameters.AddWithValue("$updatedAt", now);
                await chatCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            using var msgCmd = CreateUpsertMessageCommand(conn);
            BindMessageParameters(msgCmd, message);
            await msgCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask<IReadOnlyList<MessageRecord>> GetMessagesAsync(
        string chatJid,
        ulong? beforeTimestamp,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        }

        return new(_runner.RunReadAsync<IReadOnlyList<MessageRecord>>(async (conn, token) =>
        {
            var results = new List<MessageRecord>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = beforeTimestamp.HasValue
                ? """
                  SELECT id, chat_jid, sender_jid, from_me, timestamp, type, text, mime_type, direct_path, media_key, file_hash, file_enc_hash
                  FROM messages
                  WHERE device_id = $deviceId AND chat_jid = $chatJid AND timestamp < $beforeTs
                  ORDER BY timestamp DESC, id DESC
                  LIMIT $limit;
                  """
                : """
                  SELECT id, chat_jid, sender_jid, from_me, timestamp, type, text, mime_type, direct_path, media_key, file_hash, file_enc_hash
                  FROM messages
                  WHERE device_id = $deviceId AND chat_jid = $chatJid
                  ORDER BY timestamp DESC, id DESC
                  LIMIT $limit;
                  """;

            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$chatJid", chatJid);
            if (beforeTimestamp.HasValue)
            {
                cmd.Parameters.AddWithValue("$beforeTs", unchecked((long)beforeTimestamp.Value));
            }
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(new MessageRecord(
                    Id: reader.GetString(0),
                    RemoteJid: reader.GetString(1),
                    SenderJid: reader.IsDBNull(2) ? null : reader.GetString(2),
                    FromMe: reader.GetInt64(3) != 0,
                    Timestamp: unchecked((ulong)reader.GetInt64(4)),
                    Type: (MessageType)reader.GetInt32(5),
                    Text: reader.IsDBNull(6) ? null : reader.GetString(6),
                    MimeType: reader.IsDBNull(7) ? null : reader.GetString(7),
                    DirectPath: reader.IsDBNull(8) ? null : reader.GetString(8),
                    MediaKey: reader.IsDBNull(9) ? null : (byte[])reader.GetValue(9),
                    FileHash: reader.IsDBNull(10) ? null : (byte[])reader.GetValue(10),
                    FileEncHash: reader.IsDBNull(11) ? null : (byte[])reader.GetValue(11)));
            }

            return results;
        }, ct));
    }

    public ValueTask<ActiveMessageRange?> GetMessageRangeAsync(string chatJid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);

        return new(_runner.RunReadAsync<ActiveMessageRange?>(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT min_timestamp, max_timestamp
                FROM active_message_ranges
                WHERE chat_jid = $chatJid AND action = $action AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$chatJid", chatJid);
            cmd.Parameters.AddWithValue("$action", HistoryAction);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            return new ActiveMessageRange(
                chatJid,
                unchecked((ulong)reader.GetInt64(0)),
                unchecked((ulong)reader.GetInt64(1)));
        }, ct));
    }

    public ValueTask UpdateMessageRangeAsync(ActiveMessageRange range, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentException.ThrowIfNullOrEmpty(range.ChatJid);

        return new(_runner.RunWriteTransactionAsync(
            (conn, token) => UpsertMessageRangeInternalAsync(conn, range.ChatJid, range.MinTimestamp, range.MaxTimestamp, token),
            ct));
    }

    private async Task UpsertLidMappingsInternalAsync(
        SqliteConnection conn,
        IReadOnlyList<LidPnMappingRecord> mappings,
        long now,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO lid_pn_mapping (lid, device_id, phone_number, learning_source, updated_at)
            VALUES ($lid, $deviceId, $phoneNumber, 'history_sync', $updatedAt)
            ON CONFLICT(lid, device_id) DO UPDATE SET
                phone_number = excluded.phone_number,
                updated_at = excluded.updated_at;
            """;
        var lidParam = cmd.Parameters.Add("$lid", SqliteType.Text);
        cmd.Parameters.AddWithValue("$deviceId", _deviceId);
        var pnParam = cmd.Parameters.Add("$phoneNumber", SqliteType.Text);
        cmd.Parameters.AddWithValue("$updatedAt", now);

        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.LidJid) || string.IsNullOrWhiteSpace(mapping.PnJid))
            {
                continue;
            }

            lidParam.Value = mapping.LidJid;
            pnParam.Value = SqliteProtocolStore.NormalizePhoneJid(mapping.PnJid);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task UpsertPushnamesInternalAsync(
        SqliteConnection conn,
        IReadOnlyList<PushnameRecord> pushnames,
        long now,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO contacts (jid, device_id, name, pushname, phone_number, is_address_book, updated_at)
            VALUES ($jid, $deviceId, NULL, $pushname, NULL, 0, $updatedAt)
            ON CONFLICT(jid, device_id) DO UPDATE SET
                pushname = COALESCE(NULLIF(excluded.pushname, ''), contacts.pushname),
                updated_at = excluded.updated_at;
            """;
        var jidParam = cmd.Parameters.Add("$jid", SqliteType.Text);
        cmd.Parameters.AddWithValue("$deviceId", _deviceId);
        var pushParam = cmd.Parameters.Add("$pushname", SqliteType.Text);
        cmd.Parameters.AddWithValue("$updatedAt", now);

        foreach (var p in pushnames)
        {
            if (string.IsNullOrWhiteSpace(p.Id))
            {
                continue;
            }

            jidParam.Value = SqliteProtocolStore.NormalizePhoneJid(p.Id);
            pushParam.Value = p.Pushname ?? string.Empty;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task UpsertInlineContactsInternalAsync(
        SqliteConnection conn,
        IReadOnlyList<InlineContactRecord> contacts,
        long now,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO contacts (jid, device_id, name, pushname, phone_number, is_address_book, updated_at)
            VALUES ($jid, $deviceId, $name, $pushname, $phoneNumber, $isAddressBook, $updatedAt)
            ON CONFLICT(jid, device_id) DO UPDATE SET
                name = COALESCE(NULLIF(excluded.name, ''), contacts.name),
                pushname = COALESCE(NULLIF(excluded.pushname, ''), contacts.pushname),
                phone_number = COALESCE(NULLIF(excluded.phone_number, ''), contacts.phone_number),
                is_address_book = MAX(contacts.is_address_book, excluded.is_address_book),
                updated_at = excluded.updated_at;
            """;
        var jidParam = cmd.Parameters.Add("$jid", SqliteType.Text);
        cmd.Parameters.AddWithValue("$deviceId", _deviceId);
        var nameParam = cmd.Parameters.Add("$name", SqliteType.Text);
        var pushParam = cmd.Parameters.Add("$pushname", SqliteType.Text);
        var phoneParam = cmd.Parameters.Add("$phoneNumber", SqliteType.Text);
        var addrParam = cmd.Parameters.Add("$isAddressBook", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$updatedAt", now);

        foreach (var c in contacts)
        {
            string? normalizedPn = string.IsNullOrWhiteSpace(c.PnJid)
                ? null
                : SqliteProtocolStore.NormalizePhoneJid(c.PnJid);
            string? keyJid = !string.IsNullOrWhiteSpace(c.LidJid) ? c.LidJid : normalizedPn;
            if (string.IsNullOrWhiteSpace(keyJid))
            {
                continue;
            }

            string? displayName = !string.IsNullOrWhiteSpace(c.FullName)
                ? c.FullName
                : (!string.IsNullOrWhiteSpace(c.FirstName) ? c.FirstName : null);

            jidParam.Value = keyJid;
            nameParam.Value = (object?)displayName ?? DBNull.Value;
            pushParam.Value = (object?)c.Username ?? DBNull.Value;
            phoneParam.Value = (object?)normalizedPn ?? DBNull.Value;
            addrParam.Value = displayName != null ? 1 : 0;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task UpsertConversationsInternalAsync(
        SqliteConnection conn,
        IReadOnlyList<ConversationRecord> conversations,
        long now,
        CancellationToken ct)
    {
        using var chatCmd = conn.CreateCommand();
        chatCmd.CommandText = """
            INSERT INTO chats (id, device_id, name, unread_count, last_msg_timestamp, archived, updated_at)
            VALUES ($id, $deviceId, $name, $unread, $lastTs, $archived, $updatedAt)
            ON CONFLICT(id, device_id) DO UPDATE SET
                name = COALESCE(NULLIF(excluded.name, ''), chats.name),
                unread_count = excluded.unread_count,
                last_msg_timestamp = MAX(chats.last_msg_timestamp, excluded.last_msg_timestamp),
                archived = excluded.archived,
                updated_at = excluded.updated_at;
            """;
        var idParam = chatCmd.Parameters.Add("$id", SqliteType.Text);
        chatCmd.Parameters.AddWithValue("$deviceId", _deviceId);
        var nameParam = chatCmd.Parameters.Add("$name", SqliteType.Text);
        var unreadParam = chatCmd.Parameters.Add("$unread", SqliteType.Integer);
        var lastTsParam = chatCmd.Parameters.Add("$lastTs", SqliteType.Integer);
        var archivedParam = chatCmd.Parameters.Add("$archived", SqliteType.Integer);
        chatCmd.Parameters.AddWithValue("$updatedAt", now);

        using var msgCmd = CreateUpsertMessageCommand(conn);

        foreach (var conv in conversations)
        {
            if (string.IsNullOrWhiteSpace(conv.Id))
            {
                continue;
            }

            idParam.Value = conv.Id;
            nameParam.Value = (object?)conv.Name ?? DBNull.Value;
            unreadParam.Value = unchecked((long)conv.UnreadCount);
            lastTsParam.Value = unchecked((long)conv.LastMsgTimestamp);
            archivedParam.Value = conv.Archived ? 1 : 0;
            await chatCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            ulong? minTs = null;
            ulong? maxTs = null;

            foreach (var msg in conv.Messages)
            {
                if (string.IsNullOrWhiteSpace(msg.Id))
                {
                    continue;
                }

                var normalizedMsg = string.IsNullOrEmpty(msg.RemoteJid)
                    ? msg with { RemoteJid = conv.Id }
                    : msg;
                BindMessageParameters(msgCmd, normalizedMsg);
                await msgCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                minTs = minTs.HasValue ? Math.Min(minTs.Value, normalizedMsg.Timestamp) : normalizedMsg.Timestamp;
                maxTs = maxTs.HasValue ? Math.Max(maxTs.Value, normalizedMsg.Timestamp) : normalizedMsg.Timestamp;
            }

            if (minTs.HasValue && maxTs.HasValue)
            {
                await UpsertMessageRangeInternalAsync(conn, conv.Id, minTs.Value, maxTs.Value, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task UpsertMessageRangeInternalAsync(
        SqliteConnection conn,
        string chatJid,
        ulong minTimestamp,
        ulong maxTimestamp,
        CancellationToken ct)
    {
        if (minTimestamp > maxTimestamp)
        {
            (minTimestamp, maxTimestamp) = (maxTimestamp, minTimestamp);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO active_message_ranges (chat_jid, action, device_id, min_timestamp, max_timestamp)
            VALUES ($chatJid, $action, $deviceId, $minTs, $maxTs)
            ON CONFLICT(chat_jid, action, device_id) DO UPDATE SET
                min_timestamp = MIN(active_message_ranges.min_timestamp, excluded.min_timestamp),
                max_timestamp = MAX(active_message_ranges.max_timestamp, excluded.max_timestamp);
            """;
        cmd.Parameters.AddWithValue("$chatJid", chatJid);
        cmd.Parameters.AddWithValue("$action", HistoryAction);
        cmd.Parameters.AddWithValue("$deviceId", _deviceId);
        cmd.Parameters.AddWithValue("$minTs", unchecked((long)minTimestamp));
        cmd.Parameters.AddWithValue("$maxTs", unchecked((long)maxTimestamp));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private SqliteCommand CreateUpsertMessageCommand(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (
                chat_jid, id, device_id, sender_jid, from_me, timestamp, type,
                text, mime_type, direct_path, media_key, file_hash, file_enc_hash
            )
            VALUES (
                $chatJid, $id, $deviceId, $senderJid, $fromMe, $timestamp, $type,
                $text, $mimeType, $directPath, $mediaKey, $fileHash, $fileEncHash
            )
            ON CONFLICT(chat_jid, id, device_id) DO UPDATE SET
                sender_jid = COALESCE(excluded.sender_jid, messages.sender_jid),
                text = COALESCE(excluded.text, messages.text),
                mime_type = COALESCE(excluded.mime_type, messages.mime_type),
                direct_path = COALESCE(excluded.direct_path, messages.direct_path),
                media_key = COALESCE(excluded.media_key, messages.media_key),
                file_hash = COALESCE(excluded.file_hash, messages.file_hash),
                file_enc_hash = COALESCE(excluded.file_enc_hash, messages.file_enc_hash);
            """;
        cmd.Parameters.Add("$chatJid", SqliteType.Text);
        cmd.Parameters.Add("$id", SqliteType.Text);
        cmd.Parameters.AddWithValue("$deviceId", _deviceId);
        cmd.Parameters.Add("$senderJid", SqliteType.Text);
        cmd.Parameters.Add("$fromMe", SqliteType.Integer);
        cmd.Parameters.Add("$timestamp", SqliteType.Integer);
        cmd.Parameters.Add("$type", SqliteType.Integer);
        cmd.Parameters.Add("$text", SqliteType.Text);
        cmd.Parameters.Add("$mimeType", SqliteType.Text);
        cmd.Parameters.Add("$directPath", SqliteType.Text);
        cmd.Parameters.Add("$mediaKey", SqliteType.Blob);
        cmd.Parameters.Add("$fileHash", SqliteType.Blob);
        cmd.Parameters.Add("$fileEncHash", SqliteType.Blob);
        return cmd;
    }

    private static void BindMessageParameters(SqliteCommand cmd, MessageRecord msg)
    {
        cmd.Parameters["$chatJid"].Value = msg.RemoteJid;
        cmd.Parameters["$id"].Value = msg.Id;
        cmd.Parameters["$senderJid"].Value = (object?)msg.SenderJid ?? DBNull.Value;
        cmd.Parameters["$fromMe"].Value = msg.FromMe ? 1 : 0;
        cmd.Parameters["$timestamp"].Value = unchecked((long)msg.Timestamp);
        cmd.Parameters["$type"].Value = (int)msg.Type;
        cmd.Parameters["$text"].Value = (object?)msg.Text ?? DBNull.Value;
        cmd.Parameters["$mimeType"].Value = (object?)msg.MimeType ?? DBNull.Value;
        cmd.Parameters["$directPath"].Value = (object?)msg.DirectPath ?? DBNull.Value;
        cmd.Parameters["$mediaKey"].Value = (object?)msg.MediaKey ?? DBNull.Value;
        cmd.Parameters["$fileHash"].Value = (object?)msg.FileHash ?? DBNull.Value;
        cmd.Parameters["$fileEncHash"].Value = (object?)msg.FileEncHash ?? DBNull.Value;
    }
}
