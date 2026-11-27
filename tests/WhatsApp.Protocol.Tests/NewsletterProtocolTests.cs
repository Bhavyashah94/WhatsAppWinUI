using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class NewsletterProtocolTests
{
    private static readonly Jid ChannelJid = Jid.Parse("120363000000000001@newsletter");

    [TestMethod]
    public void NewsletterMediaTypes_PlaintextPathsAndUpload_MatchCdnSpec()
    {
        var cases = new (WaMediaType Type, string MmsType, string Path)[]
        {
            (WaMediaType.NewsletterImage, "newsletter-image", "/mms/newsletter-image"),
            (WaMediaType.NewsletterVideo, "newsletter-video", "/mms/newsletter-video"),
            (WaMediaType.NewsletterAudio, "newsletter-audio", "/mms/newsletter-audio"),
            (WaMediaType.NewsletterPtt, "newsletter-ptt", "/mms/newsletter-audio"),
            (WaMediaType.NewsletterDocument, "newsletter-document", "/mms/newsletter-document"),
            (WaMediaType.NewsletterSticker, "newsletter-sticker", "/mms/newsletter-image"),
            (WaMediaType.NewsletterGif, "newsletter-gif", "/mms/newsletter-video"),
            (WaMediaType.NewsletterLinkThumbnail, "newsletter-thumbnail-link", "/mms/newsletter-thumbnail-link"),
        };

        byte[] sample = "newsletter plaintext media"u8.ToArray();
        byte[] expectedSha = SHA256.HashData(sample);

        foreach (var (type, mmsType, path) in cases)
        {
            Assert.IsFalse(type.IsEncrypted(), $"{type} must be unencrypted plaintext media");
            Assert.AreEqual(mmsType, type.ToMmsType());
            Assert.AreEqual(path, type.GetUploadPath());

            MediaUploadEncryptionResult res = MediaCipher.EncryptForUpload(sample, type);
            Assert.AreEqual(0, res.MediaKey32.Length);
            Assert.IsNull(res.StreamingSidecar);
            CollectionAssert.AreEqual(sample, res.DataToUpload);
            CollectionAssert.AreEqual(expectedSha, res.FileSha256);
            CollectionAssert.AreEqual(expectedSha, res.FileEncSha256);
        }
    }

    [TestMethod]
    public void NewsletterStanzas_GetMessagesAndMyAddOnsAndLiveUpdates_ParseAndValidate()
    {
        Node getMsgs = NewsletterStanzas.BuildGetMessagesIq("iq-nl-1", ChannelJid, 25, beforeServerId: 900);
        Assert.AreEqual("s.whatsapp.net", getMsgs.GetAttr("to"));
        Assert.AreEqual("newsletter", getMsgs.GetAttr("xmlns"));
        Node messagesChild = getMsgs.GetChild("messages")!;
        Assert.AreEqual("jid", messagesChild.GetAttr("type"));
        Assert.AreEqual(ChannelJid.ToString(), messagesChild.GetAttr("jid"));
        Assert.AreEqual("25", messagesChild.GetAttr("count"));
        Assert.AreEqual("900", messagesChild.GetAttr("before"));

        byte[] optHash = AddonEncryption.HashPollOption("Monday");
        byte[] bodyProto = new Message { Conversation = "Channel announcement" }.ToByteArray();

        Node messagesResp = new("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new[]
        {
            new Node("messages", new Dictionary<string, NodeValue> { ["jid"] = ChannelJid }, new[]
            {
                new Node("message", new Dictionary<string, NodeValue>
                {
                    ["id"] = "3EB0CHAN01",
                    ["server_id"] = "850",
                    ["t"] = "1710000100",
                    ["type"] = "poll",
                    ["edit"] = "3",
                    ["is_sender"] = "true"
                }, new Node[]
                {
                    new("forwards_count", new Dictionary<string, NodeValue> { ["count"] = "14" }),
                    new("views_count", new Dictionary<string, NodeValue> { ["type"] = "views", ["count"] = "240" }),
                    new("rcat", new byte[] { 1, 2, 3, 4 }),
                    new("meta", new Dictionary<string, NodeValue>
                    {
                        ["original_msg_t"] = "1710000000",
                        ["msg_edit_t"] = "1710000100123",
                        ["polltype"] = "creation",
                        ["contenttype"] = "text",
                        ["questiontype"] = "question",
                        ["message_association_type"] = "media_poll",
                        ["is_wamo_sub"] = "true"
                    }, new[]
                    {
                        new Node("admin_profile", new Dictionary<string, NodeValue> { ["id"] = "ap-1" }, new[]
                        {
                            new Node("name", "Editor Alice"),
                            new Node("picture", new Dictionary<string, NodeValue> { ["id"] = "p1", ["direct_path"] = "/v/ap.jpg" })
                        })
                    }),
                    new("votes", new[]
                    {
                        new Node("vote", new Dictionary<string, NodeValue> { ["count"] = "7" }, optHash)
                    }),
                    new("reactions", new[]
                    {
                        new Node("reaction", new Dictionary<string, NodeValue> { ["code"] = "👍", ["count"] = "12" })
                    }),
                    new("plaintext", new Dictionary<string, NodeValue> { ["mediatype"] = "image" }, bodyProto)
                }),
                new Node("message", new Dictionary<string, NodeValue>
                {
                    ["id"] = "3EB0CHAN02",
                    ["server_id"] = "849",
                    ["t"] = "1709999000",
                    ["type"] = "text",
                    ["edit"] = "8"
                }, new[] { new Node("plaintext", Array.Empty<byte>()) })
            })
        });

        IReadOnlyList<NewsletterMessage> msgs = NewsletterStanzas.ParseMessagesResponse(messagesResp);
        Assert.AreEqual(2, msgs.Count);
        NewsletterMessage m0 = msgs[0];
        Assert.AreEqual(850UL, m0.ServerId);
        Assert.AreEqual("3EB0CHAN01", m0.MessageId);
        Assert.AreEqual(NewsletterMessageKind.Poll, m0.MessageKind);
        Assert.AreEqual("3", m0.EditAttribute);
        Assert.IsTrue(m0.IsSender);
        Assert.AreEqual("Channel announcement", m0.Message?.Conversation);
        Assert.AreEqual(14UL, m0.ForwardsCount);
        Assert.AreEqual(240UL, m0.ViewsCount);
        Assert.AreEqual(1710000000UL, m0.OriginalTimestampSeconds);
        Assert.AreEqual(1710000100123UL, m0.LastEditTimestampMs);
        Assert.AreEqual("creation", m0.PollType);
        Assert.AreEqual("text", m0.ContentType);
        Assert.IsTrue(m0.IsWamoSub);
        Assert.AreEqual("Editor Alice", m0.AdminProfile?.Name);
        Assert.AreEqual(1, m0.Votes.Count);
        Assert.AreEqual(7UL, m0.Votes[0].Count);
        Assert.AreEqual(1, m0.Reactions.Count);
        Assert.AreEqual("👍", m0.Reactions[0].Code);
        Assert.IsNull(msgs[1].Message);
        Assert.AreEqual("8", msgs[1].EditAttribute);

        // MyAddOns parsing + strict validation
        Node myAddOnsResp = new("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new[]
        {
            new Node("my_addons", new Node[]
            {
                new("messages", new Dictionary<string, NodeValue> { ["jid"] = "120363000000000099@newsletter" }, new[]
                {
                    new Node("message", new Dictionary<string, NodeValue> { ["server_id"] = "1" })
                }),
                new("messages", new Dictionary<string, NodeValue> { ["jid"] = ChannelJid }, new[]
                {
                    new Node("message", new Dictionary<string, NodeValue> { ["server_id"] = "850" }, new Node[]
                    {
                        new("reaction", new Dictionary<string, NodeValue> { ["code"] = "🔥", ["t"] = "1710000200" }),
                        new("votes", new Dictionary<string, NodeValue> { ["t"] = "1710000250" }, new[] { new Node("vote", optHash) })
                    })
                })
            })
        });

        IReadOnlyList<NewsletterMyAddOns> myAddOns = NewsletterStanzas.ParseMyAddOnsResponse(myAddOnsResp, ChannelJid);
        Assert.AreEqual(1, myAddOns.Count);
        Assert.AreEqual("🔥", myAddOns[0].Reaction?.Code);
        Assert.AreEqual(1, myAddOns[0].PollVote?.OptionHashes32.Count);

        Node badAddOns = new("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new[]
        {
            new Node("my_addons", new[]
            {
                new Node("messages", new Dictionary<string, NodeValue> { ["jid"] = ChannelJid }, new[]
                {
                    new Node("message", new Dictionary<string, NodeValue> { ["server_id"] = "850" }, new[]
                    {
                        new Node("votes", new Dictionary<string, NodeValue> { ["t"] = "1" }, new[] { new Node("vote", new byte[31]) })
                    })
                })
            })
        });
        Assert.ThrowsException<InvalidDataException>(() => NewsletterStanzas.ParseMyAddOnsResponse(badAddOns, ChannelJid));

        // Live updates duration + notification parsing
        Node subResp = new("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new[]
        {
            new Node("live_updates", new Dictionary<string, NodeValue> { ["duration"] = "90" })
        });
        Assert.AreEqual(90UL, NewsletterStanzas.ParseLiveUpdatesDurationSeconds(subResp));
        Assert.AreEqual(300UL, NewsletterStanzas.ParseLiveUpdatesDurationSeconds(new Node("iq")));

        Node liveNotif = new("notification", new Dictionary<string, NodeValue>
        {
            ["from"] = ChannelJid,
            ["type"] = "newsletter",
            ["t"] = "1710000300"
        }, new[]
        {
            new Node("live_updates", new[]
            {
                new Node("messages", new[]
                {
                    new Node("message", new Dictionary<string, NodeValue> { ["server_id"] = "850" }, new Node[]
                    {
                        new("forwards_count", new Dictionary<string, NodeValue> { ["count"] = "19" }),
                        new("reactions", new[] { new Node("reaction", new Dictionary<string, NodeValue> { ["code"] = "👍", ["count"] = "22" }) })
                    })
                })
            })
        });

        ParsedNewsletterLiveUpdateNotification? parsedNotif = NewsletterStanzas.ParseLiveUpdateNotification(liveNotif);
        Assert.IsNotNull(parsedNotif);
        Assert.AreEqual(ChannelJid, parsedNotif.ChannelJid);
        Assert.AreEqual(19UL, parsedNotif.Messages[0].ForwardsCount);
        Assert.AreEqual(22UL, parsedNotif.Messages[0].Reactions[0].Count);
    }

    [TestMethod]
    public void NewsletterStanzas_MexDecodersAndPlaintextMessages_WorkEndToEnd()
    {
        using JsonDocument doc = JsonDocument.Parse($$"""
            {
              "xwa2_newsletter_subscribed": [
                { "id": null, "state": { "type": "NON_EXISTING" } },
                {
                  "id": "{{ChannelJid}}",
                  "state": { "type": "ACTIVE" },
                  "thread_metadata": {
                    "name": { "text": "Tech Updates" },
                    "description": { "text": "Release notes" },
                    "subscribers_count": "12345",
                    "verification": "VERIFIED",
                    "invite": "INVITE123",
                    "handle": "techupdates",
                    "creation_time": "1700000000",
                    "picture": { "direct_path": "/v/nl-pic.jpg" },
                    "preview": { "direct_path": "/v/nl-prev.jpg" }
                  },
                  "viewer_metadata": {
                    "role": "ADMIN",
                    "settings": [
                      { "type": "MUTE_ADMIN_ACTIVITY", "value": "ON" },
                      { "type": "MUTE_FOLLOWER_ACTIVITY", "value": "OFF" }
                    ]
                  }
                }
              ]
            }
            """);

        IReadOnlyList<NewsletterMetadata> list = NewsletterStanzas.ParseSubscribedNewsletters(doc.RootElement);
        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("Tech Updates", list[0].Name);
        Assert.AreEqual(12345UL, list[0].SubscriberCount);
        Assert.AreEqual(NewsletterVerification.Verified, list[0].Verification);
        Assert.AreEqual(NewsletterRole.Admin, list[0].Role);
        Assert.AreEqual(true, list[0].Muted);
        Assert.AreEqual(false, list[0].FollowerActivityMuted);

        // Plaintext poll build + stanza encoding
        Message poll = RichMessageBuilders.BuildNewsletterPollCreation("Next release?", ["Alpha", "Beta"], selectableOptionsCount: 1);
        Assert.IsNull(poll.MessageContextInfo);
        Assert.IsFalse(poll.PollCreationMessageV3.HasEncKey);

        Node pollStanza = MessageStanzas.BuildNewsletterPlaintextStanza("NL-POLL-1", ChannelJid, poll);
        Assert.AreEqual("poll", pollStanza.GetAttr("type"));
        Node? meta = pollStanza.GetChild("meta");
        Assert.IsNotNull(meta);
        Assert.AreEqual("creation", meta.GetAttr("polltype"));
        Assert.AreEqual("text", meta.GetAttr("contenttype"));
        Assert.IsNotNull(pollStanza.GetChild("plaintext"));

        // Edit & Revoke
        Node editStanza = MessageStanzas.BuildNewsletterEditStanza(ChannelJid, "NL-MSG-1", new Message { Conversation = "Updated" });
        Assert.AreEqual("3", editStanza.GetAttr("edit"));
        Assert.IsTrue(((NodeContent.BytesContent)editStanza.GetChild("plaintext")!.Content!).Bytes.Length > 0);

        Node revokeStanza = MessageStanzas.BuildNewsletterEditStanza(ChannelJid, "NL-MSG-1", null);
        Assert.AreEqual("8", revokeStanza.GetAttr("edit"));
        Assert.AreEqual(0, ((NodeContent.BytesContent)revokeStanza.GetChild("plaintext")!.Content!).Bytes.Length);
    }
}
