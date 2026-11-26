using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Storage.Sqlite.Tests;

[TestClass]
public sealed class Phase8EStorageTests
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
    }
}
