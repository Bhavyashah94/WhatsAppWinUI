using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Storage.Sqlite;
using LocalWirePair = WhatsApp.Api.Tests.UsyncAndNotificationOrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class NewsletterOrchestrationTests
{
    private sealed class MockNewsletterHttpClient : IHttpClient
    {
        public ConcurrentQueue<(string Method, Uri Url)> Requests { get; } = new();
        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
        public string UploadPath { get; set; } = "/mms/newsletter-image/nl-img-1";

        public Task<byte[]> GetBytesAsync(Uri url, long maxBytes = 64 * 1024 * 1024, CancellationToken ct = default)
        {
            Requests.Enqueue(("GET", url));
            if (Blobs.TryGetValue(url.AbsolutePath, out byte[]? data))
            {
                return Task.FromResult(data);
            }
            throw new HttpRequestException("404", null, HttpStatusCode.NotFound);
        }

        public Task<byte[]> PostBytesAsync(Uri url, ReadOnlyMemory<byte> body, string contentType = "application/octet-stream", CancellationToken ct = default)
        {
            Requests.Enqueue(("POST", url));
            Blobs[UploadPath] = body.ToArray();
            string json = $"{{\"url\":\"https://{url.Host}{UploadPath}\",\"direct_path\":\"{UploadPath}\",\"handle\":\"nl-hnd-1\"}}";
            return Task.FromResult(Encoding.UTF8.GetBytes(json));
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Node BuildMexResult(string iqId, string jsonPayload) =>
        new("iq", new Dictionary<string, NodeValue> { ["id"] = iqId, ["type"] = "result" }, new[]
        {
            new Node("result", jsonPayload)
        });

    [TestMethod]
    public async Task NewsletterClient_MexLifecycleDirectoryAndAdmin_ExecuteOverWireAndEmitEvents()
    {
        await using var backend = new SqliteBackend(":memory:");
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15550000001", 1);
        creds.Lid = Jid.LidDevice("100000000000001", 1);
        await backend.Device.SaveAsync(creds);

        Jid channelJid = Jid.Parse("120363000000000077@newsletter");
        Jid adminPn = Jid.Parse("15550009999@s.whatsapp.net");
        Jid adminLid = Jid.Parse("100000000009999@lid");

        var lidPn = new LidPnCache();
        lidPn.Learn(adminPn.ToString(), adminLid.ToString(), LidPnLearningSource.Usync, 1710000000);

        var events = Channel.CreateUnbounded<WhatsAppEvent>();
        var http = new MockNewsletterHttpClient();
        var now = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1710001000));

        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var usync = new UsyncClient(wire.Iq, backend, lidPn, now);
        var preKeys = new PreKeyFetchClient(wire.Iq, backend, lidPn, creds);
        var retry = new RetryCoordinator(wire.Socket, backend, preKeys, lidPn, creds, e => events.Writer.WriteAsync(e));
        var sender = new MessageSender(wire.Socket, usync, preKeys, backend, lidPn, creds, retry, now);
        var mex = new MexClient(wire.Iq);
        var mediaTransfer = new MediaTransferClient(wire.Iq, http, now);
        var nl = new NewsletterClient(wire.Iq, mex, wire.Socket, sender, lidPn, mediaTransfer, e => events.Writer.WriteAsync(e), now);

        // 1) CreateAsync
        Task<NewsletterMetadata> createTask = nl.CreateAsync("WinUI News", "Channel description");
        Node createIq = await wire.ReadClientNodeAsync();
        Assert.AreEqual(MexStanzas.CreateNewsletter.Id, createIq.GetChild("query")!.GetAttr("query_id"));
        wire.SendServerNode(BuildMexResult(createIq.GetAttr("id")!, $$"""
            {"data":{"xwa2_newsletter_create":{"id":"{{channelJid}}","state":{"type":"ACTIVE"},"thread_metadata":{"name":{"text":"WinUI News"},"subscribers_count":"1","verification":"UNVERIFIED"},"viewer_metadata":{"role":"OWNER"} } } }
            """));
        NewsletterMetadata created = await createTask;
        Assert.AreEqual(NewsletterRole.Owner, created.Role);

        // 2) DemoteAdmin resolves PN -> LID automatically
        Task demoteTask = nl.DemoteAdminAsync(channelJid, adminPn);
        Node demoteIq = await wire.ReadClientNodeAsync();
        string demoteJson = demoteIq.GetChild("query")!.Content switch
        {
            NodeContent.StringContent sc => sc.Text,
            NodeContent.BytesContent bc => Encoding.UTF8.GetString(bc.Bytes),
            _ => string.Empty
        };
        StringAssert.Contains(demoteJson, adminLid.ToString());
        wire.SendServerNode(BuildMexResult(demoteIq.GetAttr("id")!, $$"""
            {"data":{"xwa2_newsletter_admin_demote":{"id":"{{channelJid}}"} } }
            """));
        await demoteTask;

        // Unknown PN without LID throws cleanly before network
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            nl.ChangeOwnerAsync(channelJid, Jid.Parse("15559998888@s.whatsapp.net")));

        // 3) SearchDirectoryAsync
        Task<NewsletterDirectoryPage> searchTask = nl.SearchDirectoryAsync("dotnet", limit: 10);
        Node searchIq = await wire.ReadClientNodeAsync();
        Assert.AreEqual(MexStanzas.FetchNewsletterDirectorySearchResults.Id, searchIq.GetChild("query")!.GetAttr("query_id"));
        wire.SendServerNode(BuildMexResult(searchIq.GetAttr("id")!, $$"""
            {"data":{"xwa2_newsletters_directory_search":{"page_info":{"endCursor":"cur-2","hasNextPage":true,"hasPreviousPage":false},"result":[{"id":"{{channelJid}}","thread_metadata":{"name":{"text":"WinUI News"},"subscribers_count":"500"} }] } } }
            """));
        NewsletterDirectoryPage page = await searchTask;
        Assert.AreEqual(1, page.Newsletters.Count);
        Assert.IsTrue(page.HasNextPage);
        Assert.AreEqual("cur-2", page.EndCursor);

        Assert.IsTrue(events.Reader.TryRead(out WhatsAppEvent? evt));
        Assert.IsInstanceOfType<NewsletterMetadataUpdatedEvent>(evt);
    }

    [TestMethod]
    public async Task NewsletterClient_PublishInboundPlaintextLiveUpdateAndMedia_WorkEndToEnd()
    {
        await using var backend = new SqliteBackend(":memory:");
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15550000001", 1);
        creds.Lid = Jid.LidDevice("100000000000001", 1);
        await backend.Device.SaveAsync(creds);

        Jid channelJid = Jid.Parse("120363000000000088@newsletter");
        var lidPn = new LidPnCache();
        var events = Channel.CreateUnbounded<WhatsAppEvent>();
        var http = new MockNewsletterHttpClient();
        var now = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1710002000));

        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var usync = new UsyncClient(wire.Iq, backend, lidPn, now);
        var preKeys = new PreKeyFetchClient(wire.Iq, backend, lidPn, creds);
        var retry = new RetryCoordinator(wire.Socket, backend, preKeys, lidPn, creds, e => events.Writer.WriteAsync(e));
        var sender = new MessageSender(wire.Socket, usync, preKeys, backend, lidPn, creds, retry, now);
        var mex = new MexClient(wire.Iq);
        var mediaTransfer = new MediaTransferClient(wire.Iq, http, now);
        var receiver = new MessageReceiver(wire.Socket, backend, lidPn, creds, retry, e => events.Writer.WriteAsync(e), now, null, mediaTransfer);
        var notifications = new NotificationRouter(backend, lidPn, creds, wire.Socket, wire.Iq, syncd: null, e => events.Writer.WriteAsync(e), now, receiver);
        var nl = new NewsletterClient(wire.Iq, mex, wire.Socket, sender, lidPn, mediaTransfer, e => events.Writer.WriteAsync(e), now);

        // 1) Outbound plaintext text + poll + reaction + poll vote + edit + revoke
        SendMessageResult sentText = await nl.PublishTextAsync(channelJid, "Hello subscribers!");
        Node sentStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("message", sentStanza.Tag);
        Assert.AreEqual(channelJid.ToString(), sentStanza.GetAttr("to"));
        Assert.AreEqual("text", sentStanza.GetAttr("type"));
        Node? ptChild = sentStanza.GetChild("plaintext");
        Assert.IsNotNull(ptChild);
        Assert.AreEqual("Hello subscribers!", Message.Parser.ParseFrom(((NodeContent.BytesContent)ptChild.Content!).Bytes).Conversation);

        await nl.PublishPollAsync(channelJid, "Favorite feature?", ["Channels", "Calling"]);
        Node sentPoll = await wire.ReadClientNodeAsync();
        Assert.AreEqual("poll", sentPoll.GetAttr("type"));
        Assert.AreEqual("creation", sentPoll.GetChild("meta")?.GetAttr("polltype"));
        Assert.AreEqual("text", sentPoll.GetChild("meta")?.GetAttr("contenttype"));

        await nl.SendReactionAsync(channelJid, 901, "🎉");
        Node sentRx = await wire.ReadClientNodeAsync();
        Assert.AreEqual("reaction", sentRx.GetAttr("type"));
        Assert.AreEqual("901", sentRx.GetAttr("server_id"));
        Assert.AreEqual("🎉", sentRx.GetChild("reaction")?.GetAttr("code"));

        await nl.SendPollVoteByOptionNamesAsync(channelJid, 902, ["Channels"]);
        Node sentVote = await wire.ReadClientNodeAsync();
        Assert.AreEqual("poll", sentVote.GetAttr("type"));
        Assert.AreEqual("902", sentVote.GetAttr("server_id"));
        Assert.AreEqual("vote", sentVote.GetChild("meta")?.GetAttr("polltype"));
        Assert.AreEqual(32, ((NodeContent.BytesContent)sentVote.GetChild("votes")!.GetChild("vote")!.Content!).Bytes.Length);

        await nl.EditMessageAsync(channelJid, sentText.MessageId, new Message { Conversation = "Edited post" });
        Node sentEdit = await wire.ReadClientNodeAsync();
        Assert.AreEqual("3", sentEdit.GetAttr("edit"));

        await nl.RevokeMessageAsync(channelJid, sentText.MessageId);
        Node sentRevoke = await wire.ReadClientNodeAsync();
        Assert.AreEqual("8", sentRevoke.GetAttr("edit"));
        Assert.AreEqual(0, ((NodeContent.BytesContent)sentRevoke.GetChild("plaintext")!.Content!).Bytes.Length);

        // 2) Inbound newsletter plaintext message -> persisted + NewsletterMessageReceivedEvent + MessageReceivedEvent + ack
        byte[] inProto = new Message { Conversation = "Incoming channel post" }.ToByteArray();
        Node inMsg = new("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "IN-NL-1",
            ["from"] = channelJid,
            ["type"] = "text",
            ["server_id"] = "950",
            ["t"] = "1710002100"
        }, new[] { new Node("plaintext", inProto) });

        await receiver.HandleMessageStanzaAsync(inMsg);
        Node ack = await wire.ReadClientNodeAsync();
        Assert.AreEqual("ack", ack.Tag);
        Assert.AreEqual("IN-NL-1", ack.GetAttr("id"));

        IReadOnlyList<MessageRecord> stored = await backend.History.GetMessagesAsync(channelJid.ToString(), null, 10);
        Assert.IsTrue(stored.Any(m => m.Id == "IN-NL-1" && m.Text == "Incoming channel post"));

        // 3) Inbound admin revoke (empty plaintext, edit="8") marks message revoked
        Node inRevoke = new("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "IN-NL-1",
            ["from"] = channelJid,
            ["type"] = "text",
            ["server_id"] = "950",
            ["edit"] = "8",
            ["t"] = "1710002150"
        }, new[] { new Node("plaintext", Array.Empty<byte>()) });
        await receiver.HandleMessageStanzaAsync(inRevoke);
        _ = await wire.ReadClientNodeAsync(); // ack
        IReadOnlyList<MessageRecord> afterRevoke = await backend.History.GetMessagesAsync(channelJid.ToString(), null, 10);
        Assert.IsTrue(afterRevoke.Single(m => m.Id == "IN-NL-1").IsRevoked);

        // 4) Inbound live_updates notification -> NewsletterLiveUpdateEvent
        Node liveNotif = new("notification", new Dictionary<string, NodeValue>
        {
            ["id"] = "N-LIVE-1",
            ["from"] = channelJid,
            ["type"] = "newsletter",
            ["t"] = "1710002200"
        }, new[]
        {
            new Node("live_updates", new[]
            {
                new Node("messages", new[]
                {
                    new Node("message", new Dictionary<string, NodeValue> { ["server_id"] = "950" }, new Node[]
                    {
                        new("reactions", new[] { new Node("reaction", new Dictionary<string, NodeValue> { ["code"] = "❤️", ["count"] = "33" }) })
                    })
                })
            })
        });
        await notifications.HandleAsync(liveNotif);
        _ = await wire.ReadClientNodeAsync(); // notif ack

        // 5) Plaintext newsletter CDN upload + DownloadMessageMediaAsync
        byte[] imgBytes = "plaintext-png-payload"u8.ToArray();
        Task<UploadedMediaResult> upTask = nl.UploadPlaintextMediaAsync(imgBytes, WaMediaType.NewsletterImage);
        Node mediaConnReq = await wire.ReadClientNodeAsync();
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue>
        {
            ["id"] = mediaConnReq.GetAttr("id")!,
            ["type"] = "result"
        }, new[]
        {
            new Node("media_conn", new Dictionary<string, NodeValue> { ["auth"] = "auth-nl-1", ["ttl"] = "3600" }, new[]
            {
                new Node("host", new Dictionary<string, NodeValue> { ["hostname"] = "mmg.whatsapp.net" })
            })
        }));
        UploadedMediaResult upRes = await upTask;
        Assert.AreEqual(0, upRes.MediaKey32.Length);
        byte[] downloaded = await mediaTransfer.DownloadMessageMediaAsync(new MessageRecord(
            "IN-NL-IMG",
            channelJid.ToString(),
            channelJid.ToString(),
            FromMe: false,
            1710002300,
            MessageType.Image,
            null,
            "image/png",
            upRes.DirectPath,
            MediaKey: null,
            FileHash: SHA256.HashData(imgBytes)));
        CollectionAssert.AreEqual(imgBytes, downloaded);

        // Check emitted event types
        var drained = new List<WhatsAppEvent>();
        while (events.Reader.TryRead(out WhatsAppEvent? e))
        {
            drained.Add(e);
        }
        Assert.IsTrue(drained.OfType<NewsletterMessageReceivedEvent>().Any(e => e.MessageId == "IN-NL-1" && e.ServerId == 950UL));
        Assert.IsTrue(drained.OfType<NewsletterLiveUpdateEvent>().Any(e => e.ChannelJid == channelJid.ToString() && e.Messages[0].Reactions[0].Count == 33));
    }
}
