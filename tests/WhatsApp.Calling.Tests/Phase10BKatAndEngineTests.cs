using System.Buffers.Binary;
using System.Text;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Calling.Tests;

[TestClass]
public sealed class Phase10BKatAndEngineTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [TestMethod]
    public void CallCrypto_And_Ssrc_MatchRustKatVectors()
    {
        byte[] callKey = Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        string peerLid = "222222222222222:0@lid";
        string callId = "00112233445566778899AABBCCDDEEFF";

        Assert.AreEqual("222222222222222:0@lid", CallCrypto.FormatParticipantId("222222222222222@lid"));
        CollectionAssert.AreEqual(
            Hex("3f5423fb6f67e4c588bf5741a3a4e92c749ee99101a47c82bb5dacec40cfc7b95d387fe9ff466f78db6cc4fdaa30"),
            CallCrypto.DeriveWaHkdf46(callKey, peerLid));

        Assert.AreEqual(1805509457u, CallCrypto.DeriveParticipantSsrc(callId, peerLid, 0));
        Assert.AreEqual(2479325408u, CallCrypto.DeriveParticipantSsrc(callId, peerLid, 1));
    }

    [TestMethod]
    public void E2eSrtp_And_WarpMiTag_MatchRustKatVectorsExact()
    {
        byte[] callKey = Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        string selfLid = "111111111111111:0@lid";
        string peerLid = "222222222222222:0@lid";
        uint ssrc = 305419896; // 0x12345678
        ushort seq = 7;
        uint roc = 0;
        byte[] payload = Hex("102132435465768798a9bacb");
        byte[] sampleHeader = Hex("90780007000003c012345678deadbeef");

        SrtpKeys peerKeys = SrtpCipher.DeriveE2eRtpKeys(callKey, peerLid);
        CollectionAssert.AreEqual(Hex("747055a9d18be0fcdc0e29d871309081"), peerKeys.CipherKey);
        CollectionAssert.AreEqual(Hex("10fb8133eff7639cc0d06788f49c"), peerKeys.Salt);
        CollectionAssert.AreEqual(Hex("8eff4bb04971d92512b034ce0ebc466059bfc6ea"), peerKeys.AuthKey);

        SrtpKeys selfKeys = SrtpCipher.DeriveE2eRtpKeys(callKey, selfLid);
        CollectionAssert.AreEqual(Hex("261bde5b556020e3e6da0f5e3132db57"), selfKeys.CipherKey);
        CollectionAssert.AreEqual(Hex("44224a8c019b379c4bbf7dda284a"), selfKeys.Salt);
        CollectionAssert.AreEqual(Hex("56b2655b60a1c17c03dfe94950d5df233481ad41"), selfKeys.AuthKey);

        byte[] iv = SrtpCipher.BuildE2eRtpIv(peerKeys.Salt, ssrc, roc, seq);
        CollectionAssert.AreEqual(Hex("10fb8133fdc335e4c0d06788f49b0000"), iv);

        byte[] cipher = payload.ToArray();
        SrtpCipher.CryptAes128CtrInPlace(peerKeys.CipherKey, iv, cipher);
        CollectionAssert.AreEqual(Hex("17b30591a2650ed7bbae9c5a"), cipher);

        byte[] tag4 = SrtpCipher.ComputeWarpMiTag(peerKeys.AuthKey, sampleHeader, roc, 4);
        CollectionAssert.AreEqual(Hex("53fada83"), tag4);
    }

    [TestMethod]
    public void Sframe_16ByteNonceAes128Gcm_MatchesRustKatVectorsExact()
    {
        byte[] callKey = Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        string selfLid = "111111111111111:0@lid";
        string peerLid = "222222222222222:0@lid";
        byte[] payload = Hex("102132435465768798a9bacb");

        CollectionAssert.AreEqual(Hex("e77cc4666a53d2264ba278b3e2b9ba1cc41ac0e3b587b09db3dd3fa646d51ae9"), SframeSession.DeriveParticipantKey32(callKey, peerLid));
        CollectionAssert.AreEqual(Hex("07e656b474ef306e7e5353f94057a8904821c0f2247b867c102e7ef4cc0dd95c"), SframeSession.DeriveParticipantKey32(callKey, selfLid));
        CollectionAssert.AreEqual(Hex("00000000000000000500000000000000"), SframeSession.CounterToIv16(5));
        CollectionAssert.AreEqual(Hex("050003"), SframeSession.BuildHeader(5, 0));

        // Verify exact KAT byte output at counter=5
        var sender = SframeSession.TryCreate(callKey, selfLid, peerLid)!;
        byte[] sealedAt0To4 = Array.Empty<byte>();
        for (int i = 0; i < 5; i++) _ = sender.Encrypt(payload);
        byte[] sealed5 = sender.Encrypt(payload);
        CollectionAssert.AreEqual(Hex("e5ef789f9fa501842a9ff5f54cc12a630d12008f1bb39060c951724f050003"), sealed5);

        var receiver = SframeSession.TryCreate(callKey, peerLid, selfLid)!;
        SframeDecryptResult dec = receiver.Decrypt(sealed5);
        Assert.AreEqual(SframeDecryptStatus.Decrypted, dec.Status);
        CollectionAssert.AreEqual(payload, dec.Data);

        // Plaintext Opus fallback & tampered tag check
        Assert.AreEqual(SframeDecryptStatus.Plaintext, receiver.Decrypt([1, 2, 3]).Status);
        byte[] tampered = sealed5.ToArray();
        tampered[0] ^= 0xFF;
        Assert.AreEqual(SframeDecryptStatus.AuthFailed, receiver.Decrypt(tampered).Status);
    }

    [TestMethod]
    public void HbhSrtp_And_Srtcp_MatchRustKatVectorsExact()
    {
        byte[] hbhKey = Hex("404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d");
        byte[] uplink30 = SrtpCipher.DeriveHbhSrtcpKeyUplink(hbhKey)!;
        CollectionAssert.AreEqual(Hex("f77854760b4340f0eabbe93930eafe0310c8f699d1ebaf1f609a1781f3fc"), uplink30);
        CollectionAssert.AreEqual(Hex("693a8070145a5d26d39183593d1b648e895a8a3f2354ab38e718cbd93822"), SrtpCipher.DeriveHbhSrtpKeyShared(hbhKey));
        CollectionAssert.AreEqual(Hex("4ad41608b1fe681b56e8230ba5fea1060de1e767fb1275d4dcf85ce3cea9"), SrtpCipher.DeriveHbhSrtcpKeyShared(hbhKey));
        CollectionAssert.AreEqual(Hex("707aad5216a25868eea63a32403b9d0c643644f691145e02b6d7d3776301"), SrtpCipher.DeriveHbhSrtcpKeyDownlink(hbhKey));

        var km = SrtpCipher.KeyingFromHbh(uplink30)!;
        var expandedRtp = SrtpCipher.ExpandLibsrtpSessionKeys(km, rtcpLabels: false);
        CollectionAssert.AreEqual(Hex("cf8c0c31437c9a5ab292cbdaa533c2c0"), expandedRtp.SessionKey16);
        CollectionAssert.AreEqual(Hex("30879d9e6a76f04e7349f40066a7"), expandedRtp.SessionSalt14);
        CollectionAssert.AreEqual(Hex("b012d6003068b460da0121866712a335987d72b8"), expandedRtp.AuthKey20);

        byte[] payload = Hex("102132435465768798a9bacb");
        SrtpCipher.CryptHbhRtpPayloadInPlace(expandedRtp, 305419896, 7, payload);
        CollectionAssert.AreEqual(Hex("363ea223cb020f6d58cea9ee"), payload);

        SrtpKeys srtcpKeys = SrtpCipher.DeriveHbhSrtcpKeysUplink(hbhKey)!;
        CollectionAssert.AreEqual(Hex("f065528cfce4757f444bb8b5892aad7c"), srtcpKeys.CipherKey);
        CollectionAssert.AreEqual(Hex("54bc95ca04c05be16456f0f12dc0"), srtcpKeys.Salt);
        CollectionAssert.AreEqual(Hex("6e6b37a126a522302a578c9713d38e85606048eb"), srtcpKeys.AuthKey);

        byte[] sr = Hex("80c8000612345678ea11180000000000000006400000000500000258");
        byte[] srtcp = SrtpCipher.ProtectSrtcp(srtcpKeys, 305419896, 1, sr);
        CollectionAssert.AreEqual(Hex("80c80006123456787d74643dbb62b23f5005bf9b4296de885c3ff6e280000001cc486281b0510f8b2a83"), srtcp);

        var unwrapped = SrtpCipher.UnprotectSrtcp(srtcpKeys, 305419896, srtcp);
        Assert.IsNotNull(unwrapped);
        CollectionAssert.AreEqual(sr, unwrapped.Value.PlainRtcp);
        Assert.AreEqual(1u, unwrapped.Value.Index);
    }

    [TestMethod]
    public void StunAndRtcp_MatchRustKatVectorsExact()
    {
        byte[] tx = Hex("a0a1a2a3a4a5a6a7a8a9aaab");
        byte[] token = Hex("1020304050607080");
        byte[] miKey = Hex("30313233343536373839616263646566");

        Assert.AreEqual(891568578u, StunRelayCodec.ComputeCrc32("abc"u8));
        CollectionAssert.AreEqual(Hex("2c84bce246c7"), StunRelayCodec.EncodeXorRelayEndpoint("157.240.226.133", 3478)!);
        CollectionAssert.AreEqual(Hex("080100002112a442a0a1a2a3a4a5a6a7a8a9aaab"), StunRelayCodec.BuildWhatsAppPing(tx));

        byte[] tokenAttr = StunRelayCodec.EncodeStunAttr(StunRelayCodec.AttrRelayToken, token);
        byte[] minimalMi = StunRelayCodec.EncodeStunRequest(StunRelayCodec.MsgAllocateRequest, tx, tokenAttr, miKey, includeFingerprint: false);
        CollectionAssert.AreEqual(Hex("000300242112a442a0a1a2a3a4a5a6a7a8a9aaab4000000810203040506070800008001450c96f7efef837bbde7e1f534dae139816d3e015"), minimalMi);

        byte[] withFp = StunRelayCodec.EncodeStunRequest(StunRelayCodec.MsgAllocateRequest, tx, tokenAttr, miKey, includeFingerprint: true);
        CollectionAssert.AreEqual(Hex("0003002c2112a442a0a1a2a3a4a5a6a7a8a9aaab4000000810203040506070800008001450c96f7efef837bbde7e1f534dae139816d3e01580280004ade1f63c"), withFp);

        byte[] wasmKat = Hex("000300942112a442a0a1a2a3a4a5a6a7a8a9aaab400000081020304050607080402400600a0618cabc85ae040a08100118a5acafae0a0a08100218d6a4e6f90f0a08080118f7dd9eb60a0a0a0801100118abccb1f30d0a0a0801100218dadaef8a050a08080218c5e9ec8e0b0a0a0802100118fdc2b1b60f0a0a0802100218b097f7b2090016000800012c84bce246c700080014816c90841f948115ce794ef87726c00ff74220c7");
        IReadOnlyList<StunAttribute> wasmKatAttrs = StunRelayCodec.ParseStunAttributes(wasmKat);
        CollectionAssert.AreEqual(
            new ushort[] { StunRelayCodec.AttrRelayToken, StunRelayCodec.AttrStreamDescriptors, StunRelayCodec.AttrWasmRelayEndpoint, StunRelayCodec.AttrMessageIntegrity },
            wasmKatAttrs.Select(a => a.Type).ToArray());
        CollectionAssert.AreEqual(
            wasmKat,
            StunRelayCodec.EncodeStunRequest(StunRelayCodec.MsgAllocateRequest, tx, wasmKat[20..^24], miKey, includeFingerprint: false));

        byte[] wasmAlloc = StunRelayCodec.BuildWasmAllocateRequest(
            tx, token, Hex("2c84bce246c7"), miKey, "CALL-ID-0001", "12345:0@lid");
        IReadOnlyList<StunAttribute> allocAttrs = StunRelayCodec.ParseStunAttributes(wasmAlloc);
        CollectionAssert.AreEqual(
            new ushort[] { StunRelayCodec.AttrRelayToken, StunRelayCodec.AttrStreamDescriptors, StunRelayCodec.AttrWasmRelayEndpoint, StunRelayCodec.AttrMessageIntegrity },
            allocAttrs.Select(a => a.Type).ToArray());
        CollectionAssert.AreEqual(StunRelayCodec.CreateWasmStreamDescriptors("CALL-ID-0001", "12345:0@lid"), allocAttrs[1].Value);

        CollectionAssert.AreEqual(Hex("81d00002123456789abcdef0"), RtpRtcpCodec.BuildCompact208(305419896, 0x9ABCDEF0));
        CollectionAssert.AreEqual(Hex("81d1000112345678"), RtpRtcpCodec.BuildCompact209(305419896));
        CollectionAssert.AreEqual(
            Hex("80c8000612345678ea11180000000000000006400000000500000258"),
            RtpRtcpCodec.BuildSenderReport(305419896, new RtcpSenderStats(5, 600, 1600), 1718000000000));
    }

    [TestMethod]
    public void CallEngine_SansIoStateMachine_RelayAudioVideoSframeAndTimeout_WorkDeterministically()
    {
        byte[] callKey = Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        var ep = new RelayEndpoint(1, "bom1", 0, 0, true, [127, 0, 0, 1, 0x0D, 0x96], [new RelayAddress(0, "127.0.0.1", null, 3478)], 10);
        var callerCfg = new CallEngineConfig("CID-1", "111@lid", "222@lid", callKey, ep, [1, 2], new byte[16], EnableSframe: true, VideoEnabled: true);
        var calleeCfg = new CallEngineConfig("CID-1", "222@lid", "111@lid", callKey, ep, [1, 2], new byte[16], EnableSframe: true, VideoEnabled: true);

        byte[] tx = Enumerable.Repeat((byte)0x55, 12).ToArray();
        var caller = new CallEngine(callerCfg, () => tx);
        var callee = new CallEngine(calleeCfg, () => tx);

        // Allocate timeout check on isolated engine
        var timeoutEngine = new CallEngine(callerCfg, () => tx);
        timeoutEngine.Start(0);
        _ = timeoutEngine.PollOutputs();
        timeoutEngine.HandleTimer(CallEngine.AllocateTimeoutMs);
        Assert.AreEqual(CallEnginePhase.Ended, timeoutEngine.Phase);

        // Start & STUN Allocate Success on both caller and callee
        caller.Start(100);
        callee.Start(100);
        _ = caller.PollOutputs();
        _ = callee.PollOutputs();

        byte[] allocSuccess = StunRelayCodec.EncodeStunRequest(StunRelayCodec.MsgAllocateSuccess, tx, ReadOnlySpan<byte>.Empty);
        caller.HandleUdpPacket(allocSuccess, 120);
        callee.HandleUdpPacket(allocSuccess, 120);
        Assert.AreEqual(CallEnginePhase.RelayReady, caller.Phase);
        Assert.AreEqual(CallEnginePhase.RelayReady, callee.Phase);

        // Keepalive ping + RTCP SR on timer
        caller.HandleTimer(120 + CallEngine.KeepaliveIntervalMs);
        var timerOutputs = caller.PollOutputs();
        Assert.IsTrue(timerOutputs.OfType<SendUdpOutput>().Any(o => StunRelayCodec.IsStunPacket(o.Datagram)));

        // Caller sends audio + IDR video -> Callee receives decrypted audio & video
        byte[] sampleAudio = "opus-frame-hello"u8.ToArray();
        byte[] sampleIdr = [0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x21];
        caller.SendAudioFrame(new AudioFrame(sampleAudio), 1500);
        caller.SendVideoFrame(new VideoFrame(sampleIdr, 90_000, IsKeyframe: true), 1500);

        foreach (SendUdpOutput pkt in caller.PollOutputs().OfType<SendUdpOutput>())
        {
            callee.HandleUdpPacket(pkt.Datagram, 1510);
        }

        var calleeOutputs = callee.PollOutputs();
        var decAudio = calleeOutputs.OfType<DecodedAudioOutput>().Single();
        CollectionAssert.AreEqual(sampleAudio, decAudio.Frame.EncodedPayload);

        var decVideo = calleeOutputs.OfType<DecodedVideoOutput>().Single();
        Assert.IsTrue(decVideo.Frame.IsKeyframe);
        CollectionAssert.AreEqual(sampleIdr, decVideo.Frame.AnnexBOrAccessUnit);
    }
}
