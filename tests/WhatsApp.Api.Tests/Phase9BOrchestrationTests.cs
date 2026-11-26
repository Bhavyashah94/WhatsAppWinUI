using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Storage.Sqlite;
using LocalWirePair = WhatsApp.Api.Tests.Phase6COrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class Phase9BOrchestrationTests
{
    private sealed class MockBusinessHttpClient : IHttpClient
    {
        public ConcurrentQueue<(Uri Url, string Body)> Posts { get; } = new();
        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
        public Func<Uri, string, (int Code, string Json)>? OnPost { get; set; }

        public ExternalBlobReference BuildAppStateReference(string directPath, byte[] plaintext, byte[] mediaKey)
        {
            byte[] enc = MediaCipher.EncryptBlob(plaintext, mediaKey, WaMediaType.AppState);
            Blobs[directPath] = enc;
            return new ExternalBlobReference
            {
                DirectPath = directPath,
                MediaKey = ByteString.CopyFrom(mediaKey),
                FileSizeBytes = (ulong)enc.Length,
                FileSha256 = ByteString.CopyFrom(SHA256.HashData(plaintext)),
                FileEncSha256 = ByteString.CopyFrom(SHA256.HashData(enc))
            };
        }

        public Task<byte[]> GetBytesAsync(Uri url, long maxBytes = 64 * 1024 * 1024, CancellationToken ct = default)
        {
            if (Blobs.TryGetValue(url.PathAndQuery, out byte[]? data))
            {
                return Task.FromResult(data);
            }
            throw new HttpRequestException("404", null, HttpStatusCode.NotFound);
        }

        public Task<byte[]> PostBytesAsync(Uri url, ReadOnlyMemory<byte> body, string contentType = "application/octet-stream", CancellationToken ct = default)
        {
            string bodyStr = Encoding.UTF8.GetString(body.Span);
            Posts.Enqueue((url, bodyStr));
            if (OnPost is not null)
            {
                var (_, json) = OnPost(url, bodyStr);
                return Task.FromResult(Encoding.UTF8.GetBytes(json));
            }
            return Task.FromResult(Encoding.UTF8.GetBytes("{}"));
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [TestMethod]
    public async Task BlockingClient_BlockUnblockIsBlockedAndNotification_EnforcesLidPnPair()
    {
        await using var backend = new SqliteBackend(":memory:");
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15550000001", 1);
        creds.Lid = Jid.LidDevice("100000000000001", 1);
        await backend.Device.SaveAsync(creds);

        Jid targetPn = Jid.Parse("15550002222@s.whatsapp.net");
        Jid targetLid = Jid.Parse("100000000000022@lid");
        var lidPn = new LidPnCache();
        lidPn.Learn(targetPn.ToString(), targetLid.ToString());

        var events = Channel.CreateUnbounded<WhatsAppEvent>();
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var blocking = new BlockingClient(wire.Iq, lidPn, e => events.Writer.WriteAsync(e));
        var notifications = new NotificationRouter(backend, lidPn, creds, wire.Socket, wire.Iq, null, e => events.Writer.WriteAsync(e));

        // 1) Block via PN -> wire receives jid=LID + pn_jid=PN
        Task blockTask = blocking.BlockAsync(targetPn);
        Node blockReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("blocklist", blockReq.GetAttr("xmlns"));
        Node item = blockReq.GetChild("item")!;
        Assert.AreEqual("block", item.GetAttr("action"));
        Assert.AreEqual(targetLid.ToString(), item.GetAttr("jid"));
        Assert.AreEqual(targetPn.ToString(), item.GetAttr("pn_jid"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = blockReq.GetAttr("id")!, ["type"] = "result" }));
        await blockTask;

        // Unmapped user fails before hitting wire
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            blocking.BlockAsync(Jid.Parse("15559990000@s.whatsapp.net")));

        // 2) IsBlockedAsync resolves PN via LID cache when server returns LID
        Task<bool> isBlockedTask = blocking.IsBlockedAsync(targetPn);
        Node getReq = await wire.ReadClientNodeAsync();
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = getReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("list", new[] { new Node("item", new Dictionary<string, NodeValue> { ["jid"] = targetLid, ["t"] = "1710000000" }) })
        }));
        Assert.IsTrue(await isBlockedTask);

        // 3) Unblock via LID works directly
        Task unblockTask = blocking.UnblockAsync(targetLid);
        Node unblockReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("unblock", unblockReq.GetChild("item")?.GetAttr("action"));
        Assert.AreEqual(targetLid.ToString(), unblockReq.GetChild("item")?.GetAttr("jid"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = unblockReq.GetAttr("id")!, ["type"] = "result" }));
        await unblockTask;

        // 4) Inbound <notification type="blocklist">
        await notifications.HandleAsync(new Node("notification", new Dictionary<string, NodeValue>
        {
            ["id"] = "blk-notif-1",
            ["from"] = StanzaBuilder.ServerPn,
            ["type"] = "blocklist",
            ["t"] = "1710000050"
        }, new[]
        {
            new Node("list", new[]
            {
                new Node("item", new Dictionary<string, NodeValue> { ["jid"] = targetLid, ["action"] = "block" })
            })
        }), sendAck: false);

        var drained = new List<WhatsAppEvent>();
        while (events.Reader.TryRead(out WhatsAppEvent? ev)) drained.Add(ev);
        Assert.IsTrue(drained.OfType<BlocklistEntryUpdatedEvent>().Count(e => e.TargetJid == targetLid.ToString() && e.Blocked) == 2);
    }

    [TestMethod]
    public async Task LabelsAndQuickReplies_OutboundAndInboundSyncdDispatch_RoundTripCleanly()
    {
        await using var backend = new SqliteBackend(":memory:");
        byte[] keyId = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
        byte[] masterKey = Enumerable.Range(20, 32).Select(i => (byte)i).ToArray();
        byte[] mediaKey = Enumerable.Range(60, 32).Select(i => (byte)i).ToArray();
        await backend.AppSync.SaveSyncKeyAsync(keyId, masterKey);
        ExpandedAppStateKeys keys = AppStateKeys.Expand(masterKey);

        var http = new MockBusinessHttpClient();
        var events = new List<WhatsAppEvent>();
        var sentMutations = new List<(string Collection, SyncdMutation.Types.SyncdOperation Op, string[] Index, SyncActionValue? Value)>();
        var colVersions = new Dictionary<string, ulong>(StringComparer.Ordinal);
        Jid chatJid = Jid.Parse("15550004444@s.whatsapp.net");

        await using var syncd = new SyncdCoordinator(
            backend,
            http,
            sendIqAsync: (iq, _ct) =>
            {
                Node? col = iq.GetChild("sync")?.GetChild("collection");
                string name = col?.GetAttr("name") ?? AppStateCollections.Regular;
                if (col?.GetChild("patch") is { } patchNode && patchNode.Content is NodeContent.BytesContent bc)
                {
                    SyncdPatch p = SyncdPatch.Parser.ParseFrom(bc.Bytes);
                    foreach (SyncdMutation m in p.Mutations)
                    {
                        (DecodedAppStateMutation decoded, _) = AppStateMutationCodec.DecodeRecord(
                            m.Operation, m.Record, keys, keyId, validateMacs: true);
                        sentMutations.Add((name, decoded.Operation, decoded.Index.ToArray(), decoded.ActionValue));
                    }

                    ulong nextVer = colVersions.GetValueOrDefault(name) + 1;
                    colVersions[name] = nextVer;

                    return ValueTask.FromResult(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = iq.GetAttr("id") ?? "1", ["type"] = "result" }, new[]
                    {
                        new Node("sync", new[]
                        {
                            new Node("collection", new Dictionary<string, NodeValue> { ["name"] = name, ["version"] = nextVer.ToString(), ["type"] = "result" })
                        })
                    }));
                }

                return ValueTask.FromResult(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = iq.GetAttr("id") ?? "1", ["type"] = "result" }, new[]
                {
                    new Node("sync", new[]
                    {
                        new Node("collection", new Dictionary<string, NodeValue> { ["name"] = name, ["version"] = colVersions.GetValueOrDefault(name).ToString() })
                    })
                }));
            },
            emitEventAsync: ev => { events.Add(ev); return ValueTask.CompletedTask; });

        var now = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1710005000));
        var labels = new LabelsClient(syncd, now);
        var quickReplies = new QuickRepliesClient(syncd, now);

        await labels.CreateOrUpdateLabelAsync("7", "VIP", 3);
        await labels.AddChatLabelAsync("7", chatJid);
        await labels.AddMessageLabelAsync("7", chatJid, "MSG-900");
        await labels.ReorderLabelsAsync([7, 3, 1]);
        await quickReplies.SetQuickReplyAsync("qr-1", "/thanks", "Thank you for reaching out!", ["thanks", "welcome"], count: 5);
        await quickReplies.DeleteQuickReplyAsync("qr-1");

        Assert.AreEqual(6, sentMutations.Count);
        Assert.IsTrue(sentMutations.All(m => m.Collection == AppStateCollections.Regular && m.Op == SyncdMutation.Types.SyncdOperation.Set));
        Assert.IsTrue(sentMutations[5].Value!.QuickReplyAction.Deleted);

        // Sync inbound snapshot on a fresh store so SyncdCoordinator applies all 6 mutations & emits events
        await using var rxBackend = new SqliteBackend(":memory:");
        await rxBackend.AppSync.SaveSyncKeyAsync(keyId, masterKey);
        var rxEvents = new List<WhatsAppEvent>();
        await using var rxSyncd = new SyncdCoordinator(
            rxBackend,
            http,
            sendIqAsync: (iq, _ct) =>
            {
                byte[] subIdx = AppStateSchemas.LabelSublist.BuildIndex("11", chatJid.ToString());
                var snap = new SyncdSnapshot { Version = new SyncdVersion { Version = 1 }, KeyId = new KeyId { Id = ByteString.CopyFrom(keyId) } };
                var valMacs = new List<byte[]>();
                byte iv = 1;
                void AddM(SyncdMutation.Types.SyncdOperation op, byte[] idx, SyncActionValue v)
                {
                    (SyncdMutation mut, byte[] mac) = AppStateMutationCodec.EncodeRecord(op, idx, v, keys, keyId, Enumerable.Repeat(iv++, 16).ToArray());
                    snap.Records.Add(mut.Record);
                    valMacs.Add(mac);
                }
                AddM(SyncdMutation.Types.SyncdOperation.Set, AppStateSchemas.LabelEdit.BuildIndex("7"),
                    new SyncActionValue { Timestamp = 100, LabelEditAction = new() { Name = "VIP", Color = 3, Deleted = false } });
                AddM(SyncdMutation.Types.SyncdOperation.Set, AppStateSchemas.LabelJid.BuildIndex("7", chatJid.ToString()),
                    new SyncActionValue { Timestamp = 101, LabelAssociationAction = new() { Labeled = true } });
                AddM(SyncdMutation.Types.SyncdOperation.Set, AppStateSchemas.LabelMessage.BuildIndex("7", chatJid.ToString(), "MSG-900", "0", "0"),
                    new SyncActionValue { Timestamp = 102, LabelAssociationAction = new() { Labeled = true } });
                AddM(SyncdMutation.Types.SyncdOperation.Set, AppStateSchemas.LabelReordering.BuildIndex(),
                    new SyncActionValue { Timestamp = 103, LabelReorderingAction = new() { SortedLabelIds = { 7, 3, 1 } } });
                AddM(SyncdMutation.Types.SyncdOperation.Set, subIdx,
                    new SyncActionValue { Timestamp = 104, LabelSublistAction = new() { SubListId = 42 } });
                AddM(SyncdMutation.Types.SyncdOperation.Set, AppStateSchemas.QuickReply.BuildIndex("qr-1"),
                    new SyncActionValue { Timestamp = 105, QuickReplyAction = new() { Shortcut = "/thanks", Message = "Ty!", Count = 5, Deleted = false } });

                byte[] h = LtHash.SubtractThenAdd(LtHash.CreateEmpty(), null, valMacs);
                snap.Mac = ByteString.CopyFrom(AppStateHasher.GenerateSnapshotMac(h, 1, AppStateCollections.Regular, keys.SnapshotMacKey));
                ExternalBlobReference sref = http.BuildAppStateReference("/v/t62/9b-snap-rx", snap.ToByteArray(), mediaKey);
                return ValueTask.FromResult(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = iq.GetAttr("id") ?? "1", ["type"] = "result" }, new[]
                {
                    new Node("sync", new[] { new Node("collection", new Dictionary<string, NodeValue> { ["name"] = AppStateCollections.Regular, ["version"] = "1" }, new[] { new Node("snapshot", sref.ToByteArray()) }) })
                }));
            },
            emitEventAsync: ev => { rxEvents.Add(ev); return ValueTask.CompletedTask; });

        await rxSyncd.SyncCollectionsAsync([AppStateCollections.Regular]);

        Assert.IsTrue(rxEvents.OfType<LabelEditUpdatedEvent>().Any(e => e.LabelId == "7" && e.Name == "VIP"));
        Assert.IsTrue(rxEvents.OfType<LabelAssociationUpdatedEvent>().Any(e => e.LabelId == "7" && e.ChatJid == chatJid.ToString() && e.Labeled));
        Assert.IsTrue(rxEvents.OfType<MessageLabelAssociationUpdatedEvent>().Any(e => e.MessageId == "MSG-900" && e.Labeled));
        Assert.IsTrue(rxEvents.OfType<LabelReorderingUpdatedEvent>().Any(e => e.SortedLabelIds.SequenceEqual([7, 3, 1])));
        Assert.IsTrue(rxEvents.OfType<LabelSublistUpdatedEvent>().Any(e => e.PredefinedId == 11 && e.SubListId == 42 && !e.Removed));
        Assert.IsTrue(rxEvents.OfType<QuickReplyUpdatedEvent>().Any(e => e.Id == "qr-1" && e.Shortcut == "/thanks"));
    }

    [TestMethod]
    public async Task BusinessClient_ProfileVerifiedNameVisitorAndOwnerCatalogAndOrder_WorkEndToEnd()
    {
        await using var backend = new SqliteBackend(":memory:");
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15550000001", 1);
        creds.Lid = Jid.LidDevice("100000000000001", 1);
        await backend.Device.SaveAsync(creds);

        Jid peerBiz = Jid.Parse("15550008888@s.whatsapp.net");
        var lidPn = new LidPnCache();
        var events = Channel.CreateUnbounded<WhatsAppEvent>();
        var http = new MockBusinessHttpClient();

        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var mex = new MexClient(wire.Iq);
        var usync = new UsyncClient(wire.Iq, backend, lidPn);
        var biz = new BusinessClient(wire.Iq, mex, usync, creds, http);
        var notifications = new NotificationRouter(backend, lidPn, creds, wire.Socket, wire.Iq, null, e => events.Writer.WriteAsync(e), businessClient: biz);

        // 1) UpdateProfileAsync
        Task updTask = biz.UpdateProfileAsync(new BusinessProfileUpdate(Description: "Custom Bakery", Websites: ["https://bakery.test"]));
        Node updReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("w:biz", updReq.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = updReq.GetAttr("id")!, ["type"] = "result" }));
        await updTask;

        // 2) GetVerifiedNameAsync via USync business query
        Task<UsyncVerifiedNameInfo?> vnTask = biz.GetVerifiedNameAsync(peerBiz);
        Node vnReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("usync", vnReq.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = vnReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("usync", new[]
            {
                new Node("list", new[]
                {
                    new Node("user", new Dictionary<string, NodeValue> { ["jid"] = peerBiz }, new[]
                    {
                        new Node("business", new[]
                        {
                            new Node("verified_name", new Dictionary<string, NodeValue> { ["name"] = "Bakery Official", ["serial"] = "987654" })
                        })
                    })
                })
            })
        }));
        UsyncVerifiedNameInfo? vn = await vnTask;
        Assert.AreEqual("Bakery Official", vn?.Name);

        // 3) Visitor Catalog HTTP GraphQL (graph.whatsapp.com/graphql/catalog)
        http.OnPost = (url, body) =>
        {
            if (url.Host == "graph.whatsapp.com")
            {
                return (200, """
                    {"data":{"xwa_product_catalog_get_product_catalog":{"product_catalog":{"products":[{"id":"p-vis-1","name":"Croissant","currency":"USD","price":"450","media":{"images":[],"videos":[]},"compliance_info":{},"status_info":{"status":"APPROVED"}}],"paging":{}}}}}
                    """);
            }
            if (body.Contains("stale-should-invalidate"))
            {
                return (200, """{"error":{"code":190,"message":"Invalid token"}}""");
            }
            return (200, """
                {"data":{"xfb_whatsapp_catalog":{"product_catalog":{"products":[{"id":"p-own-1","name":"Sourdough","currency":"USD","price":"800","media":{"images":[],"videos":[]},"compliance_info":{},"status_info":{"status":"APPROVED"}}],"paging":{}}}}}
                """);
        };

        BusinessCatalog visitorCatalog = await biz.GetCatalogAsync(peerBiz, visitorToken: "WA|visitor-tok|123");
        Assert.AreEqual("p-vis-1", visitorCatalog.Products[0].Id);
        Assert.AreEqual(BusinessStanzas.VisitorCatalogEndpoint, http.Posts.First().Url.ToString());

        // 4) Owner Catalog Token Exchange (smax_id="118" -> <notification type="business"><wa_ad_account_nonce> -> smax_id="104" -> graph.facebook.com)
        Task<BusinessCatalog> ownerTask = biz.GetCatalogAsync(creds.Pn.Value.ToNonAd());
        Node nonceReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("fb:thrift_iq", nonceReq.GetAttr("xmlns"));
        Assert.AreEqual("118", nonceReq.GetAttr("smax_id"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = nonceReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("result", new Dictionary<string, NodeValue> { ["status"] = "Success" })
        }));

        await notifications.HandleAsync(new Node("notification", new Dictionary<string, NodeValue>
        {
            ["id"] = "biz-nonce-1",
            ["from"] = StanzaBuilder.ServerPn,
            ["type"] = "business"
        }, new[] { new Node("wa_ad_account_nonce", "nonce-abc-123") }), sendAck: false);

        Node exchReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("104", exchReq.GetAttr("smax_id"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = exchReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("access_token", "EAAB_owner_token_xyz")
        }));

        BusinessCatalog ownerCatalog = await ownerTask;
        Assert.AreEqual("p-own-1", ownerCatalog.Products[0].Id);
        Assert.AreEqual(BusinessStanzas.OwnerCatalogEndpoint, http.Posts.Last().Url.ToString());

        // 5) GetOrderAsync via MEX WAWebBizQueryOrderJobQuery
        Task<BusinessOrder> orderTask = biz.GetOrderAsync(peerBiz, "ORD-42", "tok-secret");
        Node ordReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual(MexStanzas.BizQueryOrder.Id, ordReq.GetChild("query")?.GetAttr("query_id"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = ordReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("result", """
                {"data":{"xwa_checkout_get_order_info":{"order":{"creation_time_stamp":1710008000,"price_details":{"currency":"USD","subtotal_amount":"900","total_amount":"900"},"products":[{"id":"p-vis-1","name":"Croissant","currency":"USD","price":"450","quantity":2,"media":{"images":[],"videos":[]},"variant_info":{"variant_properties":[{"name":"Size","value":"Large"}]}}]}}}}
                """)
        }));
        BusinessOrder order = await orderTask;
        Assert.AreEqual(900L, order.PriceDetails?.Total?.Amount1000);
        Assert.AreEqual("Large", order.Products[0].VariantProperties[0].Value);
    }
}
