using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;
using PbHistorySyncType = global::Whatsapp.HistorySync.Types.HistorySyncType;

namespace WhatsApp.Storage.Sqlite.Tests;

[TestClass]
public sealed class SqliteHistoryStoreTests
{
    private static string CreateTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"wa_history_test_{Guid.NewGuid():N}.db");

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
    public async Task UpsertBatchAsync_PersistsContactsChatsMessagesAndStitchesRanges()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            byte[] mediaKey = RandomNumberGenerator.GetBytes(32);
            byte[] fileHash = RandomNumberGenerator.GetBytes(32);
            byte[] fileEncHash = RandomNumberGenerator.GetBytes(32);

            var batch = new HistorySyncBatch(
                PbHistorySyncType.InitialBootstrap,
                ChunkOrder: 1,
                Progress: 50,
                LidMappings: [new LidPnMappingRecord("15550100101@c.us", "100000000000101@lid")],
                Pushnames: [new PushnameRecord("100000000000101@lid", "Alice")],
                InlineContacts:
                [
                    new InlineContactRecord("15550100102@s.whatsapp.net", "100000000000102@lid", "Bob Example", "Bob", "bob")
                ],
                Conversations:
                [
                    new ConversationRecord(
                        Id: "120363000000000101@g.us",
                        Name: "Project Group",
                        UnreadCount: 2,
                        LastMsgTimestamp: 1_700_000_300,
                        Archived: false,
                        Messages:
                        [
                            new MessageRecord("M1", "120363000000000101@g.us", "100000000000101@lid", false, 1_700_000_100, MessageType.Text, "First"),
                            new MessageRecord("M2", "120363000000000101@g.us", "100000000000102@lid", true, 1_700_000_200, MessageType.Sticker, null, "image/webp", "/v/t62/s.enc", mediaKey, fileHash, fileEncHash),
                            new MessageRecord("M3", "120363000000000101@g.us", "100000000000101@lid", false, 1_700_000_300, MessageType.Text, "Third")
                        ])
                ]);

            await using (var first = new SqliteBackend(dbPath))
            {
                await first.History.UpsertBatchAsync(batch);
            }

            await using var reopened = new SqliteBackend(dbPath);
            var mappings = await reopened.Protocol.LoadLidPnMappingsAsync();
            Assert.AreEqual(1, mappings.Count);
            Assert.AreEqual("15550100101@s.whatsapp.net", mappings[0].PnJid);

            var page = await reopened.History.GetMessagesAsync("120363000000000101@g.us", beforeTimestamp: null, limit: 10);
            Assert.AreEqual(3, page.Count);
            Assert.AreEqual("M3", page[0].Id);
            Assert.AreEqual("M2", page[1].Id);
            Assert.AreEqual(MessageType.Sticker, page[1].Type);
            Assert.AreEqual("image/webp", page[1].MimeType);
            CollectionAssert.AreEqual(mediaKey, page[1].MediaKey);
            CollectionAssert.AreEqual(fileHash, page[1].FileHash);
            CollectionAssert.AreEqual(fileEncHash, page[1].FileEncHash);
            Assert.AreEqual("M1", page[2].Id);

            var range = await reopened.History.GetMessageRangeAsync("120363000000000101@g.us");
            Assert.IsNotNull(range);
            Assert.AreEqual(1_700_000_100UL, range.MinTimestamp);
            Assert.AreEqual(1_700_000_300UL, range.MaxTimestamp);

            long contactsCount = await reopened.Runner.RunReadAsync(async (conn, ct) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM contacts WHERE device_id = 1;";
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            });
            Assert.AreEqual(2L, contactsCount);
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }

    [TestMethod]
    public async Task SaveMessageAsync_And_GetMessagesAsync_SupportsCursorPaginationAndIdempotency()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            const string chatJid = "15550100199@s.whatsapp.net";
            await using var backend = new SqliteBackend(dbPath);

            for (ulong i = 1; i <= 5; i++)
            {
                await backend.History.SaveMessageAsync(new MessageRecord(
                    $"MSG_{i}",
                    chatJid,
                    chatJid,
                    i % 2 == 0,
                    1_700_001_000 + (i * 10),
                    MessageType.Text,
                    $"Body {i}"));
            }

            // Re-saving same (chat_jid, id) updates instead of duplicating rows
            await backend.History.SaveMessageAsync(new MessageRecord(
                "MSG_5",
                chatJid,
                chatJid,
                false,
                1_700_001_050,
                MessageType.Text,
                "Body 5 edited"));

            var page1 = await backend.History.GetMessagesAsync(chatJid, beforeTimestamp: null, limit: 2);
            Assert.AreEqual(2, page1.Count);
            Assert.AreEqual("MSG_5", page1[0].Id);
            Assert.AreEqual("Body 5 edited", page1[0].Text);
            Assert.AreEqual("MSG_4", page1[1].Id);

            var page2 = await backend.History.GetMessagesAsync(chatJid, beforeTimestamp: page1[^1].Timestamp, limit: 2);
            Assert.AreEqual(2, page2.Count);
            Assert.AreEqual("MSG_3", page2[0].Id);
            Assert.AreEqual("MSG_2", page2[1].Id);

            var page3 = await backend.History.GetMessagesAsync(chatJid, beforeTimestamp: page2[^1].Timestamp, limit: 2);
            Assert.AreEqual(1, page3.Count);
            Assert.AreEqual("MSG_1", page3[0].Id);
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }

    [TestMethod]
    public async Task UpdateMessageRangeAsync_ExpandsMonotonicallyAndIsolatesByDeviceId()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            const string chatJid = "100000000000101@lid";
            await using var dev1 = new SqliteBackend(dbPath, deviceId: 1);
            await using var dev2 = new SqliteBackend(dbPath, deviceId: 2);

            await dev1.History.UpdateMessageRangeAsync(new ActiveMessageRange(chatJid, 200, 300));
            // Narrower PDO response must NOT shrink stored bounds
            await dev1.History.UpdateMessageRangeAsync(new ActiveMessageRange(chatJid, 220, 280));
            // Older + newer backfill stitches full span
            await dev1.History.UpdateMessageRangeAsync(new ActiveMessageRange(chatJid, 100, 450));

            var range1 = await dev1.History.GetMessageRangeAsync(chatJid);
            Assert.IsNotNull(range1);
            Assert.AreEqual(100UL, range1.MinTimestamp);
            Assert.AreEqual(450UL, range1.MaxTimestamp);
            Assert.IsNull(await dev2.History.GetMessageRangeAsync(chatJid));
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }

    [TestMethod]
    public async Task LiveWebShapeCompatibility_RoundTripsWebIndexedDbFormats()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            await using var backend = new SqliteBackend(dbPath);

            // Signal addresses matching web.whatsapp.com signal-storage key shapes (`<user>@lid.0`, `group::sender@lid.0`)
            const string webIdentityAddr = "100000000000101@lid.0";
            byte[] peerPub = Curve25519.GenerateKeyPair().PublicKey;
            await backend.Signal.SaveIdentityAsync(webIdentityAddr, peerPub);
            CollectionAssert.AreEqual(peerPub, await backend.Signal.GetIdentityAsync(webIdentityAddr));

            var senderRec = new SenderKeyRecord();
            _ = SenderKeyCipher.CreateDistribution(senderRec);
            byte[] senderBytes = SignalStateCodec.SerializeSenderKeyRecord(senderRec);
            await backend.Signal.SaveSenderKeyAsync("120363000000000101@g.us", webIdentityAddr, senderBytes);
            CollectionAssert.AreEqual(senderBytes, await backend.Signal.GetSenderKeyAsync("120363000000000101@g.us", webIdentityAddr));

            // AppState shapes matching web.whatsapp.com model-storage (`6B` sync-keys keyId, `128B` ltHash, `32B` MACs)
            byte[] keyId6 = RandomNumberGenerator.GetBytes(6);
            byte[] keyData48 = RandomNumberGenerator.GetBytes(48);
            byte[] ltHash128 = RandomNumberGenerator.GetBytes(128);
            byte[] indexMac32 = RandomNumberGenerator.GetBytes(32);
            byte[] valueMac32 = RandomNumberGenerator.GetBytes(32);

            await backend.AppSync.SaveSyncKeyAsync(keyId6, keyData48);
            await backend.AppSync.CommitPatchAsync(AppStateCollections.CriticalBlock, 42, ltHash128, [(indexMac32, valueMac32)], []);

            CollectionAssert.AreEqual(keyData48, await backend.AppSync.GetSyncKeyAsync(keyId6));
            var (ver, hash) = await backend.AppSync.GetCollectionStateAsync(AppStateCollections.CriticalBlock);
            Assert.AreEqual(42UL, ver);
            CollectionAssert.AreEqual(ltHash128, hash);
            CollectionAssert.AreEqual(valueMac32, await backend.AppSync.GetMutationMacAsync(AppStateCollections.CriticalBlock, indexMac32));

            // Model-storage `@c.us` phoneNumber normalization & active-message-ranges `(chatId, "history")`
            await backend.History.UpsertBatchAsync(new HistorySyncBatch(
                PbHistorySyncType.Recent,
                1,
                100,
                [new LidPnMappingRecord("15550100105@c.us", "100000000000105@lid")],
                [],
                [],
                [
                    new ConversationRecord(
                        "15550100105@c.us",
                        "Web Format Chat",
                        0,
                        1_700_002_000,
                        false,
                        [new MessageRecord("false_15550100105@c.us_ABC123", "15550100105@c.us", "15550100105@c.us", false, 1_700_002_000, MessageType.Text, "hi")])
                ]));

            var loadedMappings = await backend.Protocol.LoadLidPnMappingsAsync();
            Assert.AreEqual("15550100105@s.whatsapp.net", loadedMappings[0].PnJid);
            var activeRange = await backend.History.GetMessageRangeAsync("15550100105@c.us");
            Assert.IsNotNull(activeRange);
            Assert.AreEqual(1_700_002_000UL, activeRange.MaxTimestamp);
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }
}
