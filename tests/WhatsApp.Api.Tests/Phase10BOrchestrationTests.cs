using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using WhatsApp.Calling;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Signal;
using WhatsApp.Storage.Sqlite;
using LocalWirePair = WhatsApp.Api.Tests.Phase6COrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class Phase10BOrchestrationTests
{
    private sealed class LoopbackUdpSocket : ICallUdpSocket
    {
        public Channel<byte[]> Outbound { get; } = Channel.CreateUnbounded<byte[]>();
        public Channel<byte[]> Inbound { get; } = Channel.CreateUnbounded<byte[]>();

        public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, string host, ushort port, CancellationToken ct = default) =>
            Outbound.Writer.WriteAsync(datagram.ToArray(), ct);

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken ct = default) =>
            await Inbound.Reader.ReadAsync(ct).ConfigureAwait(false);

        public ValueTask DisposeAsync()
        {
            Outbound.Writer.TryComplete();
            Inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1710010000);
        public override DateTimeOffset GetUtcNow() => _now;
        public void AdvanceMs(long ms) => _now = _now.AddMilliseconds(ms);
    }

    [TestMethod]
    public async Task CallClient_PlaceOfferAcceptAndExchangeMediaOverRelayLoop_WorksEndToEnd()
    {
        var time = new FakeTimeProvider();

        await using var aliceDb = new SqliteBackend(":memory:");
        await using var bobDb = new SqliteBackend(":memory:");

        DeviceCredentials aliceCreds = await aliceDb.Device.LoadOrCreateAsync();
        aliceCreds.Pn = Jid.PnDevice("15550000001", 0);
        aliceCreds.Lid = Jid.LidDevice("100000000000001", 0);
        await aliceDb.Device.SaveAsync(aliceCreds);

        DeviceCredentials bobCreds = await bobDb.Device.LoadOrCreateAsync();
        bobCreds.Pn = Jid.PnDevice("15550000002", 0);
        bobCreds.Lid = Jid.LidDevice("100000000000002", 0);
        await bobDb.Device.SaveAsync(bobCreds);

        var bobOtk = PreKeyManager.GeneratePreKeys(1, 1)[0];
        await bobDb.Signal.SavePreKeysAsync([(bobOtk.Id, bobOtk.RecordBytes)]);
        await bobDb.Signal.SaveSignedPreKeyAsync(new SignedPreKeyEntry(
            bobCreds.SignedPreKeyId,
            PreKeyManager.EncodeSignedPreKeyRecord(bobCreds.SignedPreKeyId, bobCreds.SignedPreKey, bobCreds.SignedPreKeySignature, 1710000000),
            bobCreds.SignedPreKeySignature,
            1710000000));

        Jid bobJid = Jid.Pn("15550000002");
        await aliceDb.Protocol.SaveDeviceListAsync(bobJid.ToString(), [0u]);

        var aliceCache = new LidPnCache();
        var bobCache = new LidPnCache();
        aliceCache.Learn("15550000002@s.whatsapp.net", "100000000000002@lid");
        bobCache.Learn("15550000001@s.whatsapp.net", "100000000000001@lid");

        await using var aliceWire = new LocalWirePair();
        await using var bobWire = new LocalWirePair();

        var aliceEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var bobEvents = Channel.CreateUnbounded<WhatsAppEvent>();

        await using var aliceRouter = new StanzaRouter(aliceWire.Socket, aliceWire.Iq, new StanzaHandlers());
        var aliceSignaling = new CallSignalingClient(aliceWire.Socket, aliceCreds, e => aliceEvents.Writer.WriteAsync(e), timeProvider: time);
        var bobSignaling = new CallSignalingClient(bobWire.Socket, bobCreds, e => bobEvents.Writer.WriteAsync(e), timeProvider: time);
        await using var bobRouter = new StanzaRouter(bobWire.Socket, bobWire.Iq, new StanzaHandlers
        {
            OnCall = (n, t) => new ValueTask(bobSignaling.HandleCallStanzaAsync(n, t))
        });

        var aliceUdp = new LoopbackUdpSocket();
        var bobUdp = new LoopbackUdpSocket();

        await using var aliceCall = new CallClient(
            aliceSignaling,
            new UsyncClient(aliceWire.Iq, aliceDb, aliceCache),
            new PreKeyFetchClient(aliceWire.Iq, aliceDb, aliceCache, aliceCreds),
            aliceDb,
            aliceCache,
            aliceCreds,
            e => aliceEvents.Writer.WriteAsync(e),
            () => aliceUdp,
            time);

        await using var bobCall = new CallClient(
            bobSignaling,
            new UsyncClient(bobWire.Iq, bobDb, bobCache),
            new PreKeyFetchClient(bobWire.Iq, bobDb, bobCache, bobCreds),
            bobDb,
            bobCache,
            bobCreds,
            e => bobEvents.Writer.WriteAsync(e),
            () => bobUdp,
            time);

        // Enforce PrivacySettingRelayAllCalls policy
        aliceSignaling.ApplyRelayAllCallsSetting(true);

        byte[] hbh = Enumerable.Range(1, 30).Select(i => (byte)i).ToArray();
        Node serverRelayNode = new("relay", new Dictionary<string, NodeValue> { ["uuid"] = "rel-1" }, new[]
        {
            new Node("key", Encoding.UTF8.GetBytes(Convert.ToBase64String(new byte[16]))),
            new Node("hbh_key", Encoding.UTF8.GetBytes(Convert.ToBase64String(hbh))),
            new Node("token", new Dictionary<string, NodeValue> { ["id"] = "0" }, new byte[] { 1, 2, 3 }),
            new Node("te2", new Dictionary<string, NodeValue> { ["relay_id"] = "1", ["token_id"] = "0", ["auth_token_id"] = "0" }, new byte[] { 127, 0, 0, 1, 0x0D, 0x96 })
        });
        RelayData relay = RelayParser.ParseRelayData(serverRelayNode)!;

        // 1) Alice places call -> fulfills prekey IQ -> sends <offer>
        Task<string> placeTask = aliceCall.PlaceCallAsync(bobJid, video: true, relay: relay, callId: "CALL-LOOP-1");
        Node pkReq = await aliceWire.ReadClientNodeAsync();
        byte[] bobOtkPub = PreKeyManager.DecodePreKeyRecord(bobOtk.RecordBytes).KeyPair.PublicKey;
        aliceWire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = pkReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("list", new[]
            {
                new Node("user", new Dictionary<string, NodeValue> { ["jid"] = bobJid }, new[]
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

        Node aliceOfferNode = await aliceWire.ReadClientNodeAsync();
        string callId = await placeTask;
        Assert.AreEqual("CALL-LOOP-1", callId);
        Assert.IsTrue(aliceCall.TryGetCall(callId, out ActiveCallSession? aliceSession));
        Assert.IsTrue(aliceSession!.ForceRelayOnly);

        // 2) Server relays Alice's offer + <relay> to Bob
        Node offerChild = aliceOfferNode.GetChild("offer")!;
        Node inboundToBob = new("call", new Dictionary<string, NodeValue>
        {
            ["from"] = Jid.PnDevice("15550000001", 0),
            ["id"] = aliceOfferNode.GetAttr("id")!,
            ["t"] = "1710010000"
        }, new[]
        {
            new Node("offer", new Dictionary<string, NodeValue>
            {
                ["call-id"] = callId,
                ["call-creator"] = aliceCreds.Lid.Value.ToNonAd()
            }, new[]
            {
                offerChild.GetChild("audio")!,
                offerChild.GetChild("video")!,
                offerChild.GetChild("enc")!,
                serverRelayNode
            })
        });

        bobWire.SendServerNode(inboundToBob);
        var bobOfferEvt = (IncomingCallReceivedEvent)await bobEvents.Reader.ReadAsync();
        ActiveCallSession bobSession = await bobCall.AcceptIncomingCallAsync(bobOfferEvt.Call, bobOfferEvt.RawStanza);
        Assert.AreEqual("CALL-LOOP-1", bobSession.CallId);

        _ = await bobWire.ReadClientNodeAsync(); // receipt
        _ = await bobWire.ReadClientNodeAsync(); // ack
        _ = await bobWire.ReadClientNodeAsync(); // preaccept
        _ = await bobWire.ReadClientNodeAsync(); // accept

        // 3) Simulate STUN Allocate Success on both UDP sockets
        byte[] aliceAlloc = await aliceUdp.Outbound.Reader.ReadAsync();
        byte[] bobAlloc = await bobUdp.Outbound.Reader.ReadAsync();
        await aliceUdp.Inbound.Writer.WriteAsync(StunRelayCodec.EncodeStunRequest(StunRelayCodec.MsgAllocateSuccess, aliceAlloc[8..20], ReadOnlySpan<byte>.Empty));
        await bobUdp.Inbound.Writer.WriteAsync(StunRelayCodec.EncodeStunRequest(StunRelayCodec.MsgAllocateSuccess, bobAlloc[8..20], ReadOnlySpan<byte>.Empty));

        // Wait briefly for state change to RelayReady
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (aliceSession.Engine.Phase != CallEnginePhase.RelayReady || bobSession.Engine.Phase != CallEnginePhase.RelayReady)
        {
            await Task.Delay(10, cts.Token);
        }

        // 4) Alice sends E2E audio packet -> routed through loopback UDP -> Bob emits CallAudioFrameReceivedEvent
        byte[] voiceBytes = "hello-bob-voip"u8.ToArray();
        await aliceCall.SendAudioFrameAsync(callId, new AudioFrame(voiceBytes));
        byte[] sentAudioRtp;
        do
        {
            sentAudioRtp = await aliceUdp.Outbound.Reader.ReadAsync(cts.Token);
        }
        while (StunRelayCodec.IsStunPacket(sentAudioRtp));

        await bobUdp.Inbound.Writer.WriteAsync(sentAudioRtp, cts.Token);

        CallAudioFrameReceivedEvent? gotAudio = null;
        while (gotAudio is null)
        {
            WhatsAppEvent ev = await bobEvents.Reader.ReadAsync(cts.Token);
            if (ev is CallAudioFrameReceivedEvent a) gotAudio = a;
        }
        CollectionAssert.AreEqual(voiceBytes, gotAudio.EncodedPayload);

        // 5) End calls cleanly
        await aliceCall.EndCallAsync(callId);
        await bobCall.EndCallAsync(callId);
        Assert.IsFalse(aliceCall.TryGetCall(callId, out _));
        Assert.IsFalse(bobCall.TryGetCall(callId, out _));
    }
}
