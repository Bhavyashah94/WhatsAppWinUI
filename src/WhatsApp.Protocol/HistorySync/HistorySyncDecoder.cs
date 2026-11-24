using PbHistorySync = global::Whatsapp.HistorySync;
using PbHistorySyncType = global::Whatsapp.HistorySync.Types.HistorySyncType;

namespace WhatsApp.Protocol.HistorySync;

/// <summary>
/// High-throughput decoder for WhatsApp History Sync payloads backed by protoc-generated schemas.
/// </summary>
public static class HistorySyncDecoder
{
    public static HistorySyncBatch DecodeEncryptedChunk(
        ReadOnlySpan<byte> encChunk,
        ReadOnlySpan<byte> mediaKey,
        uint? chunkOrder = null,
        uint? progress = null,
        ReadOnlySpan<byte> expectedEncSha256 = default)
    {
        byte[] decryptedZlib = HistorySyncCrypto.DecryptHistoryChunk(encChunk, mediaKey, expectedEncSha256);
        byte[] decompressedProto = HistorySyncInflater.Decompress(decryptedZlib);
        return Decode(decompressedProto, chunkOrder, progress);
    }

    public static HistorySyncBatch Decode(
        ReadOnlySpan<byte> decompressedProto,
        uint? chunkOrder = null,
        uint? progress = null)
    {
        if (decompressedProto.IsEmpty)
        {
            return new HistorySyncBatch(
                PbHistorySyncType.InitialBootstrap,
                chunkOrder,
                progress,
                [],
                [],
                [],
                []);
        }

        var sync = PbHistorySync.Parser.ParseFrom(decompressedProto);

        var lidMappings = new List<LidPnMappingRecord>(sync.PhoneNumberToLidMappings.Count);
        foreach (var m in sync.PhoneNumberToLidMappings)
        {
            if (!string.IsNullOrEmpty(m.PnJid) && !string.IsNullOrEmpty(m.LidJid))
            {
                lidMappings.Add(new LidPnMappingRecord(m.PnJid, m.LidJid));
            }
        }

        var pushnames = new List<PushnameRecord>(sync.Pushnames.Count);
        foreach (var pn in sync.Pushnames)
        {
            if (!string.IsNullOrEmpty(pn.Id))
            {
                pushnames.Add(new PushnameRecord(pn.Id, pn.Pushname_));
            }
        }

        var inlineContacts = new List<InlineContactRecord>(sync.InlineContacts.Count);
        foreach (var c in sync.InlineContacts)
        {
            if (!string.IsNullOrEmpty(c.PnJid) || !string.IsNullOrEmpty(c.LidJid))
            {
                inlineContacts.Add(new InlineContactRecord(
                    c.HasPnJid ? c.PnJid : null,
                    c.HasLidJid ? c.LidJid : null,
                    c.HasFullName ? c.FullName : null,
                    c.HasFirstName ? c.FirstName : null,
                    c.HasUsername ? c.Username : null));
            }
        }

        var conversations = new List<ConversationRecord>(sync.Conversations.Count);
        foreach (var conv in sync.Conversations)
        {
            conversations.Add(MapConversation(conv));
        }

        uint? effectiveChunkOrder = chunkOrder ?? (sync.HasChunkOrder ? sync.ChunkOrder : null);
        uint? effectiveProgress = progress ?? (sync.HasProgress ? sync.Progress : null);

        return new HistorySyncBatch(
            sync.SyncType,
            effectiveChunkOrder,
            effectiveProgress,
            lidMappings,
            pushnames,
            inlineContacts,
            conversations);
    }

    private static ConversationRecord MapConversation(global::Whatsapp.Conversation conv)
    {
        var messages = new List<MessageRecord>(conv.Messages.Count);

        foreach (var syncMsg in conv.Messages)
        {
            var webMsg = syncMsg.Message;
            if (webMsg == null) continue;

            string id = webMsg.Key?.HasId == true ? webMsg.Key.Id : Guid.NewGuid().ToString("N");
            string remoteJid = webMsg.Key?.HasRemoteJid == true ? webMsg.Key.RemoteJid : conv.Id;
            string? senderJid = webMsg.Key?.HasParticipant == true ? webMsg.Key.Participant : null;
            bool fromMe = webMsg.Key?.FromMe ?? false;
            ulong timestamp = webMsg.HasMessageTimestamp ? webMsg.MessageTimestamp : 0;

            var (type, text, mimeType, directPath, mediaKey, fileHash, fileEncHash) = ClassifyMessage(webMsg.Message);

            messages.Add(new MessageRecord(
                id,
                remoteJid,
                senderJid,
                fromMe,
                timestamp,
                type,
                text,
                mimeType,
                directPath,
                mediaKey,
                fileHash,
                fileEncHash));
        }

        return new ConversationRecord(
            conv.Id,
            conv.HasName ? conv.Name : null,
            conv.HasUnreadCount ? conv.UnreadCount : 0u,
            conv.HasLastMsgTimestamp ? conv.LastMsgTimestamp : 0ul,
            conv.HasArchived && conv.Archived,
            messages);
    }

    private static (MessageType Type, string? Text, string? MimeType, string? DirectPath, byte[]? MediaKey, byte[]? FileHash, byte[]? FileEncHash)
        ClassifyMessage(global::Whatsapp.Message? msg)
    {
        if (msg == null)
        {
            return (MessageType.Unknown, null, null, null, null, null, null);
        }

        // Unwrap ephemeral / view-once containers recursively
        if (msg.EphemeralMessage?.Message != null)
            return ClassifyMessage(msg.EphemeralMessage.Message);
        if (msg.ViewOnceMessage?.Message != null)
            return ClassifyMessage(msg.ViewOnceMessage.Message);
        if (msg.ViewOnceMessageV2?.Message != null)
            return ClassifyMessage(msg.ViewOnceMessageV2.Message);

        if (msg.StickerMessage != null)
        {
            var s = msg.StickerMessage;
            return (
                MessageType.Sticker,
                "[Sticker]",
                s.HasMimetype ? s.Mimetype : "image/webp",
                s.HasDirectPath ? s.DirectPath : null,
                s.HasMediaKey ? s.MediaKey.ToByteArray() : null,
                s.HasFileSha256 ? s.FileSha256.ToByteArray() : null,
                s.HasFileEncSha256 ? s.FileEncSha256.ToByteArray() : null);
        }

        if (msg.ImageMessage != null)
        {
            var img = msg.ImageMessage;
            return (
                MessageType.Image,
                img.HasCaption ? img.Caption : "📷 Photo",
                img.HasMimetype ? img.Mimetype : "image/jpeg",
                img.HasDirectPath ? img.DirectPath : null,
                img.HasMediaKey ? img.MediaKey.ToByteArray() : null,
                img.HasFileSha256 ? img.FileSha256.ToByteArray() : null,
                img.HasFileEncSha256 ? img.FileEncSha256.ToByteArray() : null);
        }

        if (msg.VideoMessage != null)
        {
            var v = msg.VideoMessage;
            return (
                MessageType.Video,
                v.HasCaption ? v.Caption : "🎥 Video",
                v.HasMimetype ? v.Mimetype : "video/mp4",
                v.HasDirectPath ? v.DirectPath : null,
                v.HasMediaKey ? v.MediaKey.ToByteArray() : null,
                v.HasFileSha256 ? v.FileSha256.ToByteArray() : null,
                v.HasFileEncSha256 ? v.FileEncSha256.ToByteArray() : null);
        }

        if (msg.AudioMessage != null)
        {
            var a = msg.AudioMessage;
            return (
                MessageType.Audio,
                "🎤 Voice message",
                a.HasMimetype ? a.Mimetype : "audio/ogg",
                a.HasDirectPath ? a.DirectPath : null,
                a.HasMediaKey ? a.MediaKey.ToByteArray() : null,
                a.HasFileSha256 ? a.FileSha256.ToByteArray() : null,
                a.HasFileEncSha256 ? a.FileEncSha256.ToByteArray() : null);
        }

        if (msg.DocumentMessage != null)
        {
            var d = msg.DocumentMessage;
            string? docTitle = d.HasTitle ? d.Title : (d.HasFileName ? d.FileName : null);
            return (
                MessageType.Document,
                d.HasCaption ? d.Caption : (string.IsNullOrEmpty(docTitle) ? "📄 Document" : $"📄 {docTitle}"),
                d.HasMimetype ? d.Mimetype : "application/octet-stream",
                d.HasDirectPath ? d.DirectPath : null,
                d.HasMediaKey ? d.MediaKey.ToByteArray() : null,
                d.HasFileSha256 ? d.FileSha256.ToByteArray() : null,
                d.HasFileEncSha256 ? d.FileEncSha256.ToByteArray() : null);
        }

        if (msg.ProtocolMessage != null)
        {
            return (MessageType.Protocol, null, null, null, null, null, null);
        }

        if (msg.HasConversation)
        {
            return (MessageType.Text, msg.Conversation, null, null, null, null, null);
        }

        if (msg.ExtendedTextMessage?.HasText == true)
        {
            return (MessageType.Text, msg.ExtendedTextMessage.Text, null, null, null, null, null);
        }

        return (MessageType.Unknown, null, null, null, null, null, null);
    }
}
