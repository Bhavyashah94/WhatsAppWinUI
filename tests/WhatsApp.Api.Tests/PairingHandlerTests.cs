using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class PairingHandlerTests
{
    private sealed class RecordingDeviceStore : IDeviceStore
    {
        private readonly Action<DeviceCredentials>? _onBeforeSave;
        public List<DeviceCredentials> SavedSnapshots { get; } = [];
        public DeviceCredentials Current { get; }

        public RecordingDeviceStore(DeviceCredentials initial, Action<DeviceCredentials>? onBeforeSave = null)
        {
            Current = initial;
            _onBeforeSave = onBeforeSave;
        }

        public ValueTask<DeviceCredentials> LoadOrCreateAsync(CancellationToken ct = default) => ValueTask.FromResult(Current);
        public ValueTask<DeviceCredentials?> LoadAsync(CancellationToken ct = default) => ValueTask.FromResult<DeviceCredentials?>(Current);

        public ValueTask SaveAsync(DeviceCredentials credentials, CancellationToken ct = default)
        {
            _onBeforeSave?.Invoke(credentials);
            SavedSnapshots.Add(new DeviceCredentials
            {
                NoiseKey = credentials.NoiseKey,
                IdentityKey = credentials.IdentityKey,
                SignedPreKey = credentials.SignedPreKey,
                SignedPreKeySignature = credentials.SignedPreKeySignature,
                SignedPreKeyId = credentials.SignedPreKeyId,
                RegistrationId = credentials.RegistrationId,
                AdvSecretKey = credentials.AdvSecretKey,
                Pn = credentials.Pn,
                Lid = credentials.Lid,
                LidMigrated = credentials.LidMigrated,
                PushName = credentials.PushName,
            });
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearPairingAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

        public int ActiveTimerCount
        {
            get { lock (_gate) return _timers.Count(t => !t.Disposed && t.DueAt.HasValue); }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public void Advance(TimeSpan delta)
        {
            List<ManualTimer> ready = [];
            lock (_gate)
            {
                _now += delta;
                foreach (var timer in _timers)
                {
                    if (!timer.Disposed && timer.DueAt.HasValue && timer.DueAt.Value <= _now)
                    {
                        ready.Add(timer);
                        timer.DueAt = timer.Period != Timeout.InfiniteTimeSpan && timer.Period > TimeSpan.Zero
                            ? _now + timer.Period
                            : null;
                    }
                }
            }

            foreach (var timer in ready)
            {
                timer.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new ManualTimer(this, callback, state, dueTime, period);
            lock (_gate)
            {
                _timers.Add(t);
            }
            if (dueTime == TimeSpan.Zero)
            {
                t.Fire();
            }
            return t;
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public DateTimeOffset? DueAt { get; set; }
            public TimeSpan Period { get; set; }
            public bool Disposed { get; private set; }

            public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                Change(dueTime, period);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._gate)
                {
                    if (Disposed) return false;
                    Period = period;
                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
                    return true;
                }
            }

            public void Fire() => _callback(_state);

            public void Dispose()
            {
                lock (_owner._gate)
                {
                    Disposed = true;
                    DueAt = null;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static Node BuildPairDeviceIq(string id, params string[] refs)
    {
        var refNodes = refs.Select(r => new Node("ref", System.Text.Encoding.UTF8.GetBytes(r))).ToArray();
        return new Node(
            "iq",
            new Dictionary<string, NodeValue> { ["id"] = id, ["type"] = "set", ["from"] = StanzaBuilder.ServerPn },
            new Node[] { new Node("pair-device", refNodes) });
    }

    private static Node BuildPairSuccessIq(
        DeviceCredentials companion,
        byte[] advSecretForHmac,
        string reqId = "pair-ok-1",
        string? jid = "15550101234:3@s.whatsapp.net",
        string? lid = "999888777:3@lid",
        string? pushName = "Linked User",
        bool? lidMigrated = true,
        uint keyIndex = 3)
    {
        var accountKp = Curve25519.GenerateKeyPair();
        byte[] details = new ADVDeviceIdentity
        {
            RawId = 999,
            Timestamp = 1700000000,
            KeyIndex = keyIndex,
            AccountType = ADVEncryptionType.E2Ee,
            DeviceType = ADVEncryptionType.E2Ee
        }.ToByteArray();

        byte[] accountSig = XEdDSA.Sign(
            accountKp.PrivateKey,
            [0x06, 0x00, .. details, .. companion.IdentityKey.PublicKey]);

        byte[] signedIdentity = new ADVSignedDeviceIdentity
        {
            Details = ByteString.CopyFrom(details),
            AccountSignatureKey = ByteString.CopyFrom(accountKp.PublicKey),
            AccountSignature = ByteString.CopyFrom(accountSig)
        }.ToByteArray();

        byte[] hmac = HMACSHA256.HashData(advSecretForHmac, signedIdentity);
        byte[] container = new ADVSignedDeviceIdentityHMAC
        {
            Details = ByteString.CopyFrom(signedIdentity),
            Hmac = ByteString.CopyFrom(hmac),
            AccountType = ADVEncryptionType.E2Ee
        }.ToByteArray();

        var deviceAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal);
        if (jid is not null) deviceAttrs["jid"] = jid;
        if (lid is not null) deviceAttrs["lid"] = lid;

        var children = new List<Node>
        {
            new Node("device-identity", container),
            new Node("device", deviceAttrs)
        };

        if (pushName is not null)
        {
            children.Add(new Node("biz", new Dictionary<string, NodeValue> { ["name"] = pushName }));
        }
        if (lidMigrated.HasValue)
        {
            children.Add(new Node("client-props", new ClientPairingProps { IsChatDbLidMigrated = lidMigrated.Value }.ToByteArray()));
        }

        return new Node(
            "iq",
            new Dictionary<string, NodeValue> { ["id"] = reqId, ["type"] = "set", ["from"] = StanzaBuilder.ServerPn },
            new Node[] { new Node("pair-success", children) });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }
            await Task.Delay(5);
        }
    }

    [TestMethod]
    public async Task PairDevice_AcksImmediately_AndRotates60sThen20sUntilExhausted()
    {
        var creds = DeviceCredentials.CreateNew();
        var store = new RecordingDeviceStore(creds);
        var time = new ManualTimeProvider();
        var sentNodes = new ConcurrentQueue<Node>();
        var events = new ConcurrentQueue<WhatsAppEvent>();
        int exhaustedCallbacks = 0;

        await using var handler = new PairingHandler(
            (n, _) => { sentNodes.Enqueue(n); return ValueTask.CompletedTask; },
            store,
            creds,
            e => { events.Enqueue(e); return ValueTask.CompletedTask; },
            _ => { Interlocked.Increment(ref exhaustedCallbacks); return ValueTask.CompletedTask; },
            new PairingOptions { TimeProvider = time, ClientType = CompanionWebClientType.Chrome });

        bool handled = await handler.HandleIqAsync(BuildPairDeviceIq("pd-1", "ref-1", "ref-2", "ref-3"));
        Assert.IsTrue(handled);

        // Immediate empty result ack
        Assert.IsTrue(sentNodes.TryDequeue(out var ack));
        Assert.AreEqual("iq", ack!.Tag);
        Assert.AreEqual("pd-1", ack.GetAttr("id"));
        Assert.AreEqual("result", ack.GetAttr("type"));

        // First ref emitted immediately with 60s timeout
        await WaitUntilAsync(() => events.Count >= 1);
        Assert.IsTrue(events.TryDequeue(out var e1));
        var qr1 = (QrCodeEvent)e1!;
        Assert.AreEqual(TimeSpan.FromSeconds(60), qr1.Timeout);
        Assert.AreEqual("ref-1", PairingCrypto.ParseQrData(qr1.QrData).PairingRef);
        Assert.IsTrue(qr1.QrData.EndsWith(",1", StringComparison.Ordinal));

        // 59s elapsed: second ref must NOT fire yet
        time.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(15);
        Assert.AreEqual(0, events.Count);

        // +1s -> 60s total: second ref fires with 20s timeout
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => events.Count >= 1);
        Assert.IsTrue(events.TryDequeue(out var e2));
        var qr2 = (QrCodeEvent)e2!;
        Assert.AreEqual(TimeSpan.FromSeconds(20), qr2.Timeout);
        Assert.AreEqual("ref-2", PairingCrypto.ParseQrData(qr2.QrData).PairingRef);

        // +20s -> third ref fires with 20s timeout
        time.Advance(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => events.Count >= 1);
        Assert.IsTrue(events.TryDequeue(out var e3));
        var qr3 = (QrCodeEvent)e3!;
        Assert.AreEqual(TimeSpan.FromSeconds(20), qr3.Timeout);
        Assert.AreEqual("ref-3", PairingCrypto.ParseQrData(qr3.QrData).PairingRef);

        // +20s -> exhausted event and callback
        time.Advance(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => Volatile.Read(ref exhaustedCallbacks) == 1 && !events.IsEmpty);
        Assert.IsTrue(events.TryDequeue(out var e4));
        Assert.IsInstanceOfType<PairQrExhaustedEvent>(e4);
    }

    [TestMethod]
    public async Task PairSuccess_PersistsCredentialsBeforeSendingPairDeviceSign_AndStopsRotation()
    {
        var creds = DeviceCredentials.CreateNew();
        var log = new List<string>();
        var store = new RecordingDeviceStore(creds, _ => log.Add("save"));
        var time = new ManualTimeProvider();
        var sentNodes = new List<Node>();
        var emittedEvents = new List<WhatsAppEvent>();

        await using var handler = new PairingHandler(
            (n, _) =>
            {
                if (n.GetChild("pair-device-sign") is not null)
                {
                    log.Add("send_sign");
                }
                sentNodes.Add(n);
                return ValueTask.CompletedTask;
            },
            store,
            creds,
            e =>
            {
                if (e is PairSuccessEvent) log.Add("emit_event");
                emittedEvents.Add(e);
                return ValueTask.CompletedTask;
            },
            options: new PairingOptions { TimeProvider = time });

        await handler.HandleIqAsync(BuildPairDeviceIq("pd-1", "ref-1", "ref-2"));
        await WaitUntilAsync(() => emittedEvents.OfType<QrCodeEvent>().Any());

        Node pairSuccessIq = BuildPairSuccessIq(creds, creds.AdvSecretKey, keyIndex: 7);
        Assert.IsTrue(await handler.HandleIqAsync(pairSuccessIq));

        // Pre-wire durability ordering: Save -> Send <pair-device-sign> -> Emit PairSuccessEvent
        CollectionAssert.AreEqual(new[] { "save", "send_sign", "emit_event" }, log);
        Assert.AreEqual(1, store.SavedSnapshots.Count);
        Assert.AreEqual("15550101234:3@s.whatsapp.net", creds.Pn?.ToString());
        Assert.AreEqual("999888777:3@lid", creds.Lid?.ToString());
        Assert.IsTrue(creds.LidMigrated);
        Assert.AreEqual("Linked User", creds.PushName);

        var signIq = sentNodes.Last();
        Assert.AreEqual("result", signIq.GetAttr("type"));
        Assert.AreEqual("pair-ok-1", signIq.GetAttr("id"));
        var diNode = signIq.GetChild("pair-device-sign")?.GetChild("device-identity");
        Assert.IsNotNull(diNode);
        Assert.AreEqual("7", diNode.GetAttr("key-index"));
        byte[] wireSigned = ((NodeContent.BytesContent)diNode.Content!).Bytes;
        Assert.IsTrue(PairingCrypto.ValidateSignedDeviceIdentity(wireSigned, creds.IdentityKey.PublicKey));

        // QR rotation timer must be cancelled
        Assert.AreEqual(0, time.ActiveTimerCount);
        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(15);
        Assert.AreEqual(1, emittedEvents.OfType<QrCodeEvent>().Count());
    }

    [TestMethod]
    public async Task PairSuccess_HmacMismatch_Sends401ErrorAndDoesNotPersist()
    {
        var creds = DeviceCredentials.CreateNew();
        var store = new RecordingDeviceStore(creds);
        var sentNodes = new List<Node>();
        var events = new List<WhatsAppEvent>();

        await using var handler = new PairingHandler(
            (n, _) => { sentNodes.Add(n); return ValueTask.CompletedTask; },
            store,
            creds,
            e => { events.Add(e); return ValueTask.CompletedTask; });

        byte[] wrongSecret = Enumerable.Repeat((byte)0xCD, 32).ToArray();
        await handler.HandleIqAsync(BuildPairSuccessIq(creds, wrongSecret, reqId: "bad-hmac"));

        Assert.AreEqual(0, store.SavedSnapshots.Count);
        Assert.IsNull(creds.Pn);
        Assert.AreEqual(1, sentNodes.Count);
        var errIq = sentNodes[0];
        Assert.AreEqual("error", errIq.GetAttr("type"));
        Assert.AreEqual("bad-hmac", errIq.GetAttr("id"));
        Assert.AreEqual("401", errIq.GetChild("error")?.GetAttr("code"));
        Assert.AreEqual("hmac-mismatch", errIq.GetChild("error")?.GetAttr("text"));

        var pairErr = (PairErrorEvent)events.Single();
        Assert.AreEqual(401, pairErr.ErrorCode);
        Assert.AreEqual("hmac-mismatch", pairErr.ErrorText);
    }

    [TestMethod]
    public async Task PairSuccess_LidMigratedPreservedOnSameAccount_ResetOnDifferentAccount()
    {
        var creds = DeviceCredentials.CreateNew();
        creds.Pn = Jid.Parse("15550101234:1@s.whatsapp.net");
        creds.Lid = Jid.Parse("111222333:1@lid");
        creds.LidMigrated = true;

        var store = new RecordingDeviceStore(creds);
        var sentNodes = new List<Node>();

        await using var handler = new PairingHandler(
            (n, _) => { sentNodes.Add(n); return ValueTask.CompletedTask; },
            store,
            creds,
            _ => ValueTask.CompletedTask);

        // Same user (new device id :4) WITHOUT client-props: LidMigrated stays true
        await handler.HandleIqAsync(BuildPairSuccessIq(
            creds,
            creds.AdvSecretKey,
            reqId: "relink-same",
            jid: "15550101234:4@s.whatsapp.net",
            lid: null,
            lidMigrated: null));
        Assert.IsTrue(creds.LidMigrated);
        Assert.AreEqual("111222333:1@lid", creds.Lid?.ToString());

        // Different user WITHOUT client-props: LidMigrated resets to false and old Lid is cleared
        await handler.HandleIqAsync(BuildPairSuccessIq(
            creds,
            creds.AdvSecretKey,
            reqId: "switch-user",
            jid: "15559998888:1@s.whatsapp.net",
            lid: null,
            lidMigrated: null));
        Assert.IsFalse(creds.LidMigrated);
        Assert.IsNull(creds.Lid);
    }

    [TestMethod]
    public async Task PairSuccess_StoreFailure_RollsBackCredentialsAndSends500Error()
    {
        var creds = DeviceCredentials.CreateNew();
        var failingStore = new RecordingDeviceStore(creds, _ => throw new IOException("disk full"));
        var sentNodes = new List<Node>();
        var events = new List<WhatsAppEvent>();

        await using var handler = new PairingHandler(
            (n, _) => { sentNodes.Add(n); return ValueTask.CompletedTask; },
            failingStore,
            creds,
            e => { events.Add(e); return ValueTask.CompletedTask; });

        await handler.HandleIqAsync(BuildPairSuccessIq(creds, creds.AdvSecretKey, reqId: "db-fail"));

        Assert.IsNull(creds.Pn);
        Assert.IsNull(creds.Lid);
        Assert.IsFalse(creds.LidMigrated);
        Assert.AreEqual("WhatsApp Desktop", creds.PushName);

        var errIq = sentNodes.Single();
        Assert.AreEqual("error", errIq.GetAttr("type"));
        Assert.AreEqual("500", errIq.GetChild("error")?.GetAttr("code"));
        Assert.AreEqual("internal-error", errIq.GetChild("error")?.GetAttr("text"));
        Assert.AreEqual(500, ((PairErrorEvent)events.Single()).ErrorCode);
    }

    [TestMethod]
    public async Task PairSuccess_InvalidCrypto_LeavesActiveQrRotationIntact_AndSupportsLidOnly()
    {
        var creds = DeviceCredentials.CreateNew();
        var store = new RecordingDeviceStore(creds);
        var time = new ManualTimeProvider();
        var sentNodes = new ConcurrentQueue<Node>();
        var events = new ConcurrentQueue<WhatsAppEvent>();

        await using var handler = new PairingHandler(
            (n, _) => { sentNodes.Enqueue(n); return ValueTask.CompletedTask; },
            store,
            creds,
            e => { events.Enqueue(e); return ValueTask.CompletedTask; },
            options: new PairingOptions { TimeProvider = time });

        await handler.HandleIqAsync(BuildPairDeviceIq("pd-1", "ref-1", "ref-2"));
        await WaitUntilAsync(() => events.OfType<QrCodeEvent>().Count() == 1);

        // Tampered HMAC arrives while QR is displayed: must NOT kill QR rotation!
        byte[] wrongSecret = Enumerable.Repeat((byte)0xEE, 32).ToArray();
        await handler.HandleIqAsync(BuildPairSuccessIq(creds, wrongSecret, reqId: "bad-1"));
        Assert.AreEqual(1, time.ActiveTimerCount);

        time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => events.OfType<QrCodeEvent>().Count() == 2);

        // Now valid LID-only pair-success arrives (matching live WASmaxMdSetRegRPC optional jid)
        await handler.HandleIqAsync(BuildPairSuccessIq(
            creds,
            creds.AdvSecretKey,
            reqId: "lid-only-ok",
            jid: null,
            lid: "888777666:9@lid"));

        Assert.AreEqual(0, time.ActiveTimerCount);
        Assert.AreEqual("888777666:9@lid", creds.Pn?.ToString());
        Assert.AreEqual("888777666:9@lid", creds.Lid?.ToString());
    }
}
