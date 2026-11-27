using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class MediaCipherAndHistorySyncProtocolTests
{
    [TestMethod]
    public void MediaCipher_UploadEncryption_StreamingSidecar_And_MmsUrls_WorkCleanly()
    {
        byte[] hashBytes = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        Assert.AreEqual("ASNFZ4mrze8", MediaCipher.ToUrlSafeBase64NoPad(hashBytes));

        Uri resumeUrl = MediaCipher.BuildMmsUploadUrl("media-bom5-2.cdn.whatsapp.net", WaMediaType.Video, hashBytes, "AUTH_TOK", hashBytes, resumeCheck: true);
        Assert.AreEqual(
            "https://media-bom5-2.cdn.whatsapp.net/mms/video/ASNFZ4mrze8?auth=AUTH_TOK&token=ASNFZ4mrze8&resume=1",
            resumeUrl.ToString());

        Uri dlUrl = MediaCipher.BuildMmsDownloadUrl("media-bom5-2.cdn.whatsapp.net", "/v/t62.7118-24/hist123.enc", "AUTH_TOK", WaMediaType.History);
        Assert.AreEqual(
            "https://media-bom5-2.cdn.whatsapp.net/v/t62.7118-24/hist123.enc?auth=AUTH_TOK&mms-type=md-msg-hist",
            dlUrl.ToString());

        // Plaintext catalog image skips encryption & keeps SHA-256
        byte[] catalogPng = new byte[512];
        new Random(7).NextBytes(catalogPng);
        MediaUploadEncryptionResult catRes = MediaCipher.EncryptForUpload(catalogPng, WaMediaType.ProductCatalogImage);
        Assert.AreEqual(0, catRes.MediaKey32.Length);
        CollectionAssert.AreEqual(catalogPng, catRes.DataToUpload);
        CollectionAssert.AreEqual(catalogPng, MediaCipher.DecryptPlaintext(catRes.DataToUpload, catRes.FileSha256));

        // Video >= 150 KiB generates multi-chunk 10-byte streaming sidecar & round-trips DecryptBlob
        byte[] videoBytes = new byte[150_000];
        new Random(99).NextBytes(videoBytes);
        MediaUploadEncryptionResult vidRes = MediaCipher.EncryptForUpload(videoBytes, WaMediaType.Video);
        Assert.AreEqual(32, vidRes.MediaKey32.Length);
        Assert.IsNotNull(vidRes.StreamingSidecar);
        // 150,000 + pkcs7 + 10B MAC spans 3 chunks of 64 KiB -> 3 * 10 = 30 sidecar bytes!
        Assert.AreEqual(30, vidRes.StreamingSidecar.Length);
        byte[] decrypted = MediaCipher.DecryptBlob(vidRes.DataToUpload, vidRes.MediaKey32, WaMediaType.Video, vidRes.FileEncSha256, vidRes.FileSha256);
        CollectionAssert.AreEqual(videoBytes, decrypted);

        // Image has no sidecar by default
        MediaUploadEncryptionResult imgRes = MediaCipher.EncryptForUpload(catalogPng, WaMediaType.Image);
        Assert.IsNull(imgRes.StreamingSidecar);
    }

    [TestMethod]
    public void MessageStanzas_BuildHistorySyncOnDemandMessage_SetsExpectedFields()
    {
        Message msg = MessageStanzas.BuildHistorySyncOnDemandMessage(
            Jid.PnDevice("15552220000", 2),
            "OLDEST-1",
            oldestMsgFromMe: true,
            oldestMsgTimestampMs: 1700000123000,
            onDemandMsgCount: 75,
            accountLid: Jid.LidDevice("100000000000001", 3),
            supportInlineResponse: true,
            initialReverseHistory: true);

        Assert.AreEqual(
            Message.Types.ProtocolMessage.Types.Type.PeerDataOperationRequestMessage,
            msg.ProtocolMessage.Type);
        var pdo = msg.ProtocolMessage.PeerDataOperationRequestMessage;
        Assert.AreEqual(Message.Types.PeerDataOperationRequestType.HistorySyncOnDemand, pdo.PeerDataOperationRequestType);
        Assert.AreEqual("15552220000@s.whatsapp.net", pdo.HistorySyncOnDemandRequest.ChatJid);
        Assert.AreEqual("OLDEST-1", pdo.HistorySyncOnDemandRequest.OldestMsgId);
        Assert.IsTrue(pdo.HistorySyncOnDemandRequest.OldestMsgFromMe);
        Assert.AreEqual(75, pdo.HistorySyncOnDemandRequest.OnDemandMsgCount);
        Assert.AreEqual("100000000000001@lid", pdo.HistorySyncOnDemandRequest.AccountLid);
        Assert.AreEqual(
            Message.Types.PeerDataOperationRequestMessage.Types.HistorySyncOnDemandRequest.Types.Mode.InitialReverseHistory,
            pdo.HistorySyncOnDemandRequest.Mode);
    }

    [TestMethod]
    public async Task MediaCipher_StreamingEncryptAndDecrypt_MatchInMemoryExactAcrossChunkBoundariesAsync()
    {
        foreach (int len in new[] { 0, 17, 65536, 150_005 })
        {
            byte[] plain = new byte[len];
            new Random(len + 1).NextBytes(plain);
            byte[] fixedKey = new byte[32];
            new Random(42).NextBytes(fixedKey);

            MediaUploadEncryptionResult inMem = MediaCipher.EncryptForUpload(plain, WaMediaType.Video, fixedKey);

            using var plainStream = new MemoryStream(plain);
            using var encStream = new MemoryStream();
            MediaStreamEncryptionResult streamed = await MediaCipher.EncryptForUploadAsync(
                plainStream, encStream, WaMediaType.Video, fixedKey);

            CollectionAssert.AreEqual(inMem.MediaKey32, streamed.MediaKey32);
            CollectionAssert.AreEqual(inMem.FileSha256, streamed.FileSha256);
            CollectionAssert.AreEqual(inMem.FileEncSha256, streamed.FileEncSha256);
            CollectionAssert.AreEqual(inMem.StreamingSidecar!, streamed.StreamingSidecar!);
            Assert.AreEqual((ulong)inMem.DataToUpload.Length, streamed.EncryptedLength);
            CollectionAssert.AreEqual(inMem.DataToUpload, encStream.ToArray());

            encStream.Position = 0;
            using var decOut = new MemoryStream();
            await MediaCipher.DecryptStreamAsync(
                encStream, decOut, streamed.MediaKey32, WaMediaType.Video, streamed.FileEncSha256, streamed.FileSha256);
            CollectionAssert.AreEqual(plain, decOut.ToArray());
        }
    }
}
