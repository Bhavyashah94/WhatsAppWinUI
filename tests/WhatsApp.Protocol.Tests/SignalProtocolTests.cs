using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public class SignalProtocolTests
{
    [TestMethod]
    public void TestPreKeySignalMessage_RoundTripSerialization()
    {
        var pkmsg = new PreKeySignalMessage
        {
            Version = 3,
            PreKeyId = 12345,
            BaseKey = new byte[32],
            IdentityKey = new byte[32],
            MessageBytes = new byte[] { 1, 2, 3, 4, 5 },
            RegistrationId = 9876,
            SignedPreKeyId = 42
        };
        Array.Fill(pkmsg.BaseKey, (byte)0xAA);
        Array.Fill(pkmsg.IdentityKey, (byte)0xBB);

        byte[] serialized = pkmsg.Serialize();
        Assert.IsTrue(serialized.Length > 0);
        Assert.AreEqual(0x33, serialized[0]); // (3 << 4) | 3

        var parsed = PreKeySignalMessage.Parse(serialized);
        Assert.AreEqual(pkmsg.Version, parsed.Version);
        Assert.AreEqual(pkmsg.PreKeyId, parsed.PreKeyId);
        CollectionAssert.AreEqual(pkmsg.BaseKey, parsed.BaseKey);
        CollectionAssert.AreEqual(pkmsg.IdentityKey, parsed.IdentityKey);
        CollectionAssert.AreEqual(pkmsg.MessageBytes, parsed.MessageBytes);
        Assert.AreEqual(pkmsg.RegistrationId, parsed.RegistrationId);
        Assert.AreEqual(pkmsg.SignedPreKeyId, parsed.SignedPreKeyId);
    }

    [TestMethod]
    public void TestSignalMessage_RoundTripSerialization()
    {
        var msg = new SignalMessage
        {
            Version = 3,
            RatchetKey = new byte[32],
            Counter = 5,
            PreviousCounter = 2,
            Ciphertext = new byte[] { 10, 20, 30, 40 }
        };
        Array.Fill(msg.RatchetKey, (byte)0xCC);
        byte[] mac = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        byte[] serialized = msg.Serialize(mac);
        Assert.IsTrue(serialized.Length > 8);
        Assert.AreEqual(0x33, serialized[0]);

        var parsed = SignalMessage.Parse(serialized);
        Assert.AreEqual(msg.Version, parsed.Version);
        CollectionAssert.AreEqual(msg.RatchetKey, parsed.RatchetKey);
        Assert.AreEqual(msg.Counter, parsed.Counter);
        Assert.AreEqual(msg.PreviousCounter, parsed.PreviousCounter);
        CollectionAssert.AreEqual(msg.Ciphertext, parsed.Ciphertext);
        CollectionAssert.AreEqual(mac, parsed.Mac);
    }

    [TestMethod]
    public void TestSignalCipher_X3DH_EndToEnd_Decryption()
    {
        // Bob is our desktop receiver:
        var bobIdentity = Curve25519.GenerateKeyPair();
        var bobSignedPreKey = Curve25519.GenerateKeyPair();
        uint bobSignedPreKeyId = 1;
        uint bobRegistrationId = 1234;

        // Alice is the sender (phone):
        var aliceIdentity = Curve25519.GenerateKeyPair();
        var aliceBaseKey = Curve25519.GenerateKeyPair();

        // 1. Alice performs X3DH initiator calculation:
        // DH1 = DH(aliceIdentity.Private, bobSignedPreKey.Public)
        // DH2 = DH(aliceBaseKey.Private, bobIdentity.Public)
        // DH3 = DH(aliceBaseKey.Private, bobSignedPreKey.Public)
        byte[] dh1 = Curve25519.CalculateSharedSecret(aliceIdentity.PrivateKey, bobSignedPreKey.PublicKey);
        byte[] dh2 = Curve25519.CalculateSharedSecret(aliceBaseKey.PrivateKey, bobIdentity.PublicKey);
        byte[] dh3 = Curve25519.CalculateSharedSecret(aliceBaseKey.PrivateKey, bobSignedPreKey.PublicKey);

        byte[] input = new byte[32 + 32 + 32 + 32];
        for (int i = 0; i < 32; i++) input[i] = 0xFF;
        Buffer.BlockCopy(dh1, 0, input, 32, 32);
        Buffer.BlockCopy(dh2, 0, input, 64, 32);
        Buffer.BlockCopy(dh3, 0, input, 96, 32);

        byte[] whisperText = "WhisperText"u8.ToArray();
        byte[] zeroSalt = new byte[32];
        byte[] secrets = HKDF.DeriveKey(HashAlgorithmName.SHA256, input, 64, zeroSalt, whisperText);
        byte[] aliceChainKey = new byte[32];
        Buffer.BlockCopy(secrets, 32, aliceChainKey, 0, 32);

        // Derive Alice's message key for counter = 0
        using var hmacStep = new HMACSHA256(aliceChainKey);
        byte[] messageKey = hmacStep.ComputeHash(new byte[] { 0x01 });

        byte[] keyMaterial = HKDF.DeriveKey(HashAlgorithmName.SHA256, messageKey, 80, zeroSalt, "WhisperMessageKeys"u8.ToArray());
        byte[] cipherKey = new byte[32];
        byte[] macKey = new byte[32];
        byte[] iv = new byte[16];
        Buffer.BlockCopy(keyMaterial, 0, cipherKey, 0, 32);
        Buffer.BlockCopy(keyMaterial, 32, macKey, 0, 32);
        Buffer.BlockCopy(keyMaterial, 64, iv, 0, 16);

        // Encrypt plaintext with PKCS7 padding
        byte[] plaintextOriginal = "Hello Signal WhatsApp Protocol!"u8.ToArray();
        int padLen = 16 - (plaintextOriginal.Length % 16);
        byte[] padded = new byte[plaintextOriginal.Length + padLen];
        Buffer.BlockCopy(plaintextOriginal, 0, padded, 0, plaintextOriginal.Length);
        for (int i = plaintextOriginal.Length; i < padded.Length; i++) padded[i] = (byte)padLen;

        using var aes = Aes.Create();
        aes.Key = cipherKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();
        byte[] ciphertext = enc.TransformFinalBlock(padded, 0, padded.Length);

        // Build inner SignalMessage (raw prefix + ciphertext)
        var innerMsg = new SignalMessage
        {
            Version = 3,
            RatchetKey = bobSignedPreKey.PublicKey, // using initial chain ratchet
            Counter = 0,
            PreviousCounter = 0,
            Ciphertext = ciphertext
        };

        // Construct raw message without MAC to compute HMAC
        using var ms = new MemoryStream();
        ms.WriteByte(0x33);
        var cos = new Google.Protobuf.CodedOutputStream(ms);
        cos.WriteTag(1, Google.Protobuf.WireFormat.WireType.LengthDelimited);
        cos.WriteBytes(Google.Protobuf.ByteString.CopyFrom(PreKeySignalMessage.TagKey(innerMsg.RatchetKey)));
        cos.WriteTag(2, Google.Protobuf.WireFormat.WireType.Varint);
        cos.WriteUInt32(innerMsg.Counter);
        cos.WriteTag(3, Google.Protobuf.WireFormat.WireType.Varint);
        cos.WriteUInt32(innerMsg.PreviousCounter);
        cos.WriteTag(4, Google.Protobuf.WireFormat.WireType.LengthDelimited);
        cos.WriteBytes(Google.Protobuf.ByteString.CopyFrom(innerMsg.Ciphertext));
        cos.Flush();
        byte[] rawWithoutMac = ms.ToArray();

        // Calculate MAC
        byte[] authData = new byte[33 + 33 + rawWithoutMac.Length];
        authData[0] = 5;
        Buffer.BlockCopy(aliceIdentity.PublicKey, 0, authData, 1, 32);
        authData[33] = 5;
        Buffer.BlockCopy(bobIdentity.PublicKey, 0, authData, 34, 32);
        Buffer.BlockCopy(rawWithoutMac, 0, authData, 66, rawWithoutMac.Length);

        using var hmacAuth = new HMACSHA256(macKey);
        byte[] fullMac = hmacAuth.ComputeHash(authData);
        byte[] mac8 = new byte[8];
        Buffer.BlockCopy(fullMac, 0, mac8, 0, 8);

        byte[] serializedInner = innerMsg.Serialize(mac8);

        // Build outer PreKeySignalMessage
        var pkmsg = new PreKeySignalMessage
        {
            Version = 3,
            BaseKey = aliceBaseKey.PublicKey,
            IdentityKey = aliceIdentity.PublicKey,
            MessageBytes = serializedInner,
            RegistrationId = bobRegistrationId,
            SignedPreKeyId = bobSignedPreKeyId
        };
        byte[] pkmsgWireBytes = pkmsg.Serialize();

        // Now Bob receives pkmsgWireBytes from the wire!
        var receivedPkmsg = PreKeySignalMessage.Parse(pkmsgWireBytes);

        var (decryptedPlaintext, session) = SignalCipher.DecryptPreKey(
            receivedPkmsg,
            bobIdentity,
            bobSignedPreKey);

        CollectionAssert.AreEqual(plaintextOriginal, decryptedPlaintext);
        Assert.IsNotNull(session);
        CollectionAssert.AreEqual(aliceIdentity.PublicKey, session.RemoteIdentity);
    }

    [TestMethod]
    public void TestUnpadWhatsAppMessage_ValidPadding_V2()
    {
        byte[] payload = new byte[] { 10, 20, 30, 40, 50 };

        // Test pad lengths 1 to 16 (standard WhatsApp padding)
        for (int pad = 1; pad <= 16; pad++)
        {
            byte[] padded = new byte[payload.Length + pad];
            Buffer.BlockCopy(payload, 0, padded, 0, payload.Length);
            for (int i = payload.Length; i < padded.Length; i++)
            {
                padded[i] = (byte)pad;
            }

            byte[] unpadded = SignalCipher.UnpadWhatsAppMessage(padded, version: 2);
            CollectionAssert.AreEqual(payload, unpadded, $"Failed for pad length {pad}");
        }
    }

    [TestMethod]
    public void TestUnpadWhatsAppMessage_CorruptPadding_ReturnsUntouched()
    {
        byte[] payload = new byte[] { 1, 2, 3, 4, 5, 0x03, 0x02, 0x03 }; // Corrupt: middle byte is 0x02, expected 0x03
        byte[] result = SignalCipher.UnpadWhatsAppMessage(payload, version: 2);
        CollectionAssert.AreEqual(payload, result);
    }

    [TestMethod]
    public void TestUnpadWhatsAppMessage_Version3_ReturnsUntouched()
    {
        byte[] payload = new byte[] { 1, 2, 3, 0x02, 0x02 };
        byte[] result = SignalCipher.UnpadWhatsAppMessage(payload, version: 3);
        CollectionAssert.AreEqual(payload, result);
    }

    [TestMethod]
    public void TestUnpadWhatsAppMessage_Empty_ReturnsEmpty()
    {
        byte[] empty = Array.Empty<byte>();
        byte[] result = SignalCipher.UnpadWhatsAppMessage(empty, version: 2);
        Assert.AreEqual(0, result.Length);
    }
}
