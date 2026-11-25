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
using WhatsApp.Protocol.Signal;
using WhatsApp.Storage.Sqlite;
using HandshakeMessage = WhatsApp.Protocol.Noise.HandshakeMessage;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class LifecycleOrchestrationTests
{
    private sealed class DeterministicTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<TimerEntry> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public override long GetTimestamp()
        {
            lock (_gate) return _now.Ticks;
        }

        public void Advance(TimeSpan delta)
        {
            List<TimerEntry> due = [];
            lock (_gate)
            {
                _now += delta;
                foreach (var timer in _timers)
                {
                    if (!timer.Disposed && timer.DueAt.HasValue && timer.DueAt.Value <= _now)
                    {
                        due.Add(timer);
                        timer.DueAt = timer.Period != Timeout.InfiniteTimeSpan && timer.Period > TimeSpan.Zero
                            ? _now + timer.Period
                            : null;
                    }
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var entry = new TimerEntry(this, callback, state, dueTime, period);
            lock (_gate)
            {
                _timers.Add(entry);
            }
            if (dueTime == TimeSpan.Zero)
            {
                entry.Fire();
            }
            return entry;
        }

        private sealed class TimerEntry : ITimer
        {
            private readonly DeterministicTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public DateTimeOffset? DueAt { get; set; }
            public TimeSpan Period { get; set; }
            public bool Disposed { get; private set; }

            public TimerEntry(DeterministicTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
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

    private sealed class MockSessionServer : IAsyncDisposable
    {
        private readonly NoiseSessionCodec _serverCodec;
        private readonly MockDuplexTransport _transport;

        public NoiseSessionSocket ClientSocket { get; }
        public IqClient Iq { get; }

        public MockSessionServer(long generation = 1)
        {
            byte[] c2s = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
            byte[] s2c = Enumerable.Range(61, 32).Select(i => (byte)i).ToArray();
            _serverCodec = new NoiseSessionCodec(new NoiseKeys(new NoiseCipher(s2c), new NoiseCipher(c2s)));
            _transport = new MockDuplexTransport();
            ClientSocket = new NoiseSessionSocket(generation, _transport, new NoiseKeys(new NoiseCipher(c2s), new NoiseCipher(s2c)));
            Iq = new IqClient(ClientSocket);
        }

        public async Task<Node> ReadNextClientNodeAsync(CancellationToken ct = default)
        {
            while (true)
            {
                if (_serverCodec.TryDecryptNextFrame(out byte[] packed))
                {
                    return new BinaryDecoder(BinaryPacker.Unpack(packed)).ReadNode();
                }
                byte[] chunk = await _transport.ReadClientChunkAsync(ct);
                _serverCodec.Feed(chunk);
            }
        }

        public void SendServerNode(Node node)
        {
            var enc = new BinaryEncoder();
            enc.WriteNode(node);
            _transport.EnqueueServerChunk(_serverCodec.EncryptFrame(BinaryPacker.Pack(enc.ToArray())));
        }

        public async ValueTask DisposeAsync()
        {
            await ClientSocket.DisposeAsync();
            await _transport.DisposeAsync();
            _serverCodec.Dispose();
        }
    }

    private sealed class MockDuplexTransport : ITransport
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>();
        private Memory<byte> _pendingRead;
        private int _closed;

        public bool IsConnected => Volatile.Read(ref _closed) == 0;

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _closed) != 0) throw new IOException("Transport closed");
            return _outbound.Writer.WriteAsync(data.ToArray(), ct).AsTask();
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_pendingRead.IsEmpty)
            {
                if (!await _inbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    return 0;
                }
                if (_inbound.Reader.TryRead(out byte[]? next))
                {
                    _pendingRead = next;
                }
            }

            int copy = Math.Min(buffer.Length, _pendingRead.Length);
            _pendingRead.Span[..copy].CopyTo(buffer.Span);
            _pendingRead = _pendingRead[copy..];
            return copy;
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            Interlocked.Exchange(ref _closed, 1);
            _inbound.Writer.TryComplete();
            _outbound.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => new(DisconnectAsync());

        public ValueTask<byte[]> ReadClientChunkAsync(CancellationToken ct = default) => _outbound.Reader.ReadAsync(ct);
        public void EnqueueServerChunk(byte[] bytes) => _inbound.Writer.TryWrite(bytes);
    }

    private sealed class ScriptedTransportFactory(Func<MockDuplexTransport, int, Task> serverScript) : ITransportFactory
    {
        private int _attempt;

        public Task<ITransport> ConnectAsync(IReadOnlyList<Uri> endpoints, CancellationToken ct = default)
        {
            int num = Interlocked.Increment(ref _attempt);
            var transport = new MockDuplexTransport();
            _ = Task.Run(() => serverScript(transport, num), ct);
            return Task.FromResult<ITransport>(transport);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public void ConnectionHealth_MatchesRustKeepaliveAndDeadSocketRules()
    {
        var time = new DeterministicTimeProvider();
        var health = new ConnectionHealth();

        long recv0 = time.GetTimestamp();
        // Received < 15s ago -> Skip and reset failures
        time.Advance(TimeSpan.FromSeconds(14));
        Assert.AreEqual(HealthAction.Skip, health.BeforeProbe(time, time.GetTimestamp(), recv0, null));

        // 16s elapsed -> Probe
        time.Advance(TimeSpan.FromSeconds(2));
        long send1 = time.GetTimestamp();
        Assert.AreEqual(HealthAction.Probe, health.BeforeProbe(time, time.GetTimestamp(), recv0, send1));

        // 1st and 2nd transient failures are NOT terminal
        Assert.IsFalse(ConnectionHealth.FailuresAreTerminal(1));
        Assert.IsFalse(ConnectionHealth.FailuresAreTerminal(2));
        Assert.IsTrue(ConnectionHealth.FailuresAreTerminal(3));

        Assert.AreEqual(HealthAction.Skip, health.ProbeCompleted(KeepaliveProbeOutcome.TransientFailure, time, time.GetTimestamp(), recv0, recv0, send1));
        Assert.AreEqual(1, health.Failures);

        // If > 20s passes since first unanswered send with no receive -> dead socket triggers Reconnect!
        time.Advance(TimeSpan.FromSeconds(21));
        Assert.AreEqual(HealthAction.Reconnect, health.BeforeProbe(time, time.GetTimestamp(), recv0, send1));

        // Fresh Ok restores failures to 0
        long recv1 = time.GetTimestamp();
        Assert.AreEqual(HealthAction.Skip, health.ProbeCompleted(KeepaliveProbeOutcome.Ok, time, recv1, recv0, recv1, null));
        Assert.AreEqual(0, health.Failures);

        // FatalFailure -> Stop
        Assert.AreEqual(HealthAction.Stop, health.ProbeCompleted(KeepaliveProbeOutcome.FatalFailure, time, recv1, recv1, recv1, null));
    }

    [TestMethod]
    public async Task OfflineBatchCoordinator_PacesBatchesCompletesAndRespectsBackpressureWatchdog()
    {
        var time = new DeterministicTimeProvider();
        var sentNodes = new ConcurrentQueue<Node>();
        var events = new ConcurrentQueue<WhatsAppEvent>();

        await using var coord = new OfflineBatchCoordinator(
            generation: 1,
            (n, _) => { sentNodes.Enqueue(n); return ValueTask.CompletedTask; },
            e => { events.Enqueue(e); return ValueTask.CompletedTask; },
            new OfflineBatchOptions
            {
                BatchSize = 200,
                RequestDebounce = TimeSpan.FromMilliseconds(100),
                InactivityTimeout = TimeSpan.FromSeconds(60),
                TimeProvider = time
            });

        // Zero count preview must not send offline_batch
        await coord.OnOfflinePreviewAsync(new OfflinePreviewInfo(0, 0, 0, 0, 0, 0, 0));
        Assert.AreEqual(0, sentNodes.Count);
        Assert.IsFalse(coord.IsArmed);

        // 450 count preview sends first batch=200 immediately
        await coord.OnOfflinePreviewAsync(new OfflinePreviewInfo(450, 5, 440, 2, 1, 0, 2));
        Assert.IsTrue(coord.IsArmed);
        Assert.IsTrue(sentNodes.TryDequeue(out var batch1));
        Assert.AreEqual("200", batch1!.GetChild("offline_batch")?.GetAttr("count"));

        // Arriving offline stanza debounces next batch request by 100ms
        var msg1 = new Node("message", new Dictionary<string, NodeValue> { ["offline"] = "0" });
        using (var lease1 = coord.TrackOfflineStanza(msg1))
        {
            Assert.IsNotNull(lease1);
            time.Advance(TimeSpan.FromMilliseconds(120));
            await WaitUntilAsync(() => !sentNodes.IsEmpty);
            Assert.IsTrue(sentNodes.TryDequeue(out var batch2));
            Assert.AreEqual("200", batch2!.GetChild("offline_batch")?.GetAttr("count"));
        }

        // Advance 60s while holding an active lease -> backpressure keeps drain alive
        using (var heldLease = coord.TrackOfflineStanza(new Node("message", new Dictionary<string, NodeValue> { ["offline"] = "1" })))
        {
            time.Advance(TimeSpan.FromSeconds(61));
            await Task.Delay(20);
            Assert.IsFalse(coord.IsCompleted);
        }

        // Now complete cleanly via <ib><offline count="450"/>
        await coord.OnOfflineCompletedAsync(450);
        Assert.IsTrue(coord.IsCompleted);
        Assert.IsFalse(coord.IsArmed);
        Assert.IsTrue(events.OfType<OfflineSyncCompletedEvent>().Any(e => e.ProcessedCount == 450 && !e.TimedOut));
    }

    [TestMethod]
    public async Task OfflineBatchCoordinator_WatchdogCompletesWhenIdleForFullWindow()
    {
        var time = new DeterministicTimeProvider();
        var events = new ConcurrentQueue<WhatsAppEvent>();

        await using var coord = new OfflineBatchCoordinator(
            generation: 1,
            (_, _) => ValueTask.CompletedTask,
            e => { events.Enqueue(e); return ValueTask.CompletedTask; },
            new OfflineBatchOptions
            {
                BatchSize = 200,
                RequestDebounce = TimeSpan.Zero,
                InactivityTimeout = TimeSpan.FromSeconds(60),
                TimeProvider = time
            });

        await coord.OnOfflinePreviewAsync(new OfflinePreviewInfo(50, 0, 50, 0, 0, 0, 0));
        using (coord.TrackOfflineStanza(new Node("message", new Dictionary<string, NodeValue> { ["offline"] = "0" })))
        {
        }

        // Inactivity loop snapshots stanzaActivity on first wake, then sees no change on second window
        time.Advance(TimeSpan.FromSeconds(61));
        await Task.Delay(20);
        time.Advance(TimeSpan.FromSeconds(61));
        await WaitUntilAsync(() => coord.IsCompleted);

        var done = events.OfType<OfflineSyncCompletedEvent>().Single();
        Assert.IsTrue(done.TimedOut);
        Assert.AreEqual(1, done.ProcessedCount);
    }

    [TestMethod]
    public async Task PreKeyUploader_RespectsThresholdPersistsBeforeWireAndAdvancesNextId()
    {
        await using var backend = new SqliteBackend(":memory:");
        await backend.InitializeAsync();
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();

        await using var server = new MockSessionServer();
        await using var router = new StanzaRouter(server.ClientSocket, server.Iq);

        // 1. Server already has >= 5 prekeys -> skips upload
        Task<PreKeyUploadResult> skipTask = PreKeyUploader.EnsureUploadedAsync(server.Iq, backend, creds, wantedCount: 6);
        Node countReq1 = await server.ReadNextClientNodeAsync();
        Assert.AreEqual("encrypt", countReq1.GetAttr("xmlns"));
        Assert.IsNotNull(countReq1.GetChild("count"));
        server.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = countReq1.GetAttr("id")!, ["type"] = "result" },
            new Node[] { new("count", new Dictionary<string, NodeValue> { ["value"] = "5" }) }));

        PreKeyUploadResult skipRes = await skipTask;
        Assert.IsFalse(skipRes.Uploaded);
        Assert.AreEqual(5, skipRes.ServerCount);
        Assert.AreEqual(1U, creds.NextPreKeyId);

        // 2. Server has 4 (< 5) -> uploads clamped batch (6), persists BEFORE sending upload IQ
        Task<PreKeyUploadResult> uploadTask = PreKeyUploader.EnsureUploadedAsync(server.Iq, backend, creds, wantedCount: 6);
        Node countReq2 = await server.ReadNextClientNodeAsync();
        server.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = countReq2.GetAttr("id")!, ["type"] = "result" },
            new Node[] { new("count", new Dictionary<string, NodeValue> { ["value"] = "4" }) }));

        Node uploadReq = await server.ReadNextClientNodeAsync();
        Assert.AreEqual("set", uploadReq.GetAttr("type"));
        Assert.AreEqual("encrypt", uploadReq.GetAttr("xmlns"));
        var keyList = uploadReq.GetChild("list")!.GetChildren("key").ToList();
        Assert.AreEqual(6, keyList.Count);

        // Prekeys 1..6 and NextPreKeyId=7 MUST already be stored in SQLite when uploadReq hit wire!
        for (uint id = 1; id <= 6; id++)
        {
            Assert.IsNotNull(await backend.Signal.GetPreKeyAsync(id), $"PreKey {id} missing before IQ ACK");
        }
        DeviceCredentials? reloaded = await backend.Device.LoadAsync();
        Assert.AreEqual(7U, reloaded!.NextPreKeyId);

        server.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = uploadReq.GetAttr("id")!, ["type"] = "result" }));
        PreKeyUploadResult uploadRes = await uploadTask;
        Assert.IsTrue(uploadRes.Uploaded);
        Assert.AreEqual(6, uploadRes.UploadedCount);
        Assert.AreEqual(7U, uploadRes.NextPreKeyId);
    }

    [TestMethod]
    public async Task ConnectionOrchestrator_PairsHandles515RestartsWithIkAndCompletesColdBoot()
    {
        await using var backend = new SqliteBackend(":memory:");
        await backend.InitializeAsync();
        var root = Curve25519.GenerateKeyPair();
        var serverStatic = Curve25519.GenerateKeyPair();
        byte[] certChain = BuildSignedCertChain(root, serverStatic.PublicKey);

        var sessionEvents = new ConcurrentQueue<WhatsAppEvent>();
        var orderedColdBootLog = new ConcurrentQueue<string>();
        var debugState = new ConcurrentQueue<string>();
        var session2ColdBootDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int sessionsObserved = 0;

        var transportFactory = new ScriptedTransportFactory(async (transport, attempt) =>
        {
            try
            {
            Interlocked.Increment(ref sessionsObserved);
            debugState.Enqueue($"attempt={attempt}:start");
            var serverEphemeral = Curve25519.GenerateKeyPair();

            if (attempt == 1)
            {
                // --- Session 1: XX Handshake -> pair-device -> pair-success -> 515 stream restart ---
                byte[] pkt1 = await transport.ReadClientChunkAsync();
                byte[] helloBytes = ExtractFrameAfterHeader(pkt1, Constants.WaConnHeader);
                byte[] clientEph = global::Whatsapp.HandshakeMessage.Parser.ParseFrom(helloBytes).ClientHello.Ephemeral.ToByteArray();

                var serverNoise = new NoiseState("Noise_XX_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);
                serverNoise.Authenticate(clientEph);
                serverNoise.Authenticate(serverEphemeral.PublicKey);
                serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientEph));
                byte[] encStatic = serverNoise.Encrypt(serverStatic.PublicKey);
                serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, clientEph));
                byte[] encCert = serverNoise.Encrypt(certChain);
                transport.EnqueueServerChunk(Framing.EncodeFrame(HandshakeMessage.EncodeServerHello(serverEphemeral.PublicKey, encStatic, encCert)));

                byte[] pkt2 = await transport.ReadClientChunkAsync();
                Assert.IsTrue(Framing.TryDecodeFrame(pkt2, out byte[] finishBytes, out _));
                var fin = global::Whatsapp.HandshakeMessage.Parser.ParseFrom(finishBytes).ClientFinish;
                byte[] clientStatic = serverNoise.Decrypt(fin.Static.ToByteArray());
                serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientStatic));
                _ = serverNoise.Decrypt(fin.Payload.ToByteArray());

                NoiseKeys sKeys = serverNoise.Split();
                using var codec = new NoiseSessionCodec(new NoiseKeys(sKeys.ReadCipher, sKeys.WriteCipher));

                // 1a. Send <pair-device>
                DeviceCredentials storedBeforePair = (await backend.Device.LoadAsync())!;
                SendEncryptedNode(transport, codec, new Node("iq", new Dictionary<string, NodeValue>
                {
                    ["id"] = "pd-live-1",
                    ["type"] = "set",
                    ["from"] = StanzaBuilder.ServerPn
                }, new Node[] { new("pair-device", new Node[] { new("ref", "live-ref-0"u8.ToArray()) }) }));

                Node ackNode = await ReadDecryptedNodeAsync(transport, codec);
                Assert.AreEqual("result", ackNode.GetAttr("type"));
                Assert.AreEqual("pd-live-1", ackNode.GetAttr("id"));

                // 1b. Send valid <pair-success> -> verify client replies <pair-device-sign>, then send 515!
                SendEncryptedNode(transport, codec, BuildPairSuccessStanza(storedBeforePair, "ps-1", "15550109999:5@s.whatsapp.net", "777666555:5@lid"));
                Node signResp = await ReadDecryptedNodeAsync(transport, codec);
                Assert.IsNotNull(signResp.GetChild("pair-device-sign"));

                SendEncryptedNode(transport, codec, new Node("stream:error", new Dictionary<string, NodeValue> { ["code"] = "515" }));
                await transport.DisconnectAsync();
            }
            else if (attempt == 2)
            {
                // --- Session 2: Must use IK 0-RTT resume! ---
                byte[] pkt1 = await transport.ReadClientChunkAsync();
                byte[] helloBytes = ExtractFrameAfterHeader(pkt1, Constants.WaConnHeader);
                var hello = global::Whatsapp.HandshakeMessage.Parser.ParseFrom(helloBytes).ClientHello;
                byte[] clientEph = hello.Ephemeral.ToByteArray();

                var serverNoise = new NoiseState("Noise_IK_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);
                serverNoise.Authenticate(serverStatic.PublicKey);
                serverNoise.Authenticate(clientEph);
                serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, clientEph));
                byte[] clientStatic = serverNoise.Decrypt(hello.Static.ToByteArray());
                serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, clientStatic));
                var loginPayload = ClientPayload.Parser.ParseFrom(serverNoise.Decrypt(hello.Payload.ToByteArray()));
                Assert.AreEqual(15550109999UL, loginPayload.Username);

                serverNoise.Authenticate(serverEphemeral.PublicKey);
                serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientEph));
                serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientStatic));
                byte[] encCert = serverNoise.Encrypt("ok"u8);

                NoiseKeys sKeys = serverNoise.Split();
                using var codec = new NoiseSessionCodec(new NoiseKeys(sKeys.ReadCipher, sKeys.WriteCipher));

                byte[] helloFrame = Framing.EncodeFrame(HandshakeMessage.EncodeServerHello(serverEphemeral.PublicKey, null, encCert));
                byte[] successFrame = EncryptNodeFrame(codec, new Node("success", new Dictionary<string, NodeValue> { ["lid"] = "777666555:5@lid" }));
                transport.EnqueueServerChunk([.. helloFrame, .. successFrame]);

                // Expect client <active/> and prekey count IQs; reply to both, then test ib/ping/offline/dirty
                bool activeSeen = false;
                bool offlineSent = false;
                bool cleanGroupsSeen = false;
                bool pingSent = false;
                bool pongSeen = false;
                bool prekeyUploadSeen = false;

                while (!(activeSeen && cleanGroupsSeen && pongSeen && prekeyUploadSeen))
                {
                    Node req = await ReadDecryptedNodeAsync(transport, codec);
                    debugState.Enqueue($"s2_rx:{req.Tag}:{req.GetAttr("xmlns")}:{req.GetAttr("id")}:{string.Join(",", (req.Content as NodeContent.NodesContent)?.Nodes.Select(n => n.Tag) ?? [])}");
                    if (req.GetChild("active") is not null)
                    {
                        activeSeen = true;
                        orderedColdBootLog.Enqueue("active");
                        SendEncryptedNode(transport, codec, new Node("iq", new Dictionary<string, NodeValue>
                        {
                            ["id"] = req.GetAttr("id")!,
                            ["type"] = "result"
                        }, new Node[] { new("active") }));

                        // Send edge_routing + offline_preview + dirty(groups) AFTER active!
                        SendEncryptedNode(transport, codec, new Node("ib", new Node[]
                        {
                            new("edge_routing", new Node[] { new("routing_info", new byte[] { 9, 8, 7 }) }),
                            new("offline_preview", new Dictionary<string, NodeValue> { ["count"] = "3", ["message"] = "3" }),
                            new("dirty", new Dictionary<string, NodeValue> { ["type"] = "groups", ["timestamp"] = "1700000100" })
                        }));

                        // Also send a server ping
                        pingSent = true;
                        SendEncryptedNode(transport, codec, new Node("iq", new Dictionary<string, NodeValue>
                        {
                            ["id"] = "srv-ping-1",
                            ["from"] = StanzaBuilder.ServerPn,
                            ["type"] = "get",
                            ["xmlns"] = "w:p"
                        }));
                    }
                    else if (req.GetAttr("xmlns") == "encrypt" && req.GetChild("count") is not null)
                    {
                        SendEncryptedNode(transport, codec, new Node("iq", new Dictionary<string, NodeValue>
                        {
                            ["id"] = req.GetAttr("id")!,
                            ["type"] = "result"
                        }, new Node[] { new("count", new Dictionary<string, NodeValue> { ["value"] = "1" }) }));
                    }
                    else if (req.GetAttr("xmlns") == "encrypt" && req.GetChild("list") is not null)
                    {
                        prekeyUploadSeen = true;
                        SendEncryptedNode(transport, codec, new Node("iq", new Dictionary<string, NodeValue>
                        {
                            ["id"] = req.GetAttr("id")!,
                            ["type"] = "result"
                        }));
                    }
                    else if (req.Tag == "ib" && req.GetChild("offline_batch") is not null && !offlineSent)
                    {
                        offlineSent = true;
                        orderedColdBootLog.Enqueue("offline_batch");
                        SendEncryptedNode(transport, codec, new Node("ib", new Node[]
                        {
                            new("offline", new Dictionary<string, NodeValue> { ["count"] = "3" })
                        }));
                        orderedColdBootLog.Enqueue("offline_done");
                    }
                    else if (req.GetAttr("xmlns") == DirtyBitsIq.Xmlns && req.GetChild("clean")?.GetAttr("type") == "groups")
                    {
                        cleanGroupsSeen = true;
                        orderedColdBootLog.Enqueue("clean_groups");
                        SendEncryptedNode(transport, codec, new Node("iq", new Dictionary<string, NodeValue>
                        {
                            ["id"] = req.GetAttr("id")!,
                            ["type"] = "result"
                        }));
                    }
                    else if (pingSent && req.GetAttr("id") == "srv-ping-1" && req.GetAttr("type") == "result")
                    {
                        pongSeen = true;
                    }
                }

                session2ColdBootDone.TrySetResult();
            }
            }
            catch (Exception ex)
            {
                session2ColdBootDone.TrySetException(new InvalidOperationException($"Attempt {attempt} failed: {ex}", ex));
            }
        });

        await using var orchestrator = new ConnectionOrchestrator(
            backend,
            transportFactory,
            new ConnectionOrchestratorOptions
            {
                InitialReconnectDelay = TimeSpan.FromMilliseconds(10),
                WantedPreKeyCount = 6,
                HandshakeOptions = new NoiseHandshakeOptions
                {
                    CertVerifierOverride = (chain, key) => NoiseCertVerifier.VerifyServerCertWithRoot(chain, key, root.PublicKey)
                }
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await orchestrator.StartAsync(cts.Token);

        try
        {
            await session2ColdBootDone.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            var drained = new List<string>();
            while (orchestrator.Events.TryRead(out var ev)) drained.Add(ev.ToString());
            Assert.Fail($"Timed out! sessions={sessionsObserved}; debug=[{string.Join(" | ", debugState)}]; trace=[{string.Join(" | ", orderedColdBootLog)}]; events=[{string.Join(" | ", drained)}]");
        }

        while (orchestrator.Events.TryRead(out var ev))
        {
            sessionEvents.Enqueue(ev);
        }

        Assert.AreEqual(2, Volatile.Read(ref sessionsObserved));
        Assert.IsTrue(sessionEvents.OfType<QrCodeEvent>().Any());
        Assert.IsTrue(sessionEvents.OfType<PairSuccessEvent>().Any());
        Assert.IsTrue(sessionEvents.OfType<ConnectedEvent>().Any());
        Assert.IsTrue(sessionEvents.OfType<OfflineSyncCompletedEvent>().Any(e => e.ProcessedCount == 3));

        // Verify clean_groups waited until after offline_batch -> offline_done!
        string[] trace = orderedColdBootLog.ToArray();
        int idxOffline = Array.IndexOf(trace, "offline_done");
        int idxCleanGroups = Array.IndexOf(trace, "clean_groups");
        Assert.IsTrue(idxOffline >= 0 && idxCleanGroups > idxOffline, $"Unexpected order: {string.Join(" -> ", trace)}");

        // Verify EdgeRoutingInfo and NextPreKeyId persisted to SQLite
        DeviceCredentials? finalCreds = await backend.Device.LoadAsync();
        Assert.IsNotNull(finalCreds);
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, finalCreds.EdgeRoutingInfo);
        Assert.AreEqual(7U, finalCreds.NextPreKeyId);

        await orchestrator.StopAsync();
    }

    [TestMethod]
    public async Task ConnectionOrchestrator_StreamError401_ClearsPairingAndStopsReconnect()
    {
        await using var backend = new SqliteBackend(":memory:");
        await backend.InitializeAsync();
        var root = Curve25519.GenerateKeyPair();
        var serverStatic = Curve25519.GenerateKeyPair();
        byte[] certChain = BuildSignedCertChain(root, serverStatic.PublicKey);

        DeviceCredentials seed = await backend.Device.LoadOrCreateAsync();
        seed.Pn = Jid.PnDevice("15550105555", 1);
        await backend.Device.SaveAsync(seed);

        int attempts = 0;
        var factory = new ScriptedTransportFactory(async (transport, attemptNum) =>
        {
            _ = attemptNum;
            Interlocked.Increment(ref attempts);
            var serverEph = Curve25519.GenerateKeyPair();

            byte[] pkt1 = await transport.ReadClientChunkAsync();
            byte[] helloBytes = ExtractFrameAfterHeader(pkt1, Constants.WaConnHeader);
            byte[] clientEph = global::Whatsapp.HandshakeMessage.Parser.ParseFrom(helloBytes).ClientHello.Ephemeral.ToByteArray();

            var serverNoise = new NoiseState("Noise_XX_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);
            serverNoise.Authenticate(clientEph);
            serverNoise.Authenticate(serverEph.PublicKey);
            serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEph.PrivateKey, clientEph));
            byte[] encStatic = serverNoise.Encrypt(serverStatic.PublicKey);
            serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, clientEph));
            byte[] encCert = serverNoise.Encrypt(certChain);
            transport.EnqueueServerChunk(Framing.EncodeFrame(HandshakeMessage.EncodeServerHello(serverEph.PublicKey, encStatic, encCert)));

            byte[] pkt2 = await transport.ReadClientChunkAsync();
            Framing.TryDecodeFrame(pkt2, out byte[] finishBytes, out _);
            var fin = global::Whatsapp.HandshakeMessage.Parser.ParseFrom(finishBytes).ClientFinish;
            byte[] clientStatic = serverNoise.Decrypt(fin.Static.ToByteArray());
            serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEph.PrivateKey, clientStatic));
            _ = serverNoise.Decrypt(fin.Payload.ToByteArray());

            NoiseKeys sKeys = serverNoise.Split();
            using var codec = new NoiseSessionCodec(new NoiseKeys(sKeys.ReadCipher, sKeys.WriteCipher));
            SendEncryptedNode(transport, codec, new Node("stream:error", new Dictionary<string, NodeValue> { ["code"] = "401" }));
        });

        await using var orchestrator = new ConnectionOrchestrator(
            backend,
            factory,
            new ConnectionOrchestratorOptions
            {
                InitialReconnectDelay = TimeSpan.FromMilliseconds(10),
                HandshakeOptions = new NoiseHandshakeOptions
                {
                    CertVerifierOverride = (chain, key) => NoiseCertVerifier.VerifyServerCertWithRoot(chain, key, root.PublicKey)
                }
            });

        var outcome = await orchestrator.RunSingleSessionAsync(CancellationToken.None);
        Assert.IsTrue(outcome.StopReconnect);
        Assert.AreEqual(1, attempts);

        DeviceCredentials? cleared = await backend.Device.LoadAsync();
        Assert.IsNotNull(cleared);
        Assert.IsNull(cleared.Pn);

        var events = new List<WhatsAppEvent>();
        while (orchestrator.Events.TryRead(out var e)) events.Add(e);
        Assert.IsTrue(events.OfType<LoggedOutEvent>().Any(e => e.ReasonCode == 401 && !e.OnConnect));
    }

    private static byte[] ExtractFrameAfterHeader(byte[] packet, byte[] header)
    {
        CollectionAssert.AreEqual(header, packet[..header.Length]);
        Assert.IsTrue(Framing.TryDecodeFrame(packet.AsSpan(header.Length), out byte[] frame, out _));
        return frame;
    }

    private static void SendEncryptedNode(MockDuplexTransport transport, NoiseSessionCodec codec, Node node) =>
        transport.EnqueueServerChunk(EncryptNodeFrame(codec, node));

    private static byte[] EncryptNodeFrame(NoiseSessionCodec codec, Node node)
    {
        var enc = new BinaryEncoder();
        enc.WriteNode(node);
        return codec.EncryptFrame(BinaryPacker.Pack(enc.ToArray()));
    }

    private static async Task<Node> ReadDecryptedNodeAsync(MockDuplexTransport transport, NoiseSessionCodec codec)
    {
        while (true)
        {
            if (codec.TryDecryptNextFrame(out byte[] packed))
            {
                return new BinaryDecoder(BinaryPacker.Unpack(packed)).ReadNode();
            }
            byte[] chunk = await transport.ReadClientChunkAsync();
            codec.Feed(chunk);
        }
    }

    private static Node BuildPairSuccessStanza(DeviceCredentials companion, string reqId, string jid, string lid)
    {
        var accountKp = Curve25519.GenerateKeyPair();
        byte[] details = new ADVDeviceIdentity
        {
            RawId = 1,
            Timestamp = 1700000000,
            KeyIndex = 5,
            AccountType = ADVEncryptionType.E2Ee,
            DeviceType = ADVEncryptionType.E2Ee
        }.ToByteArray();

        byte[] accSig = XEdDSA.Sign(accountKp.PrivateKey, [0x06, 0x00, .. details, .. companion.IdentityKey.PublicKey]);
        byte[] signedIdentity = new ADVSignedDeviceIdentity
        {
            Details = ByteString.CopyFrom(details),
            AccountSignatureKey = ByteString.CopyFrom(accountKp.PublicKey),
            AccountSignature = ByteString.CopyFrom(accSig)
        }.ToByteArray();

        byte[] hmac = HMACSHA256.HashData(companion.AdvSecretKey, signedIdentity);
        byte[] container = new ADVSignedDeviceIdentityHMAC
        {
            Details = ByteString.CopyFrom(signedIdentity),
            Hmac = ByteString.CopyFrom(hmac),
            AccountType = ADVEncryptionType.E2Ee
        }.ToByteArray();

        return new Node("iq", new Dictionary<string, NodeValue> { ["id"] = reqId, ["type"] = "set" }, new Node[]
        {
            new("pair-success", new Node[]
            {
                new("device-identity", container),
                new("device", new Dictionary<string, NodeValue> { ["jid"] = jid, ["lid"] = lid }),
                new("client-props", new ClientPairingProps { IsChatDbLidMigrated = true }.ToByteArray())
            })
        });
    }

    private static byte[] BuildSignedCertChain(KeyPair root, byte[] serverStaticPublic)
    {
        var intermediate = Curve25519.GenerateKeyPair();
        byte[] intermediateDetails = new CertChain.Types.NoiseCertificate.Types.Details
        {
            Serial = 10,
            IssuerSerial = NoiseCertVerifier.RootIssuerSerial,
            Key = ByteString.CopyFrom(intermediate.PublicKey),
            NotBefore = 1_700_000_000,
            NotAfter = 1_900_000_000
        }.ToByteArray();

        byte[] leafDetails = new CertChain.Types.NoiseCertificate.Types.Details
        {
            Serial = 11,
            IssuerSerial = 10,
            Key = ByteString.CopyFrom(serverStaticPublic),
            NotBefore = 1_700_000_000,
            NotAfter = 1_900_000_000
        }.ToByteArray();

        return new CertChain
        {
            Intermediate = new CertChain.Types.NoiseCertificate
            {
                Details = ByteString.CopyFrom(intermediateDetails),
                Signature = ByteString.CopyFrom(XEdDSA.Sign(root.PrivateKey, intermediateDetails))
            },
            Leaf = new CertChain.Types.NoiseCertificate
            {
                Details = ByteString.CopyFrom(leafDetails),
                Signature = ByteString.CopyFrom(XEdDSA.Sign(intermediate.PrivateKey, leafDetails))
            }
        }.ToByteArray();
    }
}
