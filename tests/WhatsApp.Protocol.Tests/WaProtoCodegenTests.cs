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
