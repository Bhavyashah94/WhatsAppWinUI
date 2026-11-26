using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class Phase9CProtocolTests
{
    [TestMethod]
    public void Jid_IsBot_MatchesFbidAndPnBotsAccurately()
    {
        Jid fbidBot = Jid.Parse("867051314767696@bot");
        Jid smbBot = Jid.Parse("718584497008509@bot");
        Jid pnMetaBot = Jid.Parse("13135550002@s.whatsapp.net");
        Jid pnSmbBot = Jid.Parse("13135550202@s.whatsapp.net");
        Jid pnAltBot = Jid.Parse("13165550001@s.whatsapp.net");
        Jid fbidBotDev = Jid.Parse("867051314767696:1@bot");
        Jid regularUser = Jid.Parse("15551234567@s.whatsapp.net");
        Jid lidBot = Jid.Parse("103242709127222@lid");

        Assert.IsTrue(fbidBot.IsBot());
        Assert.IsTrue(fbidBot.IsFbidBot());
        Assert.IsFalse(fbidBot.IsPnBot());

        Assert.IsTrue(smbBot.IsBot());
        Assert.IsTrue(pnMetaBot.IsBot());
        Assert.IsTrue(pnMetaBot.IsPnBot());
        Assert.IsTrue(pnSmbBot.IsBot());
        Assert.IsTrue(pnAltBot.IsBot());

        Assert.IsFalse(fbidBotDev.IsBot());
        Assert.IsFalse(regularUser.IsBot());
        Assert.IsFalse(lidBot.IsBot());
    }

    [TestMethod]
    public void BotStanzas_GetBotListV2AndV3_BuildParseAndValidate()
    {
        Node reqV2 = BotStanzas.BuildGetBotList("iq-bot-1");
        Assert.AreEqual("bot", reqV2.GetAttr("xmlns"));
        Assert.AreEqual("get", reqV2.GetAttr("type"));
        Assert.AreEqual("2", reqV2.GetChild("bot")!.GetAttr("v"));

        Node reqV3 = BotStanzas.BuildGetBotList("iq-bot-2", "hash-xyz");
        Assert.AreEqual("3", reqV3.GetChild("bot")!.GetAttr("v"));
        Assert.AreEqual("hash-xyz", reqV3.GetChild("bot")!.GetAttr("bhash"));

        Node respV2 = new("iq", new Dictionary<string, NodeValue> { ["id"] = "iq-bot-1", ["type"] = "result" }, new[]
        {
            new Node("bot", new Dictionary<string, NodeValue> { ["v"] = "2" }, new[]
            {
                new Node("default", new Dictionary<string, NodeValue> { ["jid"] = Jid.Parse("867051314767696@bot"), ["persona_id"] = "meta-ai-p1" }),
                new Node("section", new Dictionary<string, NodeValue> { ["name"] = "Featured", ["type"] = "featured", ["display_type"] = "carousel", ["show_more"] = "true" }, new[]
                {
                    new Node("theme", new Dictionary<string, NodeValue> { ["mode"] = "dark", ["primary_color"] = "#112233", ["secondary_color"] = "#445566" }),
                    new Node("bot", new Dictionary<string, NodeValue> { ["jid"] = Jid.Parse("867051314767696@bot"), ["persona_id"] = "meta-ai-p1", ["default"] = "true" }),
                    new Node("bot", new Dictionary<string, NodeValue> { ["jid"] = Jid.Parse("718584497008509@bot"), ["persona_id"] = "helper-p2" })
                })
            })
        });

        BotList parsed = BotStanzas.ParseGetBotListResponse(respV2);
        Assert.AreEqual(BotListVersion.V2, parsed.Version);
        Assert.AreEqual("meta-ai-p1", parsed.DefaultBot!.PersonaId);
        Assert.AreEqual(1, parsed.Sections.Count);
        Assert.AreEqual(BotSectionType.Featured, parsed.Sections[0].SectionType);
        Assert.AreEqual(BotSectionDisplayType.Carousel, parsed.Sections[0].DisplayType);
        Assert.AreEqual(BotThemeMode.Dark, parsed.Sections[0].Theme!.Mode);
        Assert.AreEqual(2, parsed.Flatten().Count);

        // v2 without <default> must fail
        Node badV2 = new("iq", new[] { new Node("bot", new Dictionary<string, NodeValue> { ["v"] = "2" }) });
        Assert.ThrowsException<InvalidDataException>(() => BotStanzas.ParseGetBotListResponse(badV2));

        // v3 without bhash must fail
        Node badV3 = new("iq", new[] { new Node("bot", new Dictionary<string, NodeValue> { ["v"] = "3" }) });
        Assert.ThrowsException<InvalidDataException>(() => BotStanzas.ParseGetBotListResponse(badV3));
    }

    [TestMethod]
    public void BotStanzas_MexPayloads_DecodeProfilesModesAndCrl()
    {
        string profilesJson = """
            {"data":{"xfb_fetch_genai_personas":[{"id":"persona-1","jid":"867051314767696@bot","is_meta_created":true,"creator":{"name":"Meta","profile_uri":"https://meta.ai"},"latest_published_version_for_viewer":{"name":"Meta AI","description":"Assistant","posing_as_professional":false,"icebreaker_prompt_list":["Summarize news","Draft email"]}}]}}
            """;
        var profiles = BotStanzas.ParseBotProfilesResponse(profilesJson);
        Assert.AreEqual(1, profiles.Count);
        Assert.AreEqual("Meta AI", profiles[0].Name);
        Assert.AreEqual("Meta", profiles[0].CreatorName);
        Assert.AreEqual(2, profiles[0].IcebreakerPrompts.Count);

        string modesJson = """
            {"data":{"xfb_meta_ai_modes":[{"mode_id":"fast","title":"Fast","subtitle":"Quick replies","is_experimental":false},{"mode_id":"research","title":"Research","subtitle":"Deep search","is_experimental":true}]}}
            """;
        var modes = BotStanzas.ParseDynamicAiModesResponse(modesJson);
        Assert.AreEqual(2, modes.Count);
        Assert.IsTrue(modes[1].IsExperimental);

        string crlJson = """
            {"data":{"xwa2_fetch_feature_pki_crl":{"crl":"-----BEGIN X509 CRL-----\nABC\n-----END X509 CRL-----","next_update":1710099999}}}
            """;
        BotCertificateRevocationList? crl = BotStanzas.ParseBotCrlResponse(crlJson);
        Assert.IsNotNull(crl);
        Assert.IsTrue(crl.CrlPem.Contains("BEGIN X509 CRL", StringComparison.Ordinal));
        Assert.AreEqual(1710099999L, crl.NextUpdateSeconds);
    }

    [TestMethod]
    public void BotMessageCrypto_TwoPassHkdfAesGcm_RoundTripAndFallbackWork()
    {
        byte[] secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var ctx = new BotMessageContext("BOT-REPLY-1", "100000000000001@lid", "867051314767696@bot");
        var richMsg = RichMessageBuilders.BuildRichResponseText([
            (AIRichResponseSubMessageType.AiRichResponseText, "Hello from Meta AI", null),
            (AIRichResponseSubMessageType.AiRichResponseCode, "Console.WriteLine(42);", "csharp")
        ]);
        byte[] rawProto = richMsg.ToByteArray();

        (byte[] msmsgBytes, _, _) = BotMessageCrypto.EncryptMsmsgEnvelope(rawProto, secret, ctx);

        // Direct open (no PKCS7 padding)
        byte[] opened = BotMessageCrypto.DecryptMsmsgEnvelope(msmsgBytes, secret, ctx);
        CollectionAssert.AreEqual(rawProto, opened);
        Message decoded = Message.Parser.ParseFrom(opened);
        ParsedBotRichResponse parsedRich = BotStanzas.ExtractRichResponse(decoded.RichResponseMessage);
        Assert.IsTrue(parsedRich.SummaryText.Contains("Hello from Meta AI", StringComparison.Ordinal));
        Assert.IsTrue(parsedRich.SummaryText.Contains("```csharp", StringComparison.Ordinal));

        // Streaming fallback context: primary fails on chunk id, fallback on edit_target_id succeeds
        var wrongPrimary = new BotMessageContext("CHUNK-99", "100000000000001@lid", "867051314767696@bot");
        byte[] openedViaFallback = BotMessageCrypto.DecryptMsmsgEnvelope(msmsgBytes, secret, wrongPrimary, ctx);
        CollectionAssert.AreEqual(rawProto, openedViaFallback);

        // Wrong secret / JID fails authentication
        byte[] badSecret = secret.ToArray();
        badSecret[0] ^= 0xFF;
        Assert.ThrowsException<AuthenticationTagMismatchException>(() =>
            BotMessageCrypto.DecryptMsmsgEnvelope(msmsgBytes, badSecret, ctx));
    }

    [TestMethod]
    public void BotStanzasAndBuilders_ParseMetaAndBotAttributesAndBuildInvokeFeedbackWelcome()
    {
        byte[] psi = "psi-blob"u8.ToArray();
        var fanout = new AIQueryFanout
        {
            MessageKey = new MessageKey { Id = "FAN-1" },
            Timestamp = 1710000500,
            Message = new Message { Conversation = "weather today" }
        };

        Message invoke = RichMessageBuilders.BuildBotInvokeMessage(
            new Message { Conversation = "@Meta AI hi" },
            invokerJid: "100000000000001@lid",
            personaId: "meta-ai-p1",
            mentionedJids: ["867051314767696@bot"]);

        Message psiMsg = RichMessageBuilders.BuildBotAiPsiMetadataMessage(psi, "100000000000001@lid");
        Message fanoutMsg = RichMessageBuilders.BuildBotAiQueryFanoutMessage(fanout, "100000000000001@lid");

        Assert.AreEqual("text", MessageStanzas.ClassifyStanzaType(invoke));
        Assert.AreEqual(32, invoke.MessageContextInfo.MessageSecret.Length);
        Assert.AreEqual("meta-ai-p1", invoke.MessageContextInfo.BotMetadata.PersonaId);
        Assert.AreEqual("867051314767696@bot", invoke.BotInvokeMessage.Message.ExtendedTextMessage.ContextInfo.MentionedJid[0]);
        CollectionAssert.AreEqual(psi, psiMsg.ProtocolMessage.AiPsiMetadata.ToByteArray());
        Assert.AreEqual("FAN-1", fanoutMsg.ProtocolMessage.AiQueryFanout.MessageKey.Id);

        Message feedback = RichMessageBuilders.BuildBotFeedbackMessage(
            new MessageKey { RemoteJid = "867051314767696@bot", FromMe = false, Id = "BOT-MSG-1" },
            BotFeedbackMessage.Types.BotFeedbackKind.BotFeedbackNegativeAccurate,
            text: "Wrong date",
            kindNegativeMask: (ulong)BotFeedbackMessage.Types.BotFeedbackKindMultipleNegative.BotFeedbackMultipleNegativeAccurate);
        Assert.AreEqual("text", MessageStanzas.ClassifyStanzaType(feedback));
        Assert.IsTrue(MessageStanzas.ShouldHideDecryptFail(feedback));

        Message welcome = RichMessageBuilders.BuildBotRequestWelcomeMessage(
            Message.Types.RequestWelcomeMessageMetadata.Types.WelcomeTrigger.ChatOpen,
            Message.Types.RequestWelcomeMessageMetadata.Types.LocalChatState.Empty);
        Assert.IsTrue(MessageStanzas.ShouldHideDecryptFail(welcome));

        Node incomingStanza = new("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "CHUNK-1",
            ["from"] = Jid.Parse("867051314767696@bot"),
            ["t"] = "1710000600"
        }, new[]
        {
            new Node("meta", new Dictionary<string, NodeValue>
            {
                ["target_id"] = "REQ-1",
                ["target_sender_jid"] = Jid.Parse("100000000000001@lid"),
                ["target_chat_jid"] = Jid.Parse("867051314767696@bot")
            }),
            new Node("bot", new Dictionary<string, NodeValue>
            {
                ["edit"] = "inner",
                ["edit_target_id"] = "BOT-ROOT-1",
                ["sender_timestamp_ms"] = "1710000600123",
                ["biz_bot"] = "1"
            }),
            new Node("enc", new Dictionary<string, NodeValue> { ["v"] = "2", ["type"] = "msmsg" }, new byte[] { 1, 2, 3 })
        });

        IncomingMessageStanza? parsed = MessageStanzas.ParseIncomingMessage(incomingStanza);
        Assert.IsNotNull(parsed);
        Assert.AreEqual("REQ-1", parsed.MetaTargetId);
        Assert.AreEqual("100000000000001@lid", parsed.MetaTargetSenderJid?.ToString());
        Assert.AreEqual(BotEditType.Inner, parsed.BotInfo?.EditType);
        Assert.AreEqual("BOT-ROOT-1", parsed.BotInfo?.EditTargetId);
        Assert.AreEqual("1", parsed.BotInfo?.BizBotType);
        Assert.AreEqual("msmsg", parsed.EncPayloads[0].EncType);
    }
}
