using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;
using WhatsApp.Storage.Sqlite;
using LocalWirePair = WhatsApp.Api.Tests.Phase6COrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class Phase7COrchestrationTests
{
    private sealed class MockMmsHttpClient : IHttpClient
    {
        public ConcurrentQueue<(string Method, Uri Url, int BodyLen)> Requests { get; } = new();
        public Dictionary<string, byte[]> StoredBlobs { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FailingHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ResumeOffsetToReturn { get; set; }
        public string UploadResponsePath { get; set; } = "/v/t62.7118-24/uploaded-1.enc";

        public Task<byte[]> GetBytesAsync(Uri url, long maxBytes = 64 * 1024 * 1024, CancellationToken ct = default)
        {
            Requests.Enqueue(("GET", url, 0));
            if (FailingHosts.Contains(url.Host))
            {
                throw new HttpRequestException($"Simulated CDN failure on {url.Host}");
            }
            if (url.Query.Contains("resume=1", StringComparison.Ordinal))
            {
                return Task.FromResult(Encoding.UTF8.GetBytes($"{{\"resume\":{ResumeOffsetToReturn}}}"));
            }
            if (StoredBlobs.TryGetValue(url.AbsolutePath, out byte[]? blob))
            {
                return Task.FromResult(blob);
            }
            throw new HttpRequestException("404 Not Found", null, HttpStatusCode.NotFound);
        }

        public Task<byte[]> PostBytesAsync(Uri url, ReadOnlyMemory<byte> body, string contentType = "application/octet-stream", CancellationToken ct = default)
        {
            Requests.Enqueue(("POST", url, body.Length));
            if (FailingHosts.Contains(url.Host))
            {
                throw new HttpRequestException($"Simulated CDN upload failure on {url.Host}");
            }
            StoredBlobs[UploadResponsePath] = body.ToArray();
            string json = $"{{\"url\":\"https://{url.Host}{UploadResponsePath}\",\"direct_path\":\"{UploadResponsePath}\",\"handle\":\"hnd-123\"}}";
            return Task.FromResult(Encoding.UTF8.GetBytes(json));
        }
    }

    private static byte[] CompressZlib(ReadOnlySpan<byte> raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw);
        }
        return ms.ToArray();
    }

    [TestMethod]
    public async Task MediaTransferClient_Failover_Upload_Download_And_ResumableProbe_WorkEndToEnd()
    {
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var http = new MockMmsHttpClient();
        http.FailingHosts.Add("primary-bad.cdn.whatsapp.net");

        var mediaClient = new MediaTransferClient(wire.Iq, http);

        // 1) Encrypted upload with automatic primary -> fallback failover
        byte[] imageData = new byte[8192];
        new Random(11).NextBytes(imageData);

        Task<UploadedMediaResult> uploadTask = mediaClient.UploadAsync(imageData, WaMediaType.Image);
        Node mcReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("w:m", mcReq.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = mcReq.GetAttr("id")!, ["type"] = "result" }, new Node[]
        {
            new("media_conn", new Dictionary<string, NodeValue> { ["auth"] = "TEST_AUTH", ["ttl"] = "3600" }, new Node[]
            {
                new("host", new Dictionary<string, NodeValue> { ["hostname"] = "primary-bad.cdn.whatsapp.net", ["type"] = "primary", ["fallback_hostname"] = "good-fallback.cdn.whatsapp.net" })
            })
        }));

        UploadedMediaResult uploaded = await uploadTask;
        Assert.AreEqual("/v/t62.7118-24/uploaded-1.enc", uploaded.DirectPath);
        Assert.AreEqual("hnd-123", uploaded.Handle);
        Assert.AreEqual(8192UL, uploaded.FileLength);
        Assert.IsTrue(http.Requests.Any(r => r.Method == "POST" && r.Url.Host == "good-fallback.cdn.whatsapp.net"));

        // 2) Download reuses cached media_conn (no extra IQ!) & decrypts + validates hashes
        byte[] downloaded = await mediaClient.DownloadAsync(
            uploaded.DirectPath,
            WaMediaType.Image,
            uploaded.MediaKey32,
            uploaded.FileSha256,
            uploaded.FileEncSha256);
        CollectionAssert.AreEqual(imageData, downloaded);

        // 3) Large (>= 1 MiB) upload triggers ?resume=1 probe & uploads only remaining byte range!
        byte[] bigVideo = new byte[MediaTransferClient.ResumableMinBytes + 1024];
        new Random(22).NextBytes(bigVideo);
        http.ResumeOffsetToReturn = 500_000;
        http.UploadResponsePath = "/v/t62.7118-24/resumed-vid.enc";

        UploadedMediaResult bigUploaded = await mediaClient.UploadAsync(bigVideo, WaMediaType.Video);
        Assert.AreEqual(500_000, bigUploaded.UploadedFromOffset);
        Assert.IsNotNull(bigUploaded.StreamingSidecar);
        Assert.IsTrue(http.Requests.Any(r => r.Method == "GET" && r.Url.Query.Contains("resume=1")));
        Assert.IsTrue(http.Requests.Any(r => r.Method == "POST" && r.Url.Query.Contains("bytestart=500000")));
    }

    [TestMethod]
    public async Task HistorySyncCoordinator_And_PdoHistoryClient_IngestInlineAndCdnChunks_SeedSecrets_And_RequestBackfill()
    {
        await using var backend = new SqliteBackend(":memory:");
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15551110000", 1);
        creds.Lid = Jid.LidDevice("100000000000001", 1);
        await backend.Device.SaveAsync(creds);

        var cache = new LidPnCache();
        var events = Channel.CreateUnbounded<WhatsAppEvent>();
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var http = new MockMmsHttpClient();
        var mediaClient = new MediaTransferClient(wire.Iq, http);
        var historySync = new HistorySyncCoordinator(backend, cache, mediaClient, e => events.Writer.WriteAsync(e));

        // 1) Initial bootstrap inline payload (no CDN roundtrip!)
        byte[] msgSecret = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        var hsInlineProto = new HistorySync
        {
            SyncType = HistorySync.Types.HistorySyncType.InitialBootstrap,
            ChunkOrder = 0,
            Progress = 25
        };
        hsInlineProto.PhoneNumberToLidMappings.Add(new PhoneNumberToLIDMapping
        {
            PnJid = "15552220000@s.whatsapp.net",
            LidJid = "100000000000002@lid"
        });
        var conv = new Conversation { Id = "15552220000@s.whatsapp.net", Name = "Bob" };
        conv.Messages.Add(new HistorySyncMsg
        {
            Message = new WebMessageInfo
            {
                Key = new MessageKey { Id = "HIST-1", RemoteJid = "15552220000@s.whatsapp.net", FromMe = false },
                MessageTimestamp = 1700000100,
                Message = new Message
                {
                    Conversation = "Historical hello",
                    MessageContextInfo = new MessageContextInfo { MessageSecret = ByteString.CopyFrom(msgSecret) }
                }
            }
        });
        hsInlineProto.Conversations.Add(conv);

        var inlineNotif = new Message.Types.HistorySyncNotification
        {
            SyncType = Message.Types.HistorySyncType.InitialBootstrap,
            ChunkOrder = 0,
            Progress = 25,
            InitialHistBootstrapInlinePayload = ByteString.CopyFrom(CompressZlib(hsInlineProto.ToByteArray()))
        };

        HistorySyncBatch inlineBatch = await historySync.ProcessNotificationAsync(inlineNotif);
        Assert.AreEqual(HistorySync.Types.HistorySyncType.InitialBootstrap, inlineBatch.SyncType);
        Assert.IsTrue(cache.TryGetLid("15552220000@s.whatsapp.net", out string? lid));
        Assert.AreEqual("100000000000002@lid", lid);
        Assert.IsNotNull(await backend.MsgSecrets.GetMsgSecretAsync("HIST-1"));

        // 2) CDN ON_DEMAND HistorySyncNotification download + decrypt + range stitch
        var hsOnDemandProto = new HistorySync
        {
            SyncType = HistorySync.Types.HistorySyncType.OnDemand,
            Progress = 100
        };
        var convOlder = new Conversation { Id = "15552220000@s.whatsapp.net" };
        convOlder.Messages.Add(new HistorySyncMsg
        {
            Message = new WebMessageInfo
            {
                Key = new MessageKey { Id = "HIST-0", RemoteJid = "15552220000@s.whatsapp.net", FromMe = true },
                MessageTimestamp = 1700000050,
                Message = new Message { Conversation = "Older backfilled message" }
            }
        });
        hsOnDemandProto.Conversations.Add(convOlder);

        MediaUploadEncryptionResult encHist = MediaCipher.EncryptForUpload(CompressZlib(hsOnDemandProto.ToByteArray()), WaMediaType.History);
        http.StoredBlobs["/v/hist-ondemand-1.enc"] = encHist.DataToUpload;

        var cdnNotif = new Message.Types.HistorySyncNotification
        {
            SyncType = Message.Types.HistorySyncType.OnDemand,
            DirectPath = "/v/hist-ondemand-1.enc",
            MediaKey = ByteString.CopyFrom(encHist.MediaKey32),
            FileSha256 = ByteString.CopyFrom(encHist.FileSha256),
            FileEncSha256 = ByteString.CopyFrom(encHist.FileEncSha256),
            PeerDataRequestSessionId = "PDO-SESS-9"
        };

        Task<HistorySyncBatch> cdnTask = historySync.ProcessNotificationAsync(cdnNotif);
        Node mcReq = await wire.ReadClientNodeAsync();
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = mcReq.GetAttr("id")!, ["type"] = "result" }, new Node[]
        {
            new("media_conn", new Dictionary<string, NodeValue> { ["auth"] = "HIST_AUTH", ["ttl"] = "3600" }, new Node[]
            {
                new("host", new Dictionary<string, NodeValue> { ["hostname"] = "hist-cdn.whatsapp.net", ["type"] = "primary" })
            })
        }));

        HistorySyncBatch cdnBatch = await cdnTask;
        Assert.AreEqual(HistorySync.Types.HistorySyncType.OnDemand, cdnBatch.SyncType);

        IReadOnlyList<MessageRecord> history = await backend.History.GetMessagesAsync("15552220000@s.whatsapp.net", null, 10);
        Assert.AreEqual(2, history.Count);
        Assert.AreEqual("HIST-0", history[^1].Id);

        ActiveMessageRange? range = await backend.History.GetMessageRangeAsync("15552220000@s.whatsapp.net");
        Assert.IsNotNull(range);
        Assert.AreEqual(1700000050UL, range.MinTimestamp);
        Assert.AreEqual(1700000100UL, range.MaxTimestamp);

        // 3) PdoHistoryClient picks oldest synced message (HIST-0 @ 1700000050s) automatically!
        var primaryCreds = new DeviceCredentials { RegistrationId = 999, IdentityKey = Curve25519.GenerateKeyPair(), SignedPreKeyId = 1, SignedPreKey = Curve25519.GenerateKeyPair() };
        primaryCreds.SignedPreKeySignature = XEdDSA.Sign(primaryCreds.IdentityKey.PrivateKey, WhatsApp.Protocol.Signal.PreKeySignalMessage.TagKey(primaryCreds.SignedPreKey.PublicKey));
        var primaryPreKeys = PreKeyManager.GeneratePreKeys(1, 2);
        var bundle = new PreKeyBundle(primaryCreds.RegistrationId, 0, 1, primaryCreds.SignedPreKey.PublicKey, primaryCreds.SignedPreKeySignature, primaryCreds.IdentityKey.PublicKey, primaryPreKeys[0].Id, primaryPreKeys[0].KeyPair.PublicKey);
        await backend.Protocol.SaveDeviceListAsync("100000000000001@lid", [0u, 1u]);
        await backend.Signal.SaveSessionAsync(Jid.LidDevice("100000000000001", 0).ToProtocolAddress(), SignalCipher.InitiateSession(bundle, creds.IdentityKey, creds.RegistrationId));

        var preKeys = new PreKeyFetchClient(wire.Iq, backend, cache, creds);
        var retry = new RetryCoordinator(wire.Socket, backend, preKeys, cache, creds, e => events.Writer.WriteAsync(e));
        var sender = new MessageSender(wire.Socket, new UsyncClient(wire.Iq, backend, cache), preKeys, backend, cache, creds, retry);
        var pdoClient = new PdoHistoryClient(backend, sender, creds);

        Task<SendMessageResult?> pdoTask = pdoClient.RequestNextOlderPageAsync(Jid.Pn("15552220000"), count: 40);
        Node pdoWire = await wire.ReadClientNodeAsync();
        SendMessageResult? pdoRes = await pdoTask;
        Assert.IsNotNull(pdoRes);
        Assert.AreEqual("peer", pdoWire.GetAttr("category"));
    }
}
