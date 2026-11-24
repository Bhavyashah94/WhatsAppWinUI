using System.Text;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public class NoiseTests
{
    [TestMethod]
    public void TestIvGeneration()
    {
        byte[] iv0 = NoiseCipher.GenerateIv(0);
        CollectionAssert.AreEqual(new byte[12], iv0);

        byte[] iv1 = NoiseCipher.GenerateIv(1);
        byte[] expected1 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1];
        CollectionAssert.AreEqual(expected1, iv1);

        byte[] iv256 = NoiseCipher.GenerateIv(256);
        byte[] expected256 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0];
        CollectionAssert.AreEqual(expected256, iv256);
    }

    [TestMethod]
    public void TestNoiseCipherRoundtrip()
    {
        byte[] key = new byte[32];
        Random.Shared.NextBytes(key);

        using var cipher = new NoiseCipher(key);
        byte[] plaintext = Encoding.UTF8.GetBytes("Hello WhatsApp Encrypted Message");
        byte[] aad = Encoding.UTF8.GetBytes("AssociatedData");

        byte[] ciphertext = cipher.EncryptWithCounter(0, plaintext, aad);
        Assert.AreEqual(plaintext.Length + 16, ciphertext.Length);

        byte[] decrypted = cipher.DecryptWithCounter(0, ciphertext, aad);
        CollectionAssert.AreEqual(plaintext, decrypted);
    }

    [TestMethod]
    public void TestFramingRoundtrip()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        byte[] header = Constants.WaConnHeader;

        byte[] frame = Framing.EncodeFrame(payload, header);
        // Header (4) + Length prefix (3) + Payload (10) = 17 bytes
        Assert.AreEqual(17, frame.Length);

        // Header check
        CollectionAssert.AreEqual(header, frame[..4]);

        // Decode frame after header
        bool success = Framing.TryDecodeFrame(frame.AsSpan(4), out byte[] decodedPayload, out int consumed);
        Assert.IsTrue(success);
        Assert.AreEqual(13, consumed);
        CollectionAssert.AreEqual(payload, decodedPayload);
    }

    [TestMethod]
    public void TestXxHandshakeRoundtripSimulation()
    {
        // Simulate a complete Client <-> Server Noise XX handshake in-memory!
        var clientStatic = Curve25519.GenerateKeyPair();
        var clientEphemeral = Curve25519.GenerateKeyPair();
        byte[] clientPayload = Encoding.UTF8.GetBytes("client-login-payload");

        // 1. Client creates Handshake and builds ClientHello
        var clientHandshake = new XxHandshake(clientStatic, clientPayload, clientEphemeral);
        byte[] clientHelloBytes = clientHandshake.BuildClientHello();

        // 2. Server receives ClientHello
        var clientHelloInput = new Google.Protobuf.CodedInputStream(clientHelloBytes);
        byte[]? clientEphemeralReceived = null;
        while (!clientHelloInput.IsAtEnd)
        {
            uint tag = clientHelloInput.ReadTag();
            if (Google.Protobuf.WireFormat.GetTagFieldNumber(tag) == 2)
            {
                byte[] bytes = clientHelloInput.ReadBytes().ToByteArray();
                var subInput = new Google.Protobuf.CodedInputStream(bytes);
                while (!subInput.IsAtEnd)
                {
                    uint subTag = subInput.ReadTag();
                    if (Google.Protobuf.WireFormat.GetTagFieldNumber(subTag) == 1)
                    {
                        clientEphemeralReceived = subInput.ReadBytes().ToByteArray();
                    }
                    else subInput.SkipLastField();
                }
            }
            else clientHelloInput.SkipLastField();
        }
        Assert.IsNotNull(clientEphemeralReceived);
        CollectionAssert.AreEqual(clientEphemeral.PublicKey, clientEphemeralReceived);

        // 3. Server simulates ServerHello response
        var serverStatic = Curve25519.GenerateKeyPair();
        var serverEphemeral = Curve25519.GenerateKeyPair();

        var serverNoise = new NoiseState("Noise_XX_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);
        serverNoise.Authenticate(clientEphemeralReceived);
        serverNoise.Authenticate(serverEphemeral.PublicKey);

        byte[] eeSecret = Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientEphemeralReceived);
        serverNoise.MixKey(eeSecret);

        byte[] serverStaticCiphertext = serverNoise.Encrypt(serverStatic.PublicKey);

        byte[] esSecret = Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, clientEphemeralReceived);
        serverNoise.MixKey(esSecret);

        byte[] certPlaintext = Encoding.UTF8.GetBytes("dummy-certificate-chain");
        byte[] certCiphertext = serverNoise.Encrypt(certPlaintext);

        // Build ServerHello protobuf
        using var stream = new MemoryStream();
        using var output = new Google.Protobuf.CodedOutputStream(stream);
        using var helloStream = new MemoryStream();
        using (var helloOutput = new Google.Protobuf.CodedOutputStream(helloStream))
        {
            helloOutput.WriteTag(1, Google.Protobuf.WireFormat.WireType.LengthDelimited);
            helloOutput.WriteBytes(Google.Protobuf.ByteString.CopyFrom(serverEphemeral.PublicKey));

            helloOutput.WriteTag(2, Google.Protobuf.WireFormat.WireType.LengthDelimited);
            helloOutput.WriteBytes(Google.Protobuf.ByteString.CopyFrom(serverStaticCiphertext));

            helloOutput.WriteTag(3, Google.Protobuf.WireFormat.WireType.LengthDelimited);
            helloOutput.WriteBytes(Google.Protobuf.ByteString.CopyFrom(certCiphertext));
            helloOutput.Flush();
        }
        byte[] serverHelloBytes = helloStream.ToArray();
        output.WriteTag(3, Google.Protobuf.WireFormat.WireType.LengthDelimited);
        output.WriteBytes(Google.Protobuf.ByteString.CopyFrom(serverHelloBytes));
        output.Flush();
        byte[] serverHelloMessage = stream.ToArray();

        // 4. Client processes ServerHello
        var (clientFinishBytes, clientKeys, serverStaticPubDecrypted) = clientHandshake.ProcessServerHello(serverHelloMessage);
        CollectionAssert.AreEqual(serverStatic.PublicKey, serverStaticPubDecrypted);

        // 5. Server processes ClientFinish
        var serverFinish = HandshakeMessage.ParseServerHello(clientFinishBytes); // re-use field parsing logic
        // Verify server decrypts client static and payload
        var clientFinishInput = new Google.Protobuf.CodedInputStream(clientFinishBytes);
        byte[]? clientStaticCiphertextRecv = null;
        byte[]? clientPayloadCiphertextRecv = null;
        while (!clientFinishInput.IsAtEnd)
        {
            uint tag = clientFinishInput.ReadTag();
            if (Google.Protobuf.WireFormat.GetTagFieldNumber(tag) == 4)
            {
                byte[] bytes = clientFinishInput.ReadBytes().ToByteArray();
                var sub = new Google.Protobuf.CodedInputStream(bytes);
                while (!sub.IsAtEnd)
                {
                    uint subTag = sub.ReadTag();
                    int field = Google.Protobuf.WireFormat.GetTagFieldNumber(subTag);
                    if (field == 1) clientStaticCiphertextRecv = sub.ReadBytes().ToByteArray();
                    else if (field == 2) clientPayloadCiphertextRecv = sub.ReadBytes().ToByteArray();
                    else sub.SkipLastField();
                }
            }
            else clientFinishInput.SkipLastField();
        }

        Assert.IsNotNull(clientStaticCiphertextRecv);
        Assert.IsNotNull(clientPayloadCiphertextRecv);

        byte[] clientStaticPubDecrypted = serverNoise.Decrypt(clientStaticCiphertextRecv);
        CollectionAssert.AreEqual(clientStatic.PublicKey, clientStaticPubDecrypted);

        byte[] seSecret = Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientStaticPubDecrypted);
        serverNoise.MixKey(seSecret);

        byte[] clientPayloadDecrypted = serverNoise.Decrypt(clientPayloadCiphertextRecv);
        CollectionAssert.AreEqual(clientPayload, clientPayloadDecrypted);

        var serverSplit = serverNoise.Split();
        // Responder's read is initiator's write, and responder's write is initiator's read.
        var serverReadCipher = serverSplit.WriteCipher;
        var serverWriteCipher = serverSplit.ReadCipher;

        // 6. Test Bidirectional post-handshake encryption:
        // Client write -> Server read
        byte[] msg1 = Encoding.UTF8.GetBytes("ping from client");
        byte[] ct1 = clientKeys.WriteCipher.EncryptWithCounter(0, msg1);
        byte[] pt1 = serverReadCipher.DecryptWithCounter(0, ct1);
        CollectionAssert.AreEqual(msg1, pt1);

        // Server write -> Client read
        byte[] msg2 = Encoding.UTF8.GetBytes("pong from server");
        byte[] ct2 = serverWriteCipher.EncryptWithCounter(0, msg2);
        byte[] pt2 = clientKeys.ReadCipher.DecryptWithCounter(0, ct2);
        CollectionAssert.AreEqual(msg2, pt2);
    }

    [TestMethod]
    public void TestNoisePatternPadding()
    {
        byte[] prologue = Constants.WaConnHeader;
        var unpadded = new NoiseState("Noise_XX_25519_AESGCM_SHA256", prologue);
        var padded = new NoiseState("Noise_XX_25519_AESGCM_SHA256\0\0\0\0", prologue);

        // Pattern <= 32 bytes must be padded with zeroes to 32 bytes
        CollectionAssert.AreEqual(padded.Hash, unpadded.Hash);
        CollectionAssert.AreEqual(padded.Salt, unpadded.Salt);

        // Long pattern (> 32 bytes) hashes using SHA-256
        var longPattern = new NoiseState("Noise_XXfallback_25519_AESGCM_SHA256", prologue);
        Assert.AreEqual(32, longPattern.Hash.Length);
        Assert.IsFalse(longPattern.Hash.SequenceEqual(unpadded.Hash));
    }

    [TestMethod]
    public void TestWaConnHeaderPrologue()
    {
        byte[] expected = [(byte)'W', (byte)'A', 6, 3];
        CollectionAssert.AreEqual(expected, Constants.WaConnHeader);

        var noise1 = new NoiseState("Noise_XX_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);
        var noise2 = new NoiseState("Noise_XX_25519_AESGCM_SHA256\0\0\0\0", [(byte)'W', (byte)'A', 6, 5]);

        // Different prologues produce different hashes
        Assert.IsFalse(noise1.Hash.SequenceEqual(noise2.Hash));
    }

    [TestMethod]
    public void TestIvGenerationEdgeCases()
    {
        byte[] ivMax = NoiseCipher.GenerateIv(uint.MaxValue);
        byte[] expectedMax = [0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF];
        CollectionAssert.AreEqual(expectedMax, ivMax);

        byte[] ivCustom = NoiseCipher.GenerateIv(0x12345678);
        byte[] expectedCustom = [0, 0, 0, 0, 0, 0, 0, 0, 0x12, 0x34, 0x56, 0x78];
        CollectionAssert.AreEqual(expectedCustom, ivCustom);

        byte[] iv64k = NoiseCipher.GenerateIv(0x00010000);
        byte[] expected64k = [0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x01, 0x00, 0x00];
        CollectionAssert.AreEqual(expected64k, iv64k);
    }

    [TestMethod]
    public void TestNoiseCipherTamperedCiphertextAndAad()
    {
        byte[] key = new byte[32];
        Random.Shared.NextBytes(key);

        using var cipher = new NoiseCipher(key);
        byte[] plaintext = Encoding.UTF8.GetBytes("Super secret payload");
        byte[] aad = Encoding.UTF8.GetBytes("AssociatedData");

        byte[] ciphertext = cipher.EncryptWithCounter(42, plaintext, aad);

        // 1. Valid decrypt
        byte[] decrypted = cipher.DecryptWithCounter(42, ciphertext, aad);
        CollectionAssert.AreEqual(plaintext, decrypted);

        // 2. Tampered ciphertext bit flip must throw AuthenticationTagMismatchException
        byte[] tamperedCiphertext = (byte[])ciphertext.Clone();
        tamperedCiphertext[0] ^= 0x01;
        Assert.ThrowsException<System.Security.Cryptography.AuthenticationTagMismatchException>(() =>
        {
            cipher.DecryptWithCounter(42, tamperedCiphertext, aad);
        });

        // 3. Tampered tag bit flip must throw AuthenticationTagMismatchException
        byte[] tamperedTag = (byte[])ciphertext.Clone();
        tamperedTag[^1] ^= 0x01;
        Assert.ThrowsException<System.Security.Cryptography.AuthenticationTagMismatchException>(() =>
        {
            cipher.DecryptWithCounter(42, tamperedTag, aad);
        });

        // 4. Tampered AAD must throw AuthenticationTagMismatchException
        byte[] tamperedAad = Encoding.UTF8.GetBytes("WrongAssociatedData");
        Assert.ThrowsException<System.Security.Cryptography.AuthenticationTagMismatchException>(() =>
        {
            cipher.DecryptWithCounter(42, ciphertext, tamperedAad);
        });

        // 5. Wrong counter must throw AuthenticationTagMismatchException
        Assert.ThrowsException<System.Security.Cryptography.AuthenticationTagMismatchException>(() =>
        {
            cipher.DecryptWithCounter(43, ciphertext, aad);
        });

        // 6. Ciphertext shorter than tag length (16) must throw CryptographicException
        Assert.ThrowsException<System.Security.Cryptography.CryptographicException>(() =>
        {
            cipher.DecryptWithCounter(42, new byte[15], aad);
        });

        // 7. Invalid key length must throw ArgumentException
        Assert.ThrowsException<ArgumentException>(() =>
        {
            using var invalidCipher = new NoiseCipher(new byte[16]);
        });
    }

    [TestMethod]
    public void TestFramingEdgeCases()
    {
        // 1. Decode buffer shorter than FrameLengthSize (3 bytes)
        Assert.IsFalse(Framing.TryDecodeFrame(new byte[2], out _, out int consumed1));
        Assert.AreEqual(0, consumed1);

        // 2. Decode buffer with incomplete payload
        byte[] incompleteFrame = [0x00, 0x00, 0x0A, 0x01, 0x02]; // claims 10 bytes, has 2
        Assert.IsFalse(Framing.TryDecodeFrame(incompleteFrame, out _, out int consumed2));
        Assert.AreEqual(0, consumed2);

        // 3. Zero-length payload
        byte[] emptyFrame = Framing.EncodeFrame(Array.Empty<byte>());
        Assert.AreEqual(3, emptyFrame.Length);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0 }, emptyFrame);
        Assert.IsTrue(Framing.TryDecodeFrame(emptyFrame, out byte[] emptyPayload, out int consumed3));
        Assert.AreEqual(3, consumed3);
        Assert.AreEqual(0, emptyPayload.Length);

        // 4. Decoding multiple frames from single buffer
        byte[] frame1 = Framing.EncodeFrame(Encoding.UTF8.GetBytes("FrameOne"));
        byte[] frame2 = Framing.EncodeFrame(Encoding.UTF8.GetBytes("FrameTwo"));
        byte[] concatenated = [..frame1, ..frame2];

        Assert.IsTrue(Framing.TryDecodeFrame(concatenated, out byte[] p1, out int c1));
        Assert.AreEqual("FrameOne", Encoding.UTF8.GetString(p1));
        Assert.AreEqual(frame1.Length, c1);

        Assert.IsTrue(Framing.TryDecodeFrame(concatenated.AsSpan(c1), out byte[] p2, out int c2));
        Assert.AreEqual("FrameTwo", Encoding.UTF8.GetString(p2));
        Assert.AreEqual(frame2.Length, c2);

        // 5. Exceeding max frame size
        byte[] invalidHeader = [0xFF, 0xFF, 0xFF]; // 16,777,215 which is >= FrameMaxSize (1 << 24 = 16,777,216)
        // A length of 1 << 24 is 0x01, 0x00, 0x00, 0x00 (4 bytes), but 3-byte max is 0xFFFFFF = 16,777,215 < 1 << 24.
        // If encoded frame claims length >= FrameMaxSize, it must throw InvalidDataException
        byte[] oversizedHeader = [0xFF, 0xFF, 0xFF]; // < 1<<24. Wait, 1 << 24 is 16,777,216. 3 bytes max value is 0xFFFFFF = 16,777,215.
        // So any 3 bytes fits in < 1 << 24.
    }

    [TestMethod]
    public void TestHandshakeMessageEncodeAndParseServerHello()
    {
        byte[] ephemeral = new byte[32];
        byte[] staticCt = new byte[48];
        byte[] payloadCt = new byte[64];
        Random.Shared.NextBytes(ephemeral);
        Random.Shared.NextBytes(staticCt);
        Random.Shared.NextBytes(payloadCt);

        byte[] encoded = HandshakeMessage.EncodeServerHello(ephemeral, staticCt, payloadCt);
        Assert.IsTrue(encoded.Length > 100);

        var parsed = HandshakeMessage.ParseServerHello(encoded);
        CollectionAssert.AreEqual(ephemeral, parsed.Ephemeral);
        CollectionAssert.AreEqual(staticCt, parsed.Static);
        CollectionAssert.AreEqual(payloadCt, parsed.Payload);
    }

    [TestMethod]
    public void TestXxHandshakeIncompleteServerHelloThrows()
    {
        var clientStatic = Curve25519.GenerateKeyPair();
        var handshake = new XxHandshake(clientStatic, [1, 2, 3]);

        // 1. Missing ephemeral
        byte[] missingEphemeral = HandshakeMessage.EncodeServerHello(Array.Empty<byte>(), [1, 2], [3, 4]);
        // Note: protobuf with empty byte array serializes empty bytes, which ParseServerHello reads as 0-length byte[].
        // But if field is completely omitted:
        using var stream = new MemoryStream();
        using var output = new Google.Protobuf.CodedOutputStream(stream);
        using var helloStream = new MemoryStream();
        using (var helloOutput = new Google.Protobuf.CodedOutputStream(helloStream))
        {
            // Only field 2 (static) and 3 (payload), no field 1 (ephemeral)
            helloOutput.WriteTag(2, Google.Protobuf.WireFormat.WireType.LengthDelimited);
            helloOutput.WriteBytes(Google.Protobuf.ByteString.CopyFrom(new byte[32]));
            helloOutput.WriteTag(3, Google.Protobuf.WireFormat.WireType.LengthDelimited);
            helloOutput.WriteBytes(Google.Protobuf.ByteString.CopyFrom(new byte[32]));
            helloOutput.Flush();
        }
        output.WriteTag(3, Google.Protobuf.WireFormat.WireType.LengthDelimited);
        output.WriteBytes(Google.Protobuf.ByteString.CopyFrom(helloStream.ToArray()));
        output.Flush();

        Assert.ThrowsException<InvalidDataException>(() =>
        {
            handshake.ProcessServerHello(stream.ToArray());
        });
    }

    [TestMethod]
    public void TestHkdfDerivationInMixKeyAndSplit()
    {
        var noise = new NoiseState("Noise_XX_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);

        // Initial counter is 0
        Assert.AreEqual(0u, noise.Counter);

        // Encrypt increments counter
        noise.Encrypt([1, 2, 3]);
        Assert.AreEqual(1u, noise.Counter);

        // MixKey resets counter to 0
        noise.MixKey(new byte[32]);
        Assert.AreEqual(0u, noise.Counter);

        // Split yields two distinct ciphers
        using var keys = noise.Split();
        Assert.IsNotNull(keys.WriteCipher);
        Assert.IsNotNull(keys.ReadCipher);

        // Both can encrypt
        byte[] ctWrite = keys.WriteCipher.EncryptWithCounter(0, [1, 2, 3]);
        byte[] ctRead = keys.ReadCipher.EncryptWithCounter(0, [1, 2, 3]);
        Assert.AreEqual(3 + 16, ctWrite.Length);
        Assert.AreEqual(3 + 16, ctRead.Length);
        // Different keys produce different ciphertexts for the same plaintext
        Assert.IsFalse(ctWrite.SequenceEqual(ctRead));
    }

    [TestMethod]
    public void TestNoiseCounterExhaustion()
    {
        var noise = new NoiseState("Noise_XX_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);

        // Set private counter to uint.MaxValue via reflection
        var counterField = typeof(NoiseState).GetField("_counter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(counterField);
        counterField.SetValue(noise, uint.MaxValue);

        Assert.ThrowsException<OverflowException>(() =>
        {
            noise.Encrypt([1, 2, 3]);
        });

        Assert.ThrowsException<OverflowException>(() =>
        {
            noise.Decrypt(new byte[16]);
        });
    }

    [TestMethod]
    public void TestCurve25519DiffieHellman()
    {
        var alice = Curve25519.GenerateKeyPair();
        var bob = Curve25519.GenerateKeyPair();

        Assert.AreEqual(32, alice.PrivateKey.Length);
        Assert.AreEqual(32, alice.PublicKey.Length);
        Assert.AreEqual(32, bob.PrivateKey.Length);
        Assert.AreEqual(32, bob.PublicKey.Length);

        byte[] secretAlice = Curve25519.CalculateSharedSecret(alice.PrivateKey, bob.PublicKey);
        byte[] secretBob = Curve25519.CalculateSharedSecret(bob.PrivateKey, alice.PublicKey);

        Assert.AreEqual(32, secretAlice.Length);
        CollectionAssert.AreEqual(secretAlice, secretBob);

        // Invalid key lengths must throw ArgumentException
        Assert.ThrowsException<ArgumentException>(() =>
        {
            Curve25519.CalculateSharedSecret(new byte[31], bob.PublicKey);
        });

        Assert.ThrowsException<ArgumentException>(() =>
        {
            Curve25519.CalculateSharedSecret(alice.PrivateKey, new byte[33]);
        });
    }

    [TestMethod]
    public void TestIkHandshakeRoundtripSimulation()
    {
        // 1. Setup client and server static keys
        var clientStatic = Curve25519.GenerateKeyPair();
        var clientEphemeral = Curve25519.GenerateKeyPair();
        var serverStatic = Curve25519.GenerateKeyPair();
        var serverEphemeral = Curve25519.GenerateKeyPair();

        byte[] clientPayload = Encoding.UTF8.GetBytes("client-0rtt-login-payload");
        byte[] fakeCertPayload = Encoding.UTF8.GetBytes("fake-server-cert-chain");

        // 2. Client initiates Noise IK Handshake
        var clientIk = new IkHandshake(clientStatic, serverStatic.PublicKey, clientPayload, clientEphemeral);
        byte[] clientHelloBytes = clientIk.BuildClientHello();

        // 3. Server receives ClientHello and processes it per Noise IK responder rules:
        // ClientHello contains: ephemeral (32), static (48), payload (clientPayload.Length + 16)
        var clientHelloMsg = HandshakeMessage.ParseServerHello(clientHelloBytes); // same proto parser works for ClientHello
        // Let's decode fields from clientHelloBytes
        var cis = new Google.Protobuf.CodedInputStream(clientHelloBytes);
        byte[]? clientEphFromMsg = null;
        byte[]? clientStaticEncrypted = null;
        byte[]? clientPayloadEncrypted = null;

        while (!cis.IsAtEnd)
        {
            uint tag = cis.ReadTag();
            if (Google.Protobuf.WireFormat.GetTagFieldNumber(tag) == 2) // clientHello in HandshakeMessage
            {
                byte[] innerBytes = cis.ReadBytes().ToByteArray();
                var innerCis = new Google.Protobuf.CodedInputStream(innerBytes);
                while (!innerCis.IsAtEnd)
                {
                    uint subTag = innerCis.ReadTag();
                    int fieldNum = Google.Protobuf.WireFormat.GetTagFieldNumber(subTag);
                    if (fieldNum == 1) clientEphFromMsg = innerCis.ReadBytes().ToByteArray();
                    else if (fieldNum == 2) clientStaticEncrypted = innerCis.ReadBytes().ToByteArray();
                    else if (fieldNum == 3) clientPayloadEncrypted = innerCis.ReadBytes().ToByteArray();
                    else innerCis.SkipLastField();
                }
            }
            else cis.SkipLastField();
        }

        Assert.IsNotNull(clientEphFromMsg);
        Assert.IsNotNull(clientStaticEncrypted);
        Assert.IsNotNull(clientPayloadEncrypted);
        CollectionAssert.AreEqual(clientEphemeral.PublicKey, clientEphFromMsg);

        // Server Noise IK state machine:
        var serverNoise = new NoiseState("Noise_IK_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);
        // Pre-message: <- s
        serverNoise.Authenticate(serverStatic.PublicKey);
        // -> e
        serverNoise.Authenticate(clientEphFromMsg);
        // es: DH(serverStatic.PrivateKey, clientEphFromMsg)
        byte[] esSecret = Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, clientEphFromMsg);
        serverNoise.MixKey(esSecret);
        // -> s: decrypt client static
        byte[] decryptedClientStatic = serverNoise.Decrypt(clientStaticEncrypted);
        CollectionAssert.AreEqual(clientStatic.PublicKey, decryptedClientStatic);
        // ss: DH(serverStatic.PrivateKey, decryptedClientStatic)
        byte[] ssSecret = Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, decryptedClientStatic);
        serverNoise.MixKey(ssSecret);
        // decrypt 0-RTT payload
        byte[] decryptedPayload = serverNoise.Decrypt(clientPayloadEncrypted);
        CollectionAssert.AreEqual(clientPayload, decryptedPayload);

        // Server responds:
        // <- e
        serverNoise.Authenticate(serverEphemeral.PublicKey);
        // ee: DH(serverEphemeral.PrivateKey, clientEphFromMsg)
        byte[] eeSecret = Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientEphFromMsg);
        serverNoise.MixKey(eeSecret);
        // se: DH(serverEphemeral.PrivateKey, decryptedClientStatic)
        byte[] seSecret = Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, decryptedClientStatic);
        serverNoise.MixKey(seSecret);
        // encrypt cert payload
        byte[] encryptedCert = serverNoise.Encrypt(fakeCertPayload);
        var serverKeys = serverNoise.Split();

        // Server encodes ServerHello { ephemeral, payload } (no static, indicating IK success)
        byte[] serverHelloBytes = HandshakeMessage.EncodeServerHello(serverEphemeral.PublicKey, Array.Empty<byte>(), encryptedCert);
        // Strip the empty static field to match real server hello
        using var shStream = new MemoryStream();
        using (var shOutput = new Google.Protobuf.CodedOutputStream(shStream))
        {
            using var innerShStream = new MemoryStream();
            using (var innerShOutput = new Google.Protobuf.CodedOutputStream(innerShStream))
            {
                innerShOutput.WriteTag(1, Google.Protobuf.WireFormat.WireType.LengthDelimited);
                innerShOutput.WriteBytes(Google.Protobuf.ByteString.CopyFrom(serverEphemeral.PublicKey));
                innerShOutput.WriteTag(3, Google.Protobuf.WireFormat.WireType.LengthDelimited);
                innerShOutput.WriteBytes(Google.Protobuf.ByteString.CopyFrom(encryptedCert));
                innerShOutput.Flush();
            }
            shOutput.WriteTag(3, Google.Protobuf.WireFormat.WireType.LengthDelimited);
            shOutput.WriteBytes(Google.Protobuf.ByteString.CopyFrom(innerShStream.ToArray()));
            shOutput.Flush();
        }
        byte[] cleanServerHello = shStream.ToArray();

        // 4. Client processes ServerHello
        var clientOutcome = clientIk.ProcessServerHello(cleanServerHello);
        Assert.IsNotNull(clientOutcome);
        CollectionAssert.AreEqual(fakeCertPayload, clientOutcome.Value.CertPlaintext);

        // 5. Verify bidirectional encrypted communication!
        // In Noise Protocol: client.Write uses key1, client.Read uses key2.
        // The server responder uses key1 to read and key2 to write.
        var clientKeys = clientOutcome.Value.Keys;

        // Client -> Server
        byte[] msgFromClient = Encoding.UTF8.GetBytes("Ping from client!");
        byte[] cipherFromClient = clientKeys.WriteCipher.EncryptWithCounter(0, msgFromClient, []);
        byte[] decryptedByServer = serverKeys.WriteCipher.DecryptWithCounter(0, cipherFromClient, []);
        CollectionAssert.AreEqual(msgFromClient, decryptedByServer);

        // Server -> Client
        byte[] msgFromServer = Encoding.UTF8.GetBytes("Pong from server!");
        byte[] cipherFromServer = serverKeys.ReadCipher.EncryptWithCounter(0, msgFromServer, []);
        byte[] decryptedByClient = clientKeys.ReadCipher.DecryptWithCounter(0, cipherFromServer, []);
        CollectionAssert.AreEqual(msgFromServer, decryptedByClient);
    }

    [TestMethod]
    public void TestXxFallbackHandshakeRoundtripSimulation()
    {
        // 1. Setup client and server static keys
        var clientStatic = Curve25519.GenerateKeyPair();
        var clientEphemeral = Curve25519.GenerateKeyPair();
        var staleServerStatic = Curve25519.GenerateKeyPair();
        var newServerStatic = Curve25519.GenerateKeyPair();
        var serverEphemeral = Curve25519.GenerateKeyPair();

        byte[] clientPayload = Encoding.UTF8.GetBytes("client-login-payload");
        byte[] fakeCertPayload = Encoding.UTF8.GetBytes("server-cert-chain");

        // 2. Client sent IK ClientHello with stale server static
        var clientIk = new IkHandshake(clientStatic, staleServerStatic.PublicKey, clientPayload, clientEphemeral);
        byte[] _ = clientIk.BuildClientHello();

        // 3. Server rejects IK because static rotated, sends XX ServerHello with static != null
        var serverNoise = new NoiseState("Noise_XXfallback_25519_AESGCM_SHA256", Constants.WaConnHeader);
        serverNoise.Authenticate(clientEphemeral.PublicKey);
        serverNoise.Authenticate(serverEphemeral.PublicKey);

        byte[] eeSecret = Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientEphemeral.PublicKey);
        serverNoise.MixKey(eeSecret);

        byte[] serverStaticCiphertext = serverNoise.Encrypt(newServerStatic.PublicKey);

        byte[] esSecret = Curve25519.CalculateSharedSecret(newServerStatic.PrivateKey, clientEphemeral.PublicKey);
        serverNoise.MixKey(esSecret);

        byte[] certCiphertext = serverNoise.Encrypt(fakeCertPayload);

        byte[] serverHelloBytes = HandshakeMessage.EncodeServerHello(
            serverEphemeral.PublicKey,
            serverStaticCiphertext,
            certCiphertext);

        // 4. Client sees serverHello.Static != null and switches to XxFallbackHandshake
        var ikResult = clientIk.ProcessServerHello(serverHelloBytes);
        Assert.IsNull(ikResult); // Signal to fallback

        var fallback = new XxFallbackHandshake(clientStatic, clientEphemeral, clientPayload);
        var (clientFinishBytes, clientKeys, learnedStatic) = fallback.ProcessServerHello(serverHelloBytes);

        CollectionAssert.AreEqual(newServerStatic.PublicKey, learnedStatic);

        // 5. Server processes ClientFinish
        var clientFinish = HandshakeMessage.ParseServerHello(clientFinishBytes); // parses proto
        var cis = new Google.Protobuf.CodedInputStream(clientFinishBytes);
        byte[]? clientStaticCiphertext = null;
        byte[]? payloadCiphertext = null;

        while (!cis.IsAtEnd)
        {
            uint tag = cis.ReadTag();
            if (Google.Protobuf.WireFormat.GetTagFieldNumber(tag) == 4) // clientFinish
            {
                byte[] innerBytes = cis.ReadBytes().ToByteArray();
                var innerCis = new Google.Protobuf.CodedInputStream(innerBytes);
                while (!innerCis.IsAtEnd)
                {
                    uint subTag = innerCis.ReadTag();
                    int fieldNum = Google.Protobuf.WireFormat.GetTagFieldNumber(subTag);
                    if (fieldNum == 1) clientStaticCiphertext = innerCis.ReadBytes().ToByteArray();
                    else if (fieldNum == 2) payloadCiphertext = innerCis.ReadBytes().ToByteArray();
                    else innerCis.SkipLastField();
                }
            }
            else cis.SkipLastField();
        }

        Assert.IsNotNull(clientStaticCiphertext);
        Assert.IsNotNull(payloadCiphertext);

        byte[] decryptedClientStatic = serverNoise.Decrypt(clientStaticCiphertext);
        CollectionAssert.AreEqual(clientStatic.PublicKey, decryptedClientStatic);

        // se: DH(serverEphemeral.PrivateKey, decryptedClientStatic)
        byte[] seSecret = Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, decryptedClientStatic);
        serverNoise.MixKey(seSecret);

        byte[] decryptedPayload = serverNoise.Decrypt(payloadCiphertext);
        CollectionAssert.AreEqual(clientPayload, decryptedPayload);

        var serverKeys = serverNoise.Split();

        // 6. Verify bidirectional communication post-fallback!
        byte[] testMsg = Encoding.UTF8.GetBytes("Verified XXfallback encrypted communication!");
        byte[] encrypted = clientKeys.WriteCipher.EncryptWithCounter(0, testMsg, []);
        byte[] decrypted = serverKeys.WriteCipher.DecryptWithCounter(0, encrypted, []);
        CollectionAssert.AreEqual(testMsg, decrypted);
    }
}
