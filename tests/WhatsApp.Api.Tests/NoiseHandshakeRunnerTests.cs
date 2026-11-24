using System.Threading.Channels;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Noise;
using HandshakeMessage = WhatsApp.Protocol.Noise.HandshakeMessage;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class NoiseHandshakeRunnerTests
{
    private sealed class MockDuplexTransport : ITransport
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>();
        private Memory<byte> _pendingRead;
        private bool _closed;

        public bool IsConnected => !_closed;

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (_closed) throw new InvalidOperationException("Transport closed");
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
            _closed = true;
            _inbound.Writer.TryComplete();
            _outbound.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => new(DisconnectAsync());

        // Test-side server helpers
        public ValueTask<byte[]> ReadClientChunkAsync(CancellationToken ct = default) => _outbound.Reader.ReadAsync(ct);
        public void EnqueueServerChunk(byte[] bytes) => _inbound.Writer.TryWrite(bytes);
        public void CompleteServerStream() => _inbound.Writer.TryComplete();
    }

    [TestMethod]
    public void SelectPattern_FollowsRegistrationAndServerStaticKeyAvailability()
    {
        var fresh = DeviceCredentials.CreateNew();
        Assert.AreEqual(NoiseHandshakePattern.Xx, NoiseHandshakeRunner.SelectPattern(fresh));

        var registeredNoStatic = DeviceCredentials.CreateNew();
        registeredNoStatic.Pn = Jid.PnDevice("15550101001", 1);
        Assert.AreEqual(NoiseHandshakePattern.Xx, NoiseHandshakeRunner.SelectPattern(registeredNoStatic));

        var warm = DeviceCredentials.CreateNew();
        warm.Pn = Jid.PnDevice("15550101001", 1);
        warm.ServerStaticPublic = Curve25519.GenerateKeyPair().PublicKey;
        Assert.AreEqual(NoiseHandshakePattern.Ik, NoiseHandshakeRunner.SelectPattern(warm));
        Assert.AreEqual(NoiseHandshakePattern.Xx, NoiseHandshakeRunner.SelectPattern(warm, forceFullHandshake: true));
    }

    [TestMethod]
    public async Task RunAsync_XxHandshake_VerifiesEdPreIntroCertChainAndBidirectionalKeys()
    {
        var root = Curve25519.GenerateKeyPair();
        var serverStatic = Curve25519.GenerateKeyPair();
        var serverEphemeral = Curve25519.GenerateKeyPair();
        byte[] certChain = BuildSignedCertChain(root, serverStatic.PublicKey);

        var creds = DeviceCredentials.CreateNew();
        creds.EdgeRoutingInfo = "CAgIIwgS"u8.ToArray();

        await using var transport = new MockDuplexTransport();
        var options = new NoiseHandshakeOptions
        {
            CertVerifierOverride = (chain, staticKey) => NoiseCertVerifier.VerifyServerCertWithRoot(chain, staticKey, root.PublicKey)
        };

        var clientTask = NoiseHandshakeRunner.RunAsync(transport, creds, options);

        // 1. Read client's first wire packet: [ED pre-intro + WA\x06\x03] + 3B len + ClientHello
        byte[] packet1 = await transport.ReadClientChunkAsync();
        var (expectedPreamble, _) = NoiseHandshakeHeader.BuildHandshakeHeader(creds.EdgeRoutingInfo);
        CollectionAssert.AreEqual(expectedPreamble, packet1[..expectedPreamble.Length]);

        Assert.IsTrue(Framing.TryDecodeFrame(packet1.AsSpan(expectedPreamble.Length), out byte[] clientHelloBytes, out _));
        var clientHelloPb = global::Whatsapp.HandshakeMessage.Parser.ParseFrom(clientHelloBytes);
        byte[] clientEphPub = clientHelloPb.ClientHello.Ephemeral.ToByteArray();

        // 2. Simulate WhatsApp server XX ServerHello (Noise prologue is ALWAYS Constants.WaConnHeader, never ED pre-intro)
        var serverNoise = new NoiseState("Noise_XX_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);
        serverNoise.Authenticate(clientEphPub);
        serverNoise.Authenticate(serverEphemeral.PublicKey);
        serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientEphPub));
        byte[] encryptedServerStatic = serverNoise.Encrypt(serverStatic.PublicKey);
        serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, clientEphPub));
        byte[] encryptedCert = serverNoise.Encrypt(certChain);

        byte[] serverHelloBytes = HandshakeMessage.EncodeServerHello(serverEphemeral.PublicKey, encryptedServerStatic, encryptedCert);
        byte[] serverHelloFrame = Framing.EncodeFrame(serverHelloBytes);
        // Feed ServerHello fragmented into two chunks to verify incremental assembly
        transport.EnqueueServerChunk(serverHelloFrame[..17]);
        transport.EnqueueServerChunk(serverHelloFrame[17..]);

        // 3. Read ClientFinish (no preamble header) and verify Noise payload + bidirectional transport encryption
        byte[] packet2 = await transport.ReadClientChunkAsync();
        Assert.IsTrue(Framing.TryDecodeFrame(packet2, out byte[] clientFinishBytes, out int consumed));
        Assert.AreEqual(packet2.Length, consumed);

        var result = await clientTask;
        Assert.AreEqual(NoiseHandshakePattern.Xx, result.PatternSelected);
        Assert.IsFalse(result.FallbackTaken);
        CollectionAssert.AreEqual(serverStatic.PublicKey, result.ServerStaticPublic);
        Assert.IsNotNull(result.ServerCertChain);

        var finishPb = global::Whatsapp.HandshakeMessage.Parser.ParseFrom(clientFinishBytes);
        byte[] decryptedClientStatic = serverNoise.Decrypt(finishPb.ClientFinish.Static.ToByteArray());
        CollectionAssert.AreEqual(creds.NoiseKey.PublicKey, decryptedClientStatic);
        serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, decryptedClientStatic));
        byte[] decryptedPayload = serverNoise.Decrypt(finishPb.ClientFinish.Payload.ToByteArray());
        var parsedPayload = global::Whatsapp.ClientPayload.Parser.ParseFrom(decryptedPayload);
        Assert.AreEqual(creds.PushName, parsedPayload.PushName);

        // Remember server split keys are (serverWrite = ReadKey on client, serverRead = WriteKey on client)
        NoiseKeys serverKeys = serverNoise.Split();
        using var serverSession = new NoiseSessionCodec(new NoiseKeys(serverKeys.ReadCipher, serverKeys.WriteCipher));
        using var clientSession = new NoiseSessionCodec(result.Keys, result.RemainingWireBytes);

        byte[] wireFromClient = clientSession.EncryptFrame("hello-xx"u8);
        serverSession.Feed(wireFromClient);
        Assert.IsTrue(serverSession.TryDecryptNextFrame(out byte[] serverGot));
        CollectionAssert.AreEqual("hello-xx"u8.ToArray(), serverGot);
    }

    [TestMethod]
    public async Task RunAsync_IkResume_HandoffsCoalescedSuccessFrameCleanly()
    {
        var serverStatic = Curve25519.GenerateKeyPair();
        var serverEphemeral = Curve25519.GenerateKeyPair();
        var creds = DeviceCredentials.CreateNew();
        creds.Pn = Jid.PnDevice("15550101001", 1);
        creds.ServerStaticPublic = serverStatic.PublicKey;

        await using var transport = new MockDuplexTransport();
        var clientTask = NoiseHandshakeRunner.RunAsync(transport, creds);

        byte[] packet1 = await transport.ReadClientChunkAsync();
        Assert.IsTrue(Framing.TryDecodeFrame(packet1.AsSpan(Constants.WaConnHeader.Length), out byte[] helloBytes, out _));
        var helloPb = global::Whatsapp.HandshakeMessage.Parser.ParseFrom(helloBytes);
        byte[] clientEphPub = helloPb.ClientHello.Ephemeral.ToByteArray();

        // Server IK state
        var serverNoise = new NoiseState("Noise_IK_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);
        serverNoise.Authenticate(serverStatic.PublicKey);
        serverNoise.Authenticate(clientEphPub);
        serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, clientEphPub));
        byte[] clientStaticPub = serverNoise.Decrypt(helloPb.ClientHello.Static.ToByteArray());
        CollectionAssert.AreEqual(creds.NoiseKey.PublicKey, clientStaticPub);
        serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, clientStaticPub));
        byte[] decryptedLoginPayload = serverNoise.Decrypt(helloPb.ClientHello.Payload.ToByteArray());
        var loginPb = global::Whatsapp.ClientPayload.Parser.ParseFrom(decryptedLoginPayload);
        Assert.AreEqual(15550101001UL, loginPb.Username);

        // Server replies with IK ServerHello (no Static) + coalesced encrypted <success/> frame in the SAME packet!
        serverNoise.Authenticate(serverEphemeral.PublicKey);
        serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientEphPub));
        serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientStaticPub));
        byte[] encCert = serverNoise.Encrypt("ik-cert-ok"u8);

        NoiseKeys serverKeys = serverNoise.Split();
        using var serverSession = new NoiseSessionCodec(new NoiseKeys(serverKeys.ReadCipher, serverKeys.WriteCipher));

        byte[] serverHelloFrame = Framing.EncodeFrame(HandshakeMessage.EncodeServerHello(serverEphemeral.PublicKey, null, encCert));
        byte[] coalescedPostHandshake = serverSession.EncryptFrame("coalesced-success"u8);

        transport.EnqueueServerChunk([.. serverHelloFrame, .. coalescedPostHandshake]);

        var result = await clientTask;
        Assert.AreEqual(NoiseHandshakePattern.Ik, result.PatternSelected);
        Assert.IsFalse(result.FallbackTaken);
        Assert.IsNull(result.ServerCertChain);
        Assert.IsTrue(result.RemainingWireBytes.Length > 0);

        using var clientSession = new NoiseSessionCodec(result.Keys, result.RemainingWireBytes);
        Assert.IsTrue(clientSession.TryDecryptNextFrame(out byte[] decryptedCoalesced));
        CollectionAssert.AreEqual("coalesced-success"u8.ToArray(), decryptedCoalesced);
    }

    [TestMethod]
    public async Task RunAsync_IkRejectedByServer_AutomaticallyDowngradesToXxFallback()
    {
        var root = Curve25519.GenerateKeyPair();
        var staleServerStatic = Curve25519.GenerateKeyPair();
        var actualServerStatic = Curve25519.GenerateKeyPair();
        var serverEphemeral = Curve25519.GenerateKeyPair();
        byte[] certChain = BuildSignedCertChain(root, actualServerStatic.PublicKey);

        var creds = DeviceCredentials.CreateNew();
        creds.Pn = Jid.PnDevice("15550101001", 1);
        creds.ServerStaticPublic = staleServerStatic.PublicKey;

        await using var transport = new MockDuplexTransport();
        var options = new NoiseHandshakeOptions
        {
            CertVerifierOverride = (chain, staticKey) => NoiseCertVerifier.VerifyServerCertWithRoot(chain, staticKey, root.PublicKey)
        };

        var clientTask = NoiseHandshakeRunner.RunAsync(transport, creds, options);

        byte[] packet1 = await transport.ReadClientChunkAsync();
        Assert.IsTrue(Framing.TryDecodeFrame(packet1.AsSpan(Constants.WaConnHeader.Length), out byte[] ikHelloBytes, out _));
        var ikHelloPb = global::Whatsapp.HandshakeMessage.Parser.ParseFrom(ikHelloBytes);
        byte[] clientEphPub = ikHelloPb.ClientHello.Ephemeral.ToByteArray();

        // Server cannot decrypt IK static (rotated key) -> initiates XXfallback with its new static key!
        var serverNoise = new NoiseState("Noise_XXfallback_25519_AESGCM_SHA256", Constants.WaConnHeader);
        serverNoise.Authenticate(clientEphPub);
        serverNoise.Authenticate(serverEphemeral.PublicKey);
        serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientEphPub));
        byte[] encNewStatic = serverNoise.Encrypt(actualServerStatic.PublicKey);
        serverNoise.MixKey(Curve25519.CalculateSharedSecret(actualServerStatic.PrivateKey, clientEphPub));
        byte[] encCert = serverNoise.Encrypt(certChain);

        transport.EnqueueServerChunk(Framing.EncodeFrame(
            HandshakeMessage.EncodeServerHello(serverEphemeral.PublicKey, encNewStatic, encCert)));

        byte[] packet2 = await transport.ReadClientChunkAsync();
        Assert.IsTrue(Framing.TryDecodeFrame(packet2, out byte[] finishBytes, out _));
        var finishPb = global::Whatsapp.HandshakeMessage.Parser.ParseFrom(finishBytes);

        var result = await clientTask;
        Assert.AreEqual(NoiseHandshakePattern.Ik, result.PatternSelected);
        Assert.IsTrue(result.FallbackTaken);
        CollectionAssert.AreEqual(actualServerStatic.PublicKey, result.ServerStaticPublic);
        Assert.IsNotNull(result.ServerCertChain);

        byte[] clientStatic = serverNoise.Decrypt(finishPb.ClientFinish.Static.ToByteArray());
        CollectionAssert.AreEqual(creds.NoiseKey.PublicKey, clientStatic);
        serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientStatic));
        byte[] loginBytes = serverNoise.Decrypt(finishPb.ClientFinish.Payload.ToByteArray());
        Assert.AreEqual(15550101001UL, global::Whatsapp.ClientPayload.Parser.ParseFrom(loginBytes).Username);

        NoiseKeys serverKeys = serverNoise.Split();
        using var serverSession = new NoiseSessionCodec(new NoiseKeys(serverKeys.ReadCipher, serverKeys.WriteCipher));
        using var clientSession = new NoiseSessionCodec(result.Keys);

        serverSession.Feed(clientSession.EncryptFrame("after-fallback"u8));
        Assert.IsTrue(serverSession.TryDecryptNextFrame(out byte[] serverMsg));
        CollectionAssert.AreEqual("after-fallback"u8.ToArray(), serverMsg);
    }

    [TestMethod]
    public async Task RunAsync_TimeoutAndPrematureClose_ThrowExpectedExceptions()
    {
        var creds = DeviceCredentials.CreateNew();

        await using (var stallingTransport = new MockDuplexTransport())
        {
            var fastTimeout = new NoiseHandshakeOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(50) };
            await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
                NoiseHandshakeRunner.RunAsync(stallingTransport, creds, fastTimeout));
        }

        await using (var closingTransport = new MockDuplexTransport())
        {
            var runTask = NoiseHandshakeRunner.RunAsync(closingTransport, creds);
            _ = await closingTransport.ReadClientChunkAsync();
            closingTransport.CompleteServerStream();

            await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => runTask);
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
}
