using System.Text;
using Whatsapp;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class Phase10AProtocolTests
{
    [TestMethod]
    public void CallStanzas_OfferPreAcceptAcceptRejectTerminate_PreserveMandatoryWireShape()
    {
        Jid creator = Jid.Lid("100000000000001");
        Jid callee = Jid.Pn("15552220000");
        Jid dev0 = Jid.PnDevice("15552220000", 0);
        Jid dev1 = Jid.PnDevice("15552220000", 1);

        Node offerStanza = CallStanzas.BuildOffer(
            "CALL-1",
            callee,
            creator,
            [new OfferDeviceKey(dev0, [1, 2, 3], "pkmsg"), new OfferDeviceKey(dev1, [4, 5, 6], "msg")],
            wrapperId: "w-1",
            privacyToken: [9, 8],
            capability: CallStanzas.CapabilityVideoOffer,
            deviceIdentity: [7, 7],
            multiDevice: true,
            video: true);

        Assert.AreEqual("call", offerStanza.Tag);
        Assert.AreEqual("w-1", offerStanza.GetAttr("id"));
        Node offer = offerStanza.GetChild("offer")!;
        Assert.AreEqual("CALL-1", offer.GetAttr("call-id"));

        // Server-enforced child order: privacy -> audio* -> video -> net -> capability -> destination -> encopt -> device-identity
        var offerTags = ((NodeContent.NodesContent)offer.Content!).Nodes.Select(n => n.Tag).ToArray();
        CollectionAssert.AreEqual(
            new[] { "privacy", "audio", "audio", "video", "net", "capability", "destination", "encopt", "device-identity" },
            offerTags);
        Assert.AreEqual("h.264", offer.GetChild("video")!.GetAttr("enc"));
        Assert.AreEqual("H264", offer.GetChild("video")!.GetAttr("dec"));

        Node preAccept = CallStanzas.BuildPreAccept("CALL-1", callee, creator, "w-2", video: true);
        Node paNode = preAccept.GetChild("preaccept")!;
        Assert.AreEqual("2", paNode.GetChild("encopt")!.GetAttr("keygen"));
        Assert.IsNotNull(paNode.GetChild("video"));

        Node accept = CallStanzas.BuildAccept(
            "CALL-1",
            callee,
            creator,
            "w-3",
            relayTe: [10, 0, 0, 1, 0x0D, 0x96],
            voipSettings: "{}"u8.ToArray(),
            capability: CallStanzas.CapabilityOffer,
            video: true,
            peerAbtestBucket: "bkt1");
        Node accNode = accept.GetChild("accept")!;
        Assert.AreEqual("2", accNode.GetChild("te")!.GetAttr("priority"));
        Assert.AreEqual("1", accNode.GetChild("voip_settings")!.GetAttr("uncompressed"));
        Assert.AreEqual("bkt1", accNode.GetChild("metadata")!.GetAttr("peer_abtest_bucket"));

        Node reject = CallStanzas.BuildReject("CALL-1", callee, creator, "w-4", CallStanzas.RejectReasonBusy);
        Assert.AreEqual("0", reject.GetChild("reject")!.GetAttr("count"));
        Assert.AreEqual(CallStanzas.RejectReasonBusy, reject.GetChild("reject")!.GetAttr("reason"));

        Node term = CallStanzas.BuildTerminate("CALL-1", callee, creator, "w-5", CallStanzas.TerminateReasonAcceptedElsewhere);
        Assert.AreEqual(CallStanzas.TerminateReasonAcceptedElsewhere, term.GetChild("terminate")!.GetAttr("reason"));

        Node videoUpgrade = CallStanzas.BuildVideoState("CALL-1", callee, "w-6", creator, CallVideoState.UpgradeRequestV2, CallStanzas.VideoDecH264, 1);
        Node vidStateNode = videoUpgrade.GetChild("video")!;
        Assert.AreEqual("11", vidStateNode.GetAttr("state"));
        Assert.AreEqual("video", vidStateNode.GetAttr("voip_settings"));
        Assert.AreEqual("1", vidStateNode.GetAttr("device_orientation"));

        Node muteNode = CallStanzas.BuildMuteV2("CALL-1", callee, creator, "w-7", muted: true);
        Assert.AreEqual("1", muteNode.GetChild("mute_v2")!.GetAttr("mute-state"));
    }

    [TestMethod]
    public void CallStanzas_CapabilityBitAndMlowDowngrade_MatchWireContract()
    {
        Assert.AreEqual(CapabilityBit.Set, CallStanzas.QueryCapabilityBit(1, CallStanzas.CapabilityOffer, CallStanzas.MlowCapabilityIndex));
        Assert.AreEqual(CapabilityBit.Clear, CallStanzas.QueryCapabilityBit(1, CallStanzas.CapabilityStandardOpusOffer, CallStanzas.MlowCapabilityIndex));
        Assert.AreEqual(CapabilityBit.Clear, CallStanzas.QueryCapabilityBit(null, CallStanzas.CapabilityOffer, CallStanzas.MlowCapabilityIndex));
        Assert.AreEqual(CapabilityBit.Clear, CallStanzas.QueryCapabilityBit(1, [0x01, 0x05, 0xFF], CallStanzas.MlowCapabilityIndex)); // truncated length
        Assert.AreEqual(CapabilityBit.Clear, CallStanzas.QueryCapabilityBit(1, [0x00, 0x05, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], CallStanzas.MlowCapabilityIndex)); // embedded version 0

        Assert.IsTrue(CallStanzas.MlowAfterPeerCapability(true, CapabilityBit.Unknown));
        Assert.IsTrue(CallStanzas.MlowAfterPeerCapability(true, CapabilityBit.Set));
        Assert.IsFalse(CallStanzas.MlowAfterPeerCapability(true, CapabilityBit.Clear));
    }

    [TestMethod]
    public void RelayParser_ParsesHbhKeyDoubleBase64_Te2Ipv4Ipv6AndRelayLatency()
    {
        byte[] rawHbh30 = Enumerable.Range(1, 30).Select(i => (byte)i).ToArray();
        string singleB64 = Convert.ToBase64String(rawHbh30);
        string doubleB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(singleB64));
        CollectionAssert.AreEqual(rawHbh30, RelayParser.DecodeHbhKey(Encoding.UTF8.GetBytes(doubleB64)));

        byte[] ipv4Te2 = [192, 0, 2, 10, 0x0D, 0x96]; // 192.0.2.10:3478
        byte[] ipv6Te2 = [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0x0D, 0x96];

        Node relayNode = new("relay", new Dictionary<string, NodeValue>
        {
            ["uuid"] = "relay-uuid-1",
            ["self_pid"] = "11",
            ["peer_pid"] = "22"
        }, new[]
        {
            new Node("key", Encoding.UTF8.GetBytes(Convert.ToBase64String(new byte[16]))),
            new Node("hbh_key", Encoding.UTF8.GetBytes(doubleB64)),
            new Node("warp_mi_tag_len", Encoding.UTF8.GetBytes("4")),
            new Node("token", new Dictionary<string, NodeValue> { ["id"] = "0" }, new byte[] { 0xAA, 0xBB }),
            new Node("token", new Dictionary<string, NodeValue> { ["id"] = "1" }, new byte[] { 0xCC, 0xDD }),
            new Node("auth_token", new Dictionary<string, NodeValue> { ["id"] = "0" }, new byte[] { 0x11, 0x22 }),
            new Node("te2", new Dictionary<string, NodeValue>
            {
                ["relay_id"] = "7",
                ["relay_name"] = "bom1",
                ["token_id"] = "0",
                ["xtoken_id"] = "1",
                ["auth_token_id"] = "0",
                ["fna"] = "1",
                ["c2r_rtt"] = "24",
                ["xrtt"] = "65"
            }, ipv4Te2),
            new Node("te2", new Dictionary<string, NodeValue>
            {
                ["relay_id"] = "7",
                ["relay_name"] = "bom1",
                ["token_id"] = "0",
                ["auth_token_id"] = "0"
            }, ipv6Te2)
        });

        RelayData? parsed = RelayParser.ParseRelayData(relayNode);
        Assert.IsNotNull(parsed);
        CollectionAssert.AreEqual(rawHbh30, parsed.HbhKey);
        Assert.AreEqual(4u, parsed.WarpMiTagLen);
        Assert.AreEqual(2, parsed.Candidates.Count);
        Assert.AreEqual(2, parsed.Candidates[0].Endpoint.Addresses.Count);
        Assert.AreEqual("192.0.2.10", parsed.Candidates[0].Endpoint.Addresses[0].Ipv4);
        Assert.AreEqual((ushort)3478, parsed.Candidates[0].Endpoint.Addresses[0].Port);
        Assert.IsNotNull(parsed.Candidates[0].Endpoint.Addresses[1].Ipv6);
        Assert.AreEqual(24u, parsed.Candidates[0].Endpoint.C2rRttMs);

        // Conflicting token_id for same (relay_id, kind) is rejected
        Node badRelay = new("relay", new[]
        {
            new Node("te2", new Dictionary<string, NodeValue> { ["relay_id"] = "1", ["token_id"] = "0", ["auth_token_id"] = "0" }, ipv4Te2),
            new Node("te2", new Dictionary<string, NodeValue> { ["relay_id"] = "1", ["token_id"] = "1", ["auth_token_id"] = "0" }, ipv4Te2)
        });
        Assert.IsNull(RelayParser.ParseRelayData(badRelay));

        // RelayLatency packed value
        var lat = PackedRelayLatency.FromRttMs(42);
        Assert.AreEqual(42, lat.RttMs);
        Assert.AreEqual((byte)1, lat.Round);
        Assert.IsNull(new PackedRelayLatency(0x027F_FFFF).RttMs);

        Node latStanza = CallStanzas.BuildRelayLatency(
            "CALL-9",
            Jid.Pn("1555"),
            Jid.Lid("1000"),
            [new RelayLatencyEntry(RelayLatencyKind.Normal, lat, ipv4Te2, "bom1")],
            [Jid.PnDevice("1555", 0)]);
        var entries = RelayParser.ParseRelayLatencyEntries(latStanza.GetChild("relaylatency")!);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(42, entries[0].Latency.RttMs);
        Assert.AreEqual("bom1", entries[0].RelayName);
    }

    [TestMethod]
    public void AppStateAndMessages_CallLogRelayPrivacyAndScheduledCall_MatchWireContract()
    {
        Assert.AreEqual("regular", AppStateSchemas.CallLog.Collection);
        Assert.AreEqual(1, AppStateSchemas.CallLog.Version);
        Assert.AreEqual(
            """["call_log","100000000000001@lid","CALL-42","1"]""",
            Encoding.UTF8.GetString(AppStateSchemas.CallLog.BuildIndex("100000000000001@lid", "CALL-42", "1")));

        Assert.AreEqual("regular", AppStateSchemas.RelayAllCalls.Collection);
        Assert.AreEqual(1, AppStateSchemas.RelayAllCalls.Version);

        Message sched = RichMessageBuilders.BuildScheduledCallCreation(
            1710050000000L,
            Message.Types.ScheduledCallCreationMessage.Types.CallType.Video,
            "Weekly Sync");
        Assert.AreEqual("text", MessageStanzas.ClassifyStanzaType(sched));
        Assert.AreEqual("Weekly Sync", sched.ScheduledCallCreationMessage.Title);

        Message cancel = RichMessageBuilders.BuildScheduledCallEdit(
            new MessageKey { Id = "SCHED-1", RemoteJid = "123@g.us", FromMe = true },
            Message.Types.ScheduledCallEditMessage.Types.EditType.Cancel);
        Assert.AreEqual("text", MessageStanzas.ClassifyStanzaType(cancel));
        Assert.AreEqual(Message.Types.ScheduledCallEditMessage.Types.EditType.Cancel, cancel.ScheduledCallEditMessage.EditType);
    }
}
