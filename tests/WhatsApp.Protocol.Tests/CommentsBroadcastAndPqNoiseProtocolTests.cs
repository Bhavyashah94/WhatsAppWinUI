using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Noise;
using NoiseCodec = WhatsApp.Protocol.Noise.HandshakeMessage;
using PbPqMode = Whatsapp.HandshakeMessage.Types.HandshakePqMode;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class CommentsBroadcastAndPqNoiseProtocolTests
{
    [TestMethod]
    public void AddonComment_BroadcastIqs_And_PqHandshakeCodec_RoundTrip()
    {
        // 1) EncComment encryption + decryption round-trip ("Enc Comment")
        byte[] parentSecret = Enumerable.Range(1, 32).Select(static i => (byte)i).ToArray();
        var body = new Message { ExtendedTextMessage = new Message.Types.ExtendedTextMessage { Text = "CAG reply!" } };
        (byte[] encPayload, byte[] iv12) = AddonEncryption.EncryptComment(
            body,
            parentSecret,
            "CAG-POST-1",
            "100000000000001@lid",
            "100000000000002@lid");

        Message decBody = AddonEncryption.DecryptComment(
            encPayload,
            iv12,
            parentSecret,
            "CAG-POST-1",
            "100000000000001@lid",
            "100000000000002@lid");
        Assert.AreEqual("CAG reply!", decBody.ExtendedTextMessage.Text);

        Message cmtEnv = BroadcastIqs.BuildEncCommentMessage(
            new MessageKey { RemoteJid = "1203630001@g.us", Id = "CAG-POST-1", Participant = "100000000000001@lid" },
            encPayload,
            iv12,
            new byte[32]);
        Assert.AreEqual(32, cmtEnv.MessageContextInfo.MessageSecret.Length);

        // 2) Broadcast list IQ & user broadcast stanza
        Node listReq = BroadcastIqs.BuildGetBroadcastLists("bc-1");
        Assert.AreEqual("w:b", listReq.GetAttr("xmlns"));
        Assert.IsNotNull(listReq.GetChild("lists"));

        IReadOnlyList<BroadcastListInfo> parsedLists = BroadcastIqs.ParseGetBroadcastListsResponse(new Node("iq", null, new[]
        {
            new Node("lists", new[]
            {
                new Node("list", new Dictionary<string, NodeValue> { ["id"] = "1700000001@broadcast", ["name"] = "VIPs" }, new[]
                {
                    new Node("recipient", new Dictionary<string, NodeValue> { ["jid"] = "100000000000002@lid" }),
                    new Node("recipient", new Dictionary<string, NodeValue> { ["jid"] = "100000000000003@lid" })
                })
            })
        }));
        Assert.AreEqual(1, parsedLists.Count);
        Assert.AreEqual("VIPs", parsedLists[0].Name);
        Assert.AreEqual(2, parsedLists[0].RecipientJids.Count);

        Jid bJid = Jid.Parse("1700000001@broadcast");
        Assert.IsTrue(BroadcastIqs.IsUserBroadcastJid(bJid));
        Assert.IsFalse(BroadcastIqs.IsUserBroadcastJid(StatusStanzas.StatusBroadcastJid));

        Node bStanza = BroadcastIqs.BuildBroadcastMessageStanza(
            "BC-MSG-1",
            bJid,
            [Jid.Parse("100000000000002@lid"), Jid.Parse("100000000000003@lid")],
            new Message { Conversation = "Hi VIPs" },
            [1, 2, 3],
            skdmTargets: [(Jid.Parse("100000000000002@lid"), "pkmsg", [9, 8])]);
        Assert.AreEqual("1700000001@broadcast", bStanza.GetAttr("to"));
        Assert.AreEqual("lid", bStanza.GetAttr("addressing_mode"));
        Assert.IsNull(bStanza.GetChild("meta")); // user broadcast != status@broadcast
        Assert.AreEqual(2, bStanza.GetChild("participants")!.GetChildren("to").Count());

        // 3) PQ Noise wire extensions matching live WAWebProtobufsWa6.pb tags
        byte[] pqHello = NoiseCodec.EncodePqClientHello(
            new byte[32],
            PbPqMode.XxkemFs,
            extendedEphemeral: [10, 11],
            extendedCiphertext: [20, 21],
            useExtended: true,
            simulateXxkemFs: true,
            paddedBytes: [0, 0]);
        ClientHello ch = NoiseCodec.ParseClientHello(pqHello);
        Assert.AreEqual(PbPqMode.XxkemFs, ch.PqMode);
        Assert.IsTrue(ch.UseExtended);
        Assert.IsTrue(ch.SimulateXxkemFs);
        CollectionAssert.AreEqual(new byte[] { 10, 11 }, ch.ExtendedEphemeral);

        byte[] pqFinish = NoiseCodec.EncodePqClientFinish([1, 2], [3, 4], [5, 6], [7], simulateXxkemFs: true);
        ClientFinish cf = NoiseCodec.ParseClientFinish(pqFinish);
        CollectionAssert.AreEqual(new byte[] { 5, 6 }, cf.ExtendedCiphertext);
        Assert.IsTrue(cf.SimulateXxkemFs);
    }
}
