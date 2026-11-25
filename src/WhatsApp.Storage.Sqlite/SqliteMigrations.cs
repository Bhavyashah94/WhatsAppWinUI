using Microsoft.Data.Sqlite;

namespace WhatsApp.Storage.Sqlite;

/// <summary>
/// Versioned SQL schema migrations unifying WhatsApp Web IndexedDB object stores
/// (model-storage, signal-storage) and whatsapp-rust multi-account relational tables.
/// </summary>
internal static class SqliteMigrations
{
    internal sealed record Migration(int Version, string Name, string Sql);

    internal static IReadOnlyList<Migration> All { get; } =
    [
        new(1, "001_initial_schema", """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                applied_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS device (
                id INTEGER PRIMARY KEY NOT NULL,
                lid TEXT,
                pn TEXT,
                lid_migrated INTEGER NOT NULL DEFAULT 0,
                registration_id INTEGER NOT NULL,
                noise_key BLOB NOT NULL,
                identity_key BLOB NOT NULL,
                signed_pre_key BLOB NOT NULL,
                signed_pre_key_id INTEGER NOT NULL,
                signed_pre_key_signature BLOB NOT NULL,
                adv_secret_key BLOB NOT NULL,
                push_name TEXT NOT NULL DEFAULT '',
                server_static_public BLOB,
                edge_routing_info BLOB,
                next_pre_key_id INTEGER NOT NULL DEFAULT 1,
                updated_at INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS signal_identities (
                address TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                identity_key BLOB NOT NULL,
                PRIMARY KEY (address, device_id)
            );

            CREATE TABLE IF NOT EXISTS signal_sessions (
                address TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                record BLOB NOT NULL,
                PRIMARY KEY (address, device_id)
            );

            CREATE TABLE IF NOT EXISTS prekeys (
                id INTEGER NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                key_pair BLOB NOT NULL,
                uploaded INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (id, device_id)
            );

            CREATE TABLE IF NOT EXISTS sender_keys (
                group_jid TEXT NOT NULL,
                sender_address TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                record BLOB NOT NULL,
                PRIMARY KEY (group_jid, sender_address, device_id)
            );

            CREATE TABLE IF NOT EXISTS app_state_keys (
                key_id BLOB NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                key_data BLOB NOT NULL,
                PRIMARY KEY (key_id, device_id)
            );

            CREATE TABLE IF NOT EXISTS app_state_versions (
                collection TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                version INTEGER NOT NULL DEFAULT 0,
                lt_hash BLOB NOT NULL,
                PRIMARY KEY (collection, device_id)
            );

            CREATE TABLE IF NOT EXISTS app_state_mutation_macs (
                collection TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                index_mac BLOB NOT NULL,
                value_mac BLOB NOT NULL,
                PRIMARY KEY (collection, device_id, index_mac)
            );

            CREATE TABLE IF NOT EXISTS lid_pn_mapping (
                lid TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                phone_number TEXT NOT NULL,
                learning_source TEXT NOT NULL DEFAULT '',
                updated_at INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (lid, device_id)
            );
            CREATE INDEX IF NOT EXISTS idx_lid_pn_phone
                ON lid_pn_mapping (device_id, phone_number);

            CREATE TABLE IF NOT EXISTS device_list_cache (
                user_jid TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                devices_json TEXT NOT NULL,
                updated_at INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (user_jid, device_id)
            );

            CREATE TABLE IF NOT EXISTS tc_tokens (
                jid TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                token BLOB NOT NULL,
                token_timestamp INTEGER NOT NULL,
                updated_at INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (jid, device_id)
            );

            CREATE TABLE IF NOT EXISTS msg_secrets (
                message_id TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                secret BLOB NOT NULL,
                msg_timestamp INTEGER,
                expires_at INTEGER,
                PRIMARY KEY (message_id, device_id)
            );
            CREATE INDEX IF NOT EXISTS idx_msg_secrets_expires
                ON msg_secrets (device_id, expires_at)
                WHERE expires_at IS NOT NULL;

            CREATE TABLE IF NOT EXISTS chats (
                id TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                name TEXT,
                unread_count INTEGER NOT NULL DEFAULT 0,
                last_msg_timestamp INTEGER NOT NULL DEFAULT 0,
                archived INTEGER NOT NULL DEFAULT 0,
                updated_at INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (id, device_id)
            );
            CREATE INDEX IF NOT EXISTS idx_chats_recent
                ON chats (device_id, last_msg_timestamp DESC);

            CREATE TABLE IF NOT EXISTS contacts (
                jid TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                name TEXT,
                pushname TEXT,
                phone_number TEXT,
                is_address_book INTEGER NOT NULL DEFAULT 0,
                updated_at INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (jid, device_id)
            );
            CREATE INDEX IF NOT EXISTS idx_contacts_phone
                ON contacts (device_id, phone_number);

            CREATE TABLE IF NOT EXISTS groups (
                group_jid TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                subject TEXT NOT NULL,
                owner_jid TEXT,
                creation INTEGER NOT NULL DEFAULT 0,
                description TEXT,
                updated_at INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (group_jid, device_id)
            );

            CREATE TABLE IF NOT EXISTS messages (
                chat_jid TEXT NOT NULL,
                id TEXT NOT NULL,
                device_id INTEGER NOT NULL DEFAULT 1,
                sender_jid TEXT,
                from_me INTEGER NOT NULL DEFAULT 0,
                timestamp INTEGER NOT NULL,
                type INTEGER NOT NULL,
                text TEXT,
                mime_type TEXT,
                direct_path TEXT,
                media_key BLOB,
                file_hash BLOB,
                file_enc_hash BLOB,
                PRIMARY KEY (chat_jid, id, device_id)
            );
            CREATE INDEX IF NOT EXISTS idx_messages_chat_ts
                ON messages (device_id, chat_jid, timestamp DESC);

            CREATE TABLE IF NOT EXISTS active_message_ranges (
                chat_jid TEXT NOT NULL,
                action TEXT NOT NULL DEFAULT 'history',
                device_id INTEGER NOT NULL DEFAULT 1,
                min_timestamp INTEGER NOT NULL,
                max_timestamp INTEGER NOT NULL,
                PRIMARY KEY (chat_jid, action, device_id)
            );
            """)
    ];

    internal static async Task ApplyPendingAsync(SqliteConnection connection, CancellationToken ct)
    {
        using (var bootstrapCmd = connection.CreateCommand())
        {
            bootstrapCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY NOT NULL,
                    name TEXT NOT NULL,
                    applied_at INTEGER NOT NULL
                );
                """;
            await bootstrapCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var applied = new HashSet<int>();
        using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = "SELECT version FROM schema_migrations;";
            using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        foreach (var migration in All)
        {
            if (applied.Contains(migration.Version))
            {
                continue;
            }

            using var txCmd = connection.CreateCommand();
            txCmd.CommandText = "BEGIN IMMEDIATE;";
            await txCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            try
            {
                using (var sqlCmd = connection.CreateCommand())
                {
                    sqlCmd.CommandText = migration.Sql;
                    await sqlCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                using (var recordCmd = connection.CreateCommand())
                {
                    recordCmd.CommandText = """
                        INSERT INTO schema_migrations (version, name, applied_at)
                        VALUES ($version, $name, $appliedAt);
                        """;
                    recordCmd.Parameters.AddWithValue("$version", migration.Version);
                    recordCmd.Parameters.AddWithValue("$name", migration.Name);
                    recordCmd.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    await recordCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                using var commitCmd = connection.CreateCommand();
                commitCmd.CommandText = "COMMIT;";
                await commitCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    using var rollbackCmd = connection.CreateCommand();
                    rollbackCmd.CommandText = "ROLLBACK;";
                    await rollbackCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore secondary rollback failure while preserving root exception.
                }
                throw;
            }
        }
    }
}
