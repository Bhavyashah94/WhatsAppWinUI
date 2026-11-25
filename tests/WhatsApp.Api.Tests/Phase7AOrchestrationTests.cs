using System.Security.Cryptography;
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
public sealed class Phase7AOrchestrationTests
{
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

        var spkRecord = PreKeyManager.EncodeSignedPreKeyRecord(
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
    public async Task SendDm_And_ReceiveDm_EndToEnd_With_PreKeyFetch_CompanionDsm_And_Reply()
    {
        await using var aliceDb = new SqliteBackend(":memory:");
        await using var bobDb = new SqliteBackend(":memory:");
        await using var aliceCompDb = new SqliteBackend(":memory:");

        DeviceCredentials aliceCreds = await SetupNodeCredentialsAsync(aliceDb, "15551110000", "100000000000001", 0);
        DeviceCredentials bobCreds = await SetupNodeCredentialsAsync(bobDb, "15552220000", "100000000000002", 0);
        DeviceCredentials aliceCompCreds = await SetupNodeCredentialsAsync(aliceCompDb, "15551110000", "100000000000001", 1);

        var aliceCache = new LidPnCache();
        var bobCache = new LidPnCache();
        var compCache = new LidPnCache();

        var aliceEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var bobEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var compEvents = Channel.CreateUnbounded<WhatsAppEvent>();

        await using var aliceWire = new LocalWirePair();
        await using var bobWire = new LocalWirePair();
        await using var compWire = new LocalWirePair();

        var bobRetry = new RetryCoordinator(bobWire.Socket, bobDb, new PreKeyFetchClient(bobWire.Iq, bobDb, bobCache, bobCreds), bobCache, bobCreds, e => bobEvents.Writer.WriteAsync(e));
        var bobReceiver = new MessageReceiver(bobWire.Socket, bobDb, bobCache, bobCreds, bobRetry, e => bobEvents.Writer.WriteAsync(e));

        var compRetry = new RetryCoordinator(compWire.Socket, aliceCompDb, new PreKeyFetchClient(compWire.Iq, aliceCompDb, compCache, aliceCompCreds), compCache, aliceCompCreds, e => compEvents.Writer.WriteAsync(e));
        var compReceiver = new MessageReceiver(compWire.Socket, aliceCompDb, compCache, aliceCompCreds, compRetry, e => compEvents.Writer.WriteAsync(e));

        await using var aliceRouter = new StanzaRouter(aliceWire.Socket, aliceWire.Iq, new StanzaHandlers());
        await using var bobRouter = new StanzaRouter(bobWire.Socket, bobWire.Iq, new StanzaHandlers
        {
            OnMessage = (n, t) => new ValueTask(bobReceiver.HandleMessageStanzaAsync(n, t))
        });
        await using var compRouter = new StanzaRouter(compWire.Socket, compWire.Iq, new StanzaHandlers
        {
            OnMessage = (n, t) => new ValueTask(compReceiver.HandleMessageStanzaAsync(n, t))
        });

        var aliceUsync = new UsyncClient(aliceWire.Iq, aliceDb, aliceCache);
        var alicePreKeys = new PreKeyFetchClient(aliceWire.Iq, aliceDb, aliceCache, aliceCreds);
        var aliceRetry = new RetryCoordinator(aliceWire.Socket, aliceDb, alicePreKeys, aliceCache, aliceCreds, e => aliceEvents.Writer.WriteAsync(e));
        var aliceSender = new MessageSender(aliceWire.Socket, aliceUsync, alicePreKeys, aliceDb, aliceCache, aliceCreds, aliceRetry);

        // Pre-seed cached device lists for Bob [0] & Alice [0, 1]
        await aliceDb.Protocol.SaveDeviceListAsync("15552220000@s.whatsapp.net", [0u]);
        await aliceDb.Protocol.SaveDeviceListAsync("15551110000@s.whatsapp.net", [0u, 1u]);

        // Alice sends DM -> server handles batched prekey IQ & returns Bob:0 and Alice:1 bundles
        Task<SendMessageResult> sendTask = aliceSender.SendTextAsync(Jid.Pn("15552220000"), "Hello Bob from Phase 7A!", messageId: "DM-1");

        Node preKeyReq = await aliceWire.ReadClientNodeAsync();
        Assert.AreEqual("encrypt", preKeyReq.GetAttr("xmlns"));

        byte[] bobOtpkPub = PreKeyManager.DecodePreKeyRecord((await bobDb.Signal.GetPreKeyAsync(1))!).KeyPair.PublicKey;
        byte[] compOtpkPub = PreKeyManager.DecodePreKeyRecord((await aliceCompDb.Signal.GetPreKeyAsync(1))!).KeyPair.PublicKey;

        aliceWire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = preKeyReq.GetAttr("id")!, ["type"] = "result" }, new Node[]
        {
            new("list", new Node[]
            {
                BuildPreKeyUserNode(Jid.PnDevice("15552220000", 0), bobCreds, 1, bobOtpkPub),
                BuildPreKeyUserNode(Jid.PnDevice("15551110000", 1), aliceCompCreds, 1, compOtpkPub)
            })
        }));

        Node sentMsgStanza = await aliceWire.ReadClientNodeAsync();
        SendMessageResult sendResult = await sendTask;
        Assert.AreEqual("DM-1", sendResult.MessageId);
        Assert.AreEqual(2, sendResult.TargetDeviceCount);
        Assert.IsNotNull(sentMsgStanza.GetChild("device-identity"));
        Assert.IsNotNull(sentMsgStanza.GetChild("reporting")?.GetChild("reporting_token"));

        // Deliver per-device <enc> to Bob:0 and Alice:1
        Node[] toNodes = sentMsgStanza.GetChild("participants")!.GetChildren("to").ToArray();
        Node bobEnc = toNodes.First(n => n.GetAttr("jid") == "15552220000@s.whatsapp.net").GetChild("enc")!;
        Node compEnc = toNodes.First(n => n.GetAttr("jid") == "15551110000:1@s.whatsapp.net").GetChild("enc")!;

        Assert.AreEqual("pkmsg", bobEnc.GetAttr("type"));
        Assert.AreEqual("pkmsg", compEnc.GetAttr("type"));

        bobWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "DM-1",
            ["from"] = Jid.PnDevice("15551110000", 0),
            ["t"] = "1700000200",
            ["type"] = "text"
        }, new[] { bobEnc }));

        Node bobDeliveryReceipt = await bobWire.ReadClientNodeAsync();
        Assert.AreEqual("receipt", bobDeliveryReceipt.Tag);
        Assert.AreEqual("DM-1", bobDeliveryReceipt.GetAttr("id"));

        WhatsAppEvent bobEvt = await bobEvents.Reader.ReadAsync();
        Assert.IsInstanceOfType<MessageReceivedEvent>(bobEvt);
        MessageRecord bobMsg = ((MessageReceivedEvent)bobEvt).Message;
        Assert.AreEqual("Hello Bob from Phase 7A!", bobMsg.Text);
        Assert.IsFalse(bobMsg.FromMe);

        // Consumed OTPK #1 deleted on Bob
        Assert.IsNull(await bobDb.Signal.GetPreKeyAsync(1));

        // Companion device receives DeviceSentMessage -> stored as FromMe in Bob's chat
        compWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "DM-1",
            ["from"] = Jid.PnDevice("15551110000", 0),
            ["t"] = "1700000200",
            ["type"] = "text"
        }, new[] { compEnc }));

        Node compAck = await compWire.ReadClientNodeAsync();
        Assert.AreEqual("ack", compAck.Tag);
        WhatsAppEvent compEvt = await compEvents.Reader.ReadAsync();
        MessageRecord compMsg = ((MessageReceivedEvent)compEvt).Message;
        Assert.IsTrue(compMsg.FromMe);
        Assert.AreEqual("15552220000@s.whatsapp.net", compMsg.RemoteJid);
        Assert.AreEqual("Hello Bob from Phase 7A!", compMsg.Text);
    }

    [TestMethod]
    public async Task SendGroupMessage_SelectiveSkdm_And_RotationOnRemove_EndToEnd()
    {
        await using var aliceDb = new SqliteBackend(":memory:");
        await using var bobDb = new SqliteBackend(":memory:");
        await using var carolDb = new SqliteBackend(":memory:");

        DeviceCredentials aliceCreds = await SetupNodeCredentialsAsync(aliceDb, "15551110000", "100000000000001", 0);
        DeviceCredentials bobCreds = await SetupNodeCredentialsAsync(bobDb, "15552220000", "100000000000002", 0);
        DeviceCredentials carolCreds = await SetupNodeCredentialsAsync(carolDb, "15553330000", "100000000000003", 0);

        var aliceCache = new LidPnCache();
        var bobCache = new LidPnCache();
        var carolCache = new LidPnCache();

        var aliceEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var bobEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var carolEvents = Channel.CreateUnbounded<WhatsAppEvent>();

        await using var aliceWire = new LocalWirePair();
        await using var bobWire = new LocalWirePair();
        await using var carolWire = new LocalWirePair();

        var bobRetry = new RetryCoordinator(bobWire.Socket, bobDb, new PreKeyFetchClient(bobWire.Iq, bobDb, bobCache, bobCreds), bobCache, bobCreds, e => bobEvents.Writer.WriteAsync(e));
        var bobReceiver = new MessageReceiver(bobWire.Socket, bobDb, bobCache, bobCreds, bobRetry, e => bobEvents.Writer.WriteAsync(e));
        var carolRetry = new RetryCoordinator(carolWire.Socket, carolDb, new PreKeyFetchClient(carolWire.Iq, carolDb, carolCache, carolCreds), carolCache, carolCreds, e => carolEvents.Writer.WriteAsync(e));
        var carolReceiver = new MessageReceiver(carolWire.Socket, carolDb, carolCache, carolCreds, carolRetry, e => carolEvents.Writer.WriteAsync(e));

        await using var aliceRouter = new StanzaRouter(aliceWire.Socket, aliceWire.Iq, new StanzaHandlers());
        await using var bobRouter = new StanzaRouter(bobWire.Socket, bobWire.Iq, new StanzaHandlers { OnMessage = (n, t) => new ValueTask(bobReceiver.HandleMessageStanzaAsync(n, t)) });
        await using var carolRouter = new StanzaRouter(carolWire.Socket, carolWire.Iq, new StanzaHandlers { OnMessage = (n, t) => new ValueTask(carolReceiver.HandleMessageStanzaAsync(n, t)) });

        var aliceUsync = new UsyncClient(aliceWire.Iq, aliceDb, aliceCache);
        var alicePreKeys = new PreKeyFetchClient(aliceWire.Iq, aliceDb, aliceCache, aliceCreds);
        var aliceRetry = new RetryCoordinator(aliceWire.Socket, aliceDb, alicePreKeys, aliceCache, aliceCreds, e => aliceEvents.Writer.WriteAsync(e));
        var aliceSender = new MessageSender(aliceWire.Socket, aliceUsync, alicePreKeys, aliceDb, aliceCache, aliceCreds, aliceRetry);
        var aliceNotif = new NotificationRouter(aliceDb, aliceCache, aliceCreds, aliceWire.Socket, aliceWire.Iq, null, e => aliceEvents.Writer.WriteAsync(e));

        await aliceDb.Protocol.SaveDeviceListAsync("15551110000@s.whatsapp.net", [0u]);
        await aliceDb.Protocol.SaveDeviceListAsync("15552220000@s.whatsapp.net", [0u]);
        await aliceDb.Protocol.SaveDeviceListAsync("15553330000@s.whatsapp.net", [0u]);

        Jid groupJid = Jid.Parse("120363999000@g.us");
        Jid bobJid = Jid.Pn("15552220000");
        Jid carolJid = Jid.Pn("15553330000");

        // M1: First group message must fetch Bob & Carol prekeys and emit SKDM <participants>
        Task<SendMessageResult> gm1Task = aliceSender.SendTextAsync(groupJid, "Group msg 1", [bobJid, carolJid], messageId: "GM-1");
        Node pkReq = await aliceWire.ReadClientNodeAsync();
        byte[] bobOtpk = PreKeyManager.DecodePreKeyRecord((await bobDb.Signal.GetPreKeyAsync(1))!).KeyPair.PublicKey;
        byte[] carolOtpk = PreKeyManager.DecodePreKeyRecord((await carolDb.Signal.GetPreKeyAsync(1))!).KeyPair.PublicKey;
        aliceWire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = pkReq.GetAttr("id")!, ["type"] = "result" }, new Node[]
        {
            new("list", new Node[]
            {
                BuildPreKeyUserNode(bobJid, bobCreds, 1, bobOtpk),
                BuildPreKeyUserNode(carolJid, carolCreds, 1, carolOtpk)
            })
        }));

        Node gm1Stanza = await aliceWire.ReadClientNodeAsync();
        SendMessageResult gm1Res = await gm1Task;
        Assert.IsTrue(gm1Res.Phash!.StartsWith("2:", StringComparison.Ordinal));

        Node skmsg1 = gm1Stanza.GetChild("enc")!;
        Node[] skdmTos = gm1Stanza.GetChild("participants")!.GetChildren("to").ToArray();
        Assert.AreEqual(2, skdmTos.Length);

        // Deliver SKDM + skmsg to Bob & Carol
        bobWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "GM-1",
            ["from"] = groupJid,
            ["participant"] = Jid.PnDevice("15551110000", 0),
            ["t"] = "1700000300",
            ["type"] = "text"
        }, new[] { skdmTos.First(n => n.GetAttr("jid") == "15552220000@s.whatsapp.net").GetChild("enc")!, skmsg1 }));

        _ = await bobWire.ReadClientNodeAsync(); // receipt
        MessageRecord bobGm1 = ((MessageReceivedEvent)await bobEvents.Reader.ReadAsync()).Message;
        Assert.AreEqual("Group msg 1", bobGm1.Text);

        carolWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "GM-1",
            ["from"] = groupJid,
            ["participant"] = Jid.PnDevice("15551110000", 0),
            ["t"] = "1700000300",
            ["type"] = "text"
        }, new[] { skdmTos.First(n => n.GetAttr("jid") == "15553330000@s.whatsapp.net").GetChild("enc")!, skmsg1 }));

        _ = await carolWire.ReadClientNodeAsync();
        MessageRecord carolGm1 = ((MessageReceivedEvent)await carolEvents.Reader.ReadAsync()).Message;
        Assert.AreEqual("Group msg 1", carolGm1.Text);

        // M2: Second group message has NO <participants> (selective SKDM skip!)
        Task<SendMessageResult> gm2Task = aliceSender.SendTextAsync(groupJid, "Group msg 2", [bobJid, carolJid], messageId: "GM-2");
        Node gm2Stanza = await aliceWire.ReadClientNodeAsync();
        _ = await gm2Task;
        Assert.IsNull(gm2Stanza.GetChild("participants"));

        bobWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "GM-2",
            ["from"] = groupJid,
            ["participant"] = Jid.PnDevice("15551110000", 0),
            ["t"] = "1700000310",
            ["type"] = "text"
        }, new[] { gm2Stanza.GetChild("enc")! }));
        _ = await bobWire.ReadClientNodeAsync();
        Assert.AreEqual("Group msg 2", ((MessageReceivedEvent)await bobEvents.Reader.ReadAsync()).Message.Text);

        // Simulate Carol removed (w:gp2) -> Alice deletes sender key + clears distributed devices
        await aliceNotif.HandleAsync(new Node("notification", new Dictionary<string, NodeValue>
        {
            ["id"] = "GP-REM-1",
            ["from"] = groupJid,
            ["type"] = "w:gp2",
            ["t"] = "1700000320"
        }, new Node[]
        {
            new("remove", new[] { new Node("participant", new Dictionary<string, NodeValue> { ["jid"] = carolJid }) })
        }));
        _ = await aliceWire.ReadClientNodeAsync(); // notif ack

        // M3 with [bobJid] only -> re-creates SKDM exclusively for Bob!
        Task<SendMessageResult> gm3Task = aliceSender.SendTextAsync(groupJid, "After remove Carol", [bobJid], messageId: "GM-3");
        Node gm3Stanza = await aliceWire.ReadClientNodeAsync();
        _ = await gm3Task;
        Node[] skdm3 = gm3Stanza.GetChild("participants")!.GetChildren("to").ToArray();
        Assert.AreEqual(1, skdm3.Length);
        Assert.AreEqual("15552220000@s.whatsapp.net", skdm3[0].GetAttr("jid"));

        bobWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "GM-3",
            ["from"] = groupJid,
            ["participant"] = Jid.PnDevice("15551110000", 0),
            ["t"] = "1700000330",
            ["type"] = "text"
        }, new[] { skdm3[0].GetChild("enc")!, gm3Stanza.GetChild("enc")! }));
        _ = await bobWire.ReadClientNodeAsync();
        Assert.AreEqual("After remove Carol", ((MessageReceivedEvent)await bobEvents.Reader.ReadAsync()).Message.Text);
    }

    [TestMethod]
    public async Task RetryCoordinator_HandlesInboundMaxCap_And_OutboundRetryResend()
    {
        await using var aliceDb = new SqliteBackend(":memory:");
        await using var bobDb = new SqliteBackend(":memory:");

        DeviceCredentials aliceCreds = await SetupNodeCredentialsAsync(aliceDb, "15551110000", "100000000000001", 0);
        DeviceCredentials bobCreds = await SetupNodeCredentialsAsync(bobDb, "15552220000", "100000000000002", 0);

        var aliceCache = new LidPnCache();
        var bobCache = new LidPnCache();
        var aliceEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var bobEvents = Channel.CreateUnbounded<WhatsAppEvent>();

        await using var aliceWire = new LocalWirePair();
        await using var bobWire = new LocalWirePair();

        var bobRetry = new RetryCoordinator(bobWire.Socket, bobDb, new PreKeyFetchClient(bobWire.Iq, bobDb, bobCache, bobCreds), bobCache, bobCreds, e => bobEvents.Writer.WriteAsync(e));
        var bobReceiver = new MessageReceiver(bobWire.Socket, bobDb, bobCache, bobCreds, bobRetry, e => bobEvents.Writer.WriteAsync(e));

        var alicePreKeys = new PreKeyFetchClient(aliceWire.Iq, aliceDb, aliceCache, aliceCreds);
        var aliceRetry = new RetryCoordinator(aliceWire.Socket, aliceDb, alicePreKeys, aliceCache, aliceCreds, e => aliceEvents.Writer.WriteAsync(e));
        var aliceReceiver = new MessageReceiver(aliceWire.Socket, aliceDb, aliceCache, aliceCreds, aliceRetry, e => aliceEvents.Writer.WriteAsync(e));
        var aliceUsync = new UsyncClient(aliceWire.Iq, aliceDb, aliceCache);
        var aliceSender = new MessageSender(aliceWire.Socket, aliceUsync, alicePreKeys, aliceDb, aliceCache, aliceCreds, aliceRetry);

        await using var aliceRouter = new StanzaRouter(aliceWire.Socket, aliceWire.Iq, new StanzaHandlers
        {
            OnReceipt = (n, t) => new ValueTask(aliceReceiver.HandleReceiptStanzaAsync(n, t))
        });
        await using var bobRouter = new StanzaRouter(bobWire.Socket, bobWire.Iq, new StanzaHandlers
        {
            OnMessage = (n, t) => new ValueTask(bobReceiver.HandleMessageStanzaAsync(n, t))
        });

        // 1) Bob receives undecryptable msg (missing session) -> retry 1 (no keys), retry 2 (+keys), capped at 5
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            bobWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
            {
                ["id"] = "BAD-MSG-1",
                ["from"] = Jid.PnDevice("15551110000", 0),
                ["t"] = "1700000400",
                ["type"] = "text"
            }, new[] { new Node("enc", new Dictionary<string, NodeValue> { ["v"] = "2", ["type"] = "msg" }, new byte[] { 0x33, 0x01, 0x02 }) }));

            WhatsAppEvent ev = await bobEvents.Reader.ReadAsync();
            var fail = (DecryptionFailureEvent)ev;
            Assert.AreEqual(attempt, fail.RetryCount);
            if (attempt <= RetryAndMediaRetryStanzas.MaxRetryCount)
            {
                Assert.IsTrue(fail.SentRetryReceipt);
                Node rNode = await bobWire.ReadClientNodeAsync();
                Assert.AreEqual("retry", rNode.GetAttr("type"));
                if (attempt == 1) Assert.IsNull(rNode.GetChild("keys"));
                if (attempt >= 2) Assert.IsNotNull(rNode.GetChild("keys"));
            }
            else
            {
                Assert.IsFalse(fail.SentRetryReceipt);
            }
        }

        // 2) Alice caches outbound DM and receives retry count=2 with Bob's fresh prekeys & different registrationId
        await aliceDb.Protocol.SaveDeviceListAsync("15552220000@s.whatsapp.net", [0u]);
        await aliceDb.Protocol.SaveDeviceListAsync("15551110000@s.whatsapp.net", [0u]);

        Task<SendMessageResult> dmTask = aliceSender.SendTextAsync(Jid.Pn("15552220000"), "Retry recoverable message", messageId: "RETRY-OK-1");
        Node pkReq = await aliceWire.ReadClientNodeAsync();
        byte[] bobOtpk1 = PreKeyManager.DecodePreKeyRecord((await bobDb.Signal.GetPreKeyAsync(1))!).KeyPair.PublicKey;
        aliceWire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = pkReq.GetAttr("id")!, ["type"] = "result" }, new Node[]
        {
            new("list", new[] { BuildPreKeyUserNode(Jid.PnDevice("15552220000", 0), bobCreds, 1, bobOtpk1) })
        }));
        _ = await aliceWire.ReadClientNodeAsync();
        await dmTask;

        // Simulate Bob rotated registration & sends retry receipt count=2 with bundled prekey #2
        bobCreds.RegistrationId += 10;
        byte[] bobOtpk2 = PreKeyManager.DecodePreKeyRecord((await bobDb.Signal.GetPreKeyAsync(2))!).KeyPair.PublicKey;
        Node bobRetryReceipt = RetryAndMediaRetryStanzas.BuildRetryReceipt(
            "RETRY-OK-1",
            Jid.PnDevice("15551110000", 0),
            1700000500,
            retryCount: 2,
            registrationId: bobCreds.RegistrationId,
            RetryReason.NoSession,
            localKeys: new RetryPreKeyUploadBundle(bobCreds.IdentityKey.PublicKey, 2, bobOtpk2, bobCreds.SignedPreKeyId, bobCreds.SignedPreKey.PublicKey, bobCreds.SignedPreKeySignature));

        aliceWire.SendServerNode(new Node("receipt", new Dictionary<string, NodeValue>
        {
            ["id"] = "RETRY-OK-1",
            ["from"] = Jid.PnDevice("15552220000", 0),
            ["type"] = "retry"
        }, bobRetryReceipt.Content));

        Node resendStanza = await aliceWire.ReadClientNodeAsync();
        Node retryAck = await aliceWire.ReadClientNodeAsync();
        Assert.AreEqual("message", resendStanza.Tag);
        Assert.AreEqual("2", resendStanza.GetChild("enc")?.GetAttr("count"));
        Assert.AreEqual("ack", retryAck.Tag);

        // Deliver resent stanza to Bob -> Bob decrypts cleanly!
        bobWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "RETRY-OK-1",
            ["from"] = Jid.PnDevice("15551110000", 0),
            ["t"] = "1700000505",
            ["type"] = "text"
        }, new[] { resendStanza.GetChild("enc")! }));

        _ = await bobWire.ReadClientNodeAsync(); // delivery receipt
        MessageRecord recovered = ((MessageReceivedEvent)await bobEvents.Reader.ReadAsync()).Message;
        Assert.AreEqual("Retry recoverable message", recovered.Text);
    }

    [TestMethod]
    public async Task PlaceholderResend_And_MediaRetry_EndToEnd()
    {
        await using var backend = new SqliteBackend(":memory:");
        DeviceCredentials creds = await SetupNodeCredentialsAsync(backend, "15551110000", "100000000000001", 1);
        var cache = new LidPnCache();
        var events = Channel.CreateUnbounded<WhatsAppEvent>();
        await using var wire = new LocalWirePair();

        var preKeys = new PreKeyFetchClient(wire.Iq, backend, cache, creds);
        var retry = new RetryCoordinator(wire.Socket, backend, preKeys, cache, creds, e => events.Writer.WriteAsync(e));
        var receiver = new MessageReceiver(wire.Socket, backend, cache, creds, retry, e => events.Writer.WriteAsync(e));
        var usync = new UsyncClient(wire.Iq, backend, cache);
        var sender = new MessageSender(wire.Socket, usync, preKeys, backend, cache, creds, retry);
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());

        // Companion peer session already established with primary (0)
        DeviceCredentials primaryCreds = await SetupNodeCredentialsAsync(new SqliteBackend(":memory:"), "15551110000", "100000000000001", 0);
        await backend.Protocol.SaveDeviceListAsync("100000000000001@lid", [0u, 1u]);

        Task<SendMessageResult> pdoTask = sender.RequestPlaceholderResendAsync([new MessageKey { Id = "MISS-1", RemoteJid = "15552220000@s.whatsapp.net", FromMe = false }], "PDO-REQ-1");
        Node pkReq = await wire.ReadClientNodeAsync();
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = pkReq.GetAttr("id")!, ["type"] = "result" }, new Node[]
        {
            new("list", new[] { BuildPreKeyUserNode(Jid.LidDevice("100000000000001", 0), primaryCreds, 1, PreKeyManager.CreateSignedPreKey(9, primaryCreds.IdentityKey).KeyPair.PublicKey) })
        }));
        Node pdoStanza = await wire.ReadClientNodeAsync();
        await pdoTask;
        Assert.AreEqual("peer", pdoStanza.GetAttr("category"));
        Assert.IsNull(pdoStanza.GetChild("reporting")); // peer PDO messages do not attach reporting tokens

        // MediaRetry request + server mediaretry notification decrypt
        byte[] mediaKey = RandomNumberGenerator.GetBytes(32);
        await receiver.RequestMediaRetryAsync("MEDIA-1", Jid.Pn("15552220000"), fromMe: false, mediaKey);
        Node mediaErrReceipt = await wire.ReadClientNodeAsync();
        Assert.AreEqual("server-error", mediaErrReceipt.GetAttr("type"));
        Assert.AreEqual("s.whatsapp.net", mediaErrReceipt.GetAttr("to"));

        var respProto = new MediaRetryNotification
        {
            StanzaId = "MEDIA-1",
            DirectPath = "/v/media-refreshed.enc",
            Result = MediaRetryNotification.Types.ResultType.Success
        };
        byte[] iv = RandomNumberGenerator.GetBytes(12);
        byte[] ct = new byte[respProto.CalculateSize()];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(MediaRetryCrypto.DeriveKey(mediaKey), 16))
        {
            aes.Encrypt(iv, respProto.ToByteArray(), ct, tag, System.Text.Encoding.UTF8.GetBytes("MEDIA-1"));
        }

        Node mediaNotif = new("notification", new Dictionary<string, NodeValue>
        {
            ["id"] = "MEDIA-1",
            ["from"] = Jid.Pn("15552220000"),
            ["type"] = "mediaretry"
        }, new Node[]
        {
            new("encrypt", new Node[] { new("enc_payload", (byte[])[.. ct, .. tag]), new("enc_iv", iv) }),
            new("rmr", new Dictionary<string, NodeValue> { ["jid"] = Jid.Pn("15552220000"), ["from_me"] = "false" })
        });

        MediaRetryNotificationEvent? mEvt = await receiver.HandleMediaRetryNotificationAsync(mediaNotif, mediaKey);
        Assert.IsNotNull(mEvt);
        Assert.AreEqual("ok", mEvt.Status);
        Assert.AreEqual("/v/media-refreshed.enc", mEvt.NewDirectPath);
    }
}
