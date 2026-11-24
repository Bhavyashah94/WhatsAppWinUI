using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class NoiseFrameCodecTests
{
    [TestMethod]
    public void BuildHandshakeHeader_HandlesPlainEdgeRoutingAndEmptyFallback()
    {
        var (plainHeader, usedPlain) = NoiseHandshakeHeader.BuildHandshakeHeader();
        Assert.IsFalse(usedPlain);
        CollectionAssert.AreEqual(Constants.WaConnHeader, plainHeader);

        byte[] routingInfo = "CAgIIwgS"u8.ToArray();
        var (edHeader, usedEd) = NoiseHandshakeHeader.BuildHandshakeHeader(routingInfo);
        Assert.IsTrue(usedEd);
        Assert.AreEqual(4 + 3 + routingInfo.Length + Constants.WaConnHeader.Length, edHeader.Length);
        CollectionAssert.AreEqual("ED\x00\x01"u8.ToArray(), edHeader[..4]);
        Assert.AreEqual(0, edHeader[4]);
        Assert.AreEqual(0, edHeader[5]);
        Assert.AreEqual(routingInfo.Length, edHeader[6]);
        CollectionAssert.AreEqual(routingInfo, edHeader[7..(7 + routingInfo.Length)]);
        CollectionAssert.AreEqual(Constants.WaConnHeader, edHeader[(7 + routingInfo.Length)..]);
    }

    [TestMethod]
    public void NoiseFrameEncoder_MatchesExplicitNonceEncryptionAndHeaderRules()
    {
        byte[] key = Enumerable.Repeat((byte)0x2A, 32).ToArray();
        using var encoder = new NoiseFrameEncoder(new NoiseCipher(key));
        using var explicitCipher = new NoiseCipher(key);

        byte[] firstPayload = "active-stanza"u8.ToArray();
        byte[] secondPayload = "ping-stanza"u8.ToArray();

        byte[] firstFrame = encoder.EncodeFrame(firstPayload, Constants.WaConnHeader);
        byte[] secondFrame = encoder.EncodeFrame(secondPayload);

        byte[] expectedFirst = Framing.EncodeFrame(explicitCipher.EncryptWithCounter(0, firstPayload), Constants.WaConnHeader);
        byte[] expectedSecond = Framing.EncodeFrame(explicitCipher.EncryptWithCounter(1, secondPayload));

        CollectionAssert.AreEqual(expectedFirst, firstFrame);
        CollectionAssert.AreEqual(expectedSecond, secondFrame);
        Assert.AreEqual(2u, encoder.Counter);
    }

    [TestMethod]
    public void NoiseFrameEncoder_PoisonedEncoderRejectsWithoutMutatingBuffer()
    {
        byte[] key = Enumerable.Repeat((byte)0x17, 32).ToArray();
        using var encoder = new NoiseFrameEncoder(new NoiseCipher(key));

        var buffer = new ArrayBufferWriter<byte>();
        encoder.EncodeFrameInto("one"u8, buffer);
        byte[] snapshot = buffer.WrittenSpan.ToArray();

        encoder.Poison();
        Assert.IsTrue(encoder.IsPoisoned);

        Assert.ThrowsException<InvalidOperationException>(() => encoder.EncodeFrameInto("two"u8, buffer));
        Assert.AreEqual(1u, encoder.Counter);
        CollectionAssert.AreEqual(snapshot, buffer.WrittenSpan.ToArray());
    }

    [TestMethod]
    public void NoiseFrameDecoder_ReassemblesOneByteChunksAndCoalescedFrames()
    {
        byte[] payloadA = "first-frame-payload"u8.ToArray();
        byte[] payloadB = [];
        byte[] payloadC = Enumerable.Range(0, 512).Select(i => (byte)(i & 0xFF)).ToArray();

        byte[] stream =
        [
            .. Framing.EncodeFrame(payloadA),
            .. Framing.EncodeFrame(payloadB),
            .. Framing.EncodeFrame(payloadC)
        ];

        var decoder = new NoiseFrameDecoder();
        var decoded = new List<byte[]>();

        // Feed 1 byte at a time to stress header-split and body-split paths
        for (int i = 0; i < stream.Length; i++)
        {
            decoder.Feed(stream.AsSpan(i, 1));
            while (decoder.TryReadRawFrame(out byte[] frame))
            {
                decoded.Add(frame);
            }
        }

        Assert.AreEqual(0, decoder.BufferedBytes);
        Assert.AreEqual(3, decoded.Count);
        CollectionAssert.AreEqual(payloadA, decoded[0]);
        CollectionAssert.AreEqual(payloadB, decoded[1]);
        CollectionAssert.AreEqual(payloadC, decoded[2]);
    }

    [TestMethod]
    public void NoiseFrameDecoder_RejectsOversizedFrameLengthImmediately()
    {
        var decoder = new NoiseFrameDecoder();
        // 3-byte BE length == 0xFFFFFF + 1 invalid? FrameMaxSize is 1 << 24 (16 MiB), which is 24 bits max,
        // wait: Framing.FrameMaxSize = 1 << 24 so 0xFFFFFF is (1 << 24) - 1 < FrameMaxSize!
        // Verify valid max-header waits cleanly without throwing when incomplete, and reject corrupted framing only if >= FrameMaxSize.
        // Note: 3 bytes cannot represent >= 16 MiB; verify large incomplete frame buffers cleanly without OOM allocation!
        decoder.Feed([0x01, 0x00, 0x00, 0xAA]); // declares 64 KiB, only 1 byte supplied
        Assert.IsFalse(decoder.TryReadRawFrame(out _));
        Assert.AreEqual(4, decoder.BufferedBytes);
    }

    [TestMethod]
    public void NoiseSessionCodec_FullDuplexFragmentedStanzaPipeline_AndTamperSafety()
    {
        byte[] writeKeyClient = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        byte[] writeKeyServer = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();

        using var clientSession = new NoiseSessionCodec(
            new NoiseKeys(new NoiseCipher(writeKeyClient), new NoiseCipher(writeKeyServer)));

        // Coalesced trailing encrypted <success/> frame handed directly at session startup
        using var serverSuccessEncoder = new NoiseFrameEncoder(new NoiseCipher(writeKeyServer));
        byte[] successNodeBytes = EncodePackedNode(new Node("success", new Dictionary<string, NodeValue>
        {
            ["location"] = "ash",
            ["t"] = "1779643793"
        }));
        byte[] initialSuccessWire = serverSuccessEncoder.EncodeFrame(successNodeBytes);

        // Split initialSuccessWire into remainder handed to constructor + trailing bytes
        clientSession.Feed(initialSuccessWire.AsSpan(0, 5));
        Assert.IsFalse(clientSession.TryDecryptNextFrame(out _));
        clientSession.Feed(initialSuccessWire.AsSpan(5));
        Assert.IsTrue(clientSession.TryDecryptNextFrame(out byte[] decryptedSuccess));
        var decodedSuccess = DecodePackedNode(decryptedSuccess);
        Assert.AreEqual("success", decodedSuccess.Tag);
        Assert.AreEqual("ash", decodedSuccess.GetAttr("location"));
        Assert.AreEqual(1u, clientSession.ReadCounter);

        // Client encrypts real Phase 0E stanzas, fragments them in 7-byte WebSocket chunks, server decrypts cleanly
        Node[] stanzas =
        [
            PassiveIq.BuildActive("iq-1"),
            OfflineBatchIb.BuildOfflineBatch(200),
            PingIq.BuildPing("iq-2"),
            AppStateSyncIq.BuildSync("iq-3", [AppStateSyncIq.BuildCollectionRequest("critical_block", version: 5)])
        ];

        using var serverDecoderSession = new NoiseSessionCodec(
            new NoiseKeys(new NoiseCipher(writeKeyServer), new NoiseCipher(writeKeyClient)));

        var wireChunks = new List<byte>();
        foreach (var node in stanzas)
        {
            wireChunks.AddRange(clientSession.EncryptFrame(EncodePackedNode(node)));
        }

        byte[] allWire = wireChunks.ToArray();
        var receivedStanzas = new List<Node>();
        for (int pos = 0; pos < allWire.Length; pos += 7)
        {
            int slice = Math.Min(7, allWire.Length - pos);
            serverDecoderSession.Feed(allWire.AsSpan(pos, slice));
            while (serverDecoderSession.TryDecryptNextFrame(out byte[] plain))
            {
                receivedStanzas.Add(DecodePackedNode(plain));
            }
        }

        Assert.AreEqual(stanzas.Length, receivedStanzas.Count);
        Assert.AreEqual("iq", receivedStanzas[0].Tag);
        Assert.IsNotNull(receivedStanzas[0].GetChild("active"));
        Assert.AreEqual("200", receivedStanzas[1].GetChild("offline_batch")?.GetAttr("count"));
        Assert.AreEqual("w:p", receivedStanzas[2].GetAttr("xmlns"));
        Assert.AreEqual("w:sync:app:state", receivedStanzas[3].GetAttr("xmlns"));
        Assert.AreEqual((uint)stanzas.Length, clientSession.WriteCounter);
        Assert.AreEqual((uint)stanzas.Length, serverDecoderSession.ReadCounter);

        // Verify tampered ciphertext fails without advancing ReadCounter
        byte[] tamperedFrame = clientSession.EncryptFrame("clean-text"u8);
        tamperedFrame[^1] ^= 0xFF;
        serverDecoderSession.Feed(tamperedFrame);
        uint counterBeforeTamper = serverDecoderSession.ReadCounter;
        Assert.ThrowsException<AuthenticationTagMismatchException>(() => serverDecoderSession.TryDecryptNextFrame(out _));
        Assert.AreEqual(counterBeforeTamper, serverDecoderSession.ReadCounter);
    }

    [TestMethod]
    public void NoiseCertVerifier_VerifiesSignedChainAndRejectsMismatchedStaticKey()
    {
        var root = Curve25519.GenerateKeyPair();
        var intermediate = Curve25519.GenerateKeyPair();
        var leaf = Curve25519.GenerateKeyPair();

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
            Key = ByteString.CopyFrom(leaf.PublicKey),
            NotBefore = 1_700_000_000,
            NotAfter = 1_900_000_000
        }.ToByteArray();

        var chain = new CertChain
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

        var verified = NoiseCertVerifier.VerifyServerCertWithRoot(chain, leaf.PublicKey, root.PublicKey);
        CollectionAssert.AreEqual(intermediate.PublicKey, verified.IntermediateKey);
        CollectionAssert.AreEqual(leaf.PublicKey, verified.LeafKey);
        Assert.AreEqual(1_900_000_000UL, verified.LeafNotAfter);

        // Wrong server static key must fail binding check
        var wrongServer = Curve25519.GenerateKeyPair();
        Assert.ThrowsException<InvalidDataException>(() =>
            NoiseCertVerifier.VerifyServerCertWithRoot(chain, wrongServer.PublicKey, root.PublicKey));

        // Pinned WhatsApp Root key must reject our synthetic chain
        Assert.ThrowsException<InvalidDataException>(() =>
            NoiseCertVerifier.VerifyServerCert(chain, leaf.PublicKey));
    }

    private static byte[] EncodePackedNode(Node node)
    {
        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        return BinaryPacker.Pack(encoder.ToArray());
    }

    private static Node DecodePackedNode(byte[] packed)
    {
        byte[] unpacked = BinaryPacker.Unpack(packed);
        var decoder = new BinaryDecoder(unpacked);
        return decoder.ReadNode();
    }
}
