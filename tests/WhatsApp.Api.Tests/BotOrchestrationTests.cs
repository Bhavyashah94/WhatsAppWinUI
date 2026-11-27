using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;
using WhatsApp.Storage.Sqlite;
using LocalWirePair = WhatsApp.Api.Tests.UsyncAndNotificationOrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class BotOrchestrationTests
{
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Node BuildBotPreKeyUserNode(Jid jid, DeviceCredentials creds, uint preKeyId, byte[] preKeyPub) =>
        new("user", new Dictionary<string, NodeValue> { ["jid"] = jid }, new Node[]
        {
            new("registration", StanzaBuilder.EncodeUInt32BigEndian(creds.RegistrationId)),
            new("type", new[] { PreKeyIq.DjbKeyType }),
            new("identity", creds.IdentityKey.PublicKey),
            new("key", new Node[] { new("id", StanzaBuilder.EncodeKeyId24(preKeyId)), new("value", preKeyPub) }),
            new("skey", new Node[]
            {
                new("id", StanzaBuilder.EncodeKeyId24(creds.SignedPreKeyId)),
                new("value", creds.SignedPreKey.PublicKey),
                new("signature", creds.SignedPreKeySignature)
            })
        });

    [TestMethod]
    public async Task BotClient_DirectoryProfilesModesCrlAndAutoReply_WorkEndToEnd()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1710000000));
        await using var backend = new SqliteBackend(":memory:");
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15550000001", 1);
        creds.Lid = Jid.LidDevice("100000000000001", 1);
        await backend.Device.SaveAsync(creds);

        var lidPn = new LidPnCache();
        var events = Channel.CreateUnbounded<WhatsAppEvent>();
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var usync = new UsyncClient(wire.Iq, backend, lidPn);
        var prekeys = new PreKeyFetchClient(wire.Iq, backend, lidPn, creds);
        var retry = new RetryCoordinator(wire.Socket, backend, prekeys, lidPn, creds, e => events.Writer.WriteAsync(e));
        var sender = new MessageSender(wire.Socket, usync, prekeys, backend, lidPn, creds, retry, timeProvider: time);
        var mex = new MexClient(wire.Iq);
        var bots = new BotClient(wire.Iq, mex, sender, creds, e => events.Writer.WriteAsync(e));

        // 1) GetBotListAsync
        Task<BotList> listTask = bots.GetBotListAsync();
        Node listReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("bot", listReq.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = listReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("bot", new Dictionary<string, NodeValue> { ["v"] = "2" }, new[]
            {
                new Node("default", new Dictionary<string, NodeValue> { ["jid"] = Jid.Parse("867051314767696@bot"), ["persona_id"] = "p-default" }),
                new Node("section", new Dictionary<string, NodeValue> { ["name"] = "All", ["type"] = "all" }, new[]
                {
                    new Node("bot", new Dictionary<string, NodeValue> { ["jid"] = Jid.Parse("867051314767696@bot"), ["persona_id"] = "p-default" })
                })
            })
        }));
        BotList dir = await listTask;
        Assert.AreEqual("p-default", dir.DefaultBot!.PersonaId);
        Assert.IsTrue(events.Reader.TryRead(out WhatsAppEvent? ev1) && ev1 is BotDirectoryLoadedEvent);

        // 2) GetBotProfilesAsync via MEX
        Task<IReadOnlyList<BotGqlProfile>> profTask = bots.GetBotProfilesAsync(["p-default"]);
        Node profReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual(MexStanzas.FetchBotProfilesGql.Id, profReq.GetChild("query")!.GetAttr("query_id"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = profReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("result", """{"data":{"xfb_fetch_genai_personas":[{"id":"p-default","jid":"867051314767696@bot","is_meta_created":true,"latest_published_version_for_viewer":{"name":"Meta AI","description":"Helpful AI","icebreaker_prompt_list":["Hi"]}}]}}""")
        }));
        var profs = await profTask;
        Assert.AreEqual("Meta AI", profs[0].Name);

        // 3) GetDynamicAiModesAsync
        Task<IReadOnlyList<DynamicAiMode>> modesTask = bots.GetDynamicAiModesAsync();
        Node modesReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual(MexStanzas.FetchDynamicAiModes.Id, modesReq.GetChild("query")!.GetAttr("query_id"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = modesReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("result", """{"data":{"xfb_meta_ai_modes":[{"mode_id":"fast","title":"Fast","subtitle":"Quick","is_experimental":false}]}}""")
        }));
        var modes = await modesTask;
        Assert.AreEqual("fast", modes[0].ModeId);

        // 4) GetBotCertificateRevocationListAsync + SetAiAgentAutoReplyAsync
        Task<BotCertificateRevocationList?> crlTask = bots.GetBotCertificateRevocationListAsync("bot_tee_crl");
        Node crlReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual(MexStanzas.FetchBotCertificateRevocationList.Id, crlReq.GetChild("query")!.GetAttr("query_id"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = crlReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("result", """{"data":{"xwa2_fetch_feature_pki_crl":{"crl":"PEM-CRL","next_update":1710009999}}}""")
        }));
        var crl = await crlTask;
        Assert.AreEqual("PEM-CRL", crl?.CrlPem);

        Task autoTask = bots.SetAiAgentAutoReplyAsync(Jid.Parse("15550009999@s.whatsapp.net"), enabled: true);
        Node autoReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual(MexStanzas.AiAgentAutoReplyControl.Id, autoReq.GetChild("query")!.GetAttr("query_id"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = autoReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("result", """{"data":{"ok":true}}""")
        }));
        await autoTask;

        // 5) GetUsyncBotProfileAsync via native usync
        Task<UsyncBotProfileInfo?> usyncProfTask = bots.GetUsyncBotProfileAsync(Jid.Parse("867051314767696@bot"), "p-default");
        Node usyncReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("usync", usyncReq.GetAttr("xmlns"));
        Assert.AreEqual("p-default", usyncReq.GetChild("usync")!.GetChild("list")!.GetChild("user")!.GetChild("bot")!.GetChild("profile")!.GetAttr("persona_id"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = usyncReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("usync", new Dictionary<string, NodeValue> { ["sid"] = "s1", ["index"] = "0", ["last"] = "true" }, new[]
            {
                new Node("list", new[]
                {
                    new Node("user", new Dictionary<string, NodeValue> { ["jid"] = Jid.Parse("867051314767696@bot") }, new[]
                    {
                        new Node("bot", new[]
                        {
                            new Node("profile", new Dictionary<string, NodeValue> { ["persona_id"] = "p-default" }, new[]
                            {
                                new Node("name", "Meta AI"),
                                new Node("attributes", "assistant"),
                                new Node("description", "Official Meta AI"),
                                new Node("category", "synthetic"),
                                new Node("default", "true"),
                                new Node("is_meta_created", "true")
                            })
                        })
                    })
                })
            })
        }));
        UsyncBotProfileInfo? usyncProf = await usyncProfTask;
        Assert.IsNotNull(usyncProf);
        Assert.AreEqual("Meta AI", usyncProf.Name);
        Assert.AreEqual("p-default", usyncProf.PersonaId);
        Assert.IsTrue(usyncProf.IsDefault);
        Assert.IsTrue(usyncProf.IsMetaCreated == true);
    }

    [TestMethod]
    public async Task BotClient_SendPrompt_And_MessageReceiver_MsmsgRichResponseAndFeedback_WorkEndToEnd()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1710001000);
        var time = new FakeTimeProvider(now);

        await using var aliceBackend = new SqliteBackend(":memory:");
        DeviceCredentials aliceCreds = await aliceBackend.Device.LoadOrCreateAsync();
        aliceCreds.Pn = Jid.PnDevice("15550000001", 1);
        aliceCreds.Lid = Jid.LidDevice("100000000000001", 1);
        await aliceBackend.Device.SaveAsync(aliceCreds);

        Jid botJid = Jid.Parse("867051314767696@bot");
        await using var botBackend = new SqliteBackend(":memory:");
        DeviceCredentials botCreds = await botBackend.Device.LoadOrCreateAsync();
        botCreds.Pn = botJid;
        await botBackend.Device.SaveAsync(botCreds);
        var botOtk = PreKeyManager.GeneratePreKeys(1, 1)[0];

        await aliceBackend.Protocol.SaveDeviceListAsync(botJid.ToString(), [0u]);
        await aliceBackend.Protocol.SaveDeviceListAsync(aliceCreds.Lid.Value.ToNonAdString(), [1u]);

        var lidPn = new LidPnCache();
        var events = new List<WhatsAppEvent>();
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var usync = new UsyncClient(wire.Iq, aliceBackend, lidPn);
        var prekeys = new PreKeyFetchClient(wire.Iq, aliceBackend, lidPn, aliceCreds);
        var retry = new RetryCoordinator(wire.Socket, aliceBackend, prekeys, lidPn, aliceCreds, ev => { events.Add(ev); return ValueTask.CompletedTask; });
        var sender = new MessageSender(wire.Socket, usync, prekeys, aliceBackend, lidPn, aliceCreds, retry, timeProvider: time);
        var mex = new MexClient(wire.Iq);
        var bots = new BotClient(wire.Iq, mex, sender, aliceCreds, ev => { events.Add(ev); return ValueTask.CompletedTask; });
        var receiver = new MessageReceiver(wire.Socket, aliceBackend, lidPn, aliceCreds, retry, ev => { events.Add(ev); return ValueTask.CompletedTask; }, time);

        byte[] promptSecret = RandomNumberGenerator.GetBytes(32);
        Task<SendMessageResult> promptTask = bots.SendBotPromptAsync(
            botJid,
            "Explain C# spans",
            personaId: "p-meta-ai",
            messageSecret32: promptSecret,
            messageId: "PROMPT-1");

        // Handle prekey fetch for bot@bot
        Node pkReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("encrypt", pkReq.GetAttr("xmlns"));
        byte[] botOtkPub = PreKeyManager.DecodePreKeyRecord(botOtk.RecordBytes).KeyPair.PublicKey;
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = pkReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("list", new[] { BuildBotPreKeyUserNode(botJid, botCreds, 1, botOtkPub) })
        }));

        Node sentStanza = await wire.ReadClientNodeAsync();
        Assert.AreEqual("PROMPT-1", sentStanza.GetAttr("id"));
        Node? directBotNode = sentStanza.GetChild("bot");
        Assert.IsNotNull(directBotNode);
        Assert.AreEqual("prompt", directBotNode.GetAttr("type"));
        Assert.AreEqual("direct_chat", directBotNode.GetAttr("agent_engagement_type"));
        SendMessageResult sent = await promptTask;
        Assert.AreEqual("PROMPT-1", sent.MessageId);

        // 1) Bot replies with <enc type="msmsg"> containing AIRichResponseMessage + BotMetadata,
        //    plus standalone AI_QUERY_FANOUT and AI_PSI_METADATA ProtocolMessages.
        var replyMsg = RichMessageBuilders.BuildRichResponseText([
            (AIRichResponseSubMessageType.AiRichResponseText, "Span<T> is stack-only.", null),
            (AIRichResponseSubMessageType.AiRichResponseCode, "Span<byte> s = stackalloc byte[8];", "csharp")
        ]);
        replyMsg.MessageContextInfo = new MessageContextInfo
        {
            BotMetadata = new BotMetadata { PersonaId = "p-meta-ai", BotResponseId = "RESP-1" }
        };

        var ctx1 = new BotMessageContext("BOT-REPLY-1", aliceCreds.Lid.Value.ToNonAdString(), botJid.ToString());
        (byte[] msmsg1, _, _) = BotMessageCrypto.EncryptMsmsgEnvelope(replyMsg.ToByteArray(), promptSecret, ctx1);
        Node inbound1 = new("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "BOT-REPLY-1",
            ["from"] = botJid,
            ["t"] = "1710001002"
        }, new[]
        {
            new Node("meta", new Dictionary<string, NodeValue>
            {
                ["target_id"] = "PROMPT-1",
                ["target_sender_jid"] = aliceCreds.Lid.Value.ToNonAd(),
                ["target_chat_jid"] = botJid
            }),
            new Node("bot", new Dictionary<string, NodeValue> { ["edit"] = "first", ["biz_bot"] = "1" }),
            new Node("enc", new Dictionary<string, NodeValue> { ["v"] = "2", ["type"] = "msmsg" }, msmsg1)
        });

        await receiver.HandleMessageStanzaAsync(inbound1);
        _ = await wire.ReadClientNodeAsync(); // delivery receipt

        Message fanoutMsg = RichMessageBuilders.BuildBotAiQueryFanoutMessage(new AIQueryFanout
        {
            MessageKey = new MessageKey { Id = "FAN-99" },
            Timestamp = 1710001001,
            Message = new Message { Conversation = "c# span explanation" }
        });
        (byte[] msmsgFanout, _, _) = BotMessageCrypto.EncryptMsmsgEnvelope(
            fanoutMsg.ToByteArray(),
            promptSecret,
            new BotMessageContext("FANOUT-IN-1", aliceCreds.Lid.Value.ToNonAdString(), botJid.ToString()));
        await receiver.HandleMessageStanzaAsync(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "FANOUT-IN-1",
            ["from"] = botJid,
            ["t"] = "1710001002"
        }, new[]
        {
            new Node("meta", new Dictionary<string, NodeValue> { ["target_id"] = "PROMPT-1", ["target_sender_jid"] = aliceCreds.Lid.Value.ToNonAd() }),
            new Node("enc", new Dictionary<string, NodeValue> { ["v"] = "2", ["type"] = "msmsg" }, msmsgFanout)
        }));
        _ = await wire.ReadClientNodeAsync();

        Message psiMsg = RichMessageBuilders.BuildBotAiPsiMetadataMessage("psi-reply"u8.ToArray());
        (byte[] msmsgPsi, _, _) = BotMessageCrypto.EncryptMsmsgEnvelope(
            psiMsg.ToByteArray(),
            promptSecret,
            new BotMessageContext("PSI-IN-1", aliceCreds.Lid.Value.ToNonAdString(), botJid.ToString()));
        await receiver.HandleMessageStanzaAsync(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "PSI-IN-1",
            ["from"] = botJid,
            ["t"] = "1710001002"
        }, new[]
        {
            new Node("meta", new Dictionary<string, NodeValue> { ["target_id"] = "PROMPT-1", ["target_sender_jid"] = aliceCreds.Lid.Value.ToNonAd() }),
            new Node("enc", new Dictionary<string, NodeValue> { ["v"] = "2", ["type"] = "msmsg" }, msmsgPsi)
        }));
        _ = await wire.ReadClientNodeAsync();

        var richEvt = events.OfType<BotRichResponseReceivedEvent>().Single();
        Assert.AreEqual("BOT-REPLY-1", richEvt.MessageId);
        Assert.AreEqual("p-meta-ai", richEvt.PersonaId);
        Assert.AreEqual("RESP-1", richEvt.BotResponseId);
        Assert.IsTrue(richEvt.RichResponse.SummaryText.Contains("Span<T> is stack-only.", StringComparison.Ordinal));
        Assert.AreEqual(1, events.OfType<BotQueryFanoutReceivedEvent>().Count(e => e.FanoutMessageId == "FAN-99"));
        Assert.AreEqual(1, events.OfType<BotPsiMetadataReceivedEvent>().Count());

        // 2) Bot sends a streaming INNER edit chunk encrypted under edit_target_id="BOT-REPLY-1"
        var replyChunk = RichMessageBuilders.BuildRichResponseText([
            (AIRichResponseSubMessageType.AiRichResponseText, "Span<T> provides zero-copy slicing.", null)
        ]);
        (byte[] msmsg2, _, _) = BotMessageCrypto.EncryptMsmsgEnvelope(replyChunk.ToByteArray(), promptSecret, ctx1);
        Node inboundEdit = new("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "BOT-CHUNK-2",
            ["from"] = botJid,
            ["t"] = "1710001003"
        }, new[]
        {
            new Node("meta", new Dictionary<string, NodeValue>
            {
                ["target_id"] = "PROMPT-1",
                ["target_sender_jid"] = aliceCreds.Lid.Value.ToNonAd(),
                ["target_chat_jid"] = botJid
            }),
            new Node("bot", new Dictionary<string, NodeValue> { ["edit"] = "inner", ["edit_target_id"] = "BOT-REPLY-1" }),
            new Node("enc", new Dictionary<string, NodeValue> { ["v"] = "2", ["type"] = "msmsg" }, msmsg2)
        });

        await receiver.HandleMessageStanzaAsync(inboundEdit);
        _ = await wire.ReadClientNodeAsync(); // receipt

        var editEvt = events.OfType<BotRichResponseReceivedEvent>().Last();
        Assert.AreEqual(BotEditType.Inner, editEvt.EditType);
        Assert.AreEqual("BOT-REPLY-1", editEvt.EditTargetId);
        Assert.IsTrue(editEvt.RichResponse.SummaryText.Contains("zero-copy slicing", StringComparison.Ordinal));

        // 3) User sends BotFeedbackMessage & inbound BotFeedbackMessage emits BotFeedbackReceivedEvent
        SendMessageResult fbSent = await bots.SendFeedbackAsync(
            botJid,
            new MessageKey { RemoteJid = botJid.ToString(), FromMe = false, Id = "BOT-REPLY-1" },
            BotFeedbackMessage.Types.BotFeedbackKind.BotFeedbackPositive,
            text: "Great example",
            messageId: "FB-1");
        Assert.AreEqual("FB-1", fbSent.MessageId);
        _ = await wire.ReadClientNodeAsync();

        var inboundFbProto = RichMessageBuilders.BuildBotFeedbackMessage(
            new MessageKey { RemoteJid = botJid.ToString(), FromMe = false, Id = "BOT-REPLY-1" },
            BotFeedbackMessage.Types.BotFeedbackKind.BotFeedbackNegativeHelpful,
            text: "Too short",
            kindNegativeMask: 2);
        var inboundFbCtx = new BotMessageContext("FB-IN-1", aliceCreds.Lid.Value.ToNonAdString(), botJid.ToString());
        (byte[] msmsgFb, _, _) = BotMessageCrypto.EncryptMsmsgEnvelope(inboundFbProto.ToByteArray(), promptSecret, inboundFbCtx);
        Node inboundFbNode = new("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "FB-IN-1",
            ["from"] = botJid,
            ["t"] = "1710001004"
        }, new[]
        {
            new Node("meta", new Dictionary<string, NodeValue> { ["target_id"] = "PROMPT-1", ["target_sender_jid"] = aliceCreds.Lid.Value.ToNonAd() }),
            new Node("enc", new Dictionary<string, NodeValue> { ["v"] = "2", ["type"] = "msmsg" }, msmsgFb)
        });
        await receiver.HandleMessageStanzaAsync(inboundFbNode);
        _ = await wire.ReadClientNodeAsync();

        var fbEvt = events.OfType<BotFeedbackReceivedEvent>().Single();
        Assert.AreEqual("BOT-REPLY-1", fbEvt.TargetMessageId);
        Assert.AreEqual(BotFeedbackMessage.Types.BotFeedbackKind.BotFeedbackNegativeHelpful, fbEvt.Kind);
        Assert.AreEqual("Too short", fbEvt.Text);
    }

    [TestMethod]
    public async Task BotClient_GroupMemberPrompt_EmitsBotFanoutWithBotMessageSecretAndMentionName()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1710002000));
        await using var aliceBackend = new SqliteBackend(":memory:");
        DeviceCredentials aliceCreds = await aliceBackend.Device.LoadOrCreateAsync();
        aliceCreds.Pn = Jid.PnDevice("15550000001", 1);
        aliceCreds.Lid = Jid.LidDevice("100000000000001", 1);
        await aliceBackend.Device.SaveAsync(aliceCreds);

        Jid botJid = Jid.Parse("867051314767696@bot");
        await using var botBackend = new SqliteBackend(":memory:");
        DeviceCredentials botCreds = await botBackend.Device.LoadOrCreateAsync();
        botCreds.Pn = botJid;
        await botBackend.Device.SaveAsync(botCreds);
        GeneratedPreKey botOtk = PreKeyManager.GeneratePreKeys(1, 1)[0];

        Jid groupJid = Jid.Parse("120363424906033083@g.us");
        await aliceBackend.Protocol.SaveDeviceListAsync(aliceCreds.Lid.Value.ToNonAdString(), [1u]);

        var lidPn = new LidPnCache();
        var events = new List<WhatsAppEvent>();
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var usync = new UsyncClient(wire.Iq, aliceBackend, lidPn);
        var prekeys = new PreKeyFetchClient(wire.Iq, aliceBackend, lidPn, aliceCreds);
        var retry = new RetryCoordinator(wire.Socket, aliceBackend, prekeys, lidPn, aliceCreds, ev => { events.Add(ev); return ValueTask.CompletedTask; });
        var sender = new MessageSender(wire.Socket, usync, prekeys, aliceBackend, lidPn, aliceCreds, retry, timeProvider: time);
        var mex = new MexClient(wire.Iq);
        var bots = new BotClient(wire.Iq, mex, sender, aliceCreds, ev => { events.Add(ev); return ValueTask.CompletedTask; });

        byte[] promptSecret = RandomNumberGenerator.GetBytes(32);
        Task<SendMessageResult> sendTask = bots.SendBotPromptAsync(
            groupJid,
            "@867051314767696 what is 4+4?",
            mentionedBotJid: botJid,
            groupParticipants: [aliceCreds.Lid.Value.ToNonAd(), botJid],
            messageSecret32: promptSecret,
            messageId: "GRP-BOT-1");

        Node pkReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("encrypt", pkReq.GetAttr("xmlns"));
        byte[] botOtkPub = PreKeyManager.DecodePreKeyRecord(botOtk.RecordBytes).KeyPair.PublicKey;
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = pkReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("list", new[] { BuildBotPreKeyUserNode(botJid, botCreds, 1, botOtkPub) })
        }));

        Node sentStanza = await wire.ReadClientNodeAsync();
        SendMessageResult res = await sendTask;
        Assert.AreEqual("GRP-BOT-1", res.MessageId);
        Assert.AreEqual(2, res.TargetDeviceCount);

        // Human participants node must NOT receive SKDM for the bot
        Assert.IsNull(sentStanza.GetChild("participants"));
        Assert.IsNotNull(sentStanza.GetChild("enc"));
        Assert.AreEqual("skmsg", sentStanza.GetChild("enc")!.GetAttr("type"));

        // Pairwise <bot agent_engagement_type="member"><to jid="867051314767696@bot"><enc ...>
        Node? botNode = sentStanza.GetChild("bot");
        Assert.IsNotNull(botNode);
        Assert.AreEqual("member", botNode.GetAttr("agent_engagement_type"));
        Node? toNode = botNode.GetChild("to");
        Assert.IsNotNull(toNode);
        Assert.AreEqual("867051314767696@bot", toNode.GetAttr("jid"));
        Node? encNode = toNode.GetChild("enc");
        Assert.IsNotNull(encNode);
        byte[] pkBytes = ((NodeContent.BytesContent)encNode.Content!).Bytes;

        (byte[] paddedPlain, _) = SignalCipher.DecryptPreKey(
            WhatsApp.Protocol.Signal.PreKeySignalMessage.Parse(pkBytes),
            botCreds.IdentityKey,
            botCreds.SignedPreKey,
            botOtk.KeyPair);
        Message botDecrypted = MessageFraming.DecodeAndUnpadMessage(paddedPlain);

        Assert.AreEqual("@Meta AI what is 4+4?", botDecrypted.ExtendedTextMessage.Text);
        Assert.IsFalse(botDecrypted.MessageContextInfo.HasMessageSecret);
        CollectionAssert.AreEqual(
            BotMessageCrypto.DeriveBaseBotKey(promptSecret),
            botDecrypted.MessageContextInfo.BotMessageSecret.ToByteArray());
    }

    [TestMethod]
    public async Task BotClient_DirectPrompt_PreservesMessageSecretForCompanions_And_UnwrapsCompanionDsmSecret()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1710003000));
        await using var aliceBackend = new SqliteBackend(":memory:");
        DeviceCredentials aliceCreds = await aliceBackend.Device.LoadOrCreateAsync();
        aliceCreds.Pn = Jid.PnDevice("15550000001", 1);
        aliceCreds.Lid = Jid.LidDevice("100000000000001", 1);
        await aliceBackend.Device.SaveAsync(aliceCreds);

        // Own primary/companion peer device (:0) that will both receive DSM from :1 and send DSM to :1
        await using var compBackend = new SqliteBackend(":memory:");
        DeviceCredentials compCreds = await compBackend.Device.LoadOrCreateAsync();
        compCreds.Pn = Jid.PnDevice("15550000001", 0);
        compCreds.Lid = Jid.LidDevice("100000000000001", 0);
        await compBackend.Device.SaveAsync(compCreds);
        GeneratedPreKey compOtk = PreKeyManager.GeneratePreKeys(1, 1)[0];

        Jid botJid = Jid.Parse("867051314767696@bot");
        await using var botBackend = new SqliteBackend(":memory:");
        DeviceCredentials botCreds = await botBackend.Device.LoadOrCreateAsync();
        botCreds.Pn = botJid;
        await botBackend.Device.SaveAsync(botCreds);
        GeneratedPreKey botOtk = PreKeyManager.GeneratePreKeys(1, 1)[0];

        await aliceBackend.Protocol.SaveDeviceListAsync(botJid.ToString(), [0u]);
        await aliceBackend.Protocol.SaveDeviceListAsync(aliceCreds.Lid.Value.ToNonAdString(), [0u, 1u]);

        var lidPn = new LidPnCache();
        var events = new List<WhatsAppEvent>();
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());
        var usync = new UsyncClient(wire.Iq, aliceBackend, lidPn);
        var prekeys = new PreKeyFetchClient(wire.Iq, aliceBackend, lidPn, aliceCreds);
        var retry = new RetryCoordinator(wire.Socket, aliceBackend, prekeys, lidPn, aliceCreds, ev => { events.Add(ev); return ValueTask.CompletedTask; });
        var sender = new MessageSender(wire.Socket, usync, prekeys, aliceBackend, lidPn, aliceCreds, retry, timeProvider: time);
        var mex = new MexClient(wire.Iq);
        var bots = new BotClient(wire.Iq, mex, sender, aliceCreds, ev => { events.Add(ev); return ValueTask.CompletedTask; });
        var receiver = new MessageReceiver(wire.Socket, aliceBackend, lidPn, aliceCreds, retry, ev => { events.Add(ev); return ValueTask.CompletedTask; }, time);

        byte[] promptSecret = RandomNumberGenerator.GetBytes(32);
        Task<SendMessageResult> promptTask = bots.SendBotPromptAsync(
            botJid,
            "what is 6+6?",
            personaId: "p-meta-ai",
            messageSecret32: promptSecret,
            messageId: "PROMPT-SYM-1");

        Node pkReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("encrypt", pkReq.GetAttr("xmlns"));
        byte[] botOtkPub = PreKeyManager.DecodePreKeyRecord(botOtk.RecordBytes).KeyPair.PublicKey;
        byte[] compOtkPub = PreKeyManager.DecodePreKeyRecord(compOtk.RecordBytes).KeyPair.PublicKey;
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = pkReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("list", new[]
            {
                BuildBotPreKeyUserNode(botJid, botCreds, 1, botOtkPub),
                BuildBotPreKeyUserNode(compCreds.Lid.Value, compCreds, 1, compOtkPub)
            })
        }));

        Node sentStanza = await wire.ReadClientNodeAsync();
        await promptTask;

        Node[] toNodes = sentStanza.GetChild("participants")!.GetChildren("to").ToArray();
        Node botTo = toNodes.Single(n => Jid.Parse(n.GetAttr("jid")!).User == botJid.User);
        Node compTo = toNodes.Single(n => Jid.Parse(n.GetAttr("jid")!).User == aliceCreds.Lid.Value.User);

        (byte[] botPlain, _) = SignalCipher.DecryptPreKey(
            WhatsApp.Protocol.Signal.PreKeySignalMessage.Parse(((NodeContent.BytesContent)botTo.GetChild("enc")!.Content!).Bytes),
            botCreds.IdentityKey, botCreds.SignedPreKey, botOtk.KeyPair);
        Message botMsg = MessageFraming.DecodeAndUnpadMessage(botPlain);
        Assert.IsFalse(botMsg.MessageContextInfo.HasMessageSecret);
        CollectionAssert.AreEqual(BotMessageCrypto.DeriveBaseBotKey(promptSecret), botMsg.MessageContextInfo.BotMessageSecret.ToByteArray());

        (byte[] compPlain, _) = SignalCipher.DecryptPreKey(
            WhatsApp.Protocol.Signal.PreKeySignalMessage.Parse(((NodeContent.BytesContent)compTo.GetChild("enc")!.Content!).Bytes),
            compCreds.IdentityKey, compCreds.SignedPreKey, compOtk.KeyPair);
        Message compDsm = MessageFraming.DecodeAndUnpadMessage(compPlain);
        Assert.IsNotNull(compDsm.DeviceSentMessage);
        Assert.IsNotNull(compDsm.MessageContextInfo);
        CollectionAssert.AreEqual(promptSecret, compDsm.MessageContextInfo.MessageSecret.ToByteArray());
        Assert.IsFalse(compDsm.MessageContextInfo.HasBotMessageSecret);

        // Now simulate companion :2 sending a DM prompt to botJid wrapped with hoisted outer MessageSecret
        // and delivered to :1, followed by Meta AI replying <enc type="msmsg"> referencing that prompt!
        byte[] compPromptSecret = RandomNumberGenerator.GetBytes(32);
        Message compPromptDsm = MessageFraming.WrapDeviceSentMessage(
            new Message
            {
                Conversation = "hello from companion",
                MessageContextInfo = new MessageContextInfo
                {
                    MessageSecret = ByteString.CopyFrom(compPromptSecret)
                }
            },
            botJid.ToString());

        // Establish inbound session from comp :2 -> alice :1 using alice's prekey
        GeneratedPreKey aliceOtpk = PreKeyManager.GeneratePreKeys(1, 1)[0];
        await aliceBackend.Signal.SavePreKeysAsync([(aliceOtpk.Id, aliceOtpk.RecordBytes)]);
        SignalSession compAliceSess = SignalCipher.InitiateSession(
            new PreKeyBundle(
                aliceCreds.RegistrationId,
                0,
                aliceCreds.SignedPreKeyId,
                aliceCreds.SignedPreKey.PublicKey,
                aliceCreds.SignedPreKeySignature,
                aliceCreds.IdentityKey.PublicKey,
                aliceOtpk.Id,
                aliceOtpk.KeyPair.PublicKey),
            compCreds.IdentityKey,
            compCreds.RegistrationId);
        SignalEncryptedPayload dsmEnc = SignalCipher.Encrypt(compAliceSess, MessageFraming.EncodeAndPadMessage(compPromptDsm), compCreds.IdentityKey, compCreds.RegistrationId);

        await receiver.HandleMessageStanzaAsync(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "COMP-PROMPT-1",
            ["from"] = aliceCreds.Lid.Value.ToNonAd(),
            ["t"] = "1710003005"
        }, new[]
        {
            new Node("enc", new Dictionary<string, NodeValue> { ["v"] = "2", ["type"] = dsmEnc.WireType }, dsmEnc.Bytes)
        }));
        _ = await wire.ReadClientNodeAsync(); // receipt

        var botReply = RichMessageBuilders.BuildRichResponseText([(AIRichResponseSubMessageType.AiRichResponseText, "Hello from Meta AI!", null)]);
        (byte[] msmsgReply, _, _) = BotMessageCrypto.EncryptMsmsgEnvelope(
            botReply.ToByteArray(),
            compPromptSecret,
            new BotMessageContext("BOT-AFTER-COMP-1", aliceCreds.Lid.Value.ToNonAdString(), botJid.ToString()));

        await receiver.HandleMessageStanzaAsync(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "BOT-AFTER-COMP-1",
            ["from"] = botJid,
            ["t"] = "1710003006"
        }, new[]
        {
            new Node("meta", new Dictionary<string, NodeValue> { ["target_id"] = "COMP-PROMPT-1" }),
            new Node("bot", new Dictionary<string, NodeValue> { ["edit"] = "last" }),
            new Node("enc", new Dictionary<string, NodeValue> { ["v"] = "2", ["type"] = "msmsg" }, msmsgReply)
        }));
        _ = await wire.ReadClientNodeAsync(); // receipt

        MessageReceivedEvent? rec = events.OfType<MessageReceivedEvent>().LastOrDefault(e => e.Message.Id == "BOT-AFTER-COMP-1");
        Assert.IsNotNull(rec);
        Assert.AreEqual("Hello from Meta AI!", rec.Message.Text);
    }
}
