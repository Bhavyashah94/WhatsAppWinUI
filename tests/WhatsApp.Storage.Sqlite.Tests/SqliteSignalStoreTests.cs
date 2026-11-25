using Microsoft.Data.Sqlite;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Storage.Sqlite.Tests;

[TestClass]
public sealed class SqliteSignalStoreTests
{
    private static string CreateTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"wa_signal_test_{Guid.NewGuid():N}.db");

    private static void CleanupDbFiles(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string candidate = dbPath + suffix;
            if (File.Exists(candidate))
            {
                try { File.Delete(candidate); } catch { }
            }
        }
    }

    [TestMethod]
    public async Task Session_PersistsAcrossDbReopenAndContinuesDoubleRatchet()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            var aliceId = Curve25519.GenerateKeyPair();
            var bobId = Curve25519.GenerateKeyPair();
            var bobSpk = PreKeyManager.CreateSignedPreKey(11, bobId);
            var bobOtps = PreKeyManager.GeneratePreKeys(200, 1);

            var bobBundle = new PreKeyBundle(
                RegistrationId: 1111,
                DeviceId: 0,
                bobSpk.Id,
                bobSpk.KeyPair.PublicKey,
                bobSpk.Signature,
                bobId.PublicKey,
                bobOtps[0].Id,
                bobOtps[0].KeyPair.PublicKey);

            var aliceSession = SignalCipher.InitiateSession(bobBundle, aliceId, localRegistrationId: 2222);

            // Alice sends msg #0 and #1; Bob receives #1 first (storing skipped key #0), saves session, closes DB
            var m0 = SignalCipher.Encrypt(aliceSession, "msg-0"u8, aliceId, 2222);
            var m1 = SignalCipher.Encrypt(aliceSession, "msg-1"u8, aliceId, 2222);

            await using (var first = new SqliteBackend(dbPath))
            {
                var (p1, bobSession) = SignalCipher.DecryptPreKey(
                    PreKeySignalMessage.Parse(m1.Bytes),
                    bobId,
                    bobSpk.KeyPair,
                    bobOtps[0].KeyPair);
                CollectionAssert.AreEqual("msg-1"u8.ToArray(), p1);

                await first.Signal.SaveSessionAsync("alice.0", aliceSession);
                await first.Signal.SaveSessionAsync("bob-alice.0", bobSession);
            }

            // Reopen fresh SqliteBackend and continue the ratchet bidirectionally
            await using var reopened = new SqliteBackend(dbPath);
            SignalSession? loadedBob = await reopened.Signal.GetSessionAsync("bob-alice.0");
            SignalSession? loadedAlice = await reopened.Signal.GetSessionAsync("alice.0");
            Assert.IsNotNull(loadedBob);
            Assert.IsNotNull(loadedAlice);

            // Out-of-order delayed PreKey msg #0 decrypts from persisted skipped keys
            var (p0, _) = SignalCipher.DecryptPreKey(
                PreKeySignalMessage.Parse(m0.Bytes),
                bobId,
                bobSpk.KeyPair,
                existingSession: loadedBob);
            CollectionAssert.AreEqual("msg-0"u8.ToArray(), p0);

            // Bob replies -> triggers ratchet turn on Alice's reloaded session
            var bobReply = SignalCipher.Encrypt(loadedBob, "reply-from-bob"u8, bobId, 1111);
            byte[] decryptedReply = SignalCipher.DecryptSignal(SignalMessage.Parse(bobReply.Bytes), loadedAlice, aliceId);
            CollectionAssert.AreEqual("reply-from-bob"u8.ToArray(), decryptedReply);

            await reopened.Signal.DeleteSessionAsync("bob-alice.0");
            Assert.IsNull(await reopened.Signal.GetSessionAsync("bob-alice.0"));
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }

    [TestMethod]
    public async Task Identities_PreKeys_And_SenderKeys_RoundTripAndIsolateByDeviceId()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            await using var backend1 = new SqliteBackend(dbPath, deviceId: 1);
            await using var backend2 = new SqliteBackend(dbPath, deviceId: 2);

            // Identities
            byte[] peerIdentity = Curve25519.GenerateKeyPair().PublicKey;
            await backend1.Signal.SaveIdentityAsync("100000000000101:0@lid", peerIdentity);
            CollectionAssert.AreEqual(peerIdentity, await backend1.Signal.GetIdentityAsync("100000000000101:0@lid"));
            Assert.IsNull(await backend2.Signal.GetIdentityAsync("100000000000101:0@lid"));

            // Batched PreKeys
            var preKeys = PreKeyManager.GeneratePreKeys(10, 50)
                .Select(p => (p.Id, p.RecordBytes))
                .ToList();
            await backend1.Signal.SavePreKeysAsync(preKeys);
            CollectionAssert.AreEqual(preKeys[25].RecordBytes, await backend1.Signal.GetPreKeyAsync(35));
            Assert.IsNull(await backend2.Signal.GetPreKeyAsync(35));

            await backend1.Signal.DeletePreKeyAsync(35);
            Assert.IsNull(await backend1.Signal.GetPreKeyAsync(35));

            // SenderKey serialization via SignalStateCodec
            var senderRecord = new SenderKeyRecord();
            byte[] skdm = SenderKeyCipher.CreateDistribution(senderRecord);
            byte[] encodedRecord = SignalStateCodec.SerializeSenderKeyRecord(senderRecord);

            await backend1.Signal.SaveSenderKeyAsync("120363000000000101@g.us", "100000000000101:0@lid", encodedRecord);
            byte[]? loadedRecordBytes = await backend1.Signal.GetSenderKeyAsync("120363000000000101@g.us", "100000000000101:0@lid");
            Assert.IsNotNull(loadedRecordBytes);

            var decodedRecord = SignalStateCodec.DeserializeSenderKeyRecord(loadedRecordBytes);
            var encrypted = SenderKeyCipher.Encrypt(decodedRecord, "group-persisted"u8);
            var receiverRecord = new SenderKeyRecord();
            SenderKeyCipher.ProcessDistribution(receiverRecord, skdm);
            byte[] groupPlain = SenderKeyCipher.Decrypt(receiverRecord, encrypted);
            CollectionAssert.AreEqual("group-persisted"u8.ToArray(), groupPlain);
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }
}
