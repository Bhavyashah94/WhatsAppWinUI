using System.Threading.Channels;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;
using WhatsApp.Storage.Sqlite;
using WhatsApp.Transport;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class Phase6COrchestrationTests
{
    [TestMethod]
    public async Task UsyncClient_LookupUsername_And_GetDeviceLists_VerifyAdvAndPersist()
    {
        await using var backend = new SqliteBackend(":memory:");
        var cache = new LidPnCache();
        await using var pair = new LocalWirePair();
        await using var router = new StanzaRouter(pair.Socket, pair.Iq, new StanzaHandlers());

        var primaryId = Curve25519.GenerateKeyPair();
        Jid peerPn = Jid.Pn("15551112222");
        Jid peerLid = Jid.Lid("100000000000777");

        // Pre-seed trusted primary identity in SQLite.
        await backend.Signal.SaveIdentityAsync(peerPn.ToProtocolAddress(), primaryId.PublicKey);

        var ki = new Whatsapp.ADVKeyIndexList
        {
            RawId = 55,
            Timestamp = 1700001000,
            CurrentIndex = 2
        };
        ki.ValidIndexes.AddRange([0u, 2u]);
        byte[] signedKi = PairingCrypto.SignKeyIndexList(primaryId.PrivateKey, ki);

        var usync = new UsyncClient(pair.Iq, backend, cache);

        // 1) @username -> LID lookup
        Task<UsernameLookupResult> lookupTask = usync.LookupUsernameAsync("@alice_wa");
        Node req1 = await pair.ReadClientNodeAsync();
        string iqId1 = req1.GetAttr("id")!;
        pair.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = iqId1, ["type"] = "result" }, new Node[]
        {
            new("usync", new Node[]
            {
                new("list", new Node[]
                {
                    new("user", new Dictionary<string, NodeValue> { ["jid"] = peerLid, ["pn_jid"] = peerPn }, new Node[]
                    {
                        new("contact", new Dictionary<string, NodeValue> { ["type"] = "in", ["username"] = "alice_wa" }),
                        new("business")
                    })
                })
            })
        }));

        UsernameLookupResult lookup = await lookupTask;
        Assert.AreEqual(UsernameLookupStatus.Found, lookup.Status);
        Assert.AreEqual("100000000000777@lid", lookup.LidJid?.ToString());
        Assert.IsTrue(cache.TryGetLid("15551112222@s.whatsapp.net", out string? mappedLid));
        Assert.AreEqual("100000000000777@lid", mappedLid);
        IReadOnlyList<LidPnMappingRecord> persistedMappings = await backend.Protocol.LoadLidPnMappingsAsync();
        Assert.AreEqual(1, persistedMappings.Count);

        // 2) Device list query with revoked companion (key-index 1 not in ValidIndexes=[0,2] -> dropped)
        Task<IReadOnlyDictionary<string, ResolvedDeviceList>> devsTask = usync.GetDeviceListsAsync([peerPn]);
        Node req2 = await pair.ReadClientNodeAsync();
        Assert.AreEqual("100000000000777@lid", req2.GetChild("usync")?.GetChild("list")?.GetChild("user")?.GetChild("lid")?.GetAttr("jid"));
        string iqId2 = req2.GetAttr("id")!;
        pair.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = iqId2, ["type"] = "result" }, new Node[]
        {
            new("usync", new Node[]
            {
                new("list", new Node[]
                {
                    new("user", new Dictionary<string, NodeValue> { ["jid"] = peerPn }, new Node[]
                    {
                        new("devices", new Node[]
                        {
                            new("key-index-list", new Dictionary<string, NodeValue> { ["ts"] = "1700001000" }, signedKi),
                            new("device-list", new Dictionary<string, NodeValue> { ["hash"] = "2:good" }, new Node[]
                            {
                                new("device", new Dictionary<string, NodeValue> { ["id"] = "0" }),
                                new("device", new Dictionary<string, NodeValue> { ["id"] = "1", ["key-index"] = "1" }),
                                new("device", new Dictionary<string, NodeValue> { ["id"] = "2", ["key-index"] = "2" })
                            })
                        })
                    })
                })
            })
        }));

        IReadOnlyDictionary<string, ResolvedDeviceList> devMap = await devsTask;
        ResolvedDeviceList resolved = devMap[peerPn.ToNonAdString()];
        Assert.IsTrue(resolved.AdvVerified);
        CollectionAssert.AreEqual(new uint[] { 0u, 2u }, resolved.DeviceIds.ToArray());
        CollectionAssert.AreEqual(new uint[] { 0u, 2u }, (await backend.Protocol.GetDeviceListAsync(peerPn.ToNonAdString())).ToArray());
    }

    [TestMethod]
    public async Task ProfilePictureClient_AttachesTcToken_And_HandlesResponses()
    {
        await using var backend = new SqliteBackend(":memory:");
        await using var pair = new LocalWirePair();
        await using var router = new StanzaRouter(pair.Socket, pair.Iq, new StanzaHandlers());

        Jid target = Jid.Pn("15553334444");
        await backend.Protocol.StoreTcTokenAsync(target.ToNonAdString(), [9, 8, 7], 1700000000);

        var client = new ProfilePictureClient(pair.Iq, backend);
        Task<ProfilePictureLookupResult> task = client.GetProfilePictureAsync(target, ProfilePictureType.Full, existingId: "10");

        Node req = await pair.ReadClientNodeAsync();
        Assert.AreEqual("w:profile:picture", req.GetAttr("xmlns"));
        Assert.AreEqual("image", req.GetChild("picture")?.GetAttr("type"));
        CollectionAssert.AreEqual(
            new byte[] { 9, 8, 7 },
            ((NodeContent.BytesContent)req.GetChild("picture")!.GetChild("tctoken")!.Content!).Bytes);

        pair.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = req.GetAttr("id")!, ["type"] = "result" }, new Node[]
        {
            new("picture", new Dictionary<string, NodeValue> { ["status"] = "304" })
        }));

        ProfilePictureLookupResult res = await task;
        Assert.AreEqual(ProfilePictureLookupStatus.Unchanged, res.Status);
    }

    [TestMethod]
    public async Task NotificationRouter_HandlesIdentityChange_Devices_GroupSenderKeyRotation_And_Contacts()
    {
        await using var backend = new SqliteBackend(":memory:");
        await using var pair = new LocalWirePair();
        var cache = new LidPnCache();
        var creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15559990000", 1);
        creds.Lid = Jid.LidDevice("100000000000999", 1);
        await backend.Device.SaveAsync(creds);

        Jid peerPn = Jid.Pn("15550001234");
        Jid peerLid = Jid.Lid("100000000000101");
        cache.Learn(peerPn.ToString(), peerLid.ToString(), LidPnLearningSource.HistorySync, 100);

        // Seed Signal session, identity, device list, and sender keys
        var sessionAlice = new SignalSession
        {
            LocalIdentity = creds.IdentityKey.PublicKey,
            RemoteIdentity = Curve25519.GenerateKeyPair().PublicKey,
            RootKey = Enumerable.Repeat((byte)0x44, 32).ToArray()
        };
        await backend.Signal.SaveSessionAsync(peerPn.ToProtocolAddress(), sessionAlice);
        await backend.Signal.SaveSessionAsync(Jid.PnDevice("15550001234", 2).ToProtocolAddress(), sessionAlice);
        await backend.Signal.SaveIdentityAsync(peerPn.ToProtocolAddress(), Curve25519.GenerateKeyPair().PublicKey);
        await backend.Signal.SaveIdentityAsync(peerLid.ToProtocolAddress(), Curve25519.GenerateKeyPair().PublicKey);
        await backend.Protocol.SaveDeviceListAsync(peerPn.ToString(), [0u, 2u]);
        await backend.Signal.SaveSenderKeyAsync("status@broadcast", creds.Pn.Value.ToProtocolAddress(), [1, 2, 3]);
        await backend.Signal.SaveSenderKeyAsync("1203630001@g.us", creds.Pn.Value.ToProtocolAddress(), [4, 5, 6]);

        var events = new List<WhatsAppEvent>();
        var router = new NotificationRouter(
            backend,
            cache,
            creds,
            pair.Socket,
            pair.Iq,
            syncd: null,
            ev =>
            {
                events.Add(ev);
                return ValueTask.CompletedTask;
            });

        // 1) Companion identity change is ignored; primary clears sessions + identities + status@broadcast SK + device list + ACKs.
        Node companionId = new("notification", new Dictionary<string, NodeValue>
        {
            ["id"] = "id-c",
            ["type"] = "encrypt",
            ["from"] = Jid.PnDevice("15550001234", 2)
        }, new Node[] { new("identity") });
        await router.HandleAsync(companionId);
        Assert.IsNotNull(await backend.Signal.GetSessionAsync(peerPn.ToProtocolAddress()));
        _ = await pair.ReadClientNodeAsync(); // ACK

        Node primaryId = new("notification", new Dictionary<string, NodeValue>
        {
            ["id"] = "id-p",
            ["type"] = "encrypt",
            ["from"] = peerPn,
            ["lid"] = peerLid
        }, new Node[] { new("identity") });
        await router.HandleAsync(primaryId);

        Assert.IsNull(await backend.Signal.GetSessionAsync(peerPn.ToProtocolAddress()));
        Assert.IsNull(await backend.Signal.GetSessionAsync(Jid.PnDevice("15550001234", 2).ToProtocolAddress()));
        Assert.IsNull(await backend.Signal.GetIdentityAsync(peerPn.ToProtocolAddress()));
        Assert.IsNull(await backend.Signal.GetIdentityAsync(peerLid.ToProtocolAddress()));
        Assert.AreEqual(0, (await backend.Protocol.GetDeviceListAsync(peerPn.ToString())).Count);
        Assert.IsNull(await backend.Signal.GetSenderKeyAsync("status@broadcast", creds.Pn.Value.ToProtocolAddress()));
        Assert.IsTrue(events.OfType<IdentityChangeEvent>().Any(e => e.UserJid == peerPn.ToString()));
        Node ackId = await pair.ReadClientNodeAsync();
        Assert.AreEqual("ack", ackId.Tag);
        Assert.AreEqual("id-p", ackId.GetAttr("id"));

        // 2) Device update hash notification resolves via ContactHash ("s7oK" == 100000000000001@lid)
        cache.Learn("15550000001@s.whatsapp.net", "100000000000001@lid");
        await backend.Protocol.SaveDeviceListAsync("100000000000001@lid", [0u, 4u]);
        Node devUpdate = new("notification", new Dictionary<string, NodeValue>
        {
            ["id"] = "dev-u",
            ["type"] = "devices",
            ["from"] = StanzaBuilder.ServerPn
        }, new Node[] { new("update", new Dictionary<string, NodeValue> { ["hash"] = "s7oK" }) });
        await router.HandleAsync(devUpdate);
        Assert.AreEqual(0, (await backend.Protocol.GetDeviceListAsync("100000000000001@lid")).Count);
        Assert.IsTrue(events.OfType<DeviceNotificationEvent>().Any(d => d.Operation == "update" && d.UserJid == "100000000000001@lid"));
        _ = await pair.ReadClientNodeAsync();

        // 3) Group participant remove triggers own SenderKey rotation
        Node grpRemove = new("notification", new Dictionary<string, NodeValue>
        {
            ["id"] = "grp-r",
            ["type"] = "w:gp2",
            ["from"] = new Jid("1203630001", Server.Group)
        }, new Node[]
        {
            new("remove", new Node[] { new("participant", new Dictionary<string, NodeValue> { ["jid"] = peerLid }) })
        });
        await router.HandleAsync(grpRemove);
        Assert.IsNull(await backend.Signal.GetSenderKeyAsync("1203630001@g.us", creds.Pn.Value.ToProtocolAddress()));
        Assert.IsTrue(events.OfType<GroupNotificationEvent>().Any(g => g.Action == "remove"));
        _ = await pair.ReadClientNodeAsync();

        // 4) Contacts modify notification teaches both old and new PN<->LID mappings
        Node contactModify = new("notification", new Dictionary<string, NodeValue>
        {
            ["id"] = "cnt-m",
            ["type"] = "contacts",
            ["from"] = StanzaBuilder.ServerPn
        }, new Node[]
        {
            new("modify", new Dictionary<string, NodeValue>
            {
                ["old"] = Jid.Pn("15551110001"),
                ["new"] = Jid.Pn("15551110002"),
                ["old_lid"] = Jid.Lid("100000000000301"),
                ["new_lid"] = Jid.Lid("100000000000302")
            })
        });
        await router.HandleAsync(contactModify);
        Assert.IsTrue(cache.TryGetLid("15551110002@s.whatsapp.net", out string? newLid));
        Assert.AreEqual("100000000000302@lid", newLid);
        _ = await pair.ReadClientNodeAsync();

        // 5) Picture, DisappearingMode, Status, and Mex notifications emit strongly typed events
        await router.HandleAsync(new Node("notification", new Dictionary<string, NodeValue> { ["id"] = "pic-1", ["type"] = "picture", ["from"] = peerPn }, new Node[] { new("set", new Dictionary<string, NodeValue> { ["id"] = "555" }) }));
        _ = await pair.ReadClientNodeAsync();
        await router.HandleAsync(new Node("notification", new Dictionary<string, NodeValue> { ["id"] = "dm-1", ["type"] = "disappearing_mode", ["from"] = peerPn }, new Node[] { new("disappearing_mode", new Dictionary<string, NodeValue> { ["duration"] = "604800", ["t"] = "1700000500" }) }));
        _ = await pair.ReadClientNodeAsync();
        await router.HandleAsync(new Node("notification", new Dictionary<string, NodeValue> { ["id"] = "st-1", ["type"] = "status", ["from"] = peerPn }, new Node[] { new("set", "Busy coding") }));
        _ = await pair.ReadClientNodeAsync();
        await router.HandleAsync(new Node("notification", new Dictionary<string, NodeValue> { ["id"] = "mx-1", ["type"] = "mex", ["from"] = StanzaBuilder.ServerPn }, new Node[] { new("update", new Dictionary<string, NodeValue> { ["op_name"] = "NotificationLidChange" }, "{\"lid\":\"100@lid\"}") }));
        _ = await pair.ReadClientNodeAsync();

        Assert.IsTrue(events.OfType<PictureUpdateEvent>().Any(p => p.PictureId == "555"));
        Assert.IsTrue(events.OfType<DisappearingModeUpdateEvent>().Any(d => d.DurationSeconds == 604800));
        Assert.IsTrue(events.OfType<UserAboutUpdateEvent>().Any(s => s.StatusText == "Busy coding"));
        Assert.IsTrue(events.OfType<MexNotificationEvent>().Any(m => m.Operation == "NotificationLidChange"));
    }

    internal sealed class LocalWirePair : IAsyncDisposable
    {
        private readonly LoopbackTransport _transport;
        private readonly NoiseSessionCodec _serverCodec;

        public NoiseSessionSocket Socket { get; }
        public IqClient Iq { get; }

        public LocalWirePair()
        {
            byte[] c2s = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
            byte[] s2c = Enumerable.Range(61, 32).Select(i => (byte)i).ToArray();
            _serverCodec = new NoiseSessionCodec(new NoiseKeys(new NoiseCipher(s2c), new NoiseCipher(c2s)));
            _transport = new LoopbackTransport();
            Socket = new NoiseSessionSocket(1, _transport, new NoiseKeys(new NoiseCipher(c2s), new NoiseCipher(s2c)));
            Iq = new IqClient(Socket);
        }

        public async Task<Node> ReadClientNodeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                if (_serverCodec.TryDecryptNextFrame(out byte[] packed))
                {
                    return new BinaryDecoder(BinaryPacker.Unpack(packed)).ReadNode();
                }
                byte[] wire = await _transport.Outbound.Reader.ReadAsync(cts.Token);
                _serverCodec.Feed(wire);
            }
        }

        public void SendServerNode(Node node)
        {
            var enc = new BinaryEncoder();
            enc.WriteNode(node);
            _transport.Inbound.Writer.TryWrite(_serverCodec.EncryptFrame(BinaryPacker.Pack(enc.ToArray())));
        }

        public async ValueTask DisposeAsync()
        {
            await Socket.DisposeAsync();
            await _transport.DisposeAsync();
            _serverCodec.Dispose();
        }

        private sealed class LoopbackTransport : ITransport
        {
            public Channel<byte[]> Inbound { get; } = Channel.CreateUnbounded<byte[]>();
            public Channel<byte[]> Outbound { get; } = Channel.CreateUnbounded<byte[]>();
            private Memory<byte> _pending;
            private int _closed;

            public bool IsConnected => Volatile.Read(ref _closed) == 0;

            public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
            {
                Outbound.Writer.TryWrite(data.ToArray());
                return Task.CompletedTask;
            }

            public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                while (_pending.IsEmpty)
                {
                    if (!await Inbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        return 0;
                    }
                    if (Inbound.Reader.TryRead(out byte[]? next))
                    {
                        _pending = next;
                    }
                }
                int count = Math.Min(buffer.Length, _pending.Length);
                _pending.Span[..count].CopyTo(buffer.Span);
                _pending = _pending[count..];
                return count;
            }

            public Task ConnectAsync(IReadOnlyList<Uri> endpoints, CancellationToken ct = default) => Task.CompletedTask;
            public Task DisconnectAsync(CancellationToken ct = default)
            {
                if (Interlocked.Exchange(ref _closed, 1) == 0)
                {
                    Inbound.Writer.TryComplete();
                    Outbound.Writer.TryComplete();
                }
                return Task.CompletedTask;
            }
            public ValueTask DisposeAsync() => new(DisconnectAsync());
        }
    }
}
