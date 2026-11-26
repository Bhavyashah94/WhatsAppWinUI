using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class Phase10CProtocolTests
{
    private static readonly Jid AliceLid = Jid.LidDevice("111111111111111", 0);
    private static readonly Jid AliceBare = Jid.Lid("111111111111111");
    private static readonly Jid BobBare = Jid.Lid("222222222222222");
    private static readonly Jid CarolBare = Jid.Lid("333333333333333");
    private static readonly Jid GroupJid = Jid.Create("120363000000000123", Server.Group);

    private static IReadOnlyList<Node> Children(Node n) => ((NodeContent.NodesContent)n.Content!).Nodes;

    [TestMethod]
    public void BuildInitialGroupOffer_EnforcesParticipantMinimumsAndChildOrder()
    {
        var twoUsers = new[]
        {
            new GroupCallParticipant(AliceBare, [new GroupCallDevice(AliceLid, CapabilityVersion: 1, Capability: CallStanzas.CapabilityOffer)]),
            new GroupCallParticipant(BobBare, [new GroupCallDevice(BobBare)]),
        };

        // Ad-hoc call with only 2 total participants (<3) must throw; group-bound (@g.us) with 2 succeeds.
        Assert.ThrowsException<ArgumentException>(() =>
            GroupCallStanzas.BuildInitialGroupOffer("GRP-1", "req-1", AliceBare, twoUsers));

        Node groupBound = GroupCallStanzas.BuildInitialGroupOffer(
            "GRP-1", "req-1", AliceBare, twoUsers, groupJid: GroupJid, audioRate: 16000, video: true);
        Assert.AreEqual("call", groupBound.Tag);
        Assert.AreEqual("GRP-1@call", groupBound.GetAttr("to"));

        Node offer = groupBound.GetChild("offer")!;
        Assert.AreEqual(GroupJid.ToString(), offer.GetAttr("group-jid"));
        CollectionAssert.AreEqual(
            new[] { "audio", "video", "net", "group_info" },
            Children(offer).Select(c => c.Tag).ToArray());

        // Upgrades creator's capability to CapabilityVideoOffer when video=true
        Node aliceCap = offer.GetChild("group_info")!.GetChildren("user").First().GetChild("device")!.GetChild("capability")!;
        CollectionAssert.AreEqual(
            CallStanzas.CapabilityVideoOffer,
            ((NodeContent.BytesContent)aliceCap.Content!).Bytes);
    }

    [TestMethod]
    public void GroupUpdate_And_MultiStatus207InitialAck_ParseAccurately()
    {
        byte[] relayKey = Convert.FromHexString("30313233343536373839616263646566");
        byte[] hbhKey = new byte[30];
        var te2Bytes = new byte[] { 157, 240, 226, 133, 0x0D, 0x96 }; // 157.240.226.133:3478

        var relayNode = new Node("relay", new Dictionary<string, NodeValue>
        {
            ["transaction-id"] = "2",
            ["self_pid"] = "1",
            ["uuid"] = "relay-uuid",
            ["participant_uuid"] = "part-uuid",
            ["warp_mi_tag_len"] = "4",
        }, new Node[]
        {
            new Node("key", relayKey),
            new Node("hbh_key", hbhKey),
            new Node("token", new Dictionary<string, NodeValue> { ["id"] = "0" }, new byte[] { 1, 2, 3 }),
            new Node("auth_token", new Dictionary<string, NodeValue> { ["id"] = "0" }, new byte[] { 4, 5, 6 }),
            new Node("te2", new Dictionary<string, NodeValue>
            {
                ["relay_id"] = "1",
                ["token_id"] = "0",
                ["auth_token_id"] = "0",
                ["relay_name"] = "bom1",
                ["is_fna"] = "0",
            }, te2Bytes)
        });

        var groupInfo = new Node("group_info", new Dictionary<string, NodeValue>
        {
            ["media"] = "video",
            ["transaction-id"] = "4",
            ["connected-limit"] = "32",
            ["joinable"] = "1",
            ["rekey"] = "1",
        }, new Node[]
        {
            new Node("user", new Dictionary<string, NodeValue> { ["jid"] = AliceBare, ["state"] = "connected" },
                new Node[] { new Node("device", new Dictionary<string, NodeValue> { ["jid"] = AliceLid, ["pid"] = "1" }) }),
            new Node("user", new Dictionary<string, NodeValue> { ["jid"] = BobBare, ["state"] = "connected" },
                new Node[] { new Node("device", new Dictionary<string, NodeValue> { ["jid"] = BobBare, ["pid"] = "2" }) }),
        });

        var callStanza = new Node("call", new Dictionary<string, NodeValue>
        {
            ["from"] = "GRP-1@call",
            ["id"] = "stz-upd-1",
            ["t"] = "1710010000"
        }, new Node[]
        {
            new Node("group_update", new Dictionary<string, NodeValue>
            {
                ["call-id"] = "GRP-1",
                ["call-creator"] = AliceBare,
            }, new Node[]
            {
                new Node("av_upgrade", new Dictionary<string, NodeValue> { ["av-upgradable"] = "1" }),
                relayNode,
                groupInfo,
            })
        });

        IncomingCallStanza parsed = CallStanzas.ParseIncomingCall(callStanza)!;
        Assert.AreEqual(CallActionKind.GroupUpdate, parsed.ActionKind);
        Assert.IsNotNull(parsed.GroupUpdate);
        Assert.AreEqual(4u, parsed.GroupUpdate.TransactionId);
        Assert.IsTrue(parsed.GroupUpdate.AvUpgradable);
        Assert.IsTrue(parsed.GroupUpdate.RekeyRequested);
        Assert.AreEqual("157.240.226.133", parsed.GroupUpdate.Relay!.Endpoints[0].Ipv4);
        Assert.AreEqual((ushort)3478, parsed.GroupUpdate.Relay.Endpoints[0].Port);

        // Multi-status 207 ack omitting tx/connected-limit succeeds cleanly
        var ack207 = new Node("ack", new Dictionary<string, NodeValue>
        {
            ["class"] = "call",
            ["type"] = "offer",
            ["error"] = "207",
        }, new Node[]
        {
            new Node("group_info", new Dictionary<string, NodeValue>
            {
                ["call-id"] = "GRP-1",
                ["call-creator"] = AliceBare,
                ["media"] = "audio",
            }, new Node[]
            {
                new Node("user", new Dictionary<string, NodeValue> { ["jid"] = AliceBare, ["state"] = "connected" }, new Node[] { new Node("device", new Dictionary<string, NodeValue> { ["jid"] = AliceLid }) }),
                new Node("user", new Dictionary<string, NodeValue> { ["jid"] = CarolBare, ["error"] = "404" }, new Node[] { new Node("device", new Dictionary<string, NodeValue> { ["jid"] = CarolBare }) }),
            })
        });

        GroupCallUpdate ackParsed = GroupCallStanzas.ParseInitialGroupCallAck(ack207)!;
        Assert.AreEqual(0u, ackParsed.TransactionId);
        Assert.AreEqual(404u, ackParsed.Participants[1].Error);
    }

    [TestMethod]
    public void EncRekey_And_RetryReceipt_RoundTripWithKeygen2()
    {
        var key = new OfferDeviceKey(BobBare, [0x11, 0x22, 0x33], "pkmsg");
        Node wrapped = GroupCallStanzas.BuildGroupEncRekey("GRP-1", "rk-1", BobBare, AliceBare, 5, key, [0xAA, 0xBB]);
        IncomingCallStanza parsed = CallStanzas.ParseIncomingCall(
            new Node("call", new Dictionary<string, NodeValue> { ["from"] = AliceLid, ["id"] = "rk-1" },
                Children(wrapped).ToArray()))!;

        Assert.AreEqual(CallActionKind.EncRekey, parsed.ActionKind);
        Assert.IsNotNull(parsed.EncRekey);
        Assert.AreEqual(5u, parsed.EncRekey.TransactionId);
        Assert.AreEqual(2u, parsed.EncRekey.KeyGeneration);
        Assert.AreEqual("pkmsg", parsed.EncRekey.EncryptionType);
        CollectionAssert.AreEqual(new byte[] { 0x11, 0x22, 0x33 }, parsed.EncRekey.Ciphertext);

        Node retry = GroupCallStanzas.BuildGroupEncRekeyRetryReceipt("rk-1", AliceLid, "GRP-1", AliceBare, 123456, 1);
        Assert.AreEqual("receipt", retry.Tag);
        Assert.AreEqual("enc_rekey_retry", retry.GetAttr("type"));
        Assert.AreEqual("2", retry.GetChild("enc_rekey")!.GetAttr("count"));
        Assert.AreEqual("GRP-1", retry.GetChild("enc_rekey")!.GetAttr("call-id"));
        Assert.AreEqual(4, ((NodeContent.BytesContent)retry.GetChild("registration")!.Content!).Bytes.Length);
    }

    [TestMethod]
    public void CallLinks_WaitingRoom_RaiseHand_And_ScreenShare_ParseCorrectly()
    {
        Node linkCreate = GroupCallStanzas.BuildCallLinkCreate(CallLinkMedia.Video, "lc-1");
        Assert.AreEqual("call", linkCreate.GetAttr("to"));
        Assert.AreEqual("video", linkCreate.GetChild("link_create")!.GetAttr("media"));

        var createAck = new Node("ack", new Dictionary<string, NodeValue> { ["class"] = "call", ["type"] = "link_create" },
            new Node[] { new Node("link_create", new Dictionary<string, NodeValue> { ["token"] = "tok123", ["media"] = "video" }) });
        CallLink created = GroupCallStanzas.ParseCallLinkCreateAck(createAck);
        Assert.AreEqual("https://call.whatsapp.com/video/tok123", created.Url);

        var queryAck = new Node("ack", new Dictionary<string, NodeValue> { ["class"] = "call", ["type"] = "link_query" },
            new Node[]
            {
                new Node("link_query", new Dictionary<string, NodeValue>
                {
                    ["token"] = "tok123",
                    ["media"] = "audio",
                    ["link_creator"] = AliceBare,
                }, new Node[] { new Node("waiting_room", new Dictionary<string, NodeValue> { ["enabled"] = "1", ["is_admin"] = "1" }) })
            });
        CallLinkPreview preview = GroupCallStanzas.ParseCallLinkQueryAck(queryAck);
        Assert.IsTrue(preview.WaitingRoomEnabled);
        Assert.IsTrue(preview.IsAdmin);

        var wrJoinAck = new Node("ack", new Dictionary<string, NodeValue> { ["class"] = "call", ["type"] = "link_join" },
            new Node[]
            {
                new Node("waiting_room", new Dictionary<string, NodeValue>
                {
                    ["call-id"] = "LINK-1",
                    ["call-creator"] = AliceBare,
                    ["link-token"] = "tok123",
                    ["media"] = "video",
                    ["enabled"] = "1",
                    ["is_admin"] = "0",
                    ["transaction-id"] = "3",
                }, new Node[] { new Node("user", new Dictionary<string, NodeValue> { ["jid"] = BobBare, ["state"] = "pending" }) })
            });
        CallLinkJoin joined = GroupCallStanzas.ParseCallLinkJoinAck(wrJoinAck, "tok123");
        Assert.IsTrue(joined.InWaitingRoom);
        Assert.AreEqual(1, joined.WaitingRoom!.Users.Count);

        Node rhWrapped = GroupCallStanzas.BuildRaiseHand("LINK-1", Jid.Create("LINK-1", Server.Call), AliceBare, "rh-1", raised: true);
        IncomingCallStanza rhParsed = CallStanzas.ParseIncomingCall(
            new Node("call", new Dictionary<string, NodeValue> { ["from"] = BobBare, ["id"] = "rh-1" }, Children(rhWrapped).ToArray()))!;
        Assert.AreEqual(CallActionKind.UserAction, rhParsed.ActionKind);
        Assert.IsTrue(rhParsed.RaisedHand);

        Node ssWrapped = GroupCallStanzas.BuildScreenShare("LINK-1", Jid.Create("LINK-1", Server.Call), AliceBare, "ss-1", ScreenShareState.Started, 77);
        IncomingCallStanza ssParsed = CallStanzas.ParseIncomingCall(
            new Node("call", new Dictionary<string, NodeValue> { ["from"] = BobBare, ["id"] = "ss-1" }, Children(ssWrapped).ToArray()))!;
        Assert.AreEqual(CallActionKind.ScreenShare, ssParsed.ActionKind);
        Assert.AreEqual(ScreenShareState.Started, ssParsed.ScreenShare!.State);
        Assert.AreEqual(77u, ssParsed.ScreenShare.ScreenShareId);
    }
}
