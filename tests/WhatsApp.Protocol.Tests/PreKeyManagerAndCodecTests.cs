using System.Security.Cryptography;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class PreKeyManagerAndCodecTests
{
    [TestMethod]
    public void PreKeyManager_GeneratesSignedBatchComputesDigestAndRoundTripsRecords()
    {
        var identity = Curve25519.GenerateKeyPair();
        var spk = PreKeyManager.CreateSignedPreKey(id: 7, identity, timestampSeconds: 1_779_643_793);

        Assert.IsTrue(XEdDSA.Verify(identity.PublicKey, PreKeySignalMessage.TagKey(spk.KeyPair.PublicKey), spk.Signature));
        var decodedSpk = PreKeyManager.DecodeSignedPreKeyRecord(spk.RecordBytes);
        Assert.AreEqual(7u, decodedSpk.Id);
        CollectionAssert.AreEqual(spk.KeyPair.PublicKey, decodedSpk.KeyPair.PublicKey);
        CollectionAssert.AreEqual(spk.KeyPair.PrivateKey, decodedSpk.KeyPair.PrivateKey);
        CollectionAssert.AreEqual(spk.Signature, decodedSpk.Signature);
        Assert.AreEqual(1_779_643_793UL, decodedSpk.Timestamp);

        var batch = PreKeyManager.GeneratePreKeys(startId: 100, count: 10);
        Assert.AreEqual(10, batch.Count);
        Assert.AreEqual(109u, batch[^1].Id);
        CollectionAssert.AreEqual(
            batch[3].KeyPair.PublicKey,
            PreKeyManager.TryExtractPreKeyPublicKey(batch[3].RecordBytes));
        Assert.IsNull(PreKeyManager.TryExtractPreKeyPublicKey([0xFF, 0xFF]));

        // Explicit SHA1 concatenation check matching WAWebDigestKeyJob & prekeys.rs
        var uploadItems = PreKeyManager.ToUploadItems(batch);
        Assert.AreEqual(10, uploadItems.Count);
        byte[] digest = PreKeyManager.ComputeKeyBundleDigest(
            identity.PublicKey,
            spk.KeyPair.PublicKey,
            spk.Signature,
            batch.Select(p => p.KeyPair.PublicKey));
        Assert.AreEqual(20, digest.Length);

        byte[] concat =
        [
            .. identity.PublicKey,
            .. spk.KeyPair.PublicKey,
            .. spk.Signature,
            .. batch.SelectMany(p => p.KeyPair.PublicKey)
        ];
        CollectionAssert.AreEqual(SHA1.HashData(concat), digest);
        Assert.IsTrue(PreKeyManager.NeedsReplenishment(4));
        Assert.IsFalse(PreKeyManager.NeedsReplenishment(5));
    }

    [TestMethod]
    public void SignalStateCodec_RoundTripsSessionWithPendingPreKeyAndSkippedMessages()
    {
        var aliceId = Curve25519.GenerateKeyPair();
        var bobId = Curve25519.GenerateKeyPair();
        var bobSpk = PreKeyManager.CreateSignedPreKey(11, bobId);
        var bobOtps = PreKeyManager.GeneratePreKeys(200, 1);

        var bundle = new PreKeyBundle(
            RegistrationId: 1111,
            DeviceId: 0,
            bobSpk.Id,
            bobSpk.KeyPair.PublicKey,
            bobSpk.Signature,
            bobId.PublicKey,
            bobOtps[0].Id,
            bobOtps[0].KeyPair.PublicKey);

        SignalSession aliceSession = SignalCipher.InitiateSession(bundle, aliceId, localRegistrationId: 2222);

        // Encrypt #0, #1, #2; deliver #2 first to Bob so Bob stores skipped keys #0 and #1!
        var m0 = SignalCipher.Encrypt(aliceSession, "msg-0"u8, aliceId, 2222);
        var m1 = SignalCipher.Encrypt(aliceSession, "msg-1"u8, aliceId, 2222);
        var m2 = SignalCipher.Encrypt(aliceSession, "msg-2"u8, aliceId, 2222);

        var (d2, bobSession) = SignalCipher.DecryptPreKey(
            PreKeySignalMessage.Parse(m2.Bytes), bobId, bobSpk.KeyPair, bobOtps[0].KeyPair);
        CollectionAssert.AreEqual("msg-2"u8.ToArray(), d2);

        // Persist and reload BOTH Alice and Bob sessions cleanly through SessionStructure protobuf!
        SignalSession reloadedAlice = SignalStateCodec.DeserializeSession(SignalStateCodec.SerializeSession(aliceSession));
        SignalSession reloadedBob = SignalStateCodec.DeserializeSession(SignalStateCodec.SerializeSession(bobSession));

        Assert.AreEqual(2, reloadedBob.SkippedMessageKeys.Count);
        Assert.IsNotNull(reloadedAlice.PendingPreKey);

        // Reloaded Bob still decrypts out-of-order m0 & m1 cleanly and rejects replay of m0!
        var (d0, _) = SignalCipher.DecryptPreKey(
            PreKeySignalMessage.Parse(m0.Bytes), bobId, bobSpk.KeyPair, existingSession: reloadedBob);
        var (d1, _) = SignalCipher.DecryptPreKey(
            PreKeySignalMessage.Parse(m1.Bytes), bobId, bobSpk.KeyPair, existingSession: reloadedBob);
        CollectionAssert.AreEqual("msg-0"u8.ToArray(), d0);
        CollectionAssert.AreEqual("msg-1"u8.ToArray(), d1);
        Assert.ThrowsException<CryptographicException>(() =>
            SignalCipher.DecryptPreKey(PreKeySignalMessage.Parse(m0.Bytes), bobId, bobSpk.KeyPair, existingSession: reloadedBob));

        // Reloaded Bob replies -> Reloaded Alice decrypts, ratchets, and clears PendingPreKey!
        var bobReply = SignalCipher.Encrypt(reloadedBob, "bob-after-restart"u8, bobId, 1111);
        byte[] aliceGot = SignalCipher.DecryptSignal(SignalMessage.Parse(bobReply.Bytes), reloadedAlice, aliceId);
        CollectionAssert.AreEqual("bob-after-restart"u8.ToArray(), aliceGot);
        Assert.IsNull(reloadedAlice.PendingPreKey);
    }

    [TestMethod]
    public void SignalStateCodec_RoundTripsSenderKeyRecordWithSkippedGroupSeeds()
    {
        var sender = new SenderKeyRecord();
        var receiver = new SenderKeyRecord();

        byte[] dist = SenderKeyCipher.CreateDistribution(sender);
        SenderKeyCipher.ProcessDistribution(receiver, dist);

        byte[] g0 = SenderKeyCipher.Encrypt(sender, "grp-0"u8);
        byte[] g1 = SenderKeyCipher.Encrypt(sender, "grp-1"u8);

        // Deliver g1 first so receiver stores skipped seed #0
        CollectionAssert.AreEqual("grp-1"u8.ToArray(), SenderKeyCipher.Decrypt(receiver, g1));

        // Serialize + reload both sender and receiver records through SenderKeyRecordStructure protobuf!
        var reloadedSender = SignalStateCodec.DeserializeSenderKeyRecord(SignalStateCodec.SerializeSenderKeyRecord(sender));
        var reloadedReceiver = SignalStateCodec.DeserializeSenderKeyRecord(SignalStateCodec.SerializeSenderKeyRecord(receiver));

        CollectionAssert.AreEqual("grp-0"u8.ToArray(), SenderKeyCipher.Decrypt(reloadedReceiver, g0));
        Assert.ThrowsException<CryptographicException>(() => SenderKeyCipher.Decrypt(reloadedReceiver, g0));

        byte[] g2 = SenderKeyCipher.Encrypt(reloadedSender, "grp-2-after-restart"u8);
        CollectionAssert.AreEqual("grp-2-after-restart"u8.ToArray(), SenderKeyCipher.Decrypt(reloadedReceiver, g2));
    }

    [TestMethod]
    public void SignalStateCodec_EmitsRecordStructureAndAcceptsTaggedOrLegacySessionStructure()
    {
        var aliceId = Curve25519.GenerateKeyPair();
        var bobId = Curve25519.GenerateKeyPair();
        var bobSpk = PreKeyManager.CreateSignedPreKey(19, bobId);
        var bundle = new PreKeyBundle(1111, 0, bobSpk.Id, bobSpk.KeyPair.PublicKey, bobSpk.Signature, bobId.PublicKey);

        SignalSession session = SignalCipher.InitiateSession(bundle, aliceId, 2222);
        byte[] encoded = SignalStateCodec.SerializeSession(session);

        // Verify canonical RecordStructure envelope parses cleanly
        var recordProto = global::Whatsapp.RecordStructure.Parser.ParseFrom(encoded);
        Assert.IsNotNull(recordProto.CurrentSession);

        // Legacy bare SessionStructure with 0x05-tagged 33B Curve25519 keys still deserializes cleanly
        var taggedBare = recordProto.CurrentSession.Clone();
        taggedBare.LocalIdentityPublic = Google.Protobuf.ByteString.CopyFrom(PreKeySignalMessage.TagKey(aliceId.PublicKey));
        taggedBare.RemoteIdentityPublic = Google.Protobuf.ByteString.CopyFrom(PreKeySignalMessage.TagKey(bobId.PublicKey));
        SignalSession fromLegacy = SignalStateCodec.DeserializeSession(Google.Protobuf.MessageExtensions.ToByteArray(taggedBare));

        CollectionAssert.AreEqual(aliceId.PublicKey, fromLegacy.LocalIdentity);
        CollectionAssert.AreEqual(bobId.PublicKey, fromLegacy.RemoteIdentity);
    }
}
