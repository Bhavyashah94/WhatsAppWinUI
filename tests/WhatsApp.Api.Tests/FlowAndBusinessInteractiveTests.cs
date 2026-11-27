using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Signal;
using WhatsApp.Storage.Sqlite;
using LocalWirePair = WhatsApp.Api.Tests.UsyncAndNotificationOrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class FlowAndBusinessInteractiveTests
{
    private sealed class MockFlowHttpClient : IHttpClient
    {
        public Dictionary<string, string> GetJsonResponses { get; } = new(StringComparer.Ordinal);
        public Func<Uri, string, string>? OnPost { get; set; }
        public ConcurrentQueue<(Uri Url, string Body)> Posts { get; } = new();

        public Task<byte[]> GetBytesAsync(Uri url, long maxBytes = 64 * 1024 * 1024, CancellationToken ct = default)
        {
            if (GetJsonResponses.TryGetValue(url.ToString(), out string? json))
            {
                return Task.FromResult(Encoding.UTF8.GetBytes(json));
            }
            throw new HttpRequestException($"Missing mock GET {url}");
        }

        public Task<byte[]> PostBytesAsync(Uri url, ReadOnlyMemory<byte> body, string contentType = "application/octet-stream", CancellationToken ct = default)
        {
            string bodyStr = Encoding.UTF8.GetString(body.Span);
            Posts.Enqueue((url, bodyStr));
            string resp = OnPost is not null ? OnPost(url, bodyStr) : "{}";
            return Task.FromResult(Encoding.UTF8.GetBytes(resp));
        }
    }

    [TestMethod]
    public async Task FlowClient_QueriesMexWithLidMappedPn_FetchesJson_AndRunsEncryptedDataExchange()
    {
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var mex = new MexClient(wire.Iq);
        var lidPn = new LidPnCache();
        Jid bizLid = Jid.Lid("200000000000055");
        Jid bizPn = Jid.Pn("919900000055");
        lidPn.Learn(bizPn.ToString(), bizLid.ToString());

        using var serverRsa = RSA.Create(2048);
        string serverPem = serverRsa.ExportSubjectPublicKeyInfoPem();

        var http = new MockFlowHttpClient();
        http.GetJsonResponses["https://web.whatsapp.com/flows/json/987654321"] = """{"version":"5.0","screens":[{"id":"START"}]}""";
        http.OnPost = (_, reqBody) =>
        {
            using JsonDocument reqDoc = JsonDocument.Parse(reqBody);
            byte[] aesKey = serverRsa.Decrypt(Convert.FromBase64String(reqDoc.RootElement.GetProperty("encrypted_aes_key").GetString()!), RSAEncryptionPadding.OaepSHA256);
            byte[] iv = Convert.FromBase64String(reqDoc.RootElement.GetProperty("initial_vector").GetString()!);
            byte[] invIv = iv.Select(b => (byte)~b).ToArray();
            byte[] plain = Encoding.UTF8.GetBytes("""{"screen":"NEXT","data":{"status":"ok"}}""");
            var gcm = new GcmBlockCipher(new AesEngine());
            gcm.Init(true, new AeadParameters(new KeyParameter(aesKey), 128, invIv));
            byte[] outBuf = new byte[gcm.GetOutputSize(plain.Length)];
            int written = gcm.ProcessBytes(plain, 0, plain.Length, outBuf, 0);
            gcm.DoFinal(outBuf, written);
            return Convert.ToBase64String(outBuf);
        };

        var flowClient = new FlowClient(mex, lidPn, http);

        Task<GalaxyFlowDataResult> mexTask = flowClient.GetFlowDataAsync(bizLid, "987654321");
        Node iqReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("w:mex", iqReq.GetAttr("xmlns"));
        Node queryNode = iqReq.GetChild("query")!;
        Assert.AreEqual("40083220921276114", queryNode.GetAttr("query_id"));
        string varsJson = queryNode.Content switch
        {
            NodeContent.StringContent sc => sc.Text,
            NodeContent.BytesContent bc => Encoding.UTF8.GetString(bc.Bytes),
            _ => string.Empty
        };
        Assert.IsTrue(varsJson.Contains("919900000055@s.whatsapp.net"), varsJson);

        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue>
        {
            ["id"] = iqReq.GetAttr("id")!,
            ["type"] = "result"
        }, new[]
        {
            new Node("result", """{"data":{"xwa_extensions_get_flow_data":{"extensions_flow_data":[{"flow_id":"987654321","extensions":{"metadata":{"flow_name":"demo","state":"PUBLISHED"}}}],"endpoint_public_key":{"key":"PEM","signature":"SIG"}}}}""")
        }));

        GalaxyFlowDataResult flowData = await mexTask;
        Assert.AreEqual("demo", flowData.Flows[0].FlowName);

        JsonElement flowJson = await flowClient.FetchFlowJsonAsync("987654321");
        Assert.AreEqual("5.0", flowJson.GetProperty("version").GetString());

        string endpointResp = await flowClient.SendEncryptedDataExchangeAsync(
            new Uri("https://biz.example.com/flows"),
            """{"action":"ping"}""",
            serverPem);
        Assert.IsTrue(endpointResp.Contains("\"screen\":\"NEXT\""), endpointResp);
    }

    [TestMethod]
    public async Task MessageSender_BusinessReplies_FlowAddressCallPermLocationPhoneAndOrder_WireUp()
    {
        await using var aliceDb = new SqliteBackend(":memory:", 1);
        await using var bobDb = new SqliteBackend(":memory:", 1);
        DeviceCredentials alice = await aliceDb.Device.LoadOrCreateAsync();
        alice.Pn = Jid.PnDevice("15551110000", 1);
        await aliceDb.Device.SaveAsync(alice);

        DeviceCredentials bob = await bobDb.Device.LoadOrCreateAsync();
        bob.Pn = Jid.PnDevice("15552220000", 1);
        await bobDb.Device.SaveAsync(bob);

        Jid bobBare = bob.Pn!.Value.ToNonAd();
        Jid bobDev0 = Jid.PnDevice("15552220000", 0);
        await using var bobPeer0 = new SqliteBackend(":memory:");
        DeviceCredentials bob0Creds = await bobPeer0.Device.LoadOrCreateAsync();
        await aliceDb.Signal.SaveIdentityAsync(bobDev0.ToProtocolAddress(), bob0Creds.IdentityKey.PublicKey);
        await aliceDb.Signal.SaveSessionAsync(bobDev0.ToProtocolAddress(), SignalCipher.InitiateSession(
            new PreKeyBundle(bob0Creds.RegistrationId, 0, bob0Creds.SignedPreKeyId, bob0Creds.SignedPreKey.PublicKey, bob0Creds.SignedPreKeySignature, bob0Creds.IdentityKey.PublicKey, null, null),
            alice.IdentityKey, alice.RegistrationId));
        await aliceDb.Protocol.SaveDeviceListAsync(bobBare.ToString(), [0u]);
        await aliceDb.Protocol.SaveDeviceListAsync(alice.Pn!.Value.ToNonAd().ToString(), [1u]);

        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var cache = new LidPnCache();
        var usync = new UsyncClient(wire.Iq, aliceDb, cache);
        var prekeys = new PreKeyFetchClient(wire.Iq, aliceDb, cache, alice);
        var retry = new RetryCoordinator(wire.Socket, aliceDb, prekeys, cache, alice, _ => ValueTask.CompletedTask);
        var sender = new MessageSender(wire.Socket, usync, prekeys, aliceDb, cache, alice, retry, TimeProvider.System);

        var quotedKey = new MessageKey { Id = "BIZ-1", RemoteJid = bobBare.ToString() };
        var quotedMsg = new Message { Conversation = "Please respond" };

        await sender.SendLocationResponseAsync(bobBare, 19.076, 72.877, "Mumbai", quotedKey: quotedKey, quotedMessage: quotedMsg);
        Node locStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("media", locStanza.GetAttr("type"));

        await sender.SendPhoneNumberShareAsync(bobBare, "Alice", "+15551110000", quotedKey: quotedKey, quotedMessage: quotedMsg);
        Node phoneStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("media", phoneStanza.GetAttr("type"));

        await sender.SendAddressResponseAsync(bobBare, new Dictionary<string, string?> { ["city"] = "Mumbai" }, quotedKey: quotedKey, quotedMessage: quotedMsg);
        Node addrStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("address_message", addrStanza.GetChild("biz")?.GetAttr("native_flow_name"));

        await sender.SendCallPermissionResponseAsync(bobBare, "accept", 1800000000, quotedKey: quotedKey, quotedMessage: quotedMsg);
        Node cpStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("call_permission_request", cpStanza.GetChild("biz")?.GetAttr("native_flow_name"));

        await sender.SendFlowResponseAsync(bobBare, "flow_tok_1", new Dictionary<string, object?> { ["name"] = "Alice" }, quotedKey: quotedKey, quotedMessage: quotedMsg);
        Node flowStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("galaxy_message", flowStanza.GetChild("biz")?.GetAttr("native_flow_name"));

        await sender.SendOrderAsync(bobBare, "ORD-1", "ord-tok", bobBare, itemCount: 2, totalAmount1000: 50000, totalCurrencyCode: "INR");
        Node ordStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("media", ordStanza.GetAttr("type"));

        await sender.SendSpoilerAsync(bobBare, new Message { Conversation = "Secret spoiler" });
        Node spStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("text", spStanza.GetAttr("type"));

        await sender.SendLiveLocationAsync(bobBare, 19.076, 72.8777, "Moving");
        Node llStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("media", llStanza.GetAttr("type"));

        await sender.SendScheduledCallAsync(bobBare, 1800000000000, title: "Sync");
        Node scStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("text", scStanza.GetAttr("type"));
    }

    [TestMethod]
    public async Task BusinessClient_FallsBackToMexForVisitorCatalogAndCollections_And_LinkPreviewFetcher()
    {
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var mex = new MexClient(wire.Iq);
        var cache = new LidPnCache();
        var usync = new UsyncClient(wire.Iq, new SqliteBackend(":memory:"), cache);
        var http = new MockFlowHttpClient();
        http.GetJsonResponses["https://example.com/article"] =
            "<html><head><meta property=\"og:title\" content=\"Article One\"><meta property=\"og:description\" content=\"Summary here\"></head></html>";

        await using var backend = new SqliteBackend(":memory:", 1);
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15551110000", 1);
        var biz = new BusinessClient(wire.Iq, mex, usync, creds, http);

        Task<BusinessCatalog> catTask = biz.GetCatalogAsync(Jid.Pn("19998887777"));
        Node iqReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("29516483177936279", iqReq.GetChild("query")?.GetAttr("query_id"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue>
        {
            ["id"] = iqReq.GetAttr("id")!,
            ["type"] = "result"
        }, new[]
        {
            new Node("result", """{"data":{"xwa_product_catalog_get_product_catalog":{"product_catalog":{"products":[{"id":"SKU-1","name":"Coffee"}],"paging":{"after":null,"before":null}}}}}""")
        }));

        BusinessCatalog cat = await catTask;
        Assert.AreEqual(1, cat.Products.Count);
        Assert.AreEqual("SKU-1", cat.Products[0].Id);

        LinkPreviewData? lp = await LinkPreviewFetcher.FetchAsync(http, "Read https://example.com/article now");
        Assert.IsNotNull(lp);
        Assert.AreEqual("Article One", lp.Title);
        Assert.AreEqual("Summary here", lp.Description);
    }
}
