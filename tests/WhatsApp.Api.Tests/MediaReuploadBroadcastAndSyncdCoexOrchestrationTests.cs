using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;
using WhatsApp.Storage.Sqlite;
using LocalWirePair = WhatsApp.Api.Tests.UsyncAndNotificationOrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class MediaReuploadBroadcastAndSyncdCoexOrchestrationTests
{
    private sealed class MockSyncdHttpClient : IHttpClient
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
        public Task<byte[]> GetBytesAsync(Uri url, long maxBytes = 64 * 1024 * 1024, CancellationToken ct = default) =>
            _blobs.TryGetValue(url.PathAndQuery, out byte[]? data) ? Task.FromResult(data) : throw new HttpRequestException("404");
        public Task<byte[]> PostBytesAsync(Uri url, ReadOnlyMemory<byte> body, string contentType = "application/octet-stream", CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static async Task<DeviceCredentials> SetupNodeAsync(SqliteBackend backend, string pnUser, string lidUser)
    {
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice(pnUser, 0);
        creds.Lid = Jid.LidDevice(lidUser, 0);
        await backend.Device.SaveAsync(creds);

        byte[] spkRecord = PreKeyManager.EncodeSignedPreKeyRecord(creds.SignedPreKeyId, creds.SignedPreKey, creds.SignedPreKeySignature, 1700000000);
        await backend.Signal.SaveSignedPreKeyAsync(new SignedPreKeyEntry(creds.SignedPreKeyId, spkRecord, creds.SignedPreKeySignature, 1700000000));
        var otpks = PreKeyManager.GeneratePreKeys(1, 5);
        await backend.Signal.SavePreKeysAsync(otpks.Select(k => (k.Id, k.RecordBytes)).ToArray());
        creds.NextPreKeyId = 6;
        await backend.Device.SaveAsync(creds);
        return creds;
    }

    [TestMethod]
    public async Task MediaReupload_Comments_DraftSync_And_Broadcast_WorkEndToEnd()
    {
        await using var aliceDb = new SqliteBackend(":memory:");
        await using var bobDb = new SqliteBackend(":memory:");
        DeviceCredentials aliceCreds = await SetupNodeAsync(aliceDb, "15550000001", "100000000000001");
        DeviceCredentials bobCreds = await SetupNodeAsync(bobDb, "15550000002", "100000000000002");

        Jid aliceLid = aliceCreds.Lid!.Value.ToNonAd();
        Jid bobLid = bobCreds.Lid!.Value.ToNonAd();
        Jid groupJid = Jid.Parse("120363000000008888@g.us");

        // Pre-seed Signal sessions so DMs & Broadcasts run cleanly without usync/prekey roundtrips
        byte[] aliceOtpk = PreKeyManager.DecodePreKeyRecord((await aliceDb.Signal.GetPreKeyAsync(1))!).KeyPair.PublicKey;
        var aliceBundle = new PreKeyBundle(aliceCreds.RegistrationId, 0, aliceCreds.SignedPreKeyId, aliceCreds.SignedPreKey.PublicKey,
            aliceCreds.SignedPreKeySignature, aliceCreds.IdentityKey.PublicKey, 1, aliceOtpk);
        await bobDb.Signal.SaveIdentityAsync(aliceLid.ToProtocolAddress(), aliceCreds.IdentityKey.PublicKey);
        await bobDb.Signal.SaveSessionAsync(aliceLid.ToProtocolAddress(), SignalCipher.InitiateSession(aliceBundle, bobCreds.IdentityKey, bobCreds.RegistrationId));

        byte[] bobOtpk = PreKeyManager.DecodePreKeyRecord((await bobDb.Signal.GetPreKeyAsync(1))!).KeyPair.PublicKey;
        var bobBundle = new PreKeyBundle(bobCreds.RegistrationId, 0, bobCreds.SignedPreKeyId, bobCreds.SignedPreKey.PublicKey,
            bobCreds.SignedPreKeySignature, bobCreds.IdentityKey.PublicKey, 1, bobOtpk);
        await aliceDb.Signal.SaveIdentityAsync(bobLid.ToProtocolAddress(), bobCreds.IdentityKey.PublicKey);
        await aliceDb.Signal.SaveSessionAsync(bobLid.ToProtocolAddress(), SignalCipher.InitiateSession(bobBundle, aliceCreds.IdentityKey, aliceCreds.RegistrationId));

        await aliceDb.Protocol.SaveDeviceListAsync(aliceLid.ToString(), [0u]);
        await aliceDb.Protocol.SaveDeviceListAsync(bobLid.ToString(), [0u]);
        await bobDb.Protocol.SaveDeviceListAsync(aliceLid.ToString(), [0u]);
        await bobDb.Protocol.SaveDeviceListAsync(bobLid.ToString(), [0u]);

        var aliceEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var bobEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var aliceCache = new LidPnCache();
        var bobCache = new LidPnCache();

        await using var aliceWire = new LocalWirePair();
        await using var bobWire = new LocalWirePair();

        var alicePreKeys = new PreKeyFetchClient(aliceWire.Iq, aliceDb, aliceCache, aliceCreds);
        var bobPreKeys = new PreKeyFetchClient(bobWire.Iq, bobDb, bobCache, bobCreds);
        var aliceRetry = new RetryCoordinator(aliceWire.Socket, aliceDb, alicePreKeys, aliceCache, aliceCreds, e => aliceEvents.Writer.WriteAsync(e));
        var bobRetry = new RetryCoordinator(bobWire.Socket, bobDb, bobPreKeys, bobCache, bobCreds, e => bobEvents.Writer.WriteAsync(e));
        var aliceUsync = new UsyncClient(aliceWire.Iq, aliceDb, aliceCache);
        var bobUsync = new UsyncClient(bobWire.Iq, bobDb, bobCache);
        var aliceSender = new MessageSender(aliceWire.Socket, aliceUsync, alicePreKeys, aliceDb, aliceCache, aliceCreds, aliceRetry);
        var bobSender = new MessageSender(bobWire.Socket, bobUsync, bobPreKeys, bobDb, bobCache, bobCreds, bobRetry);
        var aliceReceiver = new MessageReceiver(aliceWire.Socket, aliceDb, aliceCache, aliceCreds, aliceRetry, e => aliceEvents.Writer.WriteAsync(e));
        var reupload = new MediaReuploadCoordinator(aliceWire.Socket, aliceDb, aliceCache, aliceCreds, e => aliceEvents.Writer.WriteAsync(e));
        var notifications = new NotificationRouter(aliceDb, aliceCache, aliceCreds, aliceWire.Socket, aliceWire.Iq, null, e => aliceEvents.Writer.WriteAsync(e), messageReceiver: aliceReceiver, mediaReupload: reupload);

        await using var aliceRouter = new StanzaRouter(aliceWire.Socket, aliceWire.Iq, new StanzaHandlers
        {
            OnMessage = (n, t) => new ValueTask(aliceReceiver.HandleMessageStanzaAsync(n, t)),
            OnNotification = (n, t) => new ValueTask(notifications.HandleAsync(n, sendAck: false, handlerToken: t)),
            OnAck = (n, _) => { reupload.NotifyReceiptAck(n); return ValueTask.CompletedTask; }
        });
        await using var bobRouter = new StanzaRouter(bobWire.Socket, bobWire.Iq, new StanzaHandlers());

        // 1) MediaReuploadCoordinator: concurrent deduplication + conflict + ACK + decrypted notification
        byte[] mediaKey = Enumerable.Range(1, 32).Select(static i => (byte)i).ToArray();
        byte[] wrongKey = Enumerable.Range(2, 32).Select(static i => (byte)i).ToArray();

        Task<MediaRetryDecryptedResult> req1 = reupload.RequestAsync(new MediaReuploadRequest("MEDIA-1", bobLid, FromMe: false, mediaKey, bobLid));
        Task<MediaRetryDecryptedResult> req2 = reupload.RequestAsync(new MediaReuploadRequest("MEDIA-1", bobLid, FromMe: false, mediaKey, bobLid));
        await Assert.ThrowsExceptionAsync<MediaReuploadException>(() =>
            reupload.RequestAsync(new MediaReuploadRequest("MEDIA-1", bobLid, FromMe: false, wrongKey, bobLid)));

        Node receiptOut = await aliceWire.ReadClientNodeAsync();
        Assert.AreEqual("server-error", receiptOut.GetAttr("type"));

        // Send matching ACK then encrypted MediaRetryNotification
        aliceWire.SendServerNode(new Node("ack", new Dictionary<string, NodeValue>
        {
            ["id"] = "MEDIA-1",
            ["class"] = "receipt",
            ["type"] = "server-error",
            ["from"] = aliceLid
        }));
        byte[] retryNotifProto = new MediaRetryNotification
        {
            StanzaId = "MEDIA-1",
            DirectPath = "/v/t62/refreshed-path",
            Result = MediaRetryNotification.Types.ResultType.Success
        }.ToByteArray();
        byte[] retryKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, mediaKey, retryKey, default, "WhatsApp Media Retry Notification"u8);
        byte[] retryIv = RandomNumberGenerator.GetBytes(12);
        byte[] retryCipherWithTag = new byte[retryNotifProto.Length + 16];
        using (var aes = new AesGcm(retryKey, 16))
        {
            aes.Encrypt(retryIv, retryNotifProto, retryCipherWithTag.AsSpan(0, retryNotifProto.Length), retryCipherWithTag.AsSpan(retryNotifProto.Length, 16), "MEDIA-1"u8);
        }

        aliceWire.SendServerNode(new Node("notification", new Dictionary<string, NodeValue>
        {
            ["id"] = "MEDIA-1",
            ["from"] = StanzaBuilder.ServerPn,
            ["type"] = "mediaretry",
            ["t"] = "1700000100"
        }, new[]
        {
            new Node("encrypt", new Node[] { new("enc_payload", retryCipherWithTag), new("enc_iv", retryIv) })
        }));

        MediaRetryDecryptedResult res1 = await req1;
        MediaRetryDecryptedResult res2 = await req2;
        Assert.AreEqual("/v/t62/refreshed-path", res1.DirectPath);
        Assert.AreEqual("/v/t62/refreshed-path", res2.DirectPath);

        // 2) CommentClient + MessageReceiver EncComment decryption -> persisted MessageRecord + CommentReceivedEvent
        byte[] parentSecret = Enumerable.Range(20, 32).Select(static i => (byte)i).ToArray();
        await bobDb.MsgSecrets.PutMsgSecretsAsync([("POST-1", parentSecret, 1700000000L)]);
        await aliceDb.MsgSecrets.PutMsgSecretsAsync([("POST-1", parentSecret, 1700000000L)]);
        var comments = new CommentClient(bobSender, bobDb, bobCreds);
        var parentKey = new MessageKey { RemoteJid = groupJid.ToString(), Id = "POST-1", Participant = aliceLid.ToString(), FromMe = false };

        Task<SendMessageResult> cmtSend = comments.SendTextCommentAsync(groupJid, parentKey, "Awesome post!", [aliceLid, bobLid]);
        Node cmtWire = await bobWire.ReadClientNodeAsync();
        SendMessageResult cmtResult = await cmtSend;
        Node cmtSkdm = cmtWire.GetChild("participants")!.GetChildren("to").Single(n => n.GetAttr("jid") == aliceLid.ToString()).GetChild("enc")!;
        aliceWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = cmtResult.MessageId,
            ["from"] = groupJid,
            ["participant"] = bobLid,
            ["type"] = "text",
            ["t"] = "1700000200"
        }, new[] { cmtSkdm, cmtWire.GetChild("enc")! }));

        CommentReceivedEvent? cmtEv = null;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (cmtEv is null)
            {
                if (await aliceEvents.Reader.ReadAsync(cts.Token) is CommentReceivedEvent ce) cmtEv = ce;
            }
        }
        Assert.AreEqual("Awesome post!", cmtEv.Record.Text);
        Assert.AreEqual("POST-1", cmtEv.ParentMessageId);

        // 3) Inbound DraftMessageSync persisted in SqliteHistoryStore + FTS5 searchable message
        Message draftMsg = RichMessageBuilders.BuildDraftMessageSync(aliceLid, "Half-written reply", editTimestampMs: 1700000300000);
        Task<SendMessageResult> draftSend = bobSender.SendDmAsync(aliceLid, draftMsg, messageId: "DRAFT-1");
        Node draftWire = await bobWire.ReadClientNodeAsync();
        await draftSend;
        aliceWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "DRAFT-1",
            ["from"] = bobLid,
            ["type"] = "text",
            ["t"] = "1700000300"
        }, new[] { draftWire.GetChild("enc") ?? draftWire.GetChild("participants")!.GetChildren("to").Single().GetChild("enc")! }));

        DraftUpdatedEvent? draftEv = null;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (draftEv is null)
            {
                if (await aliceEvents.Reader.ReadAsync(cts.Token) is DraftUpdatedEvent de) draftEv = de;
            }
        }
        Assert.AreEqual("Half-written reply", draftEv.DraftText);
        ChatSummary savedDraftChat = (await aliceDb.History.GetChatsAsync()).Single(c => c.ChatJid == aliceLid.ToString());
        Assert.AreEqual("Half-written reply", savedDraftChat.DraftText);

        // 4) BroadcastClient lists + user broadcast fan-out
        var broadcasts = new BroadcastClient(aliceWire.Iq, aliceWire.Socket, aliceUsync, alicePreKeys, aliceSender, aliceDb, aliceCache, aliceCreds, aliceRetry);
        Task<IReadOnlyList<BroadcastListInfo>> bcListTask = broadcasts.GetBroadcastListsAsync();
        Node bcIq;
        do
        {
            bcIq = await aliceWire.ReadClientNodeAsync();
        }
        while (bcIq.Tag is "receipt" or "ack");
        Assert.AreEqual("w:b", bcIq.GetAttr("xmlns"));
        aliceWire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = bcIq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("lists", new[] { new Node("list", new Dictionary<string, NodeValue> { ["id"] = "1700000999@broadcast", ["name"] = "Team" }, new[] { new Node("recipient", new Dictionary<string, NodeValue> { ["jid"] = bobLid }) }) })
        }));
        IReadOnlyList<BroadcastListInfo> lists = await bcListTask;
        Assert.AreEqual("Team", lists[0].Name);

        Task<SendMessageResult> bcSendTask = broadcasts.SendBroadcastTextAsync(lists[0].ListJid, "Broadcast hello!", [bobLid], messageId: "BC-1");
        Node bcOut;
        do
        {
            bcOut = await aliceWire.ReadClientNodeAsync();
        }
        while (bcOut.Tag is "receipt" or "ack");
        await bcSendTask;
        Assert.AreEqual("1700000999@broadcast", bcOut.GetAttr("to"));
        Assert.AreEqual("lid", bcOut.GetAttr("addressing_mode"));
        Assert.IsNull(bcOut.GetChild("meta"));
    }

    [TestMethod]
    public async Task SyncdCoexV2_DefersKeyPersistenceUntilAck_And_BatchesOrphanApply()
    {
        await using var backend = new SqliteBackend(":memory:");
        byte[] keyId = [0x99, 0x88, 0x77, 0x66, 0x55, 0x44];
        byte[] masterKey = Enumerable.Range(30, 32).Select(static i => (byte)i).ToArray();
        ExpandedAppStateKeys keys = AppStateKeys.Expand(masterKey);
        var http = new MockSyncdHttpClient();
        var events = new List<WhatsAppEvent>();

        // Server delivers patch v1 BEFORE key is committed locally
        (SyncdMutation mut, byte[] valMac) = AppStateMutationCodec.EncodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.DisableLinkPreviews.BuildIndex(),
            new SyncActionValue { Timestamp = 500, PrivacySettingDisableLinkPreviewsAction = new() { IsPreviewsDisabled = true } },
            keys,
            keyId,
            Enumerable.Repeat((byte)0x0C, 16).ToArray());

        byte[] nextHash = LtHash.SubtractThenAdd(LtHash.CreateEmpty(), null, [valMac]);
        var patch = new SyncdPatch
        {
            Version = new SyncdVersion { Version = 1 },
            KeyId = new KeyId { Id = ByteString.CopyFrom(keyId) },
            SnapshotMac = ByteString.CopyFrom(AppStateHasher.GenerateSnapshotMac(nextHash, 1, AppStateCollections.Regular, keys.SnapshotMacKey))
        };
        patch.Mutations.Add(mut);
        patch.PatchMac = ByteString.CopyFrom(AppStateHasher.GeneratePatchMac(patch, AppStateCollections.Regular, keys.PatchMacKey, 1));

        await using var syncd = new SyncdCoordinator(
            backend,
            http,
            (iq, _ct) => ValueTask.FromResult(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = iq.GetAttr("id") ?? "1", ["type"] = "result" }, new[]
            {
                new Node("sync", new[]
                {
                    new Node("collection", new Dictionary<string, NodeValue> { ["name"] = AppStateCollections.Regular }, new[]
                    {
                        new Node("patches", new[] { new Node("patch", patch.ToByteArray()) })
                    })
                })
            })),
            emitEventAsync: ev => { events.Add(ev); return ValueTask.CompletedTask; },
            coexOptions: new SyncdCoexOptions(EnableCoexV2: true, PersistKeysOnlyAfterServerAck: true, BatchedOrphanApply: true));

        // 1) Stage key (Coex V2 holds it in memory until ACK, not yet in SQLite)
        await syncd.StageSyncKeyAsync(keyId, masterKey);
        Assert.AreEqual(1, syncd.PendingKeyAckCount);
        Assert.IsNull(await backend.AppSync.GetSyncKeyAsync(keyId));

        // 2) Sync arrives before ACK -> buffered cleanly as orphan rather than crashing
        IReadOnlyList<DecodedAppStateMutation> initial = await syncd.SyncCollectionAsync(AppStateCollections.Regular);
        Assert.AreEqual(0, initial.Count);
        Assert.AreEqual(1, syncd.PendingOrphanCount);

        // 3) Server ACK commits key -> batched orphan auto-applies
        await syncd.CommitPendingSyncKeyAckAsync(keyId);
        Assert.AreEqual(0, syncd.PendingKeyAckCount);
        Assert.AreEqual(0, syncd.PendingOrphanCount);
        Assert.IsNotNull(await backend.AppSync.GetSyncKeyAsync(keyId));
        Assert.IsTrue(events.OfType<LinkPreviewsDisabledUpdatedEvent>().Any(e => e.PreviewsDisabled));
    }
}
