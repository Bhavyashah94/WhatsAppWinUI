using System.Threading.Channels;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Noise;
using WhatsApp.Storage.Sqlite;
using WhatsApp.Transport;
using HandshakeMessage = WhatsApp.Protocol.Noise.HandshakeMessage;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class NoiseSessionSocketTests
{
    private sealed class ScriptedSessionTransport : ITransport
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>();
        private Memory<byte> _pendingRead;
        private int _closed;

        public bool FailNextSend { get; set; }
        public int SendCallCount { get; private set; }
        public bool IsConnected => Volatile.Read(ref _closed) == 0;

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                throw new InvalidOperationException("Transport is closed.");
            }
            SendCallCount++;
            if (FailNextSend)
            {
                return Task.FromException(new IOException("Injected socket send failure"));
            }
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
                if (_inbound.Reader.TryRead(out byte[]? chunk))
                {
                    _pendingRead = chunk;
                }
            }

            int copy = Math.Min(buffer.Length, _pendingRead.Length);
            _pendingRead.Span[..copy].CopyTo(buffer.Span);
            _pendingRead = _pendingRead[copy..];
            return copy;
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _inbound.Writer.TryComplete();
                _outbound.Writer.TryComplete();
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => new(DisconnectAsync());

        public ValueTask<byte[]> ReadOutboundWireAsync(CancellationToken ct = default) => _outbound.Reader.ReadAsync(ct);
        public void EnqueueInboundWire(byte[] bytes) => _inbound.Writer.TryWrite(bytes);
    }

    [TestMethod]
    public async Task NoiseSessionSocket_ConcurrentWritersSerializeCountersAndUnpackCoalescedInboundNodes()
    {
        byte[] c2sKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        byte[] s2cKey = Enumerable.Range(61, 32).Select(i => (byte)i).ToArray();

        using var serverCodec = new NoiseSessionCodec(new NoiseKeys(new NoiseCipher(s2cKey), new NoiseCipher(c2sKey)));
        byte[] coalescedSuccessWire = serverCodec.EncryptFrame(PackNode(new Node("success", new Dictionary<string, NodeValue>
        {
            ["location"] = "ash"
        })));

        await using var transport = new ScriptedSessionTransport();
        await using var socket = new NoiseSessionSocket(
            generation: 1,
            transport,
            new NoiseKeys(new NoiseCipher(c2sKey), new NoiseCipher(s2cKey)),
            initialRemainder: coalescedSuccessWire);

        // First node read from InboundNodes must be the coalesced <success/> frame without extra receives
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Node success = await socket.InboundNodes.ReadAsync(cts.Token);
        Assert.AreEqual("success", success.Tag);
        Assert.AreEqual("ash", success.GetAttr("location"));

        // Fire 24 concurrent stanza sends simultaneously
        const int messageCount = 24;
        Task[] sends = Enumerable.Range(0, messageCount)
            .Select(i => socket.SendNodeAsync(PingIq.BuildPing($"ping-{i}"), cts.Token))
            .ToArray();

        var decryptedByServer = new List<Node>(messageCount);
        while (decryptedByServer.Count < messageCount)
        {
            byte[] wire = await transport.ReadOutboundWireAsync(cts.Token);
            serverCodec.Feed(wire);
            while (serverCodec.TryDecryptNextFrame(out byte[] plain))
            {
                decryptedByServer.Add(UnpackNode(plain));
            }
        }

        await Task.WhenAll(sends);
        Assert.AreEqual(messageCount, decryptedByServer.Count);
        Assert.AreEqual((uint)messageCount, socket.WriteCounter);
        Assert.AreEqual((uint)messageCount, serverCodec.ReadCounter);
        CollectionAssert.AreEquivalent(
            Enumerable.Range(0, messageCount).Select(i => $"ping-{i}").ToArray(),
            decryptedByServer.Select(n => n.GetAttr("id")).ToArray());

        // Also verify inbound streaming node delivery after startup
        byte[] inboundDirty = serverCodec.EncryptFrame(PackNode(DirtyBitsIq.BuildClean("dirty-99", new DirtyBit(DirtyBitsIq.TypeGroups))));
        transport.EnqueueInboundWire(inboundDirty[..9]);
        transport.EnqueueInboundWire(inboundDirty[9..]);
        Node receivedDirty = await socket.InboundNodes.ReadAsync(cts.Token);
        Assert.AreEqual("dirty-99", receivedDirty.GetAttr("id"));
        Assert.AreEqual("groups", receivedDirty.GetChild("clean")?.GetAttr("type"));
    }

    [TestMethod]
    public async Task NoiseSessionSocket_FailedTransportWrite_PoisonsEncoderAndRejectsSubsequentSends()
    {
        byte[] key = Enumerable.Repeat((byte)0x33, 32).ToArray();
        await using var transport = new ScriptedSessionTransport();
        await using var socket = new NoiseSessionSocket(
            generation: 7,
            transport,
            new NoiseKeys(new NoiseCipher(key), new NoiseCipher(key)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendNodeAsync(PassiveIq.BuildActive("iq-ok"), cts.Token);
        _ = await transport.ReadOutboundWireAsync(cts.Token);

        transport.FailNextSend = true;
        await Assert.ThrowsExceptionAsync<IOException>(() => socket.SendNodeAsync(PingIq.BuildPing("iq-fail"), cts.Token));

        Assert.IsTrue(socket.IsWritePoisoned);
        Assert.IsFalse(socket.IsConnected);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            socket.SendNodeAsync(PingIq.BuildPing("iq-after-poison"), cts.Token));
    }

    [TestMethod]
    public async Task WhatsAppApiClient_ConnectionGeneration_IgnoresStaleGenerationDisconnect()
    {
        var root = Curve25519.GenerateKeyPair();
        var serverStatic = Curve25519.GenerateKeyPair();
        byte[] certChain = BuildSignedCertChain(root, serverStatic.PublicKey);

        int dialCount = 0;
        var factory = new WebSocketTransportFactory((_, _) =>
        {
            int attempt = Interlocked.Increment(ref dialCount);
            var t = new ScriptedSessionTransport();
            _ = RunMockXxServerOnceAsync(t, serverStatic, certChain, attempt);
            return Task.FromResult<ITransport>(t);
        });

        await using var backend = new SqliteBackend(":memory:");
        using var httpClient = new WhatsAppHttpClient();
        await using var api = new WhatsAppApiClient(backend, factory, httpClient);

        var creds = DeviceCredentials.CreateNew();
        var endpoints = WhatsAppEndpointBuilder.BuildDefaultEndpoints();
        var opts = new NoiseHandshakeOptions
        {
            CertVerifierOverride = (chain, staticKey) => NoiseCertVerifier.VerifyServerCertWithRoot(chain, staticKey, root.PublicKey)
        };

        // Connection Generation 1
        var session1 = await api.OpenSessionAsync(endpoints, creds, opts);
        Assert.AreEqual(1L, session1.Generation);
        Assert.AreEqual(1L, api.CurrentGeneration);

        // Reconnect upgrades to Connection Generation 2 and closes Gen 1
        var session2 = await api.OpenSessionAsync(endpoints, creds, opts);
        Assert.AreEqual(2L, session2.Generation);
        Assert.AreEqual(2L, api.CurrentGeneration);
        Assert.IsFalse(session1.IsConnected);
        Assert.IsTrue(session2.IsConnected);

        // Stale Gen 1 disconnect callback MUST be ignored and keep Gen 2 untouched
        bool closedStale = await api.DisconnectSessionAsync(expectedGeneration: 1);
        Assert.IsFalse(closedStale);
        Assert.AreSame(session2, api.ActiveSession);
        Assert.IsTrue(session2.IsConnected);

        // Matching Gen 2 disconnect closes cleanly
        bool closedCurrent = await api.DisconnectSessionAsync(expectedGeneration: 2);
        Assert.IsTrue(closedCurrent);
        Assert.IsNull(api.ActiveSession);
        Assert.IsFalse(session2.IsConnected);
    }

    private static async Task RunMockXxServerOnceAsync(
        ScriptedSessionTransport t,
        KeyPair serverStatic,
        byte[] certChain,
        int attempt)
    {
        try
        {
            byte[] packet1 = await t.ReadOutboundWireAsync();
            if (!Framing.TryDecodeFrame(packet1.AsSpan(Constants.WaConnHeader.Length), out byte[] helloBytes, out _))
                return;

            byte[] clientEph = global::Whatsapp.HandshakeMessage.Parser.ParseFrom(helloBytes).ClientHello.Ephemeral.ToByteArray();
            var serverEph = Curve25519.GenerateKeyPair();
            var noise = new NoiseState("Noise_XX_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);
            noise.Authenticate(clientEph);
            noise.Authenticate(serverEph.PublicKey);
            noise.MixKey(Curve25519.CalculateSharedSecret(serverEph.PrivateKey, clientEph));
            byte[] encStatic = noise.Encrypt(serverStatic.PublicKey);
            noise.MixKey(Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, clientEph));
            byte[] encCert = noise.Encrypt(certChain);

            t.EnqueueInboundWire(Framing.EncodeFrame(
                HandshakeMessage.EncodeServerHello(serverEph.PublicKey, encStatic, encCert)));

            _ = await t.ReadOutboundWireAsync(); // consume ClientFinish
        }
        catch
        {
            // Expected if test disposes transport after handshake completion.
        }
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

    private static byte[] PackNode(Node node)
    {
        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        return BinaryPacker.Pack(encoder.ToArray());
    }

    private static Node UnpackNode(byte[] packed)
    {
        byte[] unpacked = BinaryPacker.Unpack(packed);
        return new BinaryDecoder(unpacked).ReadNode();
    }
}
