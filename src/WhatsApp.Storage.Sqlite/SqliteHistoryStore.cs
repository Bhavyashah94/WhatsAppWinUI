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

    public ValueTask<int> GetMessageCountAsync(string? chatJid = null, CancellationToken ct = default)
    {
        return new(_runner.RunReadAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = string.IsNullOrWhiteSpace(chatJid)
                ? "SELECT COUNT(*) FROM messages WHERE device_id = $deviceId;"
                : "SELECT COUNT(*) FROM messages WHERE device_id = $deviceId AND chat_jid = $chatJid;";
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            if (!string.IsNullOrWhiteSpace(chatJid))
            {
                cmd.Parameters.AddWithValue("$chatJid", chatJid);
            }
            object? scalar = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return scalar is long l ? checked((int)l) : 0;
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
                  SELECT id, chat_jid, sender_jid, from_me, timestamp, type, text, mime_type, direct_path, media_key, file_hash, file_enc_hash,
                         quoted_message_id, quoted_sender_jid, quoted_text, edited_timestamp, is_revoked, file_length, reactions_json, interactive_json
                  FROM messages
                  WHERE device_id = $deviceId AND chat_jid = $chatJid AND timestamp < $beforeTs
                  ORDER BY timestamp DESC, id DESC
                  LIMIT $limit;
                  """
                : """
                  SELECT id, chat_jid, sender_jid, from_me, timestamp, type, text, mime_type, direct_path, media_key, file_hash, file_enc_hash,
                         quoted_message_id, quoted_sender_jid, quoted_text, edited_timestamp, is_revoked, file_length, reactions_json, interactive_json
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
                results.Add(ReadMessageRecord(reader));
            }

            return results;
        }, ct));
    }

    public ValueTask<MessageRecord?> GetMessageByIdAsync(
        string messageId,
        string? chatJid = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        return new(_runner.RunReadAsync<MessageRecord?>(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            string chatFilter = string.IsNullOrWhiteSpace(chatJid) ? string.Empty : " AND chat_jid = $chatJid";
            cmd.CommandText = $"""
                SELECT id, chat_jid, sender_jid, from_me, timestamp, type, text, mime_type, direct_path, media_key, file_hash, file_enc_hash,
                       quoted_message_id, quoted_sender_jid, quoted_text, edited_timestamp, is_revoked, file_length, reactions_json, interactive_json
                FROM messages
                WHERE device_id = $deviceId AND id = $id{chatFilter}
                ORDER BY timestamp DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$id", messageId);
            if (!string.IsNullOrWhiteSpace(chatJid))
            {
                cmd.Parameters.AddWithValue("$chatJid", chatJid);
            }

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadMessageRecord(reader) : null;
        }, ct));
    }

    public ValueTask<IReadOnlyList<ChatSummary>> GetChatsAsync(
        int limit = 50,
        int offset = 0,
        bool? archived = null,
        CancellationToken ct = default)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");

        return new(_runner.RunReadAsync<IReadOnlyList<ChatSummary>>(async (conn, token) =>
        {
            var results = new List<ChatSummary>();
            using var cmd = conn.CreateCommand();
            string archiveFilter = archived.HasValue ? " AND c.archived = $archived" : string.Empty;
            cmd.CommandText = $"""
                SELECT c.id,
                       COALESCE(
                           NULLIF(c.name, ''),
                           NULLIF((SELECT g.subject FROM groups g WHERE g.group_jid = c.id AND g.device_id = c.device_id LIMIT 1), ''),
                           NULLIF((SELECT co.name FROM contacts co WHERE co.jid = c.id AND co.device_id = c.device_id LIMIT 1), ''),
                           NULLIF((SELECT co2.name FROM lid_pn_mapping lm JOIN contacts co2 ON co2.device_id = c.device_id AND co2.jid = CASE WHEN lm.lid = c.id THEN lm.phone_number ELSE lm.lid END WHERE lm.device_id = c.device_id AND (lm.lid = c.id OR lm.phone_number = c.id) AND NULLIF(co2.name, '') IS NOT NULL LIMIT 1), ''),
                           NULLIF((SELECT co.pushname FROM contacts co WHERE co.jid = c.id AND co.device_id = c.device_id LIMIT 1), ''),
                           NULLIF((SELECT co2.pushname FROM lid_pn_mapping lm JOIN contacts co2 ON co2.device_id = c.device_id AND co2.jid = CASE WHEN lm.lid = c.id THEN lm.phone_number ELSE lm.lid END WHERE lm.device_id = c.device_id AND (lm.lid = c.id OR lm.phone_number = c.id) AND NULLIF(co2.pushname, '') IS NOT NULL LIMIT 1), '')
                       ) AS display_name,
                       c.unread_count, c.last_msg_timestamp, c.archived, c.pinned,
                       c.mute_end_timestamp, c.draft_text, c.draft_timestamp_ms,
                       m.text, m.type, c.is_favorite
                FROM chats c
                LEFT JOIN messages m ON m.chat_jid = c.id AND m.device_id = c.device_id
                  AND m.rowid = (
                      SELECT m2.rowid FROM messages m2
                      WHERE m2.chat_jid = c.id AND m2.device_id = c.device_id
                      ORDER BY m2.timestamp DESC, m2.id DESC
                      LIMIT 1
                  )
                WHERE c.device_id = $deviceId{archiveFilter}
                ORDER BY c.pinned DESC, c.last_msg_timestamp DESC, c.id ASC
                LIMIT $limit OFFSET $offset;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            if (archived.HasValue) cmd.Parameters.AddWithValue("$archived", archived.Value ? 1 : 0);
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(new ChatSummary(
                    ChatJid: reader.GetString(0),
                    Name: reader.IsDBNull(1) ? null : reader.GetString(1),
                    UnreadCount: unchecked((uint)Math.Max(0, reader.GetInt64(2))),
                    LastMsgTimestamp: unchecked((ulong)Math.Max(0, reader.GetInt64(3))),
                    Archived: reader.GetInt64(4) != 0,
                    Pinned: reader.GetInt64(5) != 0,
                    MuteEndTimestamp: reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    DraftText: reader.IsDBNull(7) ? null : reader.GetString(7),
                    DraftTimestampMs: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    LastMessageText: reader.IsDBNull(9) ? null : reader.GetString(9),
                    LastMessageType: reader.IsDBNull(10) ? MessageType.Unknown : (MessageType)reader.GetInt32(10),
                    IsFavorite: reader.GetInt64(11) != 0));
            }
            return results;
        }, ct));
    }

    public ValueTask<IReadOnlyList<MessageRecord>> SearchMessagesAsync(
        string query,
        string? chatJid = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        string matchExpr = BuildFtsMatchExpression(query);
        if (string.IsNullOrEmpty(matchExpr))
        {
            return new(Array.Empty<MessageRecord>());
        }

        return new(_runner.RunReadAsync<IReadOnlyList<MessageRecord>>(async (conn, token) =>
        {
            var results = new List<MessageRecord>();
            using var cmd = conn.CreateCommand();
            string chatFilter = string.IsNullOrEmpty(chatJid) ? string.Empty : " AND m.chat_jid = $chatJid";
            cmd.CommandText = $"""
                SELECT m.id, m.chat_jid, m.sender_jid, m.from_me, m.timestamp, m.type, m.text, m.mime_type,
                       m.direct_path, m.media_key, m.file_hash, m.file_enc_hash,
                       m.quoted_message_id, m.quoted_sender_jid, m.quoted_text, m.edited_timestamp,
                       m.is_revoked, m.file_length, m.reactions_json, m.interactive_json
                FROM messages_fts f
                JOIN messages m ON m.rowid = f.rowid
                WHERE m.device_id = $deviceId AND messages_fts MATCH $match{chatFilter}
                ORDER BY m.timestamp DESC, m.id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$match", matchExpr);
            if (!string.IsNullOrEmpty(chatJid)) cmd.Parameters.AddWithValue("$chatJid", chatJid);
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(ReadMessageRecord(reader));
            }
            return results;
        }, ct));
    }

    public ValueTask<IReadOnlyList<ContactRecord>> SearchContactsAsync(
        string query,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        string pattern = string.IsNullOrWhiteSpace(query) ? "%" : $"%{query.Trim()}%";

        return new(_runner.RunReadAsync<IReadOnlyList<ContactRecord>>(async (conn, token) =>
        {
            var results = new List<ContactRecord>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT jid, name, pushname, phone_number, is_address_book
                FROM contacts
                WHERE device_id = $deviceId
                  AND (jid LIKE $pattern OR name LIKE $pattern OR pushname LIKE $pattern OR phone_number LIKE $pattern)
                ORDER BY is_address_book DESC, COALESCE(NULLIF(name, ''), NULLIF(pushname, ''), jid) ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$pattern", pattern);
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(new ContactRecord(
                    Jid: reader.GetString(0),
                    Name: reader.IsDBNull(1) ? null : reader.GetString(1),
                    Pushname: reader.IsDBNull(2) ? null : reader.GetString(2),
                    PhoneNumber: reader.IsDBNull(3) ? null : reader.GetString(3),
                    IsAddressBookContact: reader.GetInt64(4) != 0));
            }
            return results;
        }, ct));
    }

    public ValueTask UpsertContactAsync(ContactRecord contact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentException.ThrowIfNullOrEmpty(contact.Jid);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO contacts (jid, device_id, name, pushname, phone_number, is_address_book, updated_at)
                VALUES ($jid, $deviceId, $name, $pushname, $phoneNumber, $isAddressBook, $updatedAt)
                ON CONFLICT(jid, device_id) DO UPDATE SET
                    name = CASE WHEN COALESCE(excluded.name, '') <> '' THEN excluded.name ELSE contacts.name END,
                    pushname = CASE WHEN COALESCE(excluded.pushname, '') <> '' THEN excluded.pushname ELSE contacts.pushname END,
                    phone_number = CASE WHEN COALESCE(excluded.phone_number, '') <> '' THEN excluded.phone_number ELSE contacts.phone_number END,
                    is_address_book = MAX(contacts.is_address_book, excluded.is_address_book),
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$jid", contact.Jid);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$name", (object?)contact.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pushname", (object?)contact.Pushname ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$phoneNumber", (object?)contact.PhoneNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$isAddressBook", contact.IsAddressBookContact ? 1 : 0);
            cmd.Parameters.AddWithValue("$updatedAt", now);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask RemoveContactAsync(string contactJid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(contactJid);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE contacts
                SET name = NULL,
                    is_address_book = 0,
                    updated_at = $updatedAt
                WHERE device_id = $deviceId AND jid = $jid;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$jid", contactJid);
            cmd.Parameters.AddWithValue("$updatedAt", now);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask UpdateChatStateAsync(
        string chatJid,
        bool? pinned = null,
        bool? archived = null,
        long? muteEndTimestamp = null,
        bool clearMute = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO chats (id, device_id, name, unread_count, last_msg_timestamp, archived, updated_at, pinned, mute_end_timestamp)
                VALUES ($chatJid, $deviceId, NULL, 0, 0, COALESCE($archived, 0), $now, COALESCE($pinned, 0), $muteEnd)
                ON CONFLICT(id, device_id) DO UPDATE SET
                    pinned = CASE WHEN $hasPinned = 1 THEN excluded.pinned ELSE chats.pinned END,
                    archived = CASE WHEN $hasArchived = 1 THEN excluded.archived ELSE chats.archived END,
                    mute_end_timestamp = CASE WHEN $hasMute = 1 THEN excluded.mute_end_timestamp ELSE chats.mute_end_timestamp END,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$chatJid", chatJid);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$hasPinned", pinned.HasValue ? 1 : 0);
            cmd.Parameters.AddWithValue("$pinned", pinned.HasValue ? (pinned.Value ? 1 : 0) : DBNull.Value);
            cmd.Parameters.AddWithValue("$hasArchived", archived.HasValue ? 1 : 0);
            cmd.Parameters.AddWithValue("$archived", archived.HasValue ? (archived.Value ? 1 : 0) : DBNull.Value);
            cmd.Parameters.AddWithValue("$hasMute", (clearMute || muteEndTimestamp.HasValue) ? 1 : 0);
            cmd.Parameters.AddWithValue("$muteEnd", clearMute || !muteEndTimestamp.HasValue ? DBNull.Value : muteEndTimestamp.Value);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask UpsertGroupAsync(GroupMetadataRecord group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentException.ThrowIfNullOrEmpty(group.Id);

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string normalizedId = GroupRegistry.NormalizeGroupId(group.Id);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO groups (group_jid, device_id, subject, owner_jid, creation, description, updated_at)
                VALUES ($jid, $deviceId, $subject, $owner, $creation, $desc, $now)
                ON CONFLICT(group_jid, device_id) DO UPDATE SET
                    subject = CASE WHEN COALESCE(excluded.subject, '') <> '' THEN excluded.subject ELSE groups.subject END,
                    owner_jid = COALESCE(excluded.owner_jid, groups.owner_jid),
                    creation = CASE WHEN excluded.creation > 0 THEN excluded.creation ELSE groups.creation END,
                    description = COALESCE(excluded.description, groups.description),
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$jid", normalizedId);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$subject", group.Subject ?? string.Empty);
            cmd.Parameters.AddWithValue("$owner", (object?)group.Owner ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$creation", unchecked((long)group.Creation));
            cmd.Parameters.AddWithValue("$desc", (object?)group.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask<GroupMetadataRecord?> GetGroupAsync(string groupJid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupJid);
        string normalizedId = GroupRegistry.NormalizeGroupId(groupJid);

        return new(_runner.RunReadAsync<GroupMetadataRecord?>(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT group_jid, subject, owner_jid, creation, description
                FROM groups
                WHERE device_id = $deviceId AND group_jid = $jid;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$jid", normalizedId);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }
            return new GroupMetadataRecord(
                Id: reader.GetString(0),
                Subject: reader.GetString(1),
                Owner: reader.IsDBNull(2) ? null : reader.GetString(2),
                Creation: unchecked((ulong)Math.Max(0, reader.GetInt64(3))),
                Description: reader.IsDBNull(4) ? null : reader.GetString(4));
        }, ct));
    }

    public ValueTask<IReadOnlyList<GroupMetadataRecord>> GetGroupsAsync(CancellationToken ct = default)
    {
        return new(_runner.RunReadAsync<IReadOnlyList<GroupMetadataRecord>>(async (conn, token) =>
        {
            var results = new List<GroupMetadataRecord>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT group_jid, subject, owner_jid, creation, description
                FROM groups
                WHERE device_id = $deviceId
                ORDER BY subject ASC, group_jid ASC;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(new GroupMetadataRecord(
                    Id: reader.GetString(0),
                    Subject: reader.GetString(1),
                    Owner: reader.IsDBNull(2) ? null : reader.GetString(2),
                    Creation: unchecked((ulong)Math.Max(0, reader.GetInt64(3))),
                    Description: reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
            return results;
        }, ct));
    }

    public ValueTask SaveDraftAsync(string chatJid, string? draftText, long timestampMs, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO chats (id, device_id, name, unread_count, last_msg_timestamp, archived, updated_at, draft_text, draft_timestamp_ms)
                VALUES ($chatJid, $deviceId, NULL, 0, 0, 0, $now, $draftText, $draftTs)
                ON CONFLICT(id, device_id) DO UPDATE SET
                    draft_text = excluded.draft_text,
                    draft_timestamp_ms = excluded.draft_timestamp_ms,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$chatJid", chatJid);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$draftText", (object?)draftText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$draftTs", string.IsNullOrEmpty(draftText) ? DBNull.Value : timestampMs);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask ApplyMessageEditAsync(string chatJid, string messageId, string? newText, long editedTimestampSeconds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE messages
                SET text = $newText,
                    edited_timestamp = $editedTs
                WHERE device_id = $deviceId AND chat_jid = $chatJid AND id = $id;
                """;
            cmd.Parameters.AddWithValue("$newText", (object?)newText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$editedTs", editedTimestampSeconds);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$chatJid", chatJid);
            cmd.Parameters.AddWithValue("$id", messageId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask MarkMessageRevokedAsync(string chatJid, string messageId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE messages
                SET is_revoked = 1,
                    text = NULL
                WHERE device_id = $deviceId AND chat_jid = $chatJid AND id = $id;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$chatJid", chatJid);
            cmd.Parameters.AddWithValue("$id", messageId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask UpsertMessageReactionAsync(string chatJid, string messageId, string senderJid, string emoji, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        ArgumentException.ThrowIfNullOrEmpty(senderJid);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            string? existingJson = null;
            using (var readCmd = conn.CreateCommand())
            {
                readCmd.CommandText = "SELECT reactions_json FROM messages WHERE device_id = $deviceId AND chat_jid = $chatJid AND id = $id;";
                readCmd.Parameters.AddWithValue("$deviceId", _deviceId);
                readCmd.Parameters.AddWithValue("$chatJid", chatJid);
                readCmd.Parameters.AddWithValue("$id", messageId);
                object? scalar = await readCmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                existingJson = scalar as string;
            }

            Dictionary<string, string> reactions = string.IsNullOrEmpty(existingJson)
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : (System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(existingJson)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(emoji))
            {
                reactions.Remove(senderJid);
            }
            else
            {
                reactions[senderJid] = emoji;
            }

            string? updatedJson = reactions.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(reactions, new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            using var updCmd = conn.CreateCommand();
            updCmd.CommandText = "UPDATE messages SET reactions_json = $json WHERE device_id = $deviceId AND chat_jid = $chatJid AND id = $id;";
            updCmd.Parameters.AddWithValue("$json", (object?)updatedJson ?? DBNull.Value);
            updCmd.Parameters.AddWithValue("$deviceId", _deviceId);
            updCmd.Parameters.AddWithValue("$chatJid", chatJid);
            updCmd.Parameters.AddWithValue("$id", messageId);
            await updCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask DeleteChatAsync(string chatJid, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            await DeleteRowsForChatAsync(conn, "messages", "chat_jid", chatJid, token).ConfigureAwait(false);
            await DeleteRowsForChatAsync(conn, "active_message_ranges", "chat_jid", chatJid, token).ConfigureAwait(false);
            await DeleteRowsForChatAsync(conn, "label_chats", "chat_jid", chatJid, token).ConfigureAwait(false);
            await DeleteRowsForChatAsync(conn, "chats", "id", chatJid, token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask ClearChatMessagesAsync(string chatJid, bool preserveStarred = true, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using (var msgCmd = conn.CreateCommand())
            {
                msgCmd.CommandText = preserveStarred
                    ? "DELETE FROM messages WHERE device_id = $deviceId AND chat_jid = $chatJid AND is_starred = 0;"
                    : "DELETE FROM messages WHERE device_id = $deviceId AND chat_jid = $chatJid;";
                msgCmd.Parameters.AddWithValue("$deviceId", _deviceId);
                msgCmd.Parameters.AddWithValue("$chatJid", chatJid);
                await msgCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await DeleteRowsForChatAsync(conn, "active_message_ranges", "chat_jid", chatJid, token).ConfigureAwait(false);

            using var chatCmd = conn.CreateCommand();
            chatCmd.CommandText = "UPDATE chats SET unread_count = 0, updated_at = $now WHERE device_id = $deviceId AND id = $chatJid;";
            chatCmd.Parameters.AddWithValue("$deviceId", _deviceId);
            chatCmd.Parameters.AddWithValue("$chatJid", chatJid);
            chatCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await chatCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask UpdateChatReadStateAsync(string chatJid, bool read, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = read
                ? """
                  UPDATE chats SET unread_count = 0, updated_at = $now
                  WHERE device_id = $deviceId AND id = $chatJid;
                  """
                : """
                  INSERT INTO chats (id, device_id, name, unread_count, last_msg_timestamp, archived, updated_at)
                  VALUES ($chatJid, $deviceId, NULL, 1, 0, 0, $now)
                  ON CONFLICT(id, device_id) DO UPDATE SET
                      unread_count = MAX(chats.unread_count, 1),
                      updated_at = excluded.updated_at;
                  """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$chatJid", chatJid);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask DeleteMessageAsync(string chatJid, string messageId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM messages WHERE device_id = $deviceId AND chat_jid = $chatJid AND id = $id;";
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$chatJid", chatJid);
            cmd.Parameters.AddWithValue("$id", messageId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask SetMessageStarredAsync(string chatJid, string messageId, bool starred, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE messages SET is_starred = $starred WHERE device_id = $deviceId AND chat_jid = $chatJid AND id = $id;";
            cmd.Parameters.AddWithValue("$starred", starred ? 1 : 0);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$chatJid", chatJid);
            cmd.Parameters.AddWithValue("$id", messageId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask SetFavoriteChatsAsync(IReadOnlyList<string> chatJids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chatJids);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var resetCmd = conn.CreateCommand())
            {
                resetCmd.CommandText = "UPDATE chats SET is_favorite = 0 WHERE device_id = $deviceId AND is_favorite <> 0;";
                resetCmd.Parameters.AddWithValue("$deviceId", _deviceId);
                await resetCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            using var favCmd = conn.CreateCommand();
            favCmd.CommandText = """
                INSERT INTO chats (id, device_id, name, unread_count, last_msg_timestamp, archived, updated_at, is_favorite)
                VALUES ($chatJid, $deviceId, NULL, 0, 0, 0, $now, 1)
                ON CONFLICT(id, device_id) DO UPDATE SET
                    is_favorite = 1,
                    updated_at = excluded.updated_at;
                """;
            SqliteParameter jidParam = favCmd.Parameters.Add("$chatJid", SqliteType.Text);
            favCmd.Parameters.AddWithValue("$deviceId", _deviceId);
            favCmd.Parameters.AddWithValue("$now", now);

            foreach (string chatJid in chatJids.Where(static s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal))
            {
                jidParam.Value = chatJid;
                await favCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, ct));
    }

    public ValueTask UpsertLabelAsync(LabelRecord label, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentException.ThrowIfNullOrEmpty(label.LabelId);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO labels (label_id, device_id, name, color, predefined_id, order_index, deleted, updated_at)
                    VALUES ($id, $deviceId, $name, $color, $predef, $ord, $deleted, $now)
                    ON CONFLICT(label_id, device_id) DO UPDATE SET
                        name = CASE WHEN excluded.name <> '' THEN excluded.name ELSE labels.name END,
                        color = excluded.color,
                        predefined_id = COALESCE(excluded.predefined_id, labels.predefined_id),
                        order_index = COALESCE(excluded.order_index, labels.order_index),
                        deleted = excluded.deleted,
                        updated_at = excluded.updated_at;
                    """;
                cmd.Parameters.AddWithValue("$id", label.LabelId);
                cmd.Parameters.AddWithValue("$deviceId", _deviceId);
                cmd.Parameters.AddWithValue("$name", label.Name ?? string.Empty);
                cmd.Parameters.AddWithValue("$color", label.Color);
                cmd.Parameters.AddWithValue("$predef", (object?)label.PredefinedId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ord", (object?)label.OrderIndex ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$deleted", label.Deleted ? 1 : 0);
                cmd.Parameters.AddWithValue("$now", now);
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            if (label.Deleted)
            {
                using var cleanupCmd = conn.CreateCommand();
                cleanupCmd.CommandText = "DELETE FROM label_chats WHERE device_id = $deviceId AND label_id = $id;";
                cleanupCmd.Parameters.AddWithValue("$deviceId", _deviceId);
                cleanupCmd.Parameters.AddWithValue("$id", label.LabelId);
                await cleanupCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, ct));
    }

    public ValueTask SetChatLabelAsync(string labelId, string chatJid, bool labeled, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(labelId);
        ArgumentException.ThrowIfNullOrEmpty(chatJid);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            if (labeled)
            {
                cmd.CommandText = """
                    INSERT INTO label_chats (label_id, chat_jid, device_id, updated_at)
                    VALUES ($labelId, $chatJid, $deviceId, $now)
                    ON CONFLICT(label_id, chat_jid, device_id) DO UPDATE SET updated_at = excluded.updated_at;
                    """;
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            else
            {
                cmd.CommandText = "DELETE FROM label_chats WHERE device_id = $deviceId AND label_id = $labelId AND chat_jid = $chatJid;";
            }
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$labelId", labelId);
            cmd.Parameters.AddWithValue("$chatJid", chatJid);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
    }

    public ValueTask ReorderLabelsAsync(IReadOnlyList<int> sortedLabelIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sortedLabelIds);
        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE labels SET order_index = $ord, updated_at = $now WHERE device_id = $deviceId AND label_id = $id;";
            SqliteParameter ordParam = cmd.Parameters.Add("$ord", SqliteType.Integer);
            SqliteParameter idParam = cmd.Parameters.Add("$id", SqliteType.Text);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            for (int i = 0; i < sortedLabelIds.Count; i++)
            {
                ordParam.Value = i;
                idParam.Value = sortedLabelIds[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, ct));
    }

    public ValueTask<IReadOnlyList<LabelRecord>> GetLabelsAsync(bool includeDeleted = false, CancellationToken ct = default)
    {
        return new(_runner.RunReadAsync<IReadOnlyList<LabelRecord>>(async (conn, token) =>
        {
            var results = new List<LabelRecord>();
            using var cmd = conn.CreateCommand();
            string filter = includeDeleted ? string.Empty : " AND deleted = 0";
            cmd.CommandText = $"""
                SELECT label_id, name, color, predefined_id, order_index, deleted
                FROM labels
                WHERE device_id = $deviceId{filter}
                ORDER BY COALESCE(order_index, 2147483647) ASC, label_id ASC;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(new LabelRecord(
                    LabelId: reader.GetString(0),
                    Name: reader.GetString(1),
                    Color: reader.GetInt32(2),
                    PredefinedId: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    OrderIndex: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    Deleted: reader.GetInt64(5) != 0));
            }
            return results;
        }, ct));
    }

    public ValueTask<IReadOnlyList<string>> GetLabelChatJidsAsync(string labelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(labelId);
        return new(_runner.RunReadAsync<IReadOnlyList<string>>(async (conn, token) =>
        {
            var results = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT chat_jid
                FROM label_chats
                WHERE device_id = $deviceId AND label_id = $labelId
                ORDER BY chat_jid ASC;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$labelId", labelId);
            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(reader.GetString(0));
            }
            return results;
        }, ct));
    }

    private async Task DeleteRowsForChatAsync(SqliteConnection conn, string table, string column, string chatJid, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE device_id = $deviceId AND {column} = $chatJid;";
        cmd.Parameters.AddWithValue("$deviceId", _deviceId);
        cmd.Parameters.AddWithValue("$chatJid", chatJid);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string BuildFtsMatchExpression(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }
        string[] tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" AND ", tokens.Select(t => $"\"{t.Replace("\"", "\"\"", StringComparison.Ordinal)}\"*"));
    }

    private static MessageRecord ReadMessageRecord(SqliteDataReader reader) =>
        new(
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
            FileEncHash: reader.IsDBNull(11) ? null : (byte[])reader.GetValue(11),
            QuotedMessageId: reader.IsDBNull(12) ? null : reader.GetString(12),
            QuotedSenderJid: reader.IsDBNull(13) ? null : reader.GetString(13),
            QuotedText: reader.IsDBNull(14) ? null : reader.GetString(14),
            EditedTimestamp: reader.IsDBNull(15) ? null : reader.GetInt64(15),
            IsRevoked: !reader.IsDBNull(16) && reader.GetInt64(16) != 0,
            FileLength: reader.IsDBNull(17) ? null : unchecked((ulong)reader.GetInt64(17)),
            ReactionsJson: reader.IsDBNull(18) ? null : reader.GetString(18),
            InteractiveJson: reader.IsDBNull(19) ? null : reader.GetString(19));

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

            ulong effectiveLastTs = conv.LastMsgTimestamp;
            foreach (var msg in conv.Messages)
            {
                if (!string.IsNullOrWhiteSpace(msg.Id) && msg.Timestamp > effectiveLastTs)
                {
                    effectiveLastTs = msg.Timestamp;
                }
            }

            idParam.Value = conv.Id;
            nameParam.Value = (object?)conv.Name ?? DBNull.Value;
            unreadParam.Value = unchecked((long)conv.UnreadCount);
            lastTsParam.Value = unchecked((long)effectiveLastTs);
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
                text, mime_type, direct_path, media_key, file_hash, file_enc_hash,
                quoted_message_id, quoted_sender_jid, quoted_text, edited_timestamp,
                is_revoked, file_length, reactions_json, interactive_json
            )
            VALUES (
                $chatJid, $id, $deviceId, $senderJid, $fromMe, $timestamp, $type,
                $text, $mimeType, $directPath, $mediaKey, $fileHash, $fileEncHash,
                $quotedMessageId, $quotedSenderJid, $quotedText, $editedTimestamp,
                $isRevoked, $fileLength, $reactionsJson, $interactiveJson
            )
            ON CONFLICT(chat_jid, id, device_id) DO UPDATE SET
                sender_jid = COALESCE(excluded.sender_jid, messages.sender_jid),
                text = COALESCE(excluded.text, messages.text),
                mime_type = COALESCE(excluded.mime_type, messages.mime_type),
                direct_path = COALESCE(excluded.direct_path, messages.direct_path),
                media_key = COALESCE(excluded.media_key, messages.media_key),
                file_hash = COALESCE(excluded.file_hash, messages.file_hash),
                file_enc_hash = COALESCE(excluded.file_enc_hash, messages.file_enc_hash),
                quoted_message_id = COALESCE(excluded.quoted_message_id, messages.quoted_message_id),
                quoted_sender_jid = COALESCE(excluded.quoted_sender_jid, messages.quoted_sender_jid),
                quoted_text = COALESCE(excluded.quoted_text, messages.quoted_text),
                edited_timestamp = COALESCE(excluded.edited_timestamp, messages.edited_timestamp),
                is_revoked = MAX(messages.is_revoked, excluded.is_revoked),
                file_length = COALESCE(excluded.file_length, messages.file_length),
                reactions_json = COALESCE(excluded.reactions_json, messages.reactions_json),
                interactive_json = COALESCE(excluded.interactive_json, messages.interactive_json);
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
        cmd.Parameters.Add("$quotedMessageId", SqliteType.Text);
        cmd.Parameters.Add("$quotedSenderJid", SqliteType.Text);
        cmd.Parameters.Add("$quotedText", SqliteType.Text);
        cmd.Parameters.Add("$editedTimestamp", SqliteType.Integer);
        cmd.Parameters.Add("$isRevoked", SqliteType.Integer);
        cmd.Parameters.Add("$fileLength", SqliteType.Integer);
        cmd.Parameters.Add("$reactionsJson", SqliteType.Text);
        cmd.Parameters.Add("$interactiveJson", SqliteType.Text);
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
        cmd.Parameters["$quotedMessageId"].Value = (object?)msg.QuotedMessageId ?? DBNull.Value;
        cmd.Parameters["$quotedSenderJid"].Value = (object?)msg.QuotedSenderJid ?? DBNull.Value;
        cmd.Parameters["$quotedText"].Value = (object?)msg.QuotedText ?? DBNull.Value;
        cmd.Parameters["$editedTimestamp"].Value = (object?)msg.EditedTimestamp ?? DBNull.Value;
        cmd.Parameters["$isRevoked"].Value = msg.IsRevoked ? 1 : 0;
        cmd.Parameters["$fileLength"].Value = msg.FileLength.HasValue ? unchecked((long)msg.FileLength.Value) : DBNull.Value;
        cmd.Parameters["$reactionsJson"].Value = (object?)msg.ReactionsJson ?? DBNull.Value;
        cmd.Parameters["$interactiveJson"].Value = (object?)msg.InteractiveJson ?? DBNull.Value;
    }
}
