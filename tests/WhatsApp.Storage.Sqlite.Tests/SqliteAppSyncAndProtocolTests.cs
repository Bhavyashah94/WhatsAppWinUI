using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Storage.Sqlite.Tests;

[TestClass]
public sealed class SqliteAppSyncAndProtocolTests
{
    private static string CreateTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"wa_appsync_test_{Guid.NewGuid():N}.db");

    private static void CleanupDbFiles(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string candidate = dbPath + suffix;
            if (File.Exists(candidate))
            {
                try { File.Delete(candidate); } catch { }
            }
        }
    }

    [TestMethod]
    public async Task AppSync_PersistsKeysCollectionVersionLtHashAndMutationMacs()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            byte[] keyId = RandomNumberGenerator.GetBytes(8);
            byte[] keyData = RandomNumberGenerator.GetBytes(32);
            byte[] hash1 = Enumerable.Repeat((byte)0x11, 128).ToArray();
            byte[] idxA = Enumerable.Repeat((byte)0xA1, 32).ToArray();
            byte[] valA = Enumerable.Repeat((byte)0xA2, 32).ToArray();
            byte[] idxB = Enumerable.Repeat((byte)0xB1, 32).ToArray();
            byte[] valB = Enumerable.Repeat((byte)0xB2, 32).ToArray();

            await using (var first = new SqliteBackend(dbPath))
            {
                var (initVer, initHash) = await first.AppSync.GetCollectionStateAsync(AppStateCollections.Regular);
                Assert.AreEqual(0UL, initVer);
                CollectionAssert.AreEqual(new byte[128], initHash);

                await first.AppSync.SaveSyncKeyAsync(keyId, keyData);
                await first.AppSync.CommitPatchAsync(
                    AppStateCollections.Regular,
                    newVersion: 1,
                    hash1,
                    addedMacs: [(idxA, valA), (idxB, valB)],
                    removedIndexMacs: []);
            }

            byte[] hash2 = Enumerable.Repeat((byte)0x22, 128).ToArray();
            byte[] idxC = Enumerable.Repeat((byte)0xC1, 32).ToArray();
            byte[] valC = Enumerable.Repeat((byte)0xC2, 32).ToArray();

            await using var second = new SqliteBackend(dbPath);
            CollectionAssert.AreEqual(keyData, await second.AppSync.GetSyncKeyAsync(keyId));

            await second.AppSync.CommitPatchAsync(
                AppStateCollections.Regular,
                newVersion: 2,
                hash2,
                addedMacs: [(idxC, valC)],
                removedIndexMacs: [idxA]);

            var (ver, ltHash) = await second.AppSync.GetCollectionStateAsync(AppStateCollections.Regular);
            Assert.AreEqual(2UL, ver);
            CollectionAssert.AreEqual(hash2, ltHash);
            Assert.IsNull(await second.AppSync.GetMutationMacAsync(AppStateCollections.Regular, idxA));
            CollectionAssert.AreEqual(valB, await second.AppSync.GetMutationMacAsync(AppStateCollections.Regular, idxB));
            CollectionAssert.AreEqual(valC, await second.AppSync.GetMutationMacAsync(AppStateCollections.Regular, idxC));
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }

    [TestMethod]
    public async Task AppSync_CommitPatchFailureRollsBackVersionAndBothMacChangesAtomically()
    {
        foreach (string triggerTarget in new[]
        {
            "UPDATE ON app_state_versions",
            "DELETE ON app_state_mutation_macs",
            "INSERT ON app_state_mutation_macs"
        })
        {
            string dbPath = CreateTempDbPath();
            try
            {
                byte[] beforeHash = Enumerable.Repeat((byte)0x01, 128).ToArray();
                byte[] afterHash = Enumerable.Repeat((byte)0x02, 128).ToArray();
                byte[] oldIdx = Enumerable.Repeat((byte)0x10, 32).ToArray();
                byte[] oldVal = Enumerable.Repeat((byte)0x11, 32).ToArray();
                byte[] newIdx = Enumerable.Repeat((byte)0x20, 32).ToArray();
                byte[] newVal = Enumerable.Repeat((byte)0x21, 32).ToArray();

                await using var dev1 = new SqliteBackend(dbPath, deviceId: 1);
                await using var dev2 = new SqliteBackend(dbPath, deviceId: 2);

                await dev1.AppSync.CommitPatchAsync(AppStateCollections.Regular, 1, beforeHash, [(oldIdx, oldVal)], []);
                await dev2.AppSync.CommitPatchAsync(AppStateCollections.Regular, 1, beforeHash, [(oldIdx, oldVal)], []);

                await dev1.Runner.RunWriteTransactionAsync(async (conn, ct) =>
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"""
                        CREATE TRIGGER fail_commit BEFORE {triggerTarget}
                        BEGIN
                            SELECT RAISE(ABORT, 'synthetic commit failure');
                        END;
                        """;
                    await cmd.ExecuteNonQueryAsync(ct);
                });

                var ex = await Assert.ThrowsExceptionAsync<SqliteException>(async () =>
                {
                    await dev1.AppSync.CommitPatchAsync(
                        AppStateCollections.Regular,
                        newVersion: 2,
                        afterHash,
                        addedMacs: [(newIdx, newVal)],
                        removedIndexMacs: [oldIdx]);
                });
                StringAssert.Contains(ex.Message, "synthetic commit failure", triggerTarget);

                foreach (var store in new[] { dev1, dev2 })
                {
                    var (ver, ltHash) = await store.AppSync.GetCollectionStateAsync(AppStateCollections.Regular);
                    Assert.AreEqual(1UL, ver, triggerTarget);
                    CollectionAssert.AreEqual(beforeHash, ltHash, triggerTarget);
                    CollectionAssert.AreEqual(oldVal, await store.AppSync.GetMutationMacAsync(AppStateCollections.Regular, oldIdx), triggerTarget);
                    Assert.IsNull(await store.AppSync.GetMutationMacAsync(AppStateCollections.Regular, newIdx), triggerTarget);
                }
            }
            finally
            {
                CleanupDbFiles(dbPath);
            }
        }
    }

    [TestMethod]
    public async Task ProtocolStore_LidPn_DeviceList_And_TcTokens_PersistAndIsolateByDeviceId()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            await using var dev1 = new SqliteBackend(dbPath, deviceId: 1);
            await using var dev2 = new SqliteBackend(dbPath, deviceId: 2);

            await dev1.Protocol.UpsertLidPnMappingsAsync(
            [
                new LidPnMappingRecord("15550100101@c.us", "100000000000101@lid"),
                new LidPnMappingRecord("15550100102@s.whatsapp.net", "100000000000102@lid")
            ]);
            await dev1.Protocol.UpsertLidPnMappingsAsync(
            [
                new LidPnMappingRecord("15550100109@s.whatsapp.net", "100000000000101@lid")
            ]);

            var mappings1 = await dev1.Protocol.LoadLidPnMappingsAsync();
            Assert.AreEqual(2, mappings1.Count);
            Assert.IsTrue(mappings1.Any(m => m.LidJid == "100000000000101@lid" && m.PnJid == "15550100109@s.whatsapp.net"));
            Assert.AreEqual(0, (await dev2.Protocol.LoadLidPnMappingsAsync()).Count);

            await dev1.Protocol.SaveDeviceListAsync("100000000000101@lid", [0u, 2u, 5u]);
            CollectionAssert.AreEqual(new uint[] { 0u, 2u, 5u }, (await dev1.Protocol.GetDeviceListAsync("100000000000101@lid")).ToArray());
            Assert.AreEqual(0, (await dev1.Protocol.GetDeviceListAsync("100000000000999@lid")).Count);
            Assert.AreEqual(0, (await dev2.Protocol.GetDeviceListAsync("100000000000101@lid")).Count);

            byte[] tcToken = [1, 2, 3, 4, 5, 6];
            await dev1.Protocol.StoreTcTokenAsync("100000000000101@lid", tcToken, timestamp: 1_700_000_000);
            CollectionAssert.AreEqual(tcToken, await dev1.Protocol.GetTcTokenAsync("100000000000101@lid"));
            Assert.IsNull(await dev2.Protocol.GetTcTokenAsync("100000000000101@lid"));
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }

    [TestMethod]
    public async Task MsgSecrets_PutGetAndExpirySweep_RespectsCutoffAndNeverExpireRows()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            await using var dev1 = new SqliteBackend(dbPath, deviceId: 1);
            await using var dev2 = new SqliteBackend(dbPath, deviceId: 2);

            byte[] sExpired = [10, 11];
            byte[] sActive = [20, 21];
            byte[] sNever = [30, 31];

            await dev1.MsgSecrets.PutMsgSecretsAsync(
            [
                ("MSG_EXPIRED", sExpired, 1_000L),
                ("MSG_ACTIVE", sActive, 2_500L),
                ("MSG_NEVER", sNever, null)
            ]);
            await dev2.MsgSecrets.PutMsgSecretsAsync([("MSG_EXPIRED", [99], 500L)]);

            var got = await dev1.MsgSecrets.GetMsgSecretAsync("MSG_ACTIVE");
            Assert.IsNotNull(got);
            CollectionAssert.AreEqual(sActive, got.Value.Secret);
            Assert.AreEqual(2_500L, got.Value.Timestamp);

            await dev1.MsgSecrets.DeleteExpiredMsgSecretsAsync(cutoffTimestamp: 1_500L);

            Assert.IsNull(await dev1.MsgSecrets.GetMsgSecretAsync("MSG_EXPIRED"));
            Assert.IsNotNull(await dev1.MsgSecrets.GetMsgSecretAsync("MSG_ACTIVE"));
            var gotNever = await dev1.MsgSecrets.GetMsgSecretAsync("MSG_NEVER");
            Assert.IsNotNull(gotNever);
            Assert.IsNull(gotNever.Value.Timestamp);

            // Device 2's row must remain intact
            Assert.IsNotNull(await dev2.MsgSecrets.GetMsgSecretAsync("MSG_EXPIRED"));
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }
}
