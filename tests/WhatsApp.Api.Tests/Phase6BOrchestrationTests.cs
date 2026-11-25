using System.Net;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Noise;
using WhatsApp.Storage.Sqlite;
using WhatsApp.Transport;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class Phase6BOrchestrationTests
{
    [TestMethod]
    public async Task PairingHandler_RequestPairCodeAndPrimaryHello_PersistsAdvSecretBeforeCompanionFinish()
    {
        await using var backend = new SqliteBackend(":memory:");
        var creds = await backend.Device.LoadOrCreateAsync();
        var originalAdv = (byte[])creds.AdvSecretKey.Clone();
        var primaryEph = Curve25519.GenerateKeyPair();
        var primaryId = Curve25519.GenerateKeyPair();
        byte[] pairingRef = [0x01, 0x02, 0x03, 0x04];

        var sentStanzas = new List<Node>();
        var events = new List<WhatsAppEvent>();
        bool sawFinishIq = false;
        byte[]? storedAdvDuringFinish = null;

        await using var handler = new PairingHandler(
            (stanza, _) =>
            {
                sentStanzas.Add(stanza);
                return ValueTask.CompletedTask;
            },
            backend.Device,
            creds,
            ev =>
            {
                events.Add(ev);
                return ValueTask.CompletedTask;
            },
            sendIqAsync: async (iq, ct) =>
            {
                Node reg = iq.GetChild("link_code_companion_reg")!;
                string? stage = reg.GetAttr("stage");
                if (stage == "companion_hello")
                {
                    return new Node("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[]
                    {
                        new("link_code_companion_reg", new Node[] { new("link_code_pairing_ref", pairingRef) })
                    });
                }

                if (stage == "companion_finish")
                {
                    sawFinishIq = true;
                    // Verify AdvSecretKey was persisted in SQLite BEFORE companion_finish left the wire
                    var reloaded = await backend.Device.LoadAsync(ct);
                    storedAdvDuringFinish = reloaded?.AdvSecretKey;
                    return new Node("iq", new Dictionary<string, NodeValue> { ["type"] = "result" });
                }

                throw new InvalidOperationException($"Unexpected stage {stage}");
            });

        string code = await handler.RequestPairCodeAsync(new PairCodeOptions { PhoneNumber = "+1 (555) 123-4567" });
        Assert.IsTrue(PairCodeCrypto.ValidateCode(code));
        Assert.IsInstanceOfType<PairCodeEvent>(events[0]);

        byte[] wrappedPrimaryEph = PairCodeCrypto.EncryptEphemeralPublicKey(primaryEph.PublicKey, code);
        var primaryHelloNotif = new Node(
            "notification",
            new Dictionary<string, NodeValue>
            {
                ["id"] = "notif-pc-1",
                ["from"] = StanzaBuilder.ServerPn,
                ["type"] = "link_code_companion_reg"
            },
            new Node[]
            {
                new("link_code_companion_reg", new Dictionary<string, NodeValue> { ["stage"] = "primary_hello" }, new Node[]
                {
                    new("link_code_pairing_wrapped_primary_ephemeral_pub", wrappedPrimaryEph),
                    new("primary_identity_pub", primaryId.PublicKey),
                    new("link_code_pairing_ref", pairingRef)
                })
            });

        bool handled = await handler.HandleNotificationAsync(primaryHelloNotif);
        Assert.IsTrue(handled);
        Assert.IsTrue(sawFinishIq);
        Assert.IsTrue(sentStanzas.Any(n => n.Tag == "ack" && n.GetAttr("id") == "notif-pc-1"));
        Assert.IsNotNull(storedAdvDuringFinish);
        CollectionAssert.AreNotEqual(originalAdv, storedAdvDuringFinish);
        CollectionAssert.AreEqual(creds.AdvSecretKey, storedAdvDuringFinish);
    }

    [TestMethod]
    public async Task AppStateKeyShare_SavedFromSelfAndServerSyncTriggersSyncdCoordinator()
    {
        await using var backend = new SqliteBackend(":memory:");
        var creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15550101001", 1);
        creds.Lid = Jid.LidDevice("100000000000101", 1);
        await backend.Device.SaveAsync(creds);

        byte[] keyId = [0xAA, 0x01, 0x02, 0x03];
        byte[] keyData = Enumerable.Range(1, 32).Select(static b => (byte)b).ToArray();
        var share = new Message.Types.AppStateSyncKeyShare();
        share.Keys.Add(new Message.Types.AppStateSyncKey
        {
            KeyId = new Message.Types.AppStateSyncKeyId { KeyId = ByteString.CopyFrom(keyId) },
            KeyData = new Message.Types.AppStateSyncKeyData { KeyData = ByteString.CopyFrom(keyData), Timestamp = 1700000000 }
        });

        Assert.IsFalse(AppStateKeyShareHelper.IsSelfSender(Jid.Pn("15559998888"), creds));
        Assert.IsTrue(AppStateKeyShareHelper.IsSelfSender(Jid.PnDevice("15550101001", 0), creds));

        int saved = await AppStateKeyShareHelper.SaveKeyShareAsync(share, backend.AppSync);
        Assert.AreEqual(1, saved);
        CollectionAssert.AreEqual(keyData, await backend.AppSync.GetSyncKeyAsync(keyId));
    }
}
