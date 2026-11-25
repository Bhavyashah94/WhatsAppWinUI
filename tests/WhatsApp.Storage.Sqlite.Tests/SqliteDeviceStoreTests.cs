using Microsoft.Data.Sqlite;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Storage.Sqlite.Tests;

[TestClass]
public sealed class SqliteDeviceStoreTests
{
    private static string CreateTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"wa_device_test_{Guid.NewGuid():N}.db");

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
    public async Task LoadOrCreateAsync_PersistsIdenticalCredentialsAcrossReopen()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            DeviceCredentials created;
            await using (var first = new SqliteBackend(dbPath))
            {
                Assert.IsNull(await first.Device.LoadAsync());
                created = await first.Device.LoadOrCreateAsync();

                // Second LoadOrCreateAsync must return existing row instead of regenerating keys
                DeviceCredentials secondCall = await first.Device.LoadOrCreateAsync();
                CollectionAssert.AreEqual(created.IdentityKey.PublicKey, secondCall.IdentityKey.PublicKey);
                CollectionAssert.AreEqual(created.NoiseKey.PrivateKey, secondCall.NoiseKey.PrivateKey);
            }

            await using var reopened = new SqliteBackend(dbPath);
            DeviceCredentials? loaded = await reopened.Device.LoadAsync();
            Assert.IsNotNull(loaded);
            Assert.IsFalse(loaded.IsRegistered);
            Assert.AreEqual(created.RegistrationId, loaded.RegistrationId);
            Assert.AreEqual(created.SignedPreKeyId, loaded.SignedPreKeyId);
            Assert.AreEqual(created.PushName, loaded.PushName);
            CollectionAssert.AreEqual(created.NoiseKey.PrivateKey, loaded.NoiseKey.PrivateKey);
            CollectionAssert.AreEqual(created.NoiseKey.PublicKey, loaded.NoiseKey.PublicKey);
            CollectionAssert.AreEqual(created.IdentityKey.PrivateKey, loaded.IdentityKey.PrivateKey);
            CollectionAssert.AreEqual(created.IdentityKey.PublicKey, loaded.IdentityKey.PublicKey);
            CollectionAssert.AreEqual(created.SignedPreKey.PrivateKey, loaded.SignedPreKey.PrivateKey);
            CollectionAssert.AreEqual(created.SignedPreKey.PublicKey, loaded.SignedPreKey.PublicKey);
            CollectionAssert.AreEqual(created.SignedPreKeySignature, loaded.SignedPreKeySignature);
            CollectionAssert.AreEqual(created.AdvSecretKey, loaded.AdvSecretKey);
            Assert.IsTrue(XEdDSA.Verify(
                loaded.IdentityKey.PublicKey,
                PreKeySignalMessage.TagKey(loaded.SignedPreKey.PublicKey),
                loaded.SignedPreKeySignature));
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }

    [TestMethod]
    public async Task SaveAsync_UpdatesPairingFieldsAndClearPairingResetsRegistration()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            await using var backend = new SqliteBackend(dbPath);
            var creds = await backend.Device.LoadOrCreateAsync();

            creds.Pn = Jid.PnDevice("15550101001", 3);
            creds.Lid = Jid.LidDevice("100000000000101", 3);
            creds.LidMigrated = true;
            creds.PushName = "Desktop User";
            creds.ServerStaticPublic = Curve25519.GenerateKeyPair().PublicKey;
            creds.EdgeRoutingInfo = [0x08, 0x08, 0x23, 0x08, 0x12];

            await backend.Device.SaveAsync(creds);
            await backend.Signal.SaveIdentityAsync("100000000000202:0@lid", creds.IdentityKey.PublicKey);

            DeviceCredentials? reloaded = await backend.Device.LoadAsync();
            Assert.IsNotNull(reloaded);
            Assert.IsTrue(reloaded.IsRegistered);
            Assert.AreEqual("15550101001:3@s.whatsapp.net", reloaded.Pn!.Value.ToString());
            Assert.AreEqual("100000000000101:3@lid", reloaded.Lid!.Value.ToString());
            Assert.IsTrue(reloaded.LidMigrated);
            Assert.AreEqual("Desktop User", reloaded.PushName);
            CollectionAssert.AreEqual(creds.ServerStaticPublic, reloaded.ServerStaticPublic);
            CollectionAssert.AreEqual(creds.EdgeRoutingInfo, reloaded.EdgeRoutingInfo);

            await backend.Device.ClearPairingAsync();

            DeviceCredentials? afterClear = await backend.Device.LoadAsync();
            Assert.IsNotNull(afterClear);
            Assert.IsFalse(afterClear.IsRegistered);
            Assert.IsNull(afterClear.Pn);
            Assert.IsNull(afterClear.Lid);
            Assert.IsFalse(afterClear.LidMigrated);
            Assert.IsNull(afterClear.ServerStaticPublic);
            Assert.IsNull(afterClear.EdgeRoutingInfo);
            Assert.IsNull(await backend.Signal.GetIdentityAsync("100000000000202:0@lid"));
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }

    [TestMethod]
    public async Task MultiAccountDeviceId_IsolatesAccountsInSameDatabase()
    {
        string dbPath = CreateTempDbPath();
        try
        {
            await using var account1 = new SqliteBackend(dbPath, deviceId: 1);
            await using var account2 = new SqliteBackend(dbPath, deviceId: 2);

            var creds1 = await account1.Device.LoadOrCreateAsync();
            var creds2 = await account2.Device.LoadOrCreateAsync();

            CollectionAssert.AreNotEqual(creds1.IdentityKey.PublicKey, creds2.IdentityKey.PublicKey);
            CollectionAssert.AreEqual(creds1.IdentityKey.PublicKey, (await account1.Device.LoadAsync())!.IdentityKey.PublicKey);
            CollectionAssert.AreEqual(creds2.IdentityKey.PublicKey, (await account2.Device.LoadAsync())!.IdentityKey.PublicKey);
        }
        finally
        {
            CleanupDbFiles(dbPath);
        }
    }
}
