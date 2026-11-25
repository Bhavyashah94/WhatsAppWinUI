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
using LocalWirePair = WhatsApp.Api.Tests.Phase6COrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class Phase7BOrchestrationTests
{
    private static async Task<(DeviceCredentials AliceCreds, DeviceCredentials BobCreds,
        SqliteBackend AliceDb, SqliteBackend BobDb,
        LocalWirePair AliceWire, LocalWirePair BobWire,
        Channel<WhatsAppEvent> AliceEvents, Channel<WhatsAppEvent> BobEvents,
        MessageSender AliceSender, MessageSender BobSender,
        PresenceClient AlicePresence, PresenceClient BobPresence,
        StanzaRouter AliceRouter, StanzaRouter BobRouter)> CreatePairedActorsAsync()
    {
        var aliceDb = new SqliteBackend(":memory:");
        var bobDb = new SqliteBackend(":memory:");

        DeviceCredentials aliceCreds = await aliceDb.Device.LoadOrCreateAsync();
        aliceCreds.Pn = Jid.PnDevice("15551110000", 0);
        aliceCreds.Lid = Jid.LidDevice("100000000000001", 0);
        aliceCreds.PushName = "Alice";
        await aliceDb.Device.SaveAsync(aliceCreds);

        DeviceCredentials bobCreds = await bobDb.Device.LoadOrCreateAsync();
        bobCreds.Pn = Jid.PnDevice("15552220000", 0);
        bobCreds.Lid = Jid.LidDevice("100000000000002", 0);
        bobCreds.PushName = "Bob";
        await bobDb.Device.SaveAsync(bobCreds);

        IReadOnlyList<GeneratedPreKey> bobKeys = PreKeyManager.GeneratePreKeys(1, 3);
        await bobDb.Signal.SavePreKeysAsync(bobKeys.Select(k => (k.Id, k.RecordBytes)).ToArray());

        var bobBundle = new PreKeyBundle(
            bobCreds.RegistrationId, 0, bobCreds.SignedPreKeyId, bobCreds.SignedPreKey.PublicKey,
            bobCreds.SignedPreKeySignature, bobCreds.IdentityKey.PublicKey, bobKeys[0].Id, bobKeys[0].KeyPair.PublicKey);
        SignalSession aliceSession = SignalCipher.InitiateSession(bobBundle, aliceCreds.IdentityKey, aliceCreds.RegistrationId);
        await aliceDb.Signal.SaveIdentityAsync(bobCreds.Pn!.Value.ToProtocolAddress(), bobCreds.IdentityKey.PublicKey);
        await aliceDb.Signal.SaveSessionAsync(bobCreds.Pn.Value.ToProtocolAddress(), aliceSession);

        // Pre-seed cached device lists so SendDmAsync uses existing session directly
        await aliceDb.Protocol.SaveDeviceListAsync("15552220000@s.whatsapp.net", [0u]);
        await aliceDb.Protocol.SaveDeviceListAsync("15551110000@s.whatsapp.net", [0u]);
        await bobDb.Protocol.SaveDeviceListAsync("15551110000@s.whatsapp.net", [0u]);
        await bobDb.Protocol.SaveDeviceListAsync("15552220000@s.whatsapp.net", [0u]);

        var aliceCache = new LidPnCache();
        var bobCache = new LidPnCache();
        aliceCache.Learn("15551110000@s.whatsapp.net", "100000000000001@lid");
        aliceCache.Learn("15552220000@s.whatsapp.net", "100000000000002@lid");
        bobCache.Learn("15551110000@s.whatsapp.net", "100000000000001@lid");
        bobCache.Learn("15552220000@s.whatsapp.net", "100000000000002@lid");

        var aliceEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var bobEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var aliceWire = new LocalWirePair();
        var bobWire = new LocalWirePair();

        var alicePreKeys = new PreKeyFetchClient(aliceWire.Iq, aliceDb, aliceCache, aliceCreds);
        var bobPreKeys = new PreKeyFetchClient(bobWire.Iq, bobDb, bobCache, bobCreds);
        var aliceRetry = new RetryCoordinator(aliceWire.Socket, aliceDb, alicePreKeys, aliceCache, aliceCreds, e => aliceEvents.Writer.WriteAsync(e));
        var bobRetry = new RetryCoordinator(bobWire.Socket, bobDb, bobPreKeys, bobCache, bobCreds, e => bobEvents.Writer.WriteAsync(e));
        var aliceReceiver = new MessageReceiver(aliceWire.Socket, aliceDb, aliceCache, aliceCreds, aliceRetry, e => aliceEvents.Writer.WriteAsync(e));
        var bobReceiver = new MessageReceiver(bobWire.Socket, bobDb, bobCache, bobCreds, bobRetry, e => bobEvents.Writer.WriteAsync(e));
        var aliceUsync = new UsyncClient(aliceWire.Iq, aliceDb, aliceCache);
        var bobUsync = new UsyncClient(bobWire.Iq, bobDb, bobCache);
        var aliceSender = new MessageSender(aliceWire.Socket, aliceUsync, alicePreKeys, aliceDb, aliceCache, aliceCreds, aliceRetry);
        var bobSender = new MessageSender(bobWire.Socket, bobUsync, bobPreKeys, bobDb, bobCache, bobCreds, bobRetry);
        var alicePresence = new PresenceClient(aliceWire.Socket, aliceDb, aliceCreds, e => aliceEvents.Writer.WriteAsync(e));
        var bobPresence = new PresenceClient(bobWire.Socket, bobDb, bobCreds, e => bobEvents.Writer.WriteAsync(e));

        var aliceRouter = new StanzaRouter(aliceWire.Socket, aliceWire.Iq, new StanzaHandlers
        {
            OnMessage = (n, t) => new ValueTask(aliceReceiver.HandleMessageStanzaAsync(n, t)),
            OnPresence = async (n, t) => _ = await alicePresence.HandlePresenceStanzaAsync(n, t),
            OnChatState = async (n, t) => _ = await alicePresence.HandleChatStateStanzaAsync(n, t)
        });
        var bobRouter = new StanzaRouter(bobWire.Socket, bobWire.Iq, new StanzaHandlers
        {
            OnMessage = (n, t) => new ValueTask(bobReceiver.HandleMessageStanzaAsync(n, t)),
            OnPresence = async (n, t) => _ = await bobPresence.HandlePresenceStanzaAsync(n, t),
            OnChatState = async (n, t) => _ = await bobPresence.HandleChatStateStanzaAsync(n, t)
        });

        return (aliceCreds, bobCreds, aliceDb, bobDb, aliceWire, bobWire, aliceEvents, bobEvents,
            aliceSender, bobSender, alicePresence, bobPresence, aliceRouter, bobRouter);
    }

    private static async Task RelayAsync(LocalWirePair fromWire, Jid sender, LocalWirePair toWire)
    {
        Node sent = await fromWire.ReadClientNodeAsync();
        Node? enc = sent.GetChild("participants")?.GetChildren("to").FirstOrDefault()?.GetChild("enc")
            ?? sent.GetChild("enc");
        var attrs = new Dictionary<string, NodeValue>
        {
            ["id"] = sent.GetAttr("id")!,
            ["from"] = sender,
            ["t"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["type"] = sent.GetAttr("type") ?? "text"
        };
        if (sent.GetAttr("edit") is { } ed) attrs["edit"] = ed;
        toWire.SendServerNode(new Node("message", attrs, enc is not null ? new[] { enc } : Array.Empty<Node>()));
        _ = await toWire.ReadClientNodeAsync(); // drain delivery receipt/ack
    }

    private static async Task<T> ReadEventAsync<T>(ChannelReader<WhatsAppEvent> reader, Func<T, bool>? predicate = null)
        where T : WhatsAppEvent
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var seen = new List<string>();
        try
        {
            while (await reader.WaitToReadAsync(cts.Token))
            {
                while (reader.TryRead(out WhatsAppEvent? ev))
                {
                    seen.Add(ev.ToString() ?? ev.GetType().Name);
                    if (ev is T typed && (predicate is null || predicate(typed)))
                    {
                        return typed;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        Assert.Fail($"Timed out waiting for {typeof(T).Name}. Seen: {string.Join(" | ", seen)}");
        throw new InvalidOperationException();
    }

    [TestMethod]
    public async Task RichMessages_Polls_Events_EncReaction_Edits_Pin_And_Presence_EndToEnd()
    {
        var (aliceCreds, bobCreds, aliceDb, bobDb, aliceWire, bobWire, aliceEvents, bobEvents,
            aliceSender, bobSender, alicePresence, bobPresence, aliceRouter, bobRouter) = await CreatePairedActorsAsync();

        try
        {
            Jid aliceJid = aliceCreds.Pn!.Value.ToNonAd();
            Jid bobJid = bobCreds.Pn!.Value.ToNonAd();

            // 1) Alice sends Poll -> Bob receives it (msg secret saved automatically), then Bob votes -> Alice decrypts vote!
            await aliceSender.SendPollAsync(bobJid, "Favorite language?", ["C#", "Rust"], selectableCount: 1, version: 5, messageId: "POLL-100");
            await RelayAsync(aliceWire, aliceJid, bobWire);
            MessageRecord bobPollRec = (await ReadEventAsync<MessageReceivedEvent>(bobEvents.Reader)).Message;
            Assert.AreEqual("Favorite language?", bobPollRec.Text);

            var pollKey = new MessageKey { Id = "POLL-100", RemoteJid = aliceJid.ToString(), FromMe = false };
            await bobSender.SendPollVoteAsync(aliceJid, pollKey, ["C#"], pollCreatorJid: aliceJid.ToString());
            await RelayAsync(bobWire, bobJid, aliceWire);
            MessageAddOnEvent voteEvt = await ReadEventAsync<MessageAddOnEvent>(aliceEvents.Reader, e => e.Kind == "poll_vote");
            Assert.AreEqual(Convert.ToHexString(AddonEncryption.HashPollOption("C#")).ToLowerInvariant(), voteEvt.PayloadText);

            // 2) Alice sends Event -> Bob RSVPs Going -> Alice decrypts EncEventResponseMessage!
            await aliceSender.SendEventAsync(bobJid, "Launch Party", "Phase 7 celebration", 1700001000);
            Node evtWire = await aliceWire.ReadClientNodeAsync();
            string evtId = evtWire.GetAttr("id")!;
            bobWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
            {
                ["id"] = evtId, ["from"] = aliceJid, ["t"] = "1700001000", ["type"] = "event"
            }, new[] { evtWire.GetChild("participants")!.GetChild("to")!.GetChild("enc")! }));
            _ = await bobWire.ReadClientNodeAsync();
            Assert.AreEqual("Launch Party", (await ReadEventAsync<MessageReceivedEvent>(bobEvents.Reader)).Message.Text);

            var evtKey = new MessageKey { Id = evtId, RemoteJid = aliceJid.ToString(), FromMe = false };
            await bobSender.SendEventResponseAsync(aliceJid, evtKey, Message.Types.EventResponseMessage.Types.EventResponseType.Going, aliceJid.ToString(), extraGuests: 1);
            await RelayAsync(bobWire, bobJid, aliceWire);
            MessageAddOnEvent rsvpEvt = await ReadEventAsync<MessageAddOnEvent>(aliceEvents.Reader, e => e.Kind == "event_response");
            Assert.AreEqual("Going", rsvpEvt.PayloadText);
            Assert.IsTrue(rsvpEvt.BoolFlag == true);

            // 3) Alice sends a normal message with reporting token (msg secret stored), then edits & Bob reacts encrypted + pins!
            await aliceSender.SendTextAsync(bobJid, "Original msg", messageId: "TXT-1");
            await RelayAsync(aliceWire, aliceJid, bobWire);
            _ = await ReadEventAsync<MessageReceivedEvent>(bobEvents.Reader);

            var txtKey = new MessageKey { Id = "TXT-1", RemoteJid = bobJid.ToString(), FromMe = true };
            await aliceSender.SendMessageEditAsync(bobJid, txtKey, new Message { Conversation = "Updated msg" }, preferSecretEncrypted: true, originalSenderJid: aliceJid.ToString());
            await RelayAsync(aliceWire, aliceJid, bobWire);
            MessageAddOnEvent editEvt = await ReadEventAsync<MessageAddOnEvent>(bobEvents.Reader, e => e.Kind == "message_edit");
            Assert.AreEqual("Updated msg", editEvt.PayloadText);

            // Bob sends EncReaction & PinInChat
            await bobSender.SendReactionAsync(aliceJid, new MessageKey { Id = "TXT-1", RemoteJid = aliceJid.ToString(), FromMe = false }, "🚀", encrypted: true, parentOriginalSenderJid: aliceJid.ToString());
            await RelayAsync(bobWire, bobJid, aliceWire);
            MessageAddOnEvent encRxEvt = await ReadEventAsync<MessageAddOnEvent>(aliceEvents.Reader, e => e.Kind == "enc_reaction");
            Assert.AreEqual("🚀", encRxEvt.PayloadText);

            await bobSender.SendPinInChatAsync(aliceJid, new MessageKey { Id = "TXT-1", RemoteJid = aliceJid.ToString(), FromMe = false }, pin: true);
            await RelayAsync(bobWire, bobJid, aliceWire);
            MessageAddOnEvent pinEvt = await ReadEventAsync<MessageAddOnEvent>(aliceEvents.Reader, e => e.Kind == "pin_in_chat");
            Assert.IsTrue(pinEvt.BoolFlag == true);

            // 4) Presence & ChatState end-to-end (with TcToken attached automatically on subscribe)
            await aliceDb.Protocol.StoreTcTokenAsync(bobJid.ToString(), [0xDE, 0xAD, 0xBE, 0xEF], 1700001000);
            await alicePresence.SubscribeAsync(bobJid);
            Node subStanza = await aliceWire.ReadClientNodeAsync();
            CollectionAssert.AreEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, ((NodeContent.BytesContent)subStanza.GetChild("tctoken")!.Content!).Bytes);

            await bobPresence.SetAvailabilityAsync(PresenceStatus.Available);
            Node availStanza = await bobWire.ReadClientNodeAsync();
            aliceWire.SendServerNode(new Node("presence", new Dictionary<string, NodeValue>
            {
                ["from"] = bobJid, ["type"] = availStanza.GetAttr("type")!
            }));
            PresenceUpdateEvent presEvt = await ReadEventAsync<PresenceUpdateEvent>(aliceEvents.Reader);
            Assert.IsTrue(presEvt.Available);
            Assert.AreEqual("15552220000@s.whatsapp.net", presEvt.UserJid);

            await bobPresence.SendChatStateAsync(aliceJid, ChatActivity.RecordingAudio);
            Node csStanza = await bobWire.ReadClientNodeAsync();
            aliceWire.SendServerNode(new Node("chatstate", new Dictionary<string, NodeValue> { ["from"] = bobJid }, csStanza.Content));
            ChatStateEvent csEvt = await ReadEventAsync<ChatStateEvent>(aliceEvents.Reader);
            Assert.AreEqual("recording_audio", csEvt.Activity);
        }
        finally
        {
            await aliceRouter.DisposeAsync();
            await bobRouter.DisposeAsync();
            await aliceWire.DisposeAsync();
            await bobWire.DisposeAsync();
            await aliceDb.DisposeAsync();
            await bobDb.DisposeAsync();
        }
    }
}
