using System.IO.Compression;
using Google.Protobuf;
using Whatsapp;
using PbHistorySync = global::Whatsapp.HistorySync;
using PbHistorySyncType = global::Whatsapp.HistorySync.Types.HistorySyncType;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class WaProtoCodegenTests
{
    [TestMethod]
    public void GeneratedHistorySync_RoundTripsSyntheticBootstrapZlib_Cleanly()
    {
        var bootstrap = new PbHistorySync
        {
            SyncType = PbHistorySyncType.InitialBootstrap,
            Progress = 25,
            ChunkOrder = 1
        };
        bootstrap.Conversations.Add(new Conversation
        {
            Id = "15550101001@s.whatsapp.net",
            Name = "Alice Example",
            UnreadCount = 2,
            LastMsgTimestamp = 1700000100
        });
        bootstrap.Conversations[0].Messages.Add(new HistorySyncMsg
        {
            Message = new WebMessageInfo
            {
                Key = new MessageKey { Id = "SYNTH_MSG_01", RemoteJid = "15550101001@s.whatsapp.net", FromMe = false },
                MessageTimestamp = 1700000100,
                Message = new Message { Conversation = "Synthetic bootstrap greeting" }
            }
        });
        bootstrap.Pushnames.Add(new Pushname { Id = "100000000000101@lid", Pushname_ = "Alice" });
        bootstrap.PhoneNumberToLidMappings.Add(new PhoneNumberToLIDMapping
        {
            PnJid = "15550101001@s.whatsapp.net",
            LidJid = "100000000000101@lid"
        });

        byte[] compressed = CompressZlib(bootstrap.ToByteArray());
        byte[] decompressed = DecompressZlib(compressed);

        var reparsed = PbHistorySync.Parser.ParseFrom(decompressed);

        Assert.IsTrue(reparsed.HasSyncType);
        Assert.AreEqual(PbHistorySyncType.InitialBootstrap, reparsed.SyncType);
        Assert.AreEqual(1, reparsed.Conversations.Count);
        Assert.AreEqual(1, reparsed.Conversations[0].Messages.Count);
        Assert.AreEqual("15550101001@s.whatsapp.net", reparsed.Conversations[0].Id);
        Assert.AreEqual("Alice", reparsed.Pushnames[0].Pushname_);
        Assert.AreEqual("100000000000101@lid", reparsed.PhoneNumberToLidMappings[0].LidJid);
    }

    [TestMethod]
    public void GeneratedHistorySync_RoundTripsSyntheticRecentGroupZlib_Cleanly()
    {
        var recent = new PbHistorySync
        {
            SyncType = PbHistorySyncType.Recent,
            Progress = 100,
            ChunkOrder = 2
        };
        recent.Conversations.Add(new Conversation
        {
            Id = "120363000000000101@g.us",
            Name = "Sample Engineering Group",
            LastMsgTimestamp = 1700000200
        });
        recent.Conversations[0].Messages.Add(new HistorySyncMsg
        {
            Message = new WebMessageInfo
            {
                Key = new MessageKey
                {
                    Id = "SYNTH_MSG_02",
                    RemoteJid = "120363000000000101@g.us",
                    Participant = "100000000000102@lid",
                    FromMe = false
                },
                MessageTimestamp = 1700000200,
                Message = new Message { Conversation = "Synthetic group update" }
            }
        });

        byte[] decompressed = DecompressZlib(CompressZlib(recent.ToByteArray()));
        var reparsed = PbHistorySync.Parser.ParseFrom(decompressed);

        Assert.IsTrue(reparsed.HasSyncType);
        Assert.AreEqual(PbHistorySyncType.Recent, reparsed.SyncType);
        Assert.AreEqual(1, reparsed.Conversations.Count);
        Assert.AreEqual("120363000000000101@g.us", reparsed.Conversations[0].Id);
        Assert.AreEqual("SYNTH_MSG_02", reparsed.Conversations[0].Messages[0].Message.Key.Id);
    }

    [TestMethod]
    public void GeneratedPeerDataOperation_HistorySyncOnDemandRequest_RoundTrips()
    {
        var msg = new Message
        {
            ProtocolMessage = new Message.Types.ProtocolMessage
            {
                Type = Message.Types.ProtocolMessage.Types.Type.PeerDataOperationRequestMessage,
                PeerDataOperationRequestMessage = new Message.Types.PeerDataOperationRequestMessage
                {
                    PeerDataOperationRequestType = Message.Types.PeerDataOperationRequestType.HistorySyncOnDemand,
                    HistorySyncOnDemandRequest = new Message.Types.PeerDataOperationRequestMessage.Types.HistorySyncOnDemandRequest
                    {
                        ChatJid = "15550101001@s.whatsapp.net",
                        OldestMsgId = "SYNTHETIC_MSG_ID_0001",
                        OldestMsgFromMe = true,
                        OldestMsgTimestampMs = 1700000000000,
                        OnDemandMsgCount = 50
                    }
                }
            }
        };

        byte[] wireBytes = msg.ToByteArray();
        var parsed = Message.Parser.ParseFrom(wireBytes);

        Assert.AreEqual(
            Message.Types.ProtocolMessage.Types.Type.PeerDataOperationRequestMessage,
            parsed.ProtocolMessage.Type);
        Assert.AreEqual(
            Message.Types.PeerDataOperationRequestType.HistorySyncOnDemand,
            parsed.ProtocolMessage.PeerDataOperationRequestMessage.PeerDataOperationRequestType);
        var req = parsed.ProtocolMessage.PeerDataOperationRequestMessage.HistorySyncOnDemandRequest;
        Assert.AreEqual("15550101001@s.whatsapp.net", req.ChatJid);
        Assert.AreEqual("SYNTHETIC_MSG_ID_0001", req.OldestMsgId);
        Assert.IsTrue(req.OldestMsgFromMe);
        Assert.AreEqual(1700000000000, req.OldestMsgTimestampMs);
        Assert.AreEqual(50, req.OnDemandMsgCount);
    }

    [TestMethod]
    public void LiveWebSyncedSchemas_DraftSync_ConversationDraft_BusinessFolder_PairingProgress_RoundTrip()
    {
        var proto = new Message
        {
            ProtocolMessage = new Message.Types.ProtocolMessage
            {
                Type = Message.Types.ProtocolMessage.Types.Type.DraftMessageSync,
                MessageEditDisplayType = Message.Types.ProtocolMessage.Types.MessageEditDisplayType.LinkPreviewOnly,
                DraftMessageSync = new DraftMessageSync
                {
                    ChatJid = "15550101001@s.whatsapp.net",
                    Action = DraftMessageSync.Types.Action.Set,
                    EditTimestampMs = 1700000500000,
                    Draft = new Message { Conversation = "Unsent draft" },
                    ClearReason = DraftMessageSync.Types.ClearReason.UserCleared
                }
            }
        };
        var parsedMsg = Message.Parser.ParseFrom(proto.ToByteArray());
        Assert.AreEqual(Message.Types.ProtocolMessage.Types.Type.DraftMessageSync, parsedMsg.ProtocolMessage.Type);
        Assert.AreEqual("Unsent draft", parsedMsg.ProtocolMessage.DraftMessageSync.Draft.Conversation);

        var conv = new Conversation
        {
            Id = "15550101001@s.whatsapp.net",
            UnreadReplyToMyMessageCount = 3,
            Draft = new ConversationDraft
            {
                Content = new Message { Conversation = "Draft text" },
                EditTimestampMs = 1700000500123,
                EditorDeviceId = 1
            }
        };
        var parsedConv = Conversation.Parser.ParseFrom(conv.ToByteArray());
        Assert.AreEqual(3U, parsedConv.UnreadReplyToMyMessageCount);
        Assert.AreEqual("Draft text", parsedConv.Draft.Content.Conversation);

        var cfg = new DeviceProps.Types.HistorySyncConfig { SupportDraftSync = true };
        Assert.IsTrue(DeviceProps.Types.HistorySyncConfig.Parser.ParseFrom(cfg.ToByteArray()).SupportDraftSync);

        var pairing = new ClientPairingProps { IsCompanionPairingProgressUiEnabled = true };
        Assert.IsTrue(ClientPairingProps.Parser.ParseFrom(pairing.ToByteArray()).IsCompanionPairingProgressUiEnabled);

        var bizAction = new SyncActionValue
        {
            Timestamp = 1700000600000,
            BusinessFolderChatPreferenceAction = new SyncActionValue.Types.BusinessFolderChatPreferenceAction
            {
                Preference = SyncActionValue.Types.BusinessFolderChatPreferenceAction.Types.BusinessFolderChatPreference.Automatic
            }
        };
        var parsedAction = SyncActionValue.Parser.ParseFrom(bizAction.ToByteArray());
        Assert.AreEqual(
            SyncActionValue.Types.BusinessFolderChatPreferenceAction.Types.BusinessFolderChatPreference.Automatic,
            parsedAction.BusinessFolderChatPreferenceAction.Preference);

        var annotation = new InteractiveAnnotation
        {
            PixKey = new StatusPixKey
            {
                Key = "pix@example.com",
                KeyType = StatusPixKey.Types.KeyType.Email,
                DisplayName = "Pix Merchant"
            }
        };
        var parsedAnnot = InteractiveAnnotation.Parser.ParseFrom(annotation.ToByteArray());
        Assert.AreEqual(StatusPixKey.Types.KeyType.Email, parsedAnnot.PixKey.KeyType);
        Assert.AreEqual("Pix Merchant", parsedAnnot.PixKey.DisplayName);

        var caps = new DeviceCapabilities
        {
            RecentConversationsOnDemand = new DeviceCapabilities.Types.RecentConversationsOnDemand { Supported = true },
            AiMuseJarvisGzip = new DeviceCapabilities.Types.AiMuseJarvisGzip { SupportVersion = 2 },
            DraftSync = new DeviceCapabilities.Types.DraftSync { Version = 1 }
        };
        var parsedCaps = DeviceCapabilities.Parser.ParseFrom(caps.ToByteArray());
        Assert.IsTrue(parsedCaps.RecentConversationsOnDemand.Supported);
        Assert.AreEqual(2U, parsedCaps.AiMuseJarvisGzip.SupportVersion);
        Assert.AreEqual(1U, parsedCaps.DraftSync.Version);

        var serverErr = new ServerErrorReceipt { StanzaId = "stz-1", KeepMediaKey = true };
        Assert.IsTrue(ServerErrorReceipt.Parser.ParseFrom(serverErr.ToByteArray()).KeepMediaKey);

        var pollOpt = new Message.Types.PollCreationMessage.Types.Option { OptionName = "A", OptionDatetime = 1700000700000 };
        Assert.AreEqual(1700000700000, Message.Types.PollCreationMessage.Types.Option.Parser.ParseFrom(pollOpt.ToByteArray()).OptionDatetime);
    }

    private static byte[] CompressZlib(byte[] uncompressed)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(uncompressed);
        }
        return output.ToArray();
    }

    private static byte[] DecompressZlib(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
