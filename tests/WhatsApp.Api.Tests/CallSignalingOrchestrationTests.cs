using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Storage.Sqlite;
using LocalWirePair = WhatsApp.Api.Tests.UsyncAndNotificationOrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class CallSignalingOrchestrationTests
{
    private sealed class MockCallAppStateHttp : IHttpClient
    {
        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);

        public ExternalBlobReference StoreBlob(string directPath, byte[] plaintext, byte[] mediaKey)
        {
            byte[] enc = MediaCipher.EncryptBlob(plaintext, mediaKey, WaMediaType.AppState);
            Blobs[directPath] = enc;
            return new ExternalBlobReference
            {
                DirectPath = directPath,
                MediaKey = ByteString.CopyFrom(mediaKey),
                FileSizeBytes = (ulong)enc.Length,
                FileSha256 = ByteString.CopyFrom(SHA256.HashData(plaintext)),
                FileEncSha256 = ByteString.CopyFrom(SHA256.HashData(enc))
            };
        }

        public Task<byte[]> GetBytesAsync(Uri url, long maxBytes = 64 * 1024 * 1024, CancellationToken ct = default) =>
            Task.FromResult(Blobs[url.PathAndQuery]);

        public Task<byte[]> PostBytesAsync(Uri url, ReadOnlyMemory<byte> body, string contentType = "application/octet-stream", CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
    }

    [TestMethod]
    public async Task StanzaRouter_And_CallSignalingClient_RouteIncomingOfferAndSendAllSignaling()
    {
        await using var backend = new SqliteBackend(":memory:");
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15550000001", 1);
        creds.Lid = Jid.LidDevice("100000000000001", 1);
        await backend.Device.SaveAsync(creds);

        var events = Channel.CreateUnbounded<WhatsAppEvent>();
        await using var wire = new LocalWirePair();
        var client = new CallSignalingClient(wire.Socket, creds, ev => events.Writer.WriteAsync(ev));

        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers
        {
            OnCall = (n, t) => new ValueTask(client.HandleCallStanzaAsync(n, t))
        });

        Jid peerDev = Jid.PnDevice("15550002222", 0);
        Jid creatorLid = Jid.Lid("100000000000022");
        byte[] te2Ipv4 = [198, 51, 100, 42, 0x0D, 0x96];

        Node offerStanza = new("call", new Dictionary<string, NodeValue>
        {
            ["from"] = peerDev,
            ["id"] = "IN-CALL-1",
            ["t"] = "1710005000",
            ["notify"] = "Bob"
        }, new[]
        {
            new Node("offer", new Dictionary<string, NodeValue>
            {
                ["call-id"] = "CID-100",
                ["call-creator"] = creatorLid,
                ["caller_pn"] = Jid.Pn("15550002222"),
                ["joinable"] = "1"
            }, new[]
            {
                new Node("audio", new Dictionary<string, NodeValue> { ["enc"] = "opus", ["rate"] = "16000" }),
                new Node("video", new Dictionary<string, NodeValue> { ["enc"] = "h.264", ["dec"] = "H264", ["device_orientation"] = "1" }),
                new Node("capability", new Dictionary<string, NodeValue> { ["ver"] = "1" }, CallStanzas.CapabilityVideoOffer),
                new Node("enc", new Dictionary<string, NodeValue> { ["v"] = "2", ["type"] = "pkmsg" }, new byte[] { 1, 2, 3 }),
                new Node("relay", new[]
                {
                    new Node("hbh_key", Encoding.UTF8.GetBytes(Convert.ToBase64String(Enumerable.Range(0, 30).Select(x => (byte)x).ToArray()))),
                    new Node("te2", new Dictionary<string, NodeValue> { ["relay_id"] = "1", ["token_id"] = "0", ["auth_token_id"] = "0" }, te2Ipv4)
                })
            })
        });

        wire.SendServerNode(offerStanza);

        WhatsAppEvent evt = await events.Reader.ReadAsync();
        Assert.IsInstanceOfType<IncomingCallReceivedEvent>(evt);
        var callEvt = (IncomingCallReceivedEvent)evt;
        Assert.AreEqual(CallActionKind.Offer, callEvt.Call.ActionKind);
        Assert.AreEqual("CID-100", callEvt.Call.CallId);
        Assert.IsTrue(callEvt.Call.IsVideo);
        Assert.AreEqual((byte)1, callEvt.Call.VideoOrientation);
        Assert.IsNotNull(callEvt.Call.Relay);
        Assert.AreEqual("198.51.100.42", callEvt.Call.Relay.Endpoints[0].Addresses[0].Ipv4);

        // Outbound offer-ack receipt + typed call ack + preaccept + accept + mute + terminate
        await client.SendOfferAckReceiptAsync(callEvt.Call);
        Node ackReceipt = await wire.ReadClientNodeAsync();
        Assert.AreEqual("receipt", ackReceipt.Tag);
        Assert.AreEqual("CID-100", ackReceipt.GetChild("offer")!.GetAttr("call-id"));

        await client.SendTypedCallAckAsync(offerStanza, "offer");
        Node callAck = await wire.ReadClientNodeAsync();
        Assert.AreEqual("ack", callAck.Tag);
        Assert.AreEqual("call", callAck.GetAttr("class"));

        await client.SendPreAcceptAsync("CID-100", peerDev, creatorLid, video: true);
        Node preAcceptNode = await wire.ReadClientNodeAsync();
        Assert.AreEqual("preaccept", preAcceptNode.GetChild("preaccept")!.Tag);

        await client.SendAcceptAsync("CID-100", peerDev, creatorLid, video: true);
        Node acceptNode = await wire.ReadClientNodeAsync();
        Assert.AreEqual("accept", acceptNode.GetChild("accept")!.Tag);

        await client.SendMuteAsync("CID-100", peerDev, creatorLid, muted: true);
        Node muteNode = await wire.ReadClientNodeAsync();
        Assert.AreEqual("1", muteNode.GetChild("mute_v2")!.GetAttr("mute-state"));

        await client.SendTerminateAsync("CID-100", peerDev, creatorLid, CallStanzas.TerminateReasonAcceptedElsewhere);
        Node termNode = await wire.ReadClientNodeAsync();
        Assert.AreEqual(CallStanzas.TerminateReasonAcceptedElsewhere, termNode.GetChild("terminate")!.GetAttr("reason"));
    }

    [TestMethod]
    public async Task SyncdAndSettings_CallLogFromMeAndRelayAllCalls_SyncAndUpdateClientPolicy()
    {
        await using var backend = new SqliteBackend(":memory:");
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15550000001", 1);
        creds.Lid = Jid.LidDevice("100000000000001", 1);
        await backend.Device.SaveAsync(creds);

        byte[] syncKeyId = [10, 20, 30, 40];
        byte[] syncKeyData = RandomNumberGenerator.GetBytes(32);
        await backend.AppSync.SaveSyncKeyAsync(syncKeyId, syncKeyData);
        ExpandedAppStateKeys keys = AppStateKeys.Expand(syncKeyData);

        await using var wire = new LocalWirePair();
        var events = new List<WhatsAppEvent>();
        var callSignaling = new CallSignalingClient(
            wire.Socket,
            creds,
            ev => { events.Add(ev); return ValueTask.CompletedTask; });

        var http = new MockCallAppStateHttp();
        byte[] mediaKey = RandomNumberGenerator.GetBytes(32);

        await using var syncd = new SyncdCoordinator(
            backend,
            http,
            (iq, ct) =>
            {
                var snap = new SyncdSnapshot { Version = new SyncdVersion { Version = 1 }, KeyId = new KeyId { Id = ByteString.CopyFrom(syncKeyId) } };
                var valMacs = new List<byte[]>();
                byte iv = 1;

                void AddMutation(byte[] indexBytes, SyncActionValue actionValue)
                {
                    (SyncdMutation mut, byte[] mac) = AppStateMutationCodec.EncodeRecord(
                        SyncdMutation.Types.SyncdOperation.Set,
                        indexBytes,
                        actionValue,
                        keys,
                        syncKeyId,
                        Enumerable.Repeat(iv++, 16).ToArray());
                    snap.Records.Add(mut.Record);
                    valMacs.Add(mac);
                }

                AddMutation(
                    AppStateSchemas.CallLog.BuildIndex("100000000000001@lid", "CALL-SYNC-1", "1"),
                    new SyncActionValue
                    {
                        Timestamp = 1710006000000L,
                        CallLogAction = new SyncActionValue.Types.CallLogAction
                        {
                            CallLogRecord = new CallLogRecord
                            {
                                CallId = "CALL-SYNC-1",
                                IsIncoming = true, // WA Web inversion quirk: FromMe must still be true because creator is own LID!
                                IsVideo = true,
                                Duration = 120,
                                CallResult = CallLogRecord.Types.CallResult.Connected
                            }
                        }
                    });

                AddMutation(
                    AppStateSchemas.RelayAllCalls.BuildIndex(),
                    new SyncActionValue
                    {
                        Timestamp = 1710006001000L,
                        PrivacySettingRelayAllCalls = new SyncActionValue.Types.PrivacySettingRelayAllCalls { IsEnabled = true }
                    });

                byte[] hash = LtHash.SubtractThenAdd(LtHash.CreateEmpty(), null, valMacs);
                snap.Mac = ByteString.CopyFrom(AppStateHasher.GenerateSnapshotMac(hash, 1, AppStateCollections.Regular, keys.SnapshotMacKey));
                ExternalBlobReference blobRef = http.StoreBlob("/v/t62/10a-snap", snap.ToByteArray(), mediaKey);

                return ValueTask.FromResult(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = iq.GetAttr("id") ?? "1", ["type"] = "result" }, new[]
                {
                    new Node("sync", new[] { new Node("collection", new Dictionary<string, NodeValue> { ["name"] = AppStateCollections.Regular, ["version"] = "1" }, new[] { new Node("snapshot", blobRef.ToByteArray()) }) })
                }));
            },
            emitEventAsync: ev =>
            {
                if (ev is RelayAllCallsUpdatedEvent rac) callSignaling.ApplyRelayAllCallsSetting(rac.Enabled);
                events.Add(ev);
                return ValueTask.CompletedTask;
            });

        Assert.IsFalse(callSignaling.RelayAllCallsEnabled);
        await syncd.SyncCollectionsAsync([AppStateCollections.Regular]);

        var syncedCall = events.OfType<CallLogSyncedEvent>().Single();
        Assert.AreEqual("CALL-SYNC-1", syncedCall.CallId);
        Assert.IsTrue(syncedCall.FromMe);
        Assert.IsTrue(syncedCall.Record.IsVideo);

        Assert.IsTrue(callSignaling.RelayAllCallsEnabled);
        Assert.IsTrue(callSignaling.ResolveShouldForceRelay());
        Assert.IsFalse(callSignaling.ResolveShouldForceRelay(perCallOverride: false));
    }
}
