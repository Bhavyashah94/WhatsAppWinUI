using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class RichMessagesAddonsAndPresenceProtocolTests
{
    [TestMethod]
    public void AddonEncryption_MatchesLiveWebCryptoVectors_And_PollOptionHash()
    {
        // Verified directly on live web.whatsapp.com via WAWebPollOptionHashUtils.getHashBufferForString('Pizza')
        Assert.AreEqual(
            "f12958816a49adfa2c6c8de8dd2144c163e92c5e375de964d533187c7d236c36",
            Convert.ToHexString(AddonEncryption.HashPollOption("Pizza")).ToLowerInvariant());

        byte[] secret = Enumerable.Repeat((byte)0x42, 32).ToArray();
        byte[] iv = Enumerable.Repeat((byte)0x11, 12).ToArray();
        const string parent = "15551110000@s.whatsapp.net";
        const string mod = "15552220000@s.whatsapp.net";

        // Verified directly on live web.whatsapp.com via crypto.subtle + WAUseCaseSecret
        var (pollEnc, _) = AddonEncryption.EncryptAddon([0x0a, 0x04, 1, 2, 3, 4], secret, "POLL-1", parent, mod, AddonModificationType.PollVote, iv);
        Assert.AreEqual("d0eb6b63e8152b3158638b7d7696158480e823d5d61c", Convert.ToHexString(pollEnc).ToLowerInvariant());

        var (rxnEnc, _) = AddonEncryption.EncryptAddon([0x0a, 0x02, 0x4f, 0x4b], secret, "MSG-1", parent, mod, AddonModificationType.EncReaction, iv);
        Assert.AreEqual("4b5d8d214814f5bbf0e56ca0c049f4b43876b236", Convert.ToHexString(rxnEnc).ToLowerInvariant());

        var (editEnc, _) = AddonEncryption.EncryptAddon([0x0a, 0x05, 0x48, 0x65, 0x6c, 0x6c, 0x6f], secret, "EDIT-1", parent, mod, AddonModificationType.MessageEdit, iv);
        Assert.AreEqual("3dcea75cd95fa96f0a261b993d28193a0c1a14d9379d30", Convert.ToHexString(editEnc).ToLowerInvariant());

        // Decryption round-trips
        CollectionAssert.AreEqual(
            new byte[] { 0x0a, 0x04, 1, 2, 3, 4 },
            AddonEncryption.DecryptAddon(pollEnc, iv, secret, "POLL-1", parent, mod, AddonModificationType.PollVote));
    }

    [TestMethod]
    public void RichMessageBuilders_CoverQuotesReactionsEditsRevokesPollsEventsSpoilersAndDraftSync()
    {
        var key = new MessageKey { Id = "TARGET-1", RemoteJid = "120363001@g.us", Participant = "15551110000@s.whatsapp.net", FromMe = false };
        var secret = Enumerable.Repeat((byte)0x77, 32).ToArray();

        Message reply = RichMessageBuilders.BuildReplyText("Reply text", key, new Message { Conversation = "Orig" });
        Assert.AreEqual("TARGET-1", reply.ExtendedTextMessage.ContextInfo.StanzaId);
        Assert.AreEqual("Orig", reply.ExtendedTextMessage.ContextInfo.QuotedMessage.Conversation);

        Message rx = RichMessageBuilders.BuildReaction(key, "🎉", 1700000000123);
        Assert.AreEqual("reaction", MessageStanzas.ClassifyStanzaType(rx));
        Assert.IsTrue(MessageStanzas.ShouldHideDecryptFail(rx));

        Message encRx = RichMessageBuilders.BuildEncReaction(key, "🔥", 1700000000456, secret, "15551110000@s.whatsapp.net", "15552220000@s.whatsapp.net");
        Message.Types.ReactionMessage decRx = AddonEncryption.DecryptReaction(
            encRx.EncReactionMessage.EncPayload.Span, encRx.EncReactionMessage.EncIv.Span, secret, "TARGET-1", "15551110000@s.whatsapp.net", "15552220000@s.whatsapp.net");
        Assert.AreEqual("🔥", decRx.Text);
        Assert.IsNull(decRx.Key);

        Message secretEdit = RichMessageBuilders.BuildSecretMessageEdit(key, new Message { Conversation = "Edited!" }, 1700000100000, secret, "15551110000@s.whatsapp.net", "15551110000@s.whatsapp.net");
        Assert.AreEqual("text", MessageStanzas.ClassifyStanzaType(secretEdit));
        Assert.IsTrue(MessageStanzas.ShouldHideDecryptFail(secretEdit, RichMessageBuilders.EditAttrMessageEdit));

        (Message revoke, string revokeAttr) = RichMessageBuilders.BuildRevoke(key, isAdminRevoke: true);
        Assert.AreEqual("8", revokeAttr);
        Assert.IsTrue(MessageStanzas.ShouldHideDecryptFail(revoke, revokeAttr));

        Message pollV5 = RichMessageBuilders.BuildPollCreation("Lunch?", ["Pizza", "Sushi"], selectableOptionsCount: 1, version: 5, quizCorrectOption: "Pizza", allowAddOption: true, messageSecret32: secret);
        Assert.AreEqual("poll", MessageStanzas.ClassifyStanzaType(pollV5));
        Assert.AreEqual(Message.Types.PollType.Quiz, pollV5.PollCreationMessageV5.PollType);

        Message addOpt = RichMessageBuilders.BuildPollAddOption(key, "Tacos", secret, "15551110000@s.whatsapp.net", "15552220000@s.whatsapp.net");
        Assert.AreEqual("poll", MessageStanzas.ClassifyStanzaType(addOpt));

        Message spoiler = RichMessageBuilders.WrapSpoiler(new Message
        {
            DocumentWithCaptionMessage = new Message.Types.FutureProofMessage
            {
                Message = new Message { ImageMessage = new Message.Types.ImageMessage { Caption = "Surprise!" } }
            }
        });
        Assert.AreEqual("media", MessageStanzas.ClassifyStanzaType(spoiler));
        Assert.AreEqual("image", MessageStanzas.ClassifyMediaType(spoiler));

        Message draft = RichMessageBuilders.BuildDraftMessageSync(Jid.Pn("15559990000"), "unsent draft", 1700000555000);
        Assert.AreEqual(Message.Types.ProtocolMessage.Types.Type.DraftMessageSync, draft.ProtocolMessage.Type);
        Assert.AreEqual(DraftMessageSync.Types.Action.Set, draft.ProtocolMessage.DraftMessageSync.Action);
    }

    [TestMethod]
    public void PresenceAndChatStateStanzas_MatchLiveWapWireVectors_And_ReceiptDowngradesToSent()
    {
        // Decode exact live WAWap.encodeStanza outputs captured from web.whatsapp.com
        Node presAvail = new BinaryDecoder(BinaryPacker.Unpack(Convert.FromHexString("00f8051f048889fc0a57696e55492055736572"))).ReadNode();
        Assert.AreEqual("presence", presAvail.Tag);
        Assert.AreEqual("available", presAvail.GetAttr("type"));
        Assert.AreEqual("WinUI User", presAvail.GetAttr("name"));

        Node composing = new BinaryDecoder(BinaryPacker.Unpack(Convert.FromHexString("00f8042e11faff8615552220000f03f801f80134"))).ReadNode();
        Node recording = new BinaryDecoder(BinaryPacker.Unpack(Convert.FromHexString("00f8042e11faff8615552220000f03f801f8033437a1"))).ReadNode();
        Node paused = new BinaryDecoder(BinaryPacker.Unpack(Convert.FromHexString("00f8042e11faff8615552220000f03f801f8019a"))).ReadNode();

        ParsedChatStateStanza? pComp = PresenceStanzas.ParseIncomingChatState(new Node("chatstate", new Dictionary<string, NodeValue> { ["from"] = composing.GetAttr("to")! }, composing.Content));
        ParsedChatStateStanza? pRec = PresenceStanzas.ParseIncomingChatState(new Node("chatstate", new Dictionary<string, NodeValue> { ["from"] = recording.GetAttr("to")! }, recording.Content));
        ParsedChatStateStanza? pPau = PresenceStanzas.ParseIncomingChatState(new Node("chatstate", new Dictionary<string, NodeValue> { ["from"] = paused.GetAttr("to")! }, paused.Content));

        Assert.AreEqual(ChatActivity.Typing, pComp!.Activity);
        Assert.AreEqual(ChatActivity.RecordingAudio, pRec!.Activity);
        Assert.AreEqual(ChatActivity.Idle, pPau!.Activity);

        Node sub = PresenceStanzas.BuildSubscribe(Jid.Pn("15552220000"), name: "Bob", groupContext: Jid.Parse("120363001@g.us"), tcToken: [0x01, 0x02]);
        Assert.AreEqual("120363001@g.us", sub.GetAttr("context"));
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x02 }, ((NodeContent.BytesContent)sub.GetChild("tctoken")!.Content!).Bytes);

        // Receipt with <error reason="lid" type="feature-incapable"/> downgrades delivery -> sent
        Node sentReceipt = new("receipt", new Dictionary<string, NodeValue>
        {
            ["id"] = "MSG-SENT-1",
            ["from"] = Jid.Lid("100000000000002")
        }, new[] { new Node("error", new Dictionary<string, NodeValue> { ["reason"] = "lid", ["type"] = "feature-incapable" }) });

        ParsedReceiptStanza? parsed = RetryAndMediaRetryStanzas.ParseIncomingReceipt(sentReceipt);
        Assert.IsNotNull(parsed);
        Assert.AreEqual("sent", parsed.Type);

        string vcard = RichMessageBuilders.BuildVCard("Alice Smith", "+15551234567", "Contoso");
        Message contact = RichMessageBuilders.BuildContact("Alice Smith", vcard);
        Assert.AreEqual("Alice Smith", contact.ContactMessage.DisplayName);
        Assert.IsTrue(contact.ContactMessage.Vcard.Contains("waid=15551234567"));
        Assert.AreEqual("media", MessageStanzas.ClassifyStanzaType(contact));
        Assert.AreEqual("vcard", MessageStanzas.ClassifyMediaType(contact));
    }
}
