using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class Phase8CProtocolTests
{
    [TestMethod]
    public void StatusStanzas_Participants_PrivacyMeta_And_BroadcastStanza_FollowWireRules()
    {
        Jid ownLid = Jid.Parse("100000000000001:1@lid");
        Jid aliceLid = Jid.Parse("100000000000002:2@lid");
        Jid bobPn = Jid.Parse("15550000003:3@s.whatsapp.net");

        // AssembleStatusParticipants deduplicates, strips device IDs, and includes own bare JID
        IReadOnlyList<Jid> audience = StatusStanzas.AssembleStatusParticipants(
            [aliceLid, aliceLid.ToNonAd(), bobPn],
            ownLid);
        CollectionAssert.AreEqual(
            new[]
            {
                Jid.Parse("100000000000002@lid"),
                Jid.Parse("15550000003@s.whatsapp.net"),
                Jid.Parse("100000000000001@lid")
            },
            audience.ToArray());

        Assert.ThrowsException<ArgumentException>(() =>
            StatusStanzas.AssembleStatusParticipants([Jid.Parse("120363000000000001@g.us")], ownLid));

        // Privacy meta presence rules
        Message textStatus = StatusStanzas.BuildTextStatus("Hello Stories", 0xFF224466);
        Message imageStatus = StatusStanzas.BuildImageStatus("/v/img", "image/jpeg", new byte[32], new byte[32], new byte[32], 1024, 1700000000, caption: "Pic");
        Message videoStatus = StatusStanzas.BuildVideoStatus("/v/vid", "video/mp4", new byte[32], new byte[32], new byte[32], 2048, 1700000000, 15, caption: "Clip");
        Message musicStatus = StatusStanzas.BuildMusicStatus("Starlight", "Muse", "https://song.uri", "https://art.uri", (int)Message.Types.MusicMessage.Types.MusicMessageStyle.Vinyl);
        Message revokeStatus = StatusStanzas.BuildStatusRevoke("MSG-STATUS-1");
        Message rxStatus = new()
        {
            ReactionMessage = new Message.Types.ReactionMessage
            {
                Key = new MessageKey { RemoteJid = "status@broadcast", Id = "MSG-STATUS-1", Participant = "100000000000002@lid" },
                Text = "🔥"
            }
        };

        Assert.IsTrue(StatusStanzas.StatusCarriesPrivacyMeta(textStatus));
        Assert.IsTrue(StatusStanzas.StatusCarriesPrivacyMeta(imageStatus));
        Assert.IsTrue(StatusStanzas.StatusCarriesPrivacyMeta(videoStatus));
        Assert.IsTrue(StatusStanzas.StatusCarriesPrivacyMeta(musicStatus));
        Assert.IsFalse(StatusStanzas.StatusCarriesPrivacyMeta(revokeStatus));
        Assert.IsFalse(StatusStanzas.StatusCarriesPrivacyMeta(rxStatus));
        Assert.AreEqual("MSG-STATUS-1", StatusStanzas.GetStatusRevokeTargetId(revokeStatus));
        Assert.IsNull(StatusStanzas.GetStatusRevokeTargetId(textStatus));

        // BuildStatusBroadcastStanza: text post includes SKDM <to><enc/>, bare audience <to>, addressing_mode, and <meta status_setting="allowlist"/>
        Jid aliceDev0 = Jid.Parse("100000000000002@lid");
        Node postStanza = StatusStanzas.BuildStatusBroadcastStanza(
            "ST-POST-1",
            audience,
            textStatus,
            [1, 2, 3],
            skdmTargets: [(aliceDev0, "msg", [9, 8, 7])],
            privacy: StatusPrivacySetting.AllowList,
            addressingMode: "lid");

        Assert.AreEqual("status@broadcast", postStanza.GetAttr("to"));
        Assert.AreEqual("text", postStanza.GetAttr("type"));
        Assert.AreEqual("lid", postStanza.GetAttr("addressing_mode"));
        Assert.AreEqual("allowlist", postStanza.GetChild("meta")?.GetAttr("status_setting"));
        Assert.AreEqual("skmsg", postStanza.GetChild("enc")?.GetAttr("type"));

        Node[] participants = postStanza.GetChild("participants")!.GetChildren("to").ToArray();
        Assert.AreEqual(3, participants.Length);
        Assert.AreEqual("100000000000002@lid", participants[0].GetAttr("jid"));
        Assert.IsNotNull(participants[0].GetChild("enc")); // SKDM target
        Assert.AreEqual("15550000003@s.whatsapp.net", participants[1].GetAttr("jid"));
        Assert.IsNull(participants[1].GetChild("enc")); // bare audience entry
        Assert.AreEqual("100000000000001@lid", participants[2].GetAttr("jid"));

        // Revoke stanza omits <meta status_setting> and avoids server 479 SmaxInvalid
        Node revokeStanza = StatusStanzas.BuildStatusBroadcastStanza(
            "ST-REVOKE-1",
            audience,
            revokeStatus,
            [4, 5, 6],
            privacy: StatusPrivacySetting.AllowList);
        Assert.IsNull(revokeStanza.GetChild("meta"));
    }

    [TestMethod]
    public void StatusStanzas_Music_GroupStatusV2_QuestionAnswer_StickerInteraction_And_Classify()
    {
        Message music = StatusStanzas.BuildMusicStatus("Bohemian Rhapsody", "Queen", "spotify:track:123", style: 3);
        Assert.AreEqual("Bohemian Rhapsody", music.MusicMessage.EmbeddedMusic.Title);
        Assert.AreEqual("Queen", music.MusicMessage.EmbeddedMusic.Author);
        Assert.AreEqual("spotify:track:123", music.MusicMessage.SongUri);
        Assert.AreEqual(3, music.MusicMessage.Style);

        Message groupStatus = StatusStanzas.BuildGroupStatusV2(StatusStanzas.BuildTextStatus("Group update!"));
        Assert.AreEqual("Group update!", groupStatus.GroupStatusMessageV2.Message.ExtendedTextMessage.Text);
        Assert.AreEqual("text", MessageStanzas.ClassifyStanzaType(groupStatus));

        var key = new MessageKey
        {
            RemoteJid = "status@broadcast",
            Id = "ST-QA-1",
            Participant = "100000000000002@lid"
        };
        Message qa = StatusStanzas.BuildStatusQuestionAnswer(key, "42!");
        Assert.AreEqual("42!", qa.StatusQuestionAnswerMessage.Text);
        Assert.AreEqual("text", MessageStanzas.ClassifyStanzaType(qa));

        Message sticker = StatusStanzas.BuildStatusStickerInteraction(
            key,
            "stk-heart",
            Message.Types.StatusStickerInteractionMessage.Types.StatusStickerType.Reaction);
        Assert.AreEqual("stk-heart", sticker.StatusStickerInteractionMessage.StickerKey);
        Assert.AreEqual("text", MessageStanzas.ClassifyStanzaType(sticker));
    }
}
