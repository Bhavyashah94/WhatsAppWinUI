using WhatsApp.Calling;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Calling.Tests;

[TestClass]
public sealed class GroupCallMediaEngineTests
{
    private static readonly Jid Creator = Jid.Lid("111111111111111");
    private static readonly Jid AliceDev = Jid.LidDevice("111111111111111", 0);
    private static readonly Jid BobBare = Jid.Lid("222222222222222");
    private static readonly Jid BobDev = Jid.LidDevice("222222222222222", 0);

    private static byte[] Hex(string h) => Convert.FromHexString(h);

    [TestMethod]
    public void WasmGroupAllocate_MatchesRustCapturedSubscriptionAndHbhFecKatBytes()
    {
        // Exact inputs & expected bytes captured from whatsapp-rust wacore/src/voip/stun.rs:786-831
        uint[] streamSsrcs =
        [
            0x3ea2_6c0cu,
            0x0bf9_9b28u,
            0xf42e_4556u,
            0x14e8_f126u,
            0xbb16_134fu,
            0x98b1_4f00u,
            0xe0e0_4163u,
            0x74ed_8516u,
            0xdea8_a613u,
        ];
        byte[] tx = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        byte[] endpoint = [0x2c, 0x84, 0xbc, 0xe2, 0xb5, 0xc7];
        byte[] miKey = "0123456789abcdef"u8.ToArray();

        byte[] pkt = StunRelayCodec.BuildWasmGroupAllocateRequest(
            tx,
            [1, 2, 3],
            endpoint,
            miKey,
            streamSsrcs,
            0xb31d_ed3eu,
            [0xc1a1_7938u, 0x1bb2_0c84u],
            [2u, 1u, 2u]);

        IReadOnlyList<StunAttribute> attrs = StunRelayCodec.ParseStunAttributes(pkt);
        CollectionAssert.AreEqual(
            new ushort[]
            {
                StunRelayCodec.AttrRelayToken,
                StunRelayCodec.AttrSenderSubscriptionsV2,
                StunRelayCodec.AttrReceiverSubscriptions,
                StunRelayCodec.AttrStreamDescriptors,
                StunRelayCodec.AttrParticipantCount,
                StunRelayCodec.AttrWasmRelayEndpoint,
                StunRelayCodec.AttrMessageIntegrity,
            },
            attrs.Select(a => a.Type).ToArray());

        CollectionAssert.AreEqual(
            Hex("0a1f0a1d0a0fa6e2a3a701cfa6d8d80b809ec5c5091204080110011204080210010a130a110a0fe38281870e968ab6a70793cca2f50d0a1a0a180a0e8cd889f503a8b6e65fd68ab9a10f12020801120208020a110a0f0a05bedaf7980b1202080112020802"),
            attrs[1].Value);
        CollectionAssert.AreEqual(Hex("1202080112020802"), attrs[2].Value);
        CollectionAssert.AreEqual(
            Hex("0a06188cd889f5030a07100118a8b6e65f0a08100218d68ab9a10f0a08080118a6e2a3a7010a0a0801100118cfa6d8d80b0a0a0801100218809ec5c5090a08080218e38281870e0a0a0802100118968ab6a7070a0a080210021893cca2f50d0a0a0803100318b8f2858d0c0a0a08041003188499c8dd01"),
            attrs[3].Value);
        CollectionAssert.AreEqual(new byte[] { 2 }, attrs[4].Value);
    }

    [TestMethod]
    public void GroupCallState_EnforcesTransactionOrderPidInheritanceAndControls()
    {
        var state = new GroupCallState("GRP-1", Creator);
        var ackSnap = new GroupCallUpdate("GRP-1", Creator, null, 0, "audio", 0, true, false, true,
        [
            new GroupCallParticipant(Creator, [new GroupCallDevice(AliceDev)], State: "connected"),
            new GroupCallParticipant(BobBare, [new GroupCallDevice(BobDev)], State: "connected"),
        ]);
        Assert.AreEqual(GroupStateApply.Applied, state.ApplyInitialOfferAck(ackSnap));
        Assert.IsTrue(state.ConsumeOwedRekey());
        Assert.IsFalse(state.ConsumeOwedRekey());

        var relay1 = new GroupCallRelay(1, 1, "u", "p", false, 4, new byte[16], new byte[30], [], [], []);
        var tx2 = new GroupCallUpdate("GRP-1", Creator, null, 2, "video", 32, true, true, false,
        [
            new GroupCallParticipant(Creator, [new GroupCallDevice(AliceDev, Pid: 1)], State: "connected"),
            new GroupCallParticipant(BobBare, [new GroupCallDevice(BobDev, Pid: 2)], State: "connected"),
        ], relay1);
        Assert.AreEqual(GroupStateApply.Applied, state.ApplyUpdate(tx2));

        // Stale roster transaction without newer relay must be rejected
        var stale = tx2 with { TransactionId = 1, Relay = null };
        Assert.AreEqual(GroupStateApply.Stale, state.ApplyUpdate(stale));

        // Roster-only tx=3 inheriting missing PID=2 and preserving relay1
        var tx3 = new GroupCallUpdate("GRP-1", Creator, null, 3, "video", 32, true, true, false,
        [
            new GroupCallParticipant(Creator, [new GroupCallDevice(AliceDev)], State: "connected"),
            new GroupCallParticipant(BobBare, [new GroupCallDevice(BobDev)], State: "connected"),
        ]);
        Assert.AreEqual(GroupStateApply.Applied, state.ApplyUpdate(tx3));
        Assert.AreEqual(2u, state.Snapshot!.Participants[1].Devices[0].Pid);
        Assert.AreEqual(relay1, state.Snapshot.Relay);

        state.ApplyRaiseHand(BobDev, true);
        state.ApplyScreenShare(BobDev, new ScreenShare(ScreenShareState.Started, 2, 9));
        Assert.IsTrue(state.RaisedHands.Contains(BobBare));
        Assert.IsTrue(state.ScreenShares.ContainsKey(BobBare));
    }

    [TestMethod]
    public void GroupMediaSession_BuffersEpochsDetectsConflictsAndExchangesAudioAndScreenShareVideo()
    {
        var sender = new GroupMediaSession("GRP-1", Creator, AliceDev, enableSframe: true);
        var receiver = new GroupMediaSession("GRP-1", Creator, BobDev, enableSframe: true);

        byte[] epoch2 = CallCrypto.GenerateCallKey();
        byte[] conflictingEpoch2 = CallCrypto.GenerateCallKey();

        // Future epoch tx=2 arrives before roster tx=2 -> Buffered
        Assert.AreEqual(GroupEpochApply.Buffered, sender.ApplyRawEpoch(2, epoch2));
        Assert.AreEqual(GroupEpochApply.Buffered, receiver.ApplyRawEpoch(2, epoch2));
        Assert.AreEqual(GroupEpochApply.Conflicting, receiver.ApplyRawEpoch(2, conflictingEpoch2));
        Assert.AreEqual(GroupEpochApply.Duplicate, receiver.ApplyRawEpoch(2, epoch2));

        var rosterTx2 = new GroupCallUpdate("GRP-1", Creator, null, 2, "video", 32, true, true, false,
        [
            new GroupCallParticipant(Creator, [new GroupCallDevice(AliceDev, Pid: 1)], State: "connected"),
            new GroupCallParticipant(BobBare, [new GroupCallDevice(BobDev, Pid: 2)], State: "connected"),
        ]);

        Assert.AreEqual(GroupStateApply.Applied, sender.ApplyGroupUpdate(rosterTx2));
        Assert.AreEqual(GroupStateApply.Applied, receiver.ApplyGroupUpdate(rosterTx2));
        Assert.AreEqual(2u, sender.InstalledEpochTransaction);
        Assert.AreEqual(2u, receiver.InstalledEpochTransaction);

        byte[] opus = "group-opus-hello"u8.ToArray();
        byte[] rtpAudio = sender.ProtectAudioPacket(new AudioFrame(opus))!;
        ParticipantAudioOutput decAud = receiver.UnprotectAudio(rtpAudio)!;
        CollectionAssert.AreEqual(opus, decAud.Frame.EncodedPayload);
        Assert.AreEqual(1u, decAud.Pid);

        byte[] screenIdr = [0x00, 0x00, 0x00, 0x01, 0x65, 0xAA, 0xBB];
        byte[] rtpScreen = sender.ProtectVideoPacket(new VideoFrame(screenIdr, 90_000, IsKeyframe: true), screenShare: true)!;
        ParticipantVideoOutput decScreen = receiver.UnprotectVideo(rtpScreen)!;
        CollectionAssert.AreEqual(screenIdr, decScreen.Frame.AnnexBOrAccessUnit);
        Assert.AreEqual(sender.LocalScreenShareSsrc, decScreen.Header.Ssrc);
    }
}
