using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Storage.Sqlite.Tests;

[TestClass]
public sealed class RichHistoryAndSearchStorageTests
{
    [TestMethod]
    public async Task RichHistory_ChatsPagination_Drafts_Mutations_And_Fts5Search_WorkEndToEnd()
    {
        await using var backend = new SqliteBackend(":memory:");
        await backend.InitializeAsync();

        await backend.History.UpsertBatchAsync(new HistorySyncBatch(
            Whatsapp.HistorySync.Types.HistorySyncType.InitialBootstrap,
            ChunkOrder: 0,
            Progress: 100,
            LidMappings: [],
            Pushnames: [new PushnameRecord("15550000002@s.whatsapp.net", "Bob Push")],
            InlineContacts: [new InlineContactRecord("15550000001@s.whatsapp.net", "100000000000001@lid", "Alice Johnson", "Alice", "alicej")],
            Conversations: []));

        await backend.History.SaveMessageAsync(new MessageRecord(
            Id: "M-1",
            RemoteJid: "100000000000001@lid",
            SenderJid: "100000000000001@lid",
            FromMe: false,
            Timestamp: 1700000100,
            Type: MessageType.Image,
            Text: "Launch telemetry dashboard ready",
            MimeType: "image/jpeg",
            DirectPath: "/v/t62/pic",
            MediaKey: new byte[32],
            QuotedMessageId: "M-0",
            QuotedSenderJid: "100000000000099@lid",
            QuotedText: "Send screenshot",
            FileLength: 4096));

        await backend.History.SaveMessageAsync(new MessageRecord(
            Id: "M-2",
            RemoteJid: "15550000002@s.whatsapp.net",
            SenderJid: "15550000002@s.whatsapp.net",
            FromMe: false,
            Timestamp: 1700000200,
            Type: MessageType.Text,
            Text: "Dinner at 7pm?"));

        // Draft persistence + GetChatsAsync
        await backend.History.SaveDraftAsync("100000000000001@lid", "Looking good!", 1700000300000);
        IReadOnlyList<ChatSummary> chats = await backend.History.GetChatsAsync();
        Assert.AreEqual(2, chats.Count);
        ChatSummary aliceChat = chats.Single(c => c.ChatJid == "100000000000001@lid");
        Assert.AreEqual("Looking good!", aliceChat.DraftText);
        Assert.AreEqual("Launch telemetry dashboard ready", aliceChat.LastMessageText);
        Assert.AreEqual(MessageType.Image, aliceChat.LastMessageType);

        // Clear draft
        await backend.History.SaveDraftAsync("100000000000001@lid", null, 1700000400000);
        Assert.IsNull((await backend.History.GetChatsAsync()).Single(c => c.ChatJid == "100000000000001@lid").DraftText);

        // Reaction, Edit, FTS5 update & Revoke
        await backend.History.UpsertMessageReactionAsync("100000000000001@lid", "M-1", "100000000000099@lid", "🔥");
        await backend.History.ApplyMessageEditAsync("100000000000001@lid", "M-1", "Release telemetry v2 updated", 1700000500);

        IReadOnlyList<MessageRecord> ftsHit = await backend.History.SearchMessagesAsync("telemetry v2");
        Assert.AreEqual(1, ftsHit.Count);
        Assert.AreEqual("M-1", ftsHit[0].Id);
        Assert.AreEqual(4096UL, ftsHit[0].FileLength);
        Assert.AreEqual("Send screenshot", ftsHit[0].QuotedText);
        Assert.AreEqual(1700000500L, ftsHit[0].EditedTimestamp);
        var reactions = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(ftsHit[0].ReactionsJson!);
        Assert.AreEqual("🔥", reactions?["100000000000099@lid"]);

        // Revoke removes message text from FTS5 index via update trigger
        await backend.History.MarkMessageRevokedAsync("100000000000001@lid", "M-1");
        Assert.AreEqual(0, (await backend.History.SearchMessagesAsync("telemetry")).Count);
        Assert.IsTrue((await backend.History.GetMessagesAsync("100000000000001@lid", null, 10))[0].IsRevoked);

        // Contact search
        IReadOnlyList<ContactRecord> foundContacts = await backend.History.SearchContactsAsync("Alice");
        Assert.AreEqual(1, foundContacts.Count);
        Assert.AreEqual("Alice Johnson", foundContacts[0].Name);
        Assert.AreEqual(2, await backend.History.GetMessageCountAsync());
        Assert.AreEqual(1, await backend.History.GetMessageCountAsync("100000000000001@lid"));
    }

    [TestMethod]
    public async Task UpsertBatchAsync_Backfills_ChatLastMsgTimestamp_From_Messages_When_ConversationTs_Zero()
    {
        await using var backend = new SqliteBackend(":memory:");
        await backend.InitializeAsync();

        await backend.History.UpsertBatchAsync(new HistorySyncBatch(
            Whatsapp.HistorySync.Types.HistorySyncType.InitialBootstrap,
            ChunkOrder: 0,
            Progress: 100,
            LidMappings: [],
            Pushnames: [],
            InlineContacts: [],
            Conversations:
            [
                new ConversationRecord(
                    "100000000000055@lid",
                    "Test Chat",
                    UnreadCount: 0,
                    LastMsgTimestamp: 0,
                    Archived: false,
                    Messages:
                    [
                        new MessageRecord("HM-1", "100000000000055@lid", "100000000000055@lid", false, 1700000900, MessageType.Text, "hi")
                    ])
            ]));

        ChatSummary chat = (await backend.History.GetChatsAsync()).Single();
        Assert.AreEqual(1700000900UL, chat.LastMsgTimestamp);
        Assert.AreEqual(1, await backend.History.GetMessageCountAsync());
    }

    [TestMethod]
    public async Task ChatActions_Favorites_Labels_ClearAndDelete_PersistCorrectly()
    {
        await using var backend = new SqliteBackend(":memory:");
        await backend.InitializeAsync();

        await backend.History.SaveMessageAsync(new MessageRecord("M-1", "111@lid", "111@lid", false, 1700000001, MessageType.Text, "Keep starred"));
        await backend.History.SaveMessageAsync(new MessageRecord("M-2", "111@lid", "111@lid", false, 1700000002, MessageType.Text, "Drop normal"));
        await backend.History.UpdateChatReadStateAsync("111@lid", read: false);
        Assert.AreEqual(1u, (await backend.History.GetChatsAsync()).Single().UnreadCount);

        await backend.History.UpdateChatReadStateAsync("111@lid", read: true);
        Assert.AreEqual(0u, (await backend.History.GetChatsAsync()).Single().UnreadCount);

        await backend.History.SetMessageStarredAsync("111@lid", "M-1", starred: true);
        await backend.History.ClearChatMessagesAsync("111@lid", preserveStarred: true);
        Assert.AreEqual(1, await backend.History.GetMessageCountAsync("111@lid"));
        Assert.IsNotNull(await backend.History.GetMessageByIdAsync("M-1", "111@lid"));
        Assert.IsNull(await backend.History.GetMessageByIdAsync("M-2", "111@lid"));

        await backend.History.SetFavoriteChatsAsync(["111@lid", "222@g.us"]);
        IReadOnlyList<ChatSummary> favs = await backend.History.GetChatsAsync();
        Assert.IsTrue(favs.Single(c => c.ChatJid == "111@lid").IsFavorite);
        Assert.IsTrue(favs.Single(c => c.ChatJid == "222@g.us").IsFavorite);

        await backend.History.UpsertLabelAsync(new LabelRecord("10", "pepsi", Color: 2));
        await backend.History.SetChatLabelAsync("10", "111@lid", labeled: true);
        await backend.History.ReorderLabelsAsync([10]);
        IReadOnlyList<LabelRecord> labels = await backend.History.GetLabelsAsync();
        Assert.AreEqual("pepsi", labels.Single().Name);
        CollectionAssert.AreEqual(new[] { "111@lid" }, (await backend.History.GetLabelChatJidsAsync("10")).ToArray());

        await backend.History.DeleteChatAsync("111@lid");
        Assert.AreEqual(0, await backend.History.GetMessageCountAsync("111@lid"));
        Assert.IsFalse((await backend.History.GetChatsAsync()).Any(c => c.ChatJid == "111@lid"));
        Assert.AreEqual(0, (await backend.History.GetLabelChatJidsAsync("10")).Count);
    }
}
