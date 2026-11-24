using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class StanzaBuildersTests
{
    [TestMethod]
    public void PassiveAndPingIqs_BuildAndRoundTrip()
    {
        var active = RoundTrip(PassiveIq.BuildActive("iq-active-1"));
        Assert.AreEqual("iq", active.Tag);
        Assert.AreEqual("set", active.GetAttr("type"));
        Assert.AreEqual("passive", active.GetAttr("xmlns"));
        Assert.AreEqual("s.whatsapp.net", active.GetAttr("to"));
        Assert.IsNotNull(active.GetChild("active"));

        var activeResult = new Node("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[] { new("active") });
        Assert.IsTrue(PassiveIq.IsSuccessResponse(activeResult, expectPassive: false));
        Assert.IsFalse(PassiveIq.IsSuccessResponse(activeResult, expectPassive: true));

        var passive = RoundTrip(PassiveIq.BuildPassive("iq-passive-2"));
        Assert.IsNotNull(passive.GetChild("passive"));

        var ping = RoundTrip(PingIq.BuildPing("iq-ping-3"));
        Assert.AreEqual("iq", ping.Tag);
        Assert.AreEqual("get", ping.GetAttr("type"));
        Assert.AreEqual("w:p", ping.GetAttr("xmlns"));
    }

    [TestMethod]
    public void OfflineBatchAndDirtyBits_BuildAndRoundTrip()
    {
        var batch = RoundTrip(OfflineBatchIb.BuildOfflineBatch(200));
        Assert.AreEqual("ib", batch.Tag);
        var batchChild = batch.GetChild("offline_batch");
        Assert.IsNotNull(batchChild);
        Assert.AreEqual("200", batchChild.GetAttr("count"));

        var preview = RoundTrip(new Node("ib", new Node[]
        {
            new("offline_preview", new Dictionary<string, NodeValue> { ["count"] = "42" })
        }));
        Assert.IsTrue(OfflineBatchIb.TryParseOfflinePreview(preview, out int previewCount));
        Assert.AreEqual(42, previewCount);

        var dirty = RoundTrip(DirtyBitsIq.BuildClean(
            "iq-dirty-1",
            new DirtyBit(DirtyBitsIq.TypeGroups, 1779643793),
            new DirtyBit(DirtyBitsIq.TypeSyncdAppState)));
        Assert.AreEqual("urn:xmpp:whatsapp:dirty", dirty.GetAttr("xmlns"));
        var cleanChildren = dirty.GetChildren("clean").ToList();
        Assert.AreEqual(2, cleanChildren.Count);
        Assert.AreEqual("groups", cleanChildren[0].GetAttr("type"));
        Assert.AreEqual("1779643793", cleanChildren[0].GetAttr("timestamp"));
        Assert.AreEqual("syncd_app_state", cleanChildren[1].GetAttr("type"));
        Assert.IsNull(cleanChildren[1].GetAttr("timestamp"));
    }

    [TestMethod]
    public void PreKeyIqs_BuildAndRoundTrip()
    {
        var countIq = RoundTrip(PreKeyIq.BuildPreKeyCount("iq-pk-count"));
        Assert.AreEqual("encrypt", countIq.GetAttr("xmlns"));
        Assert.IsNotNull(countIq.GetChild("count"));

        var countResp = new Node("iq", new Node[]
        {
            new("count", new Dictionary<string, NodeValue> { ["value"] = "42" })
        });
        Assert.AreEqual(42, PreKeyIq.ParsePreKeyCount(countResp));

        var digestIq = RoundTrip(PreKeyIq.BuildDigestKeyBundle("iq-pk-digest"));
        Assert.IsNotNull(digestIq.GetChild("digest"));

        var targetJid = Jid.PnDevice("15550101001", 0);
        var fetchIq = RoundTrip(PreKeyIq.BuildFetchPreKeys("iq-pk-fetch", [targetJid], reason: "retry"));
        var keyNode = fetchIq.GetChild("key");
        Assert.IsNotNull(keyNode);
        var userNode = keyNode.GetChild("user");
        Assert.IsNotNull(userNode);
        Assert.AreEqual("15550101001@s.whatsapp.net", userNode.GetAttr("jid"));
        Assert.AreEqual("retry", userNode.GetAttr("reason"));

        byte[] idPub = Enumerable.Repeat((byte)0x11, 32).ToArray();
        byte[] sPub = Enumerable.Repeat((byte)0x22, 32).ToArray();
        byte[] sSig = Enumerable.Repeat((byte)0x33, 64).ToArray();
        byte[] otPub = Enumerable.Repeat((byte)0x44, 32).ToArray();

        var uploadIq = RoundTrip(PreKeyIq.BuildUploadPreKeys(
            "iq-pk-upload",
            registrationId: 0x01020304,
            idPub,
            signedPreKeyId: 0x00050607,
            sPub,
            sSig,
            [new PreKeyPairBytes(0x00000809, otPub)]));

        Assert.AreEqual("set", uploadIq.GetAttr("type"));
        Assert.IsNotNull(uploadIq.GetChild("registration"));
        CollectionAssert.AreEqual(
            new byte[] { 0x01, 0x02, 0x03, 0x04 },
            ((NodeContent.BytesContent)uploadIq.GetChild("registration")!.Content!).Bytes);

        var skey = uploadIq.GetChild("skey");
        Assert.IsNotNull(skey);
        CollectionAssert.AreEqual(
            new byte[] { 0x05, 0x06, 0x07 },
            ((NodeContent.BytesContent)skey.GetChild("id")!.Content!).Bytes);
    }

    [TestMethod]
    public void AppStateUsyncMediaPrivacyBlocklist_BuildAndRoundTrip()
    {
        var appState = RoundTrip(AppStateSyncIq.BuildSync(
            "iq-syncd-1",
            [
                AppStateSyncIq.BuildCollectionRequest("critical_block", version: 12, returnSnapshot: true),
                AppStateSyncIq.BuildCollectionRequest("regular", encodedPatch: [1, 2, 3])
            ],
            authoritative: true));
        Assert.AreEqual("w:sync:app:state", appState.GetAttr("xmlns"));
        var syncNode = appState.GetChild("sync");
        Assert.IsNotNull(syncNode);
        Assert.AreEqual("3", syncNode.GetAttr("data_namespace"));
        Assert.AreEqual(2, syncNode.GetChildren("collection").Count());

        var usync = RoundTrip(UsyncIq.BuildDeviceListQuery("iq-usync-1", "sid-1", [Jid.Pn("15550101001")]));
        Assert.AreEqual("usync", usync.GetAttr("xmlns"));
        var usyncChild = usync.GetChild("usync");
        Assert.IsNotNull(usyncChild);
        Assert.AreEqual("sid-1", usyncChild.GetAttr("sid"));
        Assert.IsNotNull(usyncChild.GetChild("query")?.GetChild("devices"));

        var mediaConn = RoundTrip(MediaConnIq.BuildGetMediaConn("iq-mc-1"));
        Assert.AreEqual("w:m", mediaConn.GetAttr("xmlns"));
        Assert.IsNotNull(mediaConn.GetChild("media_conn"));

        var privacySet = RoundTrip(PrivacyIq.BuildSetPrivacy("iq-priv-1", "readreceipts", "all"));
        Assert.AreEqual("privacy", privacySet.GetAttr("xmlns"));
        Assert.AreEqual("readreceipts", privacySet.GetChild("privacy")?.GetChild("category")?.GetAttr("name"));

        var blockIq = RoundTrip(BlocklistIq.BuildUpdateItem("iq-blk-1", Jid.Lid("10000001"), block: true, pnJid: Jid.Pn("15550101001")));
        Assert.AreEqual("blocklist", blockIq.GetAttr("xmlns"));
        Assert.AreEqual("block", blockIq.GetChild("item")?.GetAttr("action"));
    }

    [TestMethod]
    public void AckAndReceiptStanzas_BuildAndRoundTrip()
    {
        var ack = RoundTrip(AckStanza.Build("stz-1", "message", Jid.Pn("15550101001"), type: "sender"));
        Assert.AreEqual("ack", ack.Tag);
        Assert.AreEqual("stz-1", ack.GetAttr("id"));
        Assert.AreEqual("message", ack.GetAttr("class"));
        Assert.AreEqual("sender", ack.GetAttr("type"));

        var receipt = RoundTrip(ReceiptStanza.Build(
            "msg-1",
            new Jid("120363000000000", Server.Group),
            type: ReceiptStanza.TypeRead,
            participant: Jid.Lid("10000001"),
            additionalMessageIds: ["msg-2", "msg-3"]));
        Assert.AreEqual("receipt", receipt.Tag);
        Assert.AreEqual("read", receipt.GetAttr("type"));
        var list = receipt.GetChild("list");
        Assert.IsNotNull(list);
        Assert.AreEqual(2, list.GetChildren("item").Count());
    }

    private static Node RoundTrip(Node original)
    {
        var encoder = new BinaryEncoder();
        encoder.WriteNode(original);
        byte[] packed = BinaryPacker.Pack(encoder.ToArray());
        byte[] unpacked = BinaryPacker.Unpack(packed);
        var decoder = new BinaryDecoder(unpacked);
        Node decoded = decoder.ReadNode();
        Assert.IsTrue(decoder.IsFinished, $"Expected decoder to consume all bytes for <{original.Tag}>");
        return decoded;
    }
}
