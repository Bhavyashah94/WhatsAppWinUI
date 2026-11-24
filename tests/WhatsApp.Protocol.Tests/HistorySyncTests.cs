using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using WhatsApp.Protocol.HistorySync;
using PbHistorySync = global::Whatsapp.HistorySync;
using PbHistorySyncType = global::Whatsapp.HistorySync.Types.HistorySyncType;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class HistorySyncTests
{
    [TestMethod]
    public void HistorySyncCrypto_Derives_Expected_Key_Lengths()
    {
        byte[] mediaKey = new byte[32];
        RandomNumberGenerator.Fill(mediaKey);

        var (iv, cipherKey, macKey) = HistorySyncCrypto.DeriveMediaKeys(mediaKey, HistorySyncCrypto.HistoryInfo);

        Assert.AreEqual(16, iv.Length, "IV must be 16 bytes");
        Assert.AreEqual(32, cipherKey.Length, "CipherKey must be 32 bytes");
        Assert.AreEqual(32, macKey.Length, "MacKey must be 32 bytes");
    }

    [TestMethod]
    public void HistorySyncCrypto_RoundTrip_Encryption_Decryption()
    {
        byte[] mediaKey = new byte[32];
        RandomNumberGenerator.Fill(mediaKey);

        byte[] payload = Encoding.UTF8.GetBytes("Test History Payload for WhatsApp Multi-Device Parity Verification!");

        byte[] encBlob = HistorySyncCrypto.EncryptBlob(payload, mediaKey, HistorySyncCrypto.HistoryInfo);
        Assert.IsTrue(encBlob.Length > payload.Length, "Encrypted blob must include padding and 10-byte MAC");

        byte[] decrypted = HistorySyncCrypto.DecryptHistoryChunk(encBlob, mediaKey);
        CollectionAssert.AreEqual(payload, decrypted, "Decrypted payload must match original plaintext");
    }

    [TestMethod]
    public void HistorySyncCrypto_Rejects_Corrupted_Mac()
    {
        byte[] mediaKey = new byte[32];
        RandomNumberGenerator.Fill(mediaKey);
        byte[] payload = Encoding.UTF8.GetBytes("Test Integrity");

        byte[] encBlob = HistorySyncCrypto.EncryptBlob(payload, mediaKey, HistorySyncCrypto.HistoryInfo);
        encBlob[^1] ^= 0xFF;

        Assert.ThrowsException<CryptographicException>(() =>
        {
            HistorySyncCrypto.DecryptHistoryChunk(encBlob, mediaKey);
        });
    }

    [TestMethod]
    public void HistorySyncCrypto_Rejects_Mismatched_EncSha256()
    {
        byte[] mediaKey = new byte[32];
        RandomNumberGenerator.Fill(mediaKey);
        byte[] payload = Encoding.UTF8.GetBytes("Test Hash Mismatch");

        byte[] encBlob = HistorySyncCrypto.EncryptBlob(payload, mediaKey, HistorySyncCrypto.HistoryInfo);
        byte[] wrongHash = new byte[32];

        Assert.ThrowsException<CryptographicException>(() =>
        {
            HistorySyncCrypto.DecryptHistoryChunk(encBlob, mediaKey, expectedEncSha256: wrongHash);
        });
    }

    [TestMethod]
    public void HistorySyncInflater_RoundTrip_Zlib_Compression_Decompression()
    {
        byte[] original = new byte[10000];
        RandomNumberGenerator.Fill(original);

        byte[] compressed = HistorySyncInflater.CompressZlib(original);
        Assert.IsTrue(compressed.Length > 0);

        byte[] decompressed = HistorySyncInflater.Decompress(compressed);
        CollectionAssert.AreEqual(original, decompressed);
    }

    [TestMethod]
    public void HistorySyncDecoder_Harvests_Field15_LidMappings()
    {
        var sync = new PbHistorySync { SyncType = PbHistorySyncType.Recent };

        sync.PhoneNumberToLidMappings.Add(new global::Whatsapp.PhoneNumberToLIDMapping
        {
            PnJid = "15550100101@s.whatsapp.net",
            LidJid = "100000000000101@lid"
        });

        sync.PhoneNumberToLidMappings.Add(new global::Whatsapp.PhoneNumberToLIDMapping
        {
            PnJid = "15550100102@s.whatsapp.net",
            LidJid = "100000000000102@lid"
        });

        byte[] protoBytes = sync.ToByteArray();

        var batch = HistorySyncDecoder.Decode(protoBytes, chunkOrder: 1, progress: 10);

        Assert.AreEqual(PbHistorySyncType.Recent, batch.SyncType);
        Assert.AreEqual(1u, batch.ChunkOrder);
        Assert.AreEqual(10u, batch.Progress);
        Assert.AreEqual(2, batch.LidMappings.Count);

        Assert.AreEqual("15550100101@s.whatsapp.net", batch.LidMappings[0].PnJid);
        Assert.AreEqual("100000000000101@lid", batch.LidMappings[0].LidJid);

        Assert.AreEqual("15550100102@s.whatsapp.net", batch.LidMappings[1].PnJid);
        Assert.AreEqual("100000000000102@lid", batch.LidMappings[1].LidJid);
    }

    [TestMethod]
    public void HistorySyncDecoder_Harvests_Conversations_And_Classifies_Messages()
    {
        var conv = new global::Whatsapp.Conversation
        {
            Id = "120363000000000101@g.us",
            Name = "Sample Discussion Group",
            UnreadCount = 3,
            LastMsgTimestamp = 1700000834
        };

        var textMsg = new global::Whatsapp.HistorySyncMsg
        {
            Message = new global::Whatsapp.WebMessageInfo
            {
                Key = new global::Whatsapp.MessageKey { Id = "MSG_1", RemoteJid = conv.Id, FromMe = false, Participant = "100000000000201@lid" },
                MessageTimestamp = 1700000829,
                Message = new global::Whatsapp.Message { Conversation = "Synthetic sample message payload" }
            }
        };

        byte[] dummyMediaKey = new byte[32];
        var stickerMsg = new global::Whatsapp.HistorySyncMsg
        {
            Message = new global::Whatsapp.WebMessageInfo
            {
                Key = new global::Whatsapp.MessageKey { Id = "MSG_2", RemoteJid = conv.Id, FromMe = false, Participant = "100000000000202@lid" },
                MessageTimestamp = 1700000834,
                Message = new global::Whatsapp.Message
                {
                    StickerMessage = new global::Whatsapp.Message.Types.StickerMessage
                    {
                        DirectPath = "/v/t62.15575-24/dummy.enc",
                        MediaKey = ByteString.CopyFrom(dummyMediaKey),
                        Mimetype = "image/webp"
                    }
                }
            }
        };

        conv.Messages.Add(textMsg);
        conv.Messages.Add(stickerMsg);

        var sync = new PbHistorySync { SyncType = PbHistorySyncType.Recent };
        sync.Conversations.Add(conv);

        byte[] protoBytes = sync.ToByteArray();
        var batch = HistorySyncDecoder.Decode(protoBytes);

        Assert.AreEqual(1, batch.Conversations.Count);
        var decodedConv = batch.Conversations[0];

        Assert.AreEqual("120363000000000101@g.us", decodedConv.Id);
        Assert.AreEqual("Sample Discussion Group", decodedConv.Name);
        Assert.AreEqual(3u, decodedConv.UnreadCount);
        Assert.AreEqual(2, decodedConv.Messages.Count);

        var m1 = decodedConv.Messages[0];
        Assert.AreEqual(MessageType.Text, m1.Type);
        Assert.AreEqual("Synthetic sample message payload", m1.Text);
        Assert.AreEqual("100000000000201@lid", m1.SenderJid);

        var m2 = decodedConv.Messages[1];
        Assert.AreEqual(MessageType.Sticker, m2.Type);
        Assert.AreEqual("image/webp", m2.MimeType);
        Assert.AreEqual("/v/t62.15575-24/dummy.enc", m2.DirectPath);
        Assert.AreEqual("100000000000202@lid", m2.SenderJid);
    }

    [TestMethod]
    public void HistorySyncDecoder_Full_EndToEnd_EncryptedChunk_Pipeline()
    {
        byte[] mediaKey = new byte[32];
        RandomNumberGenerator.Fill(mediaKey);

        var sync = new PbHistorySync { SyncType = PbHistorySyncType.Full };
        sync.PhoneNumberToLidMappings.Add(new global::Whatsapp.PhoneNumberToLIDMapping
        {
            PnJid = "15550100103@s.whatsapp.net",
            LidJid = "100000000000103@lid"
        });
        sync.Pushnames.Add(new global::Whatsapp.Pushname
        {
            Id = "100000000000103@lid",
            Pushname_ = "Alice"
        });

        byte[] rawProto = sync.ToByteArray();
        byte[] zlibCompressed = HistorySyncInflater.CompressZlib(rawProto);
        byte[] encChunk = HistorySyncCrypto.EncryptBlob(zlibCompressed, mediaKey, HistorySyncCrypto.HistoryInfo);

        var batch = HistorySyncDecoder.DecodeEncryptedChunk(encChunk, mediaKey, chunkOrder: 1, progress: 100);

        Assert.AreEqual(PbHistorySyncType.Full, batch.SyncType);
        Assert.AreEqual(1, batch.LidMappings.Count);
        Assert.AreEqual("15550100103@s.whatsapp.net", batch.LidMappings[0].PnJid);
        Assert.AreEqual("100000000000103@lid", batch.LidMappings[0].LidJid);

        Assert.AreEqual(1, batch.Pushnames.Count);
        Assert.AreEqual("Alice", batch.Pushnames[0].Pushname);
    }
}
