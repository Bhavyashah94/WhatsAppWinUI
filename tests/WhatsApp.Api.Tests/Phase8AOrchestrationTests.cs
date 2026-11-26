using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;
using WhatsApp.Storage.Sqlite;
using LocalWirePair = WhatsApp.Api.Tests.Phase6COrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class Phase8AOrchestrationTests
{
    private sealed class MockGroupHistoryHttpClient : IHttpClient
    {
        public ConcurrentQueue<(string Method, Uri Url, int BodyLen)> Requests { get; } = new();
        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
        public string UploadPath { get; set; } = "/v/t62.7118-24/grp-hist-1.enc";

        public Task<byte[]> GetBytesAsync(Uri url, long maxBytes = 64 * 1024 * 1024, CancellationToken ct = default)
        {
            Requests.Enqueue(("GET", url, 0));
            if (Blobs.TryGetValue(url.AbsolutePath, out byte[]? blob))
            {
                return Task.FromResult(blob);
            }
            throw new HttpRequestException("404", null, HttpStatusCode.NotFound);
        }

        public Task<byte[]> PostBytesAsync(Uri url, ReadOnlyMemory<byte> body, string contentType = "application/octet-stream", CancellationToken ct = default)
        {
            Requests.Enqueue(("POST", url, body.Length));
            Blobs[UploadPath] = body.ToArray();
            string json = $"{{\"url\":\"https://{url.Host}{UploadPath}\",\"direct_path\":\"{UploadPath}\",\"handle\":\"gh-hnd-1\"}}";
            return Task.FromResult(Encoding.UTF8.GetBytes(json));
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _now = utcNow;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static async Task<DeviceCredentials> SetupNodeCredentialsAsync(
        SqliteBackend backend,
        string pnUser,
        string lidUser,
        ushort deviceId = 0)
    {
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice(pnUser, deviceId);
        creds.Lid = Jid.LidDevice(lidUser, deviceId);
        creds.PushName = pnUser;

        var primaryAccount = Curve25519.GenerateKeyPair();
        byte[] advDetails = new ADVDeviceIdentity { RawId = creds.RegistrationId, Timestamp = 1700000000, KeyIndex = deviceId }.ToByteArray();
        byte[] accSig = XEdDSA.Sign(primaryAccount.PrivateKey, [6, 0, .. advDetails, .. creds.IdentityKey.PublicKey]);
        byte[] devSig = XEdDSA.Sign(creds.IdentityKey.PrivateKey, [6, 1, .. advDetails, .. creds.IdentityKey.PublicKey, .. primaryAccount.PublicKey]);
        creds.AccountIdentityBytes = new ADVSignedDeviceIdentity
        {
            Details = ByteString.CopyFrom(advDetails),
            AccountSignatureKey = ByteString.CopyFrom(primaryAccount.PublicKey),
            AccountSignature = ByteString.CopyFrom(accSig),
            DeviceSignature = ByteString.CopyFrom(devSig)
        }.ToByteArray();
        await backend.Device.SaveAsync(creds);

        byte[] spkRecord = PreKeyManager.EncodeSignedPreKeyRecord(
            creds.SignedPreKeyId,
            creds.SignedPreKey,
            creds.SignedPreKeySignature,
            1700000000);
        await backend.Signal.SaveSignedPreKeyAsync(new SignedPreKeyEntry(
            creds.SignedPreKeyId,
            spkRecord,
            creds.SignedPreKeySignature,
            1700000000));

        var otpks = PreKeyManager.GeneratePreKeys(1, 5);
        await backend.Signal.SavePreKeysAsync(otpks.Select(k => (k.Id, k.RecordBytes)).ToArray());
        creds.NextPreKeyId = 6;
        await backend.Device.SaveAsync(creds);

        return creds;
    }

    private static Node BuildPreKeyUserNode(Jid jid, DeviceCredentials creds, uint preKeyId, byte[] preKeyPub)
    {
        var children = new List<Node>
        {
            new("registration", StanzaBuilder.EncodeUInt32BigEndian(creds.RegistrationId)),
            new("type", new[] { PreKeyIq.DjbKeyType }),
            new("identity", creds.IdentityKey.PublicKey),
            new("key", new Node[] { new("id", StanzaBuilder.EncodeKeyId24(preKeyId)), new("value", preKeyPub) }),
            new("skey", new Node[]
            {
                new("id", StanzaBuilder.EncodeKeyId24(creds.SignedPreKeyId)),
                new("value", creds.SignedPreKey.PublicKey),
                new("signature", creds.SignedPreKeySignature)
            })
        };
        if (jid.Device != 0 && creds.AccountIdentityBytes is { Length: > 0 } adv)
        {
            children.Add(new Node("device-identity", adv));
        }
        return new Node("user", new Dictionary<string, NodeValue> { ["jid"] = jid }, children.ToArray());
    }

    [TestMethod]
    public async Task GroupClient_CreatePhashCache_MexMutation_Notifications_And_MemberLabel_WorkEndToEnd()
    {
        await using var backend = new SqliteBackend(":memory:");
        await using var peerDb = new SqliteBackend(":memory:");
        DeviceCredentials creds = await SetupNodeCredentialsAsync(backend, "15550000001", "100000000000001", 1);
        DeviceCredentials peerCreds = await SetupNodeCredentialsAsync(peerDb, "15559990009", "100000000000009", 0);

        var events = Channel.CreateUnbounded<WhatsAppEvent>();
        var lidPn = new LidPnCache();
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());

        var usync = new UsyncClient(wire.Iq, backend, lidPn);
        var preKeyFetcher = new PreKeyFetchClient(wire.Iq, backend, lidPn, creds);
        var retryCoord = new RetryCoordinator(wire.Socket, backend, preKeyFetcher, lidPn, creds, e => events.Writer.WriteAsync(e));
        var sender = new MessageSender(wire.Socket, usync, preKeyFetcher, backend, lidPn, creds, retryCoord);
        var mex = new MexClient(wire.Iq);
        var groups = new GroupClient(wire.Iq, mex, sender, backend, lidPn, emitEvent: e => events.Writer.WriteAsync(e));
        var notifications = new NotificationRouter(backend, lidPn, creds, wire.Socket, wire.Iq, syncd: null, e => events.Writer.WriteAsync(e), groupClient: groups);

        Jid groupJid = Jid.Parse("120363000000000555@g.us");
        Jid peerLid = peerCreds.Lid!.Value.ToNonAd();
        Jid peerPn = peerCreds.Pn!.Value.ToNonAd();

        // 1) Create group + learn LID<->PN + cache metadata
        Task<GroupMetadata> createTask = groups.CreateGroupAsync(new GroupCreateOptions("Test Room", []));
        Node createReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("w:g2", createReq.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = createReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("group", new Dictionary<string, NodeValue> { ["id"] = groupJid.User, ["subject"] = "Test Room", ["addressing_mode"] = "lid" }, new[]
            {
                new Node("participant", new Dictionary<string, NodeValue> { ["jid"] = creds.Lid!.Value.ToNonAd(), ["type"] = "superadmin" }),
                new Node("participant", new Dictionary<string, NodeValue> { ["jid"] = peerLid, ["phone_number"] = peerPn })
            })
        }));
        GroupMetadata created = await createTask;
        Assert.AreEqual(2, created.Participants.Count);
        Assert.IsTrue(groups.TryGetCached(groupJid, out _));
        Assert.AreEqual(peerPn.ToNonAdString(), lidPn.TryGetPn(peerLid.ToNonAdString(), out string? mappedPn) ? mappedPn : null);

        // 2) QueryMetadata sends phash and keeps cache on NotModified (no <group>)
        Task<GroupMetadata?> queryTask = groups.QueryMetadataAsync(groupJid);
        Node queryReq = await wire.ReadClientNodeAsync();
        string? sentPhash = queryReq.GetChild("query")!.GetAttr("phash");
        Assert.IsFalse(string.IsNullOrEmpty(sentPhash));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = queryReq.GetAttr("id")!, ["type"] = "result" }));
        GroupMetadata? cached = await queryTask;
        Assert.AreEqual("Test Room", cached?.Subject);

        // 3) MEX UpdateMemberShareHistoryMode
        Task mexTask = groups.SetMemberShareHistoryModeAsync(groupJid, MemberShareHistoryMode.AllMemberShare);
        Node mexReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("w:mex", mexReq.GetAttr("xmlns"));
        Assert.AreEqual("9418211574894172", mexReq.GetChild("query")!.GetAttr("query_id"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = mexReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("result", $"{{\"data\":{{\"xwa2_group_update_property\":{{\"id\":\"{groupJid}\",\"state\":\"ACTIVE\"}}}}}}")
        }));
        await mexTask;
        Assert.IsTrue(groups.TryGetCached(groupJid, out GroupMetadata? afterMex));
        Assert.AreEqual(MemberShareHistoryMode.AllMemberShare, afterMex?.ShareHistoryMode);

        // 4) UpdateMemberLabel sends SenderKey message with <meta appdata="member_tag" tag_reason="user_update"/>
        await backend.Protocol.SaveDeviceListAsync(peerLid.ToNonAdString(), [0u]);
        await backend.Protocol.SaveDeviceListAsync(creds.Lid!.Value.ToNonAdString(), [1u]);
        byte[] peerOtpkPub = PreKeyManager.DecodePreKeyRecord((await peerDb.Signal.GetPreKeyAsync(1))!).KeyPair.PublicKey;

        Task<SendMessageResult> labelTask = groups.UpdateMemberLabelAsync(groupJid, "Moderator");
        Node preKeyReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("encrypt", preKeyReq.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = preKeyReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("list", new[] { BuildPreKeyUserNode(peerLid, peerCreds, 1, peerOtpkPub) })
        }));
        await labelTask;

        Node msgStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("message", msgStanza.Tag);
        Assert.AreEqual("text", msgStanza.GetAttr("type"));
        Node? meta = msgStanza.GetChild("meta");
        Assert.AreEqual("member_tag", meta?.GetAttr("appdata"));
        Assert.AreEqual("user_update", meta?.GetAttr("tag_reason"));

        // 5) NotificationRouter updates GroupClient cache & retires SenderKey on remove
        await notifications.HandleAsync(new Node("notification", new Dictionary<string, NodeValue>
        {
            ["from"] = groupJid,
            ["id"] = "gp2-n-1",
            ["type"] = "w:gp2",
            ["t"] = "1700001000"
        }, new Node[]
        {
            new("subject", new Dictionary<string, NodeValue> { ["subject"] = "Updated Subject" }),
            new("remove", null, new[] { new Node("participant", new Dictionary<string, NodeValue> { ["jid"] = peerLid }) })
        }), sendAck: false);

        Assert.IsTrue(groups.TryGetCached(groupJid, out GroupMetadata? afterNotif));
        Assert.AreEqual("Updated Subject", afterNotif?.Subject);
        Assert.AreEqual(1, afterNotif?.Participants.Count);
    }

    [TestMethod]
    public async Task GroupHistoryCoordinator_ShareDirectBundleThenNotice_And_ReceiverDecrypt_WorkEndToEnd()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_010_000));
        await using var senderBackend = new SqliteBackend(":memory:");
        await using var receiverBackend = new SqliteBackend(":memory:");
        await using var existingBackend = new SqliteBackend(":memory:");

        DeviceCredentials senderCreds = await SetupNodeCredentialsAsync(senderBackend, "15551110001", "100000000000001", 1);
        DeviceCredentials recvCreds = await SetupNodeCredentialsAsync(receiverBackend, "15552220002", "100000000000002", 0);
        DeviceCredentials existingCreds = await SetupNodeCredentialsAsync(existingBackend, "15553330003", "100000000000099", 0);

        var http = new MockGroupHistoryHttpClient();
        var senderEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var recvEvents = Channel.CreateUnbounded<WhatsAppEvent>();

        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var senderCache = new LidPnCache(clock);
        var recvCache = new LidPnCache(clock);

        var usync = new UsyncClient(wire.Iq, senderBackend, senderCache, clock);
        var preKeyFetcher = new PreKeyFetchClient(wire.Iq, senderBackend, senderCache, senderCreds);
        var retryCoord = new RetryCoordinator(wire.Socket, senderBackend, preKeyFetcher, senderCache, senderCreds, e => senderEvents.Writer.WriteAsync(e));
        var messageSender = new MessageSender(wire.Socket, usync, preKeyFetcher, senderBackend, senderCache, senderCreds, retryCoord, clock);
        var mediaTransfer = new MediaTransferClient(wire.Iq, http, clock);
        var mex = new MexClient(wire.Iq);
        var groups = new GroupClient(wire.Iq, mex, messageSender, senderBackend, senderCache, timeProvider: clock);
        var groupHistory = new GroupHistoryCoordinator(wire.Iq, messageSender, mediaTransfer, senderCreds, clock);
        groupHistory.UpdateAccountProps(new Dictionary<uint, string>
        {
            [AbPropsIq.PropGroupHistorySend] = "true",
            [AbPropsIq.PropGroupHistoryCountLimit] = "10",
            [AbPropsIq.PropGroupHistoryTimeLimitSecs] = "86400"
        });

        Jid groupJid = Jid.Parse("120363000000000888@g.us");
        Jid existingMember = existingCreds.Lid!.Value.ToNonAd();
        Jid newOptedIn = recvCreds.Lid!.Value.ToNonAd();

        await senderBackend.Protocol.SaveDeviceListAsync(senderCreds.Lid!.Value.ToNonAdString(), [1u]);
        await senderBackend.Protocol.SaveDeviceListAsync(newOptedIn.ToNonAdString(), [0u]);
        await senderBackend.Protocol.SaveDeviceListAsync(existingMember.ToNonAdString(), [0u]);

        byte[] recvOtpkPub = PreKeyManager.DecodePreKeyRecord((await receiverBackend.Signal.GetPreKeyAsync(1))!).KeyPair.PublicKey;
        byte[] exOtpkPub = PreKeyManager.DecodePreKeyRecord((await existingBackend.Signal.GetPreKeyAsync(1))!).KeyPair.PublicKey;

        var historyMessages = new[]
        {
            new WebMessageInfo
            {
                Key = new MessageKey { RemoteJid = groupJid.ToString(), Id = "OLD-MSG-1", FromMe = true },
                Message = new Message { Conversation = "Welcome to the team!" },
                MessageTimestamp = 1_700_009_000UL,
                Status = WebMessageInfo.Types.Status.ServerAck
            }
        };

        Task<GroupHistoryAddResult> addHistTask = groupHistory.AddParticipantsWithHistoryAsync(
            groups,
            groupJid,
            [newOptedIn],
            [newOptedIn],
            historyMessages);

        // IQ 1: add participant
        Node addReq = await wire.ReadClientNodeAsync();
        Assert.IsNotNull(addReq.GetChild("add"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = addReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("add", null, new[] { new Node("participant", new Dictionary<string, NodeValue> { ["jid"] = newOptedIn }) })
        }));

        // IQ 2: CheckGroupHistoryContext -> QueryMetadataAsync(bypassCache: true)
        Node gqReq = await wire.ReadClientNodeAsync();
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = gqReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("group", new Dictionary<string, NodeValue> { ["id"] = groupJid.User, ["addressing_mode"] = "lid" }, new Node[]
            {
                new("participant", new Dictionary<string, NodeValue> { ["jid"] = senderCreds.Lid!.Value.ToNonAd(), ["type"] = "admin" }),
                new("participant", new Dictionary<string, NodeValue> { ["jid"] = existingMember }),
                new("participant", new Dictionary<string, NodeValue> { ["jid"] = newOptedIn }),
                new("group_history")
            })
        }));

        // IQ 3: GroupPropsSpec abt request
        Node gpReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("abt", gpReq.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = gpReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("props", new Dictionary<string, NodeValue> { ["hash"] = "grp-h-1" })
        }));

        // IQ 4: media_conn for /mms/group-history upload
        Node mcReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("w:m", mcReq.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = mcReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("media_conn", new Dictionary<string, NodeValue> { ["auth"] = "GH_AUTH", ["ttl"] = "3600" }, new[]
            {
                new Node("host", new Dictionary<string, NodeValue> { ["hostname"] = "cdn.whatsapp.net", ["type"] = "primary" })
            })
        }));

        // IQ 5 & 6: DeliverPreparedShareAsync re-checks group metadata & group props
        Node gqReq2 = await wire.ReadClientNodeAsync();
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = gqReq2.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("group", new Dictionary<string, NodeValue> { ["id"] = groupJid.User, ["addressing_mode"] = "lid" }, new Node[]
            {
                new("participant", new Dictionary<string, NodeValue> { ["jid"] = senderCreds.Lid!.Value.ToNonAd(), ["type"] = "admin" }),
                new("participant", new Dictionary<string, NodeValue> { ["jid"] = existingMember }),
                new("participant", new Dictionary<string, NodeValue> { ["jid"] = newOptedIn }),
                new("group_history")
            })
        }));
        Node gpReq2 = await wire.ReadClientNodeAsync();
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = gpReq2.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("props", new Dictionary<string, NodeValue> { ["hash"] = "grp-h-1" })
        }));

        // IQ 7: encrypt prekey fetch ONLY for newOptedIn!
        Node pkReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("encrypt", pkReq.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = pkReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("list", new[] { BuildPreKeyUserNode(newOptedIn, recvCreds, 1, recvOtpkPub) })
        }));

        // Node 8: Direct pairwise bundle <message> (NO skmsg!)
        Node bundleStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("message", bundleStanza.Tag);
        Assert.AreEqual("media", bundleStanza.GetAttr("type"));
        Assert.IsNull(bundleStanza.GetChild("enc")); // NEVER SenderKey!
        Node[] bundleTo = bundleStanza.GetChild("participants")!.GetChildren("to").ToArray();
        Assert.AreEqual(1, bundleTo.Length);
        Assert.AreEqual(newOptedIn, Jid.Parse(bundleTo[0].GetAttr("jid")!));
        Assert.AreEqual("group_history", bundleTo[0].GetChild("enc")!.GetAttr("mediatype"));

        // Correlate server ACK for bundle -> unlocks notice broadcast
        string bundleId = bundleStanza.GetAttr("id")!;
        messageSender.TryResolveMessageAck(new Node("ack", new Dictionary<string, NodeValue> { ["class"] = "message", ["id"] = bundleId }));

        // Prekey fetch for remaining group member (existingMember) before SenderKey notice SKDM
        Node pkReq2 = await wire.ReadClientNodeAsync();
        Assert.AreEqual("encrypt", pkReq2.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = pkReq2.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("list", new[] { BuildPreKeyUserNode(existingMember, existingCreds, 1, exOtpkPub) })
        }));

        // Node 9: Group-wide SenderKey notice <message>
        Node noticeStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("message", noticeStanza.Tag);
        Assert.AreEqual("text", noticeStanza.GetAttr("type"));
        Assert.AreEqual("skmsg", noticeStanza.GetChild("enc")?.GetAttr("type"));
        messageSender.TryResolveMessageAck(new Node("ack", new Dictionary<string, NodeValue> { ["class"] = "message", ["id"] = noticeStanza.GetAttr("id")! }));

        GroupHistoryAddResult finalRes = await addHistTask;
        Assert.AreEqual(GroupHistoryShareStatus.Shared, finalRes.HistoryShare.Status);
        Assert.AreEqual(1, finalRes.HistoryShare.MessageCount);

        // Verify receiver decrypts the bundle stanza & downloads + inflates Welcome to the team!
        await using var recvWire = new LocalWirePair();
        await using var recvRouter = new StanzaRouter(recvWire.Socket, recvWire.Iq, new StanzaHandlers());
        var recvMedia = new MediaTransferClient(recvWire.Iq, http, clock);
        var recvPreKeys = new PreKeyFetchClient(recvWire.Iq, receiverBackend, recvCache, recvCreds);
        var recvRetry = new RetryCoordinator(recvWire.Socket, receiverBackend, recvPreKeys, recvCache, recvCreds, e => recvEvents.Writer.WriteAsync(e));
        var receiver = new MessageReceiver(recvWire.Socket, receiverBackend, recvCache, recvCreds, recvRetry, e => recvEvents.Writer.WriteAsync(e), clock, mediaTransfer: recvMedia);

        Node encForReceiver = bundleTo[0].GetChild("enc")!;
        var inboundBundleNode = new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = bundleId,
            ["from"] = groupJid,
            ["participant"] = senderCreds.Lid!.Value,
            ["type"] = "media",
            ["t"] = "1700010000"
        }, new[] { encForReceiver });

        Task recvTask = receiver.HandleMessageStanzaAsync(inboundBundleNode);
        Node recvMcReq = await recvWire.ReadClientNodeAsync();
        Assert.AreEqual("w:m", recvMcReq.GetAttr("xmlns"));
        recvWire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = recvMcReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("media_conn", new Dictionary<string, NodeValue> { ["auth"] = "GH_AUTH", ["ttl"] = "3600" }, new[]
            {
                new Node("host", new Dictionary<string, NodeValue> { ["hostname"] = "cdn.whatsapp.net", ["type"] = "primary" })
            })
        }));
        await recvTask;

        var emitted = new List<WhatsAppEvent>();
        while (recvEvents.Reader.TryRead(out WhatsAppEvent? ev)) emitted.Add(ev);
        var ghEvt = emitted.OfType<GroupHistoryBundleReceivedEvent>().Single();
        Assert.AreEqual(1, ghEvt.Messages.Count);
        Assert.AreEqual("Welcome to the team!", ghEvt.Messages[0].Text);

        IReadOnlyList<MessageRecord> stored = await receiverBackend.History.GetMessagesAsync(groupJid.ToString(), beforeTimestamp: null, limit: 10);
        Assert.IsTrue(stored.Any(m => m.Id == "OLD-MSG-1" && m.Text == "Welcome to the team!"));
    }
}
