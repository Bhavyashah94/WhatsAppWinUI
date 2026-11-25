using Microsoft.Data.Sqlite;

namespace WhatsApp.Storage.Sqlite.Tests;

[TestClass]
public sealed class SqliteRunnerAndMigrationTests
{
    private static string CreateTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"wa_sqlite_test_{Guid.NewGuid():N}.db");

    private static void CleanupDbFiles(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string candidate = dbPath + suffix;
            if (File.Exists(candidate))
            {
                try { File.Delete(candidate); } catch { /* ignore transient Windows file lock */ }
            }
        }
    }

    [TestMethod]
    public async Task InitializeAsync_EnablesWalPragmasAndCreatesAllNormalizedTables()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            await using var backend = new SqliteBackend(dbPath);
            await backend.InitializeAsync();

            var (journalMode, synchronous, foreignKeys, tables, indexes, migrationCount) =
                await backend.Runner.RunReadAsync(async (conn, ct) =>
                {
                    static async Task<string> ScalarTextAsync(SqliteConnection c, string sql, CancellationToken token)
                    {
                        using var cmd = c.CreateCommand();
                        cmd.CommandText = sql;
                        return Convert.ToString(await cmd.ExecuteScalarAsync(token)) ?? string.Empty;
                    }

                    string jm = await ScalarTextAsync(conn, "PRAGMA journal_mode;", ct);
                    string sync = await ScalarTextAsync(conn, "PRAGMA synchronous;", ct);
                    string fk = await ScalarTextAsync(conn, "PRAGMA foreign_keys;", ct);

                    var tbls = new HashSet<string>(StringComparer.Ordinal);
                    using (var tCmd = conn.CreateCommand())
                    {
                        tCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
                        using var r = await tCmd.ExecuteReaderAsync(ct);
                        while (await r.ReadAsync(ct)) tbls.Add(r.GetString(0));
                    }

                    var idxs = new HashSet<string>(StringComparer.Ordinal);
                    using (var iCmd = conn.CreateCommand())
                    {
                        iCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index';";
                        using var r = await iCmd.ExecuteReaderAsync(ct);
                        while (await r.ReadAsync(ct)) idxs.Add(r.GetString(0));
                    }

                    using var mCmd = conn.CreateCommand();
                    mCmd.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
                    long migCount = Convert.ToInt64(await mCmd.ExecuteScalarAsync(ct));

                    return (jm, sync, fk, tbls, idxs, migCount);
                });

            Assert.AreEqual("wal", journalMode.ToLowerInvariant());
            Assert.AreEqual("1", synchronous, "PRAGMA synchronous=NORMAL corresponds to 1");
            Assert.AreEqual("1", foreignKeys, "PRAGMA foreign_keys=ON corresponds to 1");
            Assert.AreEqual(3L, migrationCount);

            foreach (string expectedTable in new[]
            {
                "schema_migrations",
                "device",
                "signal_identities",
                "signal_sessions",
                "prekeys",
                "signed_prekeys",
                "sender_keys",
                "sender_key_devices",
                "app_state_keys",
                "app_state_versions",
                "app_state_mutation_macs",
                "lid_pn_mapping",
                "device_list_cache",
                "tc_tokens",
                "msg_secrets",
                "chats",
                "contacts",
                "groups",
                "messages",
                "active_message_ranges"
            })
            {
                Assert.IsTrue(tables.Contains(expectedTable), $"Missing expected table: {expectedTable}");
            }

            foreach (string expectedIndex in new[]
            {
                "idx_lid_pn_phone",
                "idx_msg_secrets_expires",
                "idx_chats_recent",
                "idx_contacts_phone",
                "idx_messages_chat_ts"
            })
            {
                Assert.IsTrue(indexes.Contains(expectedIndex), $"Missing expected index: {expectedIndex}");
            }
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_IsIdempotentAcrossReopens()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            await using (var first = new SqliteBackend(dbPath))
            {
                await first.InitializeAsync();
                await first.InitializeAsync();
            }

            await using var second = new SqliteBackend(dbPath);
            await second.InitializeAsync();

            long migrationCount = await second.Runner.RunReadAsync(async (conn, ct) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            });

            Assert.AreEqual(3L, migrationCount);
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }

    [TestMethod]
    public async Task RunWriteTransactionAsync_RollsBackAtomicallyOnException()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            await using var backend = new SqliteBackend(dbPath);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            {
                await backend.Runner.RunWriteTransactionAsync(async (conn, ct) =>
                {
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.CommandText = """
                        INSERT INTO contacts (jid, device_id, name)
                        VALUES ('15550100101@s.whatsapp.net', 1, 'Alice Example');
                        """;
                    await insertCmd.ExecuteNonQueryAsync(ct);

                    throw new InvalidOperationException("Simulated failure mid-transaction");
                });
            });

            long count = await backend.Runner.RunReadAsync(async (conn, ct) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM contacts;";
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            });

            Assert.AreEqual(0L, count, "Failed write transaction must roll back cleanly");
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }

    [TestMethod]
    public async Task RunWriteTransactionAsync_HandlesConcurrentWritersWithoutBusyFailure()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            await using var backend = new SqliteBackend(dbPath);
            await backend.InitializeAsync();

            var tasks = Enumerable.Range(1, 24).Select(i =>
                backend.Runner.RunWriteTransactionAsync(async (conn, ct) =>
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO contacts (jid, device_id, name, phone_number)
                        VALUES ($jid, 1, $name, $phone);
                        """;
                    cmd.Parameters.AddWithValue("$jid", $"100000000000{i:D3}@lid");
                    cmd.Parameters.AddWithValue("$name", $"Contact {i}");
                    cmd.Parameters.AddWithValue("$phone", $"1555010{i:D4}@s.whatsapp.net");
                    await cmd.ExecuteNonQueryAsync(ct);
                }));

            await Task.WhenAll(tasks);

            long count = await backend.Runner.RunReadAsync(async (conn, ct) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM contacts;";
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            });

            Assert.AreEqual(24L, count);
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }
}
