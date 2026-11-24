using System.Security.Cryptography;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class SignalDoubleRatchetTests
{
    private static PreKeyBundle BuildBundle(
        KeyPair identity,
        KeyPair signedPreKey,
        uint signedPreKeyId,
        uint registrationId,
        uint? preKeyId = null,
        KeyPair? oneTimePreKey = null)
    {
        byte[] sig = XEdDSA.Sign(identity.PrivateKey, PreKeySignalMessage.TagKey(signedPreKey.PublicKey));
        return new PreKeyBundle(
            registrationId,
            DeviceId: 0,
            signedPreKeyId,
            signedPreKey.PublicKey,
            sig,
            identity.PublicKey,
            preKeyId,
            oneTimePreKey?.PublicKey);
    }

    [TestMethod]
    public void InitiateSession_RejectsForgedSignedPreKeySignature()
    {
        var aliceIdentity = Curve25519.GenerateKeyPair();
        var bobIdentity = Curve25519.GenerateKeyPair();
        var bobSignedPreKey = Curve25519.GenerateKeyPair();
        var attackerKey = Curve25519.GenerateKeyPair();

        byte[] badSig = XEdDSA.Sign(attackerKey.PrivateKey, PreKeySignalMessage.TagKey(bobSignedPreKey.PublicKey));
        var badBundle = new PreKeyBundle(100, 0, 1, bobSignedPreKey.PublicKey, badSig, bobIdentity.PublicKey);

        Assert.ThrowsException<CryptographicException>(() =>
            SignalCipher.InitiateSession(badBundle, aliceIdentity, localRegistrationId: 42));
    }

    [TestMethod]
    public void DoubleRatchet_BidirectionalMultiTurnWithOneTimePreKey_SucceedsAndSwitchesFromPkmsgToMsg()
    {
        var aliceId = Curve25519.GenerateKeyPair();
        const uint aliceRegId = 1001;
        var bobId = Curve25519.GenerateKeyPair();
        var bobSpk = Curve25519.GenerateKeyPair();
        var bobOpk = Curve25519.GenerateKeyPair();
        const uint bobRegId = 2002;

        PreKeyBundle bobBundle = BuildBundle(bobId, bobSpk, signedPreKeyId: 7, bobRegId, preKeyId: 88, bobOpk);
        SignalSession aliceSession = SignalCipher.InitiateSession(bobBundle, aliceId, aliceRegId);

        // Alice -> Bob #1 and #2 before Bob replies: both emit PreKeySignalMessage
        var enc1 = SignalCipher.Encrypt(aliceSession, "hello bob #1"u8, aliceId, aliceRegId);
        var enc2 = SignalCipher.Encrypt(aliceSession, "hello bob #2"u8, aliceId, aliceRegId);
        Assert.AreEqual(SignalCiphertextType.PreKeyMessage, enc1.Type);
        Assert.AreEqual("pkmsg", enc1.WireType);
        Assert.AreEqual(SignalCiphertextType.PreKeyMessage, enc2.Type);

        var (dec1, bobSession) = SignalCipher.DecryptPreKey(
            PreKeySignalMessage.Parse(enc1.Bytes), bobId, bobSpk, bobOpk);
        CollectionAssert.AreEqual("hello bob #1"u8.ToArray(), dec1);

        var (dec2, bobSession2) = SignalCipher.DecryptPreKey(
            PreKeySignalMessage.Parse(enc2.Bytes), bobId, bobSpk, bobOpk, existingSession: bobSession);
        Assert.AreSame(bobSession, bobSession2);
        CollectionAssert.AreEqual("hello bob #2"u8.ToArray(), dec2);

        // Bob replies #3 -> Alice decrypts, ratchets, and clears PendingPreKey!
        var enc3 = SignalCipher.Encrypt(bobSession, "hey alice #3"u8, bobId, bobRegId);
        Assert.AreEqual(SignalCiphertextType.SignalMessage, enc3.Type);
        Assert.AreEqual("msg", enc3.WireType);

        byte[] dec3 = SignalCipher.DecryptSignal(SignalMessage.Parse(enc3.Bytes), aliceSession, aliceId);
        CollectionAssert.AreEqual("hey alice #3"u8.ToArray(), dec3);
        Assert.IsNull(aliceSession.PendingPreKey);

        // Alice replies #4 -> now uses compact SignalMessage ("msg")!
        var enc4 = SignalCipher.Encrypt(aliceSession, "back to you #4"u8, aliceId, aliceRegId);
        Assert.AreEqual(SignalCiphertextType.SignalMessage, enc4.Type);
        byte[] dec4 = SignalCipher.DecryptSignal(SignalMessage.Parse(enc4.Bytes), bobSession, bobId);
        CollectionAssert.AreEqual("back to you #4"u8.ToArray(), dec4);

        // Multiple back-and-forth turns
        for (int turn = 0; turn < 5; turn++)
        {
            byte[] aMsg = System.Text.Encoding.UTF8.GetBytes($"alice-turn-{turn}");
            var aWire = SignalCipher.Encrypt(aliceSession, aMsg, aliceId, aliceRegId);
            CollectionAssert.AreEqual(aMsg, SignalCipher.DecryptSignal(SignalMessage.Parse(aWire.Bytes), bobSession, bobId));

            byte[] bMsg = System.Text.Encoding.UTF8.GetBytes($"bob-turn-{turn}");
            var bWire = SignalCipher.Encrypt(bobSession, bMsg, bobId, bobRegId);
            CollectionAssert.AreEqual(bMsg, SignalCipher.DecryptSignal(SignalMessage.Parse(bWire.Bytes), aliceSession, aliceId));
        }
    }

    [TestMethod]
    public void DoubleRatchet_WithoutOneTimePreKey_AndTamperedMacRejectedAtomically()
    {
        var aliceId = Curve25519.GenerateKeyPair();
        var bobId = Curve25519.GenerateKeyPair();
        var bobSpk = Curve25519.GenerateKeyPair();

        PreKeyBundle bobBundleNoOtp = BuildBundle(bobId, bobSpk, signedPreKeyId: 12, registrationId: 3003);
        SignalSession aliceSession = SignalCipher.InitiateSession(bobBundleNoOtp, aliceId, localRegistrationId: 4004);

        var enc1 = SignalCipher.Encrypt(aliceSession, "no-otp-hello"u8, aliceId, 4004);
        var (dec1, bobSession) = SignalCipher.DecryptPreKey(
            PreKeySignalMessage.Parse(enc1.Bytes), bobId, bobSpk);
        CollectionAssert.AreEqual("no-otp-hello"u8.ToArray(), dec1);

        // Next message forged/tampered MAC must fail without corrupting Bob's receiving state
        var enc2 = SignalCipher.Encrypt(aliceSession, "legit-second"u8, aliceId, 4004);
        byte[] innerWire = PreKeySignalMessage.Parse(enc2.Bytes).MessageBytes;
        byte[] tamperedInner = (byte[])innerWire.Clone();
        tamperedInner[^1] ^= 0xFF;

        uint counterBefore = bobSession.Counter;
        Assert.ThrowsException<CryptographicException>(() =>
            SignalCipher.DecryptSignal(SignalMessage.Parse(tamperedInner), bobSession, bobId));
        Assert.AreEqual(counterBefore, bobSession.Counter);

        // Untampered second message still decrypts cleanly
        byte[] dec2 = SignalCipher.DecryptSignal(SignalMessage.Parse(innerWire), bobSession, bobId);
        CollectionAssert.AreEqual("legit-second"u8.ToArray(), dec2);
    }

    [TestMethod]
    public void DoubleRatchet_OutOfOrderPreKeyAndWithinChain_DecryptsAndRejectsReplays()
    {
        var aliceId = Curve25519.GenerateKeyPair();
        var bobId = Curve25519.GenerateKeyPair();
        var bobSpk = Curve25519.GenerateKeyPair();

        var bobBundle = BuildBundle(bobId, bobSpk, signedPreKeyId: 1, registrationId: 5005);
        var aliceSession = SignalCipher.InitiateSession(bobBundle, aliceId, localRegistrationId: 6006);

        var pk0 = SignalCipher.Encrypt(aliceSession, "msg-0"u8, aliceId, 6006);
        var pk1 = SignalCipher.Encrypt(aliceSession, "msg-1"u8, aliceId, 6006);
        var pk2 = SignalCipher.Encrypt(aliceSession, "msg-2"u8, aliceId, 6006);
        var pk3 = SignalCipher.Encrypt(aliceSession, "msg-3"u8, aliceId, 6006);

        // #3 arrives BEFORE #0, #1, #2!
        var (dec3, bobSession) = SignalCipher.DecryptPreKey(PreKeySignalMessage.Parse(pk3.Bytes), bobId, bobSpk);
        CollectionAssert.AreEqual("msg-3"u8.ToArray(), dec3);
        Assert.AreEqual(3, bobSession.SkippedMessageKeys.Count);

        // Tampered skipped #1 probe must fail WITHOUT removing skipped key #1!
        var parsedPk1 = PreKeySignalMessage.Parse(pk1.Bytes);
        byte[] tamperedInner1 = (byte[])parsedPk1.MessageBytes.Clone();
        tamperedInner1[^1] ^= 0xAA;
        parsedPk1.MessageBytes = tamperedInner1;
        Assert.ThrowsException<CryptographicException>(() =>
            SignalCipher.DecryptPreKey(parsedPk1, bobId, bobSpk, existingSession: bobSession));
        Assert.AreEqual(3, bobSession.SkippedMessageKeys.Count);

        // Now real #1, #0, #2 arrive out of order and decrypt cleanly
        var (dec1, _) = SignalCipher.DecryptPreKey(PreKeySignalMessage.Parse(pk1.Bytes), bobId, bobSpk, existingSession: bobSession);
        var (dec0, _) = SignalCipher.DecryptPreKey(PreKeySignalMessage.Parse(pk0.Bytes), bobId, bobSpk, existingSession: bobSession);
        var (dec2, _) = SignalCipher.DecryptPreKey(PreKeySignalMessage.Parse(pk2.Bytes), bobId, bobSpk, existingSession: bobSession);

        CollectionAssert.AreEqual("msg-0"u8.ToArray(), dec0);
        CollectionAssert.AreEqual("msg-1"u8.ToArray(), dec1);
        CollectionAssert.AreEqual("msg-2"u8.ToArray(), dec2);
        Assert.AreEqual(0, bobSession.SkippedMessageKeys.Count);

        // Replaying #1 or #3 must fail permanently
        Assert.ThrowsException<CryptographicException>(() =>
            SignalCipher.DecryptPreKey(PreKeySignalMessage.Parse(pk1.Bytes), bobId, bobSpk, existingSession: bobSession));
        Assert.ThrowsException<CryptographicException>(() =>
            SignalCipher.DecryptPreKey(PreKeySignalMessage.Parse(pk3.Bytes), bobId, bobSpk, existingSession: bobSession));
    }

    [TestMethod]
    public void DoubleRatchet_DelayedMessageFromPriorRatchetTurn_StillDecryptsAfterNewRatchetTurn()
    {
        var aliceId = Curve25519.GenerateKeyPair();
        var bobId = Curve25519.GenerateKeyPair();
        var bobSpk = Curve25519.GenerateKeyPair();

        var bobBundle = BuildBundle(bobId, bobSpk, signedPreKeyId: 2, registrationId: 7001);
        var aliceSession = SignalCipher.InitiateSession(bobBundle, aliceId, 8001);

        // Alice sends A0 and A1 on ratchet R_A0; only A0 arrives first
        var a0 = SignalCipher.Encrypt(aliceSession, "a0"u8, aliceId, 8001);
        var a1Delayed = SignalCipher.Encrypt(aliceSession, "a1-delayed"u8, aliceId, 8001);

        var (decA0, bobSession) = SignalCipher.DecryptPreKey(PreKeySignalMessage.Parse(a0.Bytes), bobId, bobSpk);
        CollectionAssert.AreEqual("a0"u8.ToArray(), decA0);

        // Bob replies B0 -> Alice ratchets to R_A1 and sends A2 (arrives BEFORE A1!)
        var b0 = SignalCipher.Encrypt(bobSession, "b0"u8, bobId, 7001);
        CollectionAssert.AreEqual("b0"u8.ToArray(), SignalCipher.DecryptSignal(SignalMessage.Parse(b0.Bytes), aliceSession, aliceId));

        var a2 = SignalCipher.Encrypt(aliceSession, "a2-new-ratchet"u8, aliceId, 8001);
        Assert.AreEqual(SignalCiphertextType.SignalMessage, a2.Type);
        CollectionAssert.AreEqual("a2-new-ratchet"u8.ToArray(), SignalCipher.DecryptSignal(SignalMessage.Parse(a2.Bytes), bobSession, bobId));

        // Finally delayed A1 (from old ratchet R_A0) arrives at Bob -> still decrypts cleanly!
        var (decA1, _) = SignalCipher.DecryptPreKey(PreKeySignalMessage.Parse(a1Delayed.Bytes), bobId, bobSpk, existingSession: bobSession);
        CollectionAssert.AreEqual("a1-delayed"u8.ToArray(), decA1);

        // Replay of A1 fails
        Assert.ThrowsException<CryptographicException>(() =>
            SignalCipher.DecryptPreKey(PreKeySignalMessage.Parse(a1Delayed.Bytes), bobId, bobSpk, existingSession: bobSession));
    }

    [TestMethod]
    public void DoubleRatchet_FutureJumpLimitAndBoundedSkippedEviction()
    {
        var aliceId = Curve25519.GenerateKeyPair();
        var bobId = Curve25519.GenerateKeyPair();
        var bobSpk = Curve25519.GenerateKeyPair();

        var aliceSession = SignalCipher.InitiateSession(BuildBundle(bobId, bobSpk, 3, 9001), aliceId, 9002);
        var first = SignalCipher.Encrypt(aliceSession, "first"u8, aliceId, 9002);
        var (_, bobSession) = SignalCipher.DecryptPreKey(PreKeySignalMessage.Parse(first.Bytes), bobId, bobSpk);

        // Craft a bogus SignalMessage jumping > PeerFutureLimit (2000)
        var hugeJump = new SignalMessage
        {
            Version = 3,
            RatchetKey = aliceSession.LocalRatchetKeyPair!.Value.PublicKey,
            Counter = 2500,
            PreviousCounter = 0,
            Ciphertext = new byte[16]
        };
        byte[] body = hugeJump.SerializeWithoutMac();
        byte[] parsedReady = [.. body, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.ThrowsException<CryptographicException>(() =>
            SignalCipher.DecryptSignal(SignalMessage.Parse(parsedReady), bobSession, bobId));
        Assert.AreEqual(0, bobSession.SkippedMessageKeys.Count);

        // Test FIFO bounded eviction when > MaxSkippedKeys are stored
        byte[] dummyRatchet = Curve25519.GenerateKeyPair().PublicKey;
        var batch = Enumerable.Range(0, SignalSession.MaxSkippedKeys + 25)
            .Select(i => ((uint)i, Enumerable.Repeat((byte)(i & 0xFF), 32).ToArray()))
            .ToArray();
        bobSession.StoreSkippedKeys(dummyRatchet, batch);
        Assert.AreEqual(SignalSession.MaxSkippedKeys, bobSession.SkippedMessageKeys.Count);
        Assert.IsFalse(bobSession.TryGetSkippedKey(dummyRatchet, 0, out _));
        Assert.IsTrue(bobSession.TryGetSkippedKey(dummyRatchet, (uint)(SignalSession.MaxSkippedKeys + 24), out _));
    }
}
