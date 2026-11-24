using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using WhatsApp.Protocol.HistorySync;
using PbHistorySync = global::Whatsapp.HistorySync;
using PbHistorySyncType = global::Whatsapp.HistorySync.Types.HistorySyncType;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class WirePacketParityTests
{
    private static byte[] CreateSyntheticWebP(int totalLength = 256)
    {
        var webp = new byte[totalLength];
        RandomNumberGenerator.Fill(webp);
        "RIFF"u8.CopyTo(webp.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(webp.AsSpan(4, 4), totalLength - 8);
        "WEBP"u8.CopyTo(webp.AsSpan(8, 4));
        return webp;
    }

    [TestMethod]
    public void Decrypt_Synthetic_Sticker_Packet_Succeeds_And_Matches_Hashes_BitForBit()
    {
        byte[] mediaKey = RandomNumberGenerator.GetBytes(32);
        byte[] plainWebP = CreateSyntheticWebP();
        byte[] expectedFilehash = SHA256.HashData(plainWebP);

        byte[] encBytes = HistorySyncCrypto.EncryptBlob(plainWebP, mediaKey, HistorySyncCrypto.StickerInfo);
        byte[] expectedEncFilehash = SHA256.HashData(encBytes);

        // 1. Encrypted payload is AES-CBC padded plaintext + 10-byte truncated HMAC
        Assert.AreEqual(((plainWebP.Length / 16) + 1) * 16 + 10, encBytes.Length);

        // 2. Decrypt with HistorySyncCrypto engine (matching MediaCipher HKDF-112 + AES-256-CBC + HMAC-SHA256)
        byte[] decrypted = HistorySyncCrypto.DecryptBlob(
            encBytes,
            mediaKey,
            HistorySyncCrypto.StickerInfo,
            expectedEncFilehash);

        // 3. Verify plaintext length and SHA-256 match
        Assert.AreEqual(plainWebP.Length, decrypted.Length);
        CollectionAssert.AreEqual(expectedFilehash, SHA256.HashData(decrypted));
        CollectionAssert.AreEqual(plainWebP, decrypted);

        // 4. Verify WebP container header
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(decrypted, 0, 4));
        Assert.AreEqual("WEBP", Encoding.ASCII.GetString(decrypted, 8, 4));
    }

    [TestMethod]
    public void Decrypt_Synthetic_Sticker_Packet_Tampering_Throws_CryptographicException()
    {
        byte[] mediaKey = RandomNumberGenerator.GetBytes(32);
        byte[] plainWebP = CreateSyntheticWebP();
        byte[] encBytes = HistorySyncCrypto.EncryptBlob(plainWebP, mediaKey, HistorySyncCrypto.StickerInfo);

        // Tamper 1 byte in ciphertext
        byte[] tampered = (byte[])encBytes.Clone();
        tampered[50] ^= 0xFF;

        Assert.ThrowsException<CryptographicException>(() =>
        {
            HistorySyncCrypto.DecryptBlob(tampered, mediaKey, HistorySyncCrypto.StickerInfo);
        });

        // Tamper 1 byte in trailing MAC
        byte[] tamperedMac = (byte[])encBytes.Clone();
        tamperedMac[^3] ^= 0x01;

        Assert.ThrowsException<CryptographicException>(() =>
        {
            HistorySyncCrypto.DecryptBlob(tamperedMac, mediaKey, HistorySyncCrypto.StickerInfo);
        });

        // Wrong media key
        byte[] wrongKey = (byte[])mediaKey.Clone();
        wrongKey[0] ^= 0x42;

        Assert.ThrowsException<CryptographicException>(() =>
        {
            HistorySyncCrypto.DecryptBlob(encBytes, wrongKey, HistorySyncCrypto.StickerInfo);
        });
    }

    [TestMethod]
    public void Decompress_And_Decode_Synthetic_Recent_Chunk_Extracts_Entities()
    {
        var proto = new PbHistorySync
        {
            SyncType = PbHistorySyncType.Recent,
            ChunkOrder = 6,
            Progress = 75
        };

        for (int c = 1; c <= 4; c++)
        {
            var conv = new global::Whatsapp.Conversation
            {
                Id = $"12036300000000010{c}@g.us",
                Name = $"Synthetic Group {c}",
                UnreadCount = (uint)c,
                LastMsgTimestamp = (ulong)(1700000000 + c)
            };
            conv.Messages.Add(new global::Whatsapp.HistorySyncMsg
            {
                Message = new global::Whatsapp.WebMessageInfo
                {
                    Key = new global::Whatsapp.MessageKey
                    {
                        Id = $"SYNTH_C6_{c}",
                        RemoteJid = conv.Id,
                        Participant = $"10000000000020{c}@lid",
                        FromMe = false
                    },
                    MessageTimestamp = (ulong)(1700000000 + c),
                    Message = new global::Whatsapp.Message { Conversation = $"Message payload {c}" }
                }
            });
            proto.Conversations.Add(conv);
        }

        byte[] compressed = HistorySyncInflater.CompressZlib(proto.ToByteArray());
        // RFC 1950 Zlib CMF byte: 0x78 (deflate with 32K window)
        Assert.AreEqual(0x78, compressed[0]);

        byte[] decompressed = HistorySyncInflater.Decompress(compressed);
        Assert.IsTrue(decompressed.Length <= HistorySyncInflater.DefaultMaxDecompressedBytes);

        var batch = HistorySyncDecoder.Decode(decompressed, chunkOrder: 6, progress: 75);
        Assert.AreEqual(4, batch.Conversations.Count);

        int totalMessages = 0;
        foreach (var conv in batch.Conversations)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(conv.Id));
            totalMessages += conv.Messages.Count;
        }

        Assert.AreEqual(4, totalMessages);
    }

    [TestMethod]
    public void Decompress_And_Decode_Synthetic_Full_Chunk_Extracts_Entities()
    {
        var proto = new PbHistorySync
        {
            SyncType = PbHistorySyncType.Full,
            ChunkOrder = 7,
            Progress = 100
        };

        for (int i = 1; i <= 5; i++)
        {
            string jid = $"1555010030{i}@s.whatsapp.net";
            var conv = new global::Whatsapp.Conversation
            {
                Id = jid,
                Name = $"Contact {i}",
                LastMsgTimestamp = (ulong)(1700001000 + i)
            };
            conv.Messages.Add(new global::Whatsapp.HistorySyncMsg
            {
                Message = new global::Whatsapp.WebMessageInfo
                {
                    Key = new global::Whatsapp.MessageKey { Id = $"SYNTH_C7_{i}_A", RemoteJid = jid, FromMe = true },
                    MessageTimestamp = (ulong)(1700001000 + i),
                    Message = new global::Whatsapp.Message { Conversation = $"Direct message {i}" }
                }
            });
            conv.Messages.Add(new global::Whatsapp.HistorySyncMsg
            {
                Message = new global::Whatsapp.WebMessageInfo
                {
                    Key = new global::Whatsapp.MessageKey { Id = $"SYNTH_C7_{i}_B", RemoteJid = jid, FromMe = false },
                    MessageTimestamp = (ulong)(1700001010 + i),
                    Message = new global::Whatsapp.Message
                    {
                        StickerMessage = new global::Whatsapp.Message.Types.StickerMessage
                        {
                            DirectPath = $"/v/t62.15575-24/synth_{i}.enc",
                            Mimetype = "image/webp"
                        }
                    }
                }
            });
            proto.Conversations.Add(conv);
        }

        byte[] compressed = HistorySyncInflater.CompressZlib(proto.ToByteArray());
        Assert.AreEqual(0x78, compressed[0]);

        byte[] decompressed = HistorySyncInflater.Decompress(compressed);
        var batch = HistorySyncDecoder.Decode(decompressed, chunkOrder: 7, progress: 100);

        Assert.AreEqual(5, batch.Conversations.Count);
        int totalMessages = batch.Conversations.Sum(c => c.Messages.Count);
        Assert.AreEqual(10, totalMessages);
    }
}
