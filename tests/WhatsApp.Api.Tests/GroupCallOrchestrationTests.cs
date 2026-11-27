using System.Threading.Channels;
using WhatsApp.Calling;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Signal;
using WhatsApp.Storage.Sqlite;
using LocalWirePair = WhatsApp.Api.Tests.UsyncAndNotificationOrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class GroupCallOrchestrationTests
{
    private static IReadOnlyList<Node> Children(Node n) => ((NodeContent.NodesContent)n.Content!).Nodes;

    [TestMethod]
    public async Task CallClient_CallLinksWaitingRoom_EncRekeyAndRetryReceipt_WorkEndToEnd()
    {
        await using var aliceDb = new SqliteBackend(":memory:");
        await using var bobDb = new SqliteBackend(":memory:");

        DeviceCredentials aliceCreds = await aliceDb.Device.LoadOrCreateAsync();
        aliceCreds.Pn = Jid.PnDevice("15550000011", 0);
        aliceCreds.Lid = Jid.LidDevice("111111111111111", 0);
        aliceCreds.AccountIdentityBytes = [0x0A, 0x0B, 0x0C];
        await aliceDb.Device.SaveAsync(aliceCreds);

        DeviceCredentials bobCreds = await bobDb.Device.LoadOrCreateAsync();
        bobCreds.Pn = Jid.PnDevice("15550000012", 0);
        bobCreds.Lid = Jid.LidDevice("222222222222222", 0);
        await bobDb.Device.SaveAsync(bobCreds);

        var bobOtk = PreKeyManager.GeneratePreKeys(1, 1)[0];
        await bobDb.Signal.SavePreKeysAsync([(bobOtk.Id, bobOtk.RecordBytes)]);
        await bobDb.Signal.SaveSignedPreKeyAsync(new SignedPreKeyEntry(
            bobCreds.SignedPreKeyId,
            PreKeyManager.EncodeSignedPreKeyRecord(bobCreds.SignedPreKeyId, bobCreds.SignedPreKey, bobCreds.SignedPreKeySignature, 1710000000),
            bobCreds.SignedPreKeySignature,
            1710000000));

        var aliceCache = new LidPnCache();
        var bobCache = new LidPnCache();
        await using var aliceWire = new LocalWirePair();
        await using var bobWire = new LocalWirePair();

        var aliceEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var bobEvents = Channel.CreateUnbounded<WhatsAppEvent>();

        var aliceSig = new CallSignalingClient(aliceWire.Socket, aliceCreds, e => aliceEvents.Writer.WriteAsync(e));
        var bobSig = new CallSignalingClient(bobWire.Socket, bobCreds, e => bobEvents.Writer.WriteAsync(e));

        await using var aliceCall = new CallClient(
            aliceSig,
            new UsyncClient(aliceWire.Iq, aliceDb, aliceCache),
            new PreKeyFetchClient(aliceWire.Iq, aliceDb, aliceCache, aliceCreds),
            aliceDb,
            aliceCache,
            aliceCreds,
            e => aliceEvents.Writer.WriteAsync(e));

        await using var bobCall = new CallClient(
            bobSig,
            new UsyncClient(bobWire.Iq, bobDb, bobCache),
            new PreKeyFetchClient(bobWire.Iq, bobDb, bobCache, bobCreds),
            bobDb,
            bobCache,
            bobCreds,
            e => bobEvents.Writer.WriteAsync(e));

        await using var aliceRouter = new StanzaRouter(aliceWire.Socket, aliceWire.Iq, new StanzaHandlers
        {
            OnAck = (n, t) => { aliceSig.TryResolveCallAck(n); return ValueTask.CompletedTask; }
        });
        await using var bobRouter = new StanzaRouter(bobWire.Socket, bobWire.Iq, new StanzaHandlers
        {
            OnAck = (n, t) => { bobSig.TryResolveCallAck(n); return ValueTask.CompletedTask; },
            OnCall = async (n, t) =>
            {
                IncomingCallStanza? parsed = await bobSig.HandleCallStanzaAsync(n, t);
                if (parsed is not null)
                {
                    await bobCall.ProcessIncomingCallStanzaAsync(parsed, n, t);
                }
            }
        });

        // 1) Create + Join a Call Link
        Task<CallLink> linkTask = aliceCall.CreateCallLinkAsync(CallLinkMedia.Video);
        Node createReq = await aliceWire.ReadClientNodeAsync();
        Assert.AreEqual("link_create", createReq.GetChild("link_create")!.Tag);
        aliceWire.SendServerNode(new Node("ack", new Dictionary<string, NodeValue>
        {
            ["id"] = createReq.GetAttr("id")!,
            ["class"] = "call",
            ["type"] = "link_create"
        }, new Node[] { new Node("link_create", new Dictionary<string, NodeValue> { ["token"] = "tok-xyz", ["media"] = "video" }) }));
        CallLink link = await linkTask;
        Assert.AreEqual("https://call.whatsapp.com/video/tok-xyz", link.Url);

        Task<CallLinkJoin> joinTask = bobCall.JoinCallLinkAsync(link.Token, CallLinkMedia.Video);
        Node joinReq = await bobWire.ReadClientNodeAsync();
        bobWire.SendServerNode(new Node("ack", new Dictionary<string, NodeValue>
        {
            ["id"] = joinReq.GetAttr("id")!,
            ["class"] = "call",
            ["type"] = "link_join"
        },
        new Node[]
        {
            new Node("group_info", new Dictionary<string, NodeValue>
            {
                ["call-id"] = "GRP-LINK-1",
                ["call-creator"] = aliceCreds.Lid.Value.ToNonAd(),
                ["media"] = "video",
                ["transaction-id"] = "2",
                ["connected-limit"] = "32",
                ["joinable"] = "1",
            },
            new Node[]
            {
                new Node("user", new Dictionary<string, NodeValue> { ["jid"] = aliceCreds.Lid.Value.ToNonAd(), ["state"] = "connected" },
                    new Node[] { new Node("device", new Dictionary<string, NodeValue> { ["jid"] = aliceCreds.Lid.Value, ["pid"] = "1" }) }),
                new Node("user", new Dictionary<string, NodeValue> { ["jid"] = bobCreds.Lid.Value.ToNonAd(), ["state"] = "connected" },
                    new Node[] { new Node("device", new Dictionary<string, NodeValue> { ["jid"] = bobCreds.Lid.Value, ["pid"] = "2" }) }),
            })
        }));
        CallLinkJoin joined = await joinTask;
        Assert.AreEqual("GRP-LINK-1", joined.CallId);

        // 2) Initial Group Offer + ACK registers Alice's group session, then DistributeEpochKeyAsync delivers <enc_rekey>
        Jid carolBare = Jid.Lid("333333333333333");
        Task<ActiveGroupCall> startTask = aliceCall.StartGroupCallAsync([bobCreds.Lid.Value, carolBare], video: true, callId: "GRP-LINK-1");
        Node offerReq = await aliceWire.ReadClientNodeAsync();
        aliceWire.SendServerNode(new Node("ack", new Dictionary<string, NodeValue>
        {
            ["id"] = offerReq.GetAttr("id")!,
            ["class"] = "call",
            ["type"] = "offer"
        },
        new Node[]
        {
            new Node("group_info", new Dictionary<string, NodeValue>
            {
                ["call-id"] = "GRP-LINK-1",
                ["call-creator"] = aliceCreds.Lid.Value.ToNonAd(),
                ["media"] = "video",
                ["transaction-id"] = "2",
                ["connected-limit"] = "32",
            },
            new Node[]
            {
                new Node("user", new Dictionary<string, NodeValue> { ["jid"] = aliceCreds.Lid.Value.ToNonAd(), ["state"] = "connected" },
                    new Node[] { new Node("device", new Dictionary<string, NodeValue> { ["jid"] = aliceCreds.Lid.Value, ["pid"] = "1" }) }),
                new Node("user", new Dictionary<string, NodeValue> { ["jid"] = bobCreds.Lid.Value.ToNonAd(), ["state"] = "connected" },
                    new Node[] { new Node("device", new Dictionary<string, NodeValue> { ["jid"] = bobCreds.Lid.Value, ["pid"] = "2" }) }),
            })
        }));
        ActiveGroupCall aliceGrp = await startTask;
        aliceGrp.Media.ApplyGroupUpdate(joined.Group!);

        Task<byte[]> rekeyTask = aliceCall.DistributeEpochKeyAsync("GRP-LINK-1", 2, [bobCreds.Lid.Value]);
        Node pkReq = await aliceWire.ReadClientNodeAsync();
        byte[] bobOtkPub = PreKeyManager.DecodePreKeyRecord(bobOtk.RecordBytes).KeyPair.PublicKey;
        aliceWire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = pkReq.GetAttr("id")!, ["type"] = "result" }, new Node[]
        {
            new Node("list", new Node[]
            {
                new Node("user", new Dictionary<string, NodeValue> { ["jid"] = bobCreds.Lid.Value }, new Node[]
                {
                    new Node("registration", StanzaBuilder.EncodeUInt32BigEndian(bobCreds.RegistrationId)),
                    new Node("type", new[] { PreKeyIq.DjbKeyType }),
                    new Node("identity", bobCreds.IdentityKey.PublicKey),
                    new Node("key", new Node[] { new("id", StanzaBuilder.EncodeKeyId24(1)), new("value", bobOtkPub) }),
                    new Node("skey", new Node[]
                    {
                        new("id", StanzaBuilder.EncodeKeyId24(bobCreds.SignedPreKeyId)),
                        new("value", bobCreds.SignedPreKey.PublicKey),
                        new("signature", bobCreds.SignedPreKeySignature)
                    })
                })
            })
        }));

        _ = await rekeyTask;
        Node sentRekey = await aliceWire.ReadClientNodeAsync();
        Assert.AreEqual("enc_rekey", sentRekey.GetChild("enc_rekey")!.Tag);

        // Deliver Alice's Signal-encrypted <enc_rekey> to Bob
        bobWire.SendServerNode(new Node("call", new Dictionary<string, NodeValue>
        {
            ["from"] = aliceCreds.Lid.Value,
            ["id"] = sentRekey.GetAttr("id")!,
        }, Children(sentRekey).ToArray()));

        Node bobAck = await bobWire.ReadClientNodeAsync();
        Assert.AreEqual("ack", bobAck.Tag);
        Assert.AreEqual("enc_rekey", bobAck.GetAttr("type"));

        Assert.IsTrue(bobCall.TryGetGroupCall("GRP-LINK-1", out ActiveGroupCall? bobGrp));
        Assert.AreEqual(2u, bobGrp!.Media.InstalledEpochTransaction);

        // Media protected by Alice with epoch 2 decrypts cleanly on Bob!
        byte[] audioPkt = aliceGrp.Media.ProtectAudioPacket(new AudioFrame("group-audio"u8.ToArray()))!;
        CollectionAssert.AreEqual("group-audio"u8.ToArray(), bobGrp.Media.UnprotectAudio(audioPkt)!.Frame.EncodedPayload);

        // 3) Undecryptable <enc_rekey> triggers enc_rekey_retry receipt
        Node badRekey = GroupCallStanzas.BuildGroupEncRekey(
            "GRP-LINK-1", "rk-bad", bobCreds.Lid.Value, aliceCreds.Lid.Value.ToNonAd(), 3,
            new OfferDeviceKey(bobCreds.Lid.Value, [0x00, 0x01, 0x02], "msg"));
        bobWire.SendServerNode(new Node("call", new Dictionary<string, NodeValue>
        {
            ["from"] = aliceCreds.Lid.Value,
            ["id"] = "rk-bad"
        }, Children(badRekey).ToArray()));

        Node retryReceipt = await bobWire.ReadClientNodeAsync();
        Assert.AreEqual("receipt", retryReceipt.Tag);
        Assert.AreEqual("enc_rekey_retry", retryReceipt.GetAttr("type"));
        Assert.AreEqual("rk-bad", retryReceipt.GetAttr("id"));

        // 4) Raise hand & Screen share propagation
        Node rhNode = GroupCallStanzas.BuildRaiseHand("GRP-LINK-1", Jid.Create("GRP-LINK-1", Server.Call), aliceCreds.Lid.Value.ToNonAd(), "rh-1", true);
        bobWire.SendServerNode(new Node("call", new Dictionary<string, NodeValue>
        {
            ["from"] = aliceCreds.Lid.Value,
            ["id"] = "rh-1",
            ["participant"] = aliceCreds.Lid.Value.ToNonAd(),
        }, Children(rhNode).ToArray()));
        Node rhAck = await bobWire.ReadClientNodeAsync();
        Assert.AreEqual("user_action", rhAck.GetAttr("type"));
        Assert.IsTrue(bobGrp.State.RaisedHands.Contains(aliceCreds.Lid.Value.ToNonAd()));
    }
}
