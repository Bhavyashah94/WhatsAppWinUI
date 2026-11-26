using WhatsApp.Protocol.Binary.Stanzas;
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
            string? senderJid = webMsg.Key?.HasParticipant == true
                ? webMsg.Key.Participant
                : (webMsg.HasParticipant ? webMsg.Participant : null);
            bool fromMe = webMsg.Key?.FromMe ?? false;
            ulong timestamp = webMsg.HasMessageTimestamp ? webMsg.MessageTimestamp : 0;

            MessageRecord mapped = MapWebMessage(id, remoteJid, senderJid, fromMe, timestamp, webMsg);
            messages.Add(mapped);
        }

        ulong effectiveLastMsgTs = conv.HasLastMsgTimestamp ? conv.LastMsgTimestamp : 0ul;
        foreach (var m in messages)
        {
            if (m.Timestamp > effectiveLastMsgTs)
            {
                effectiveLastMsgTs = m.Timestamp;
            }
        }

        return new ConversationRecord(
            conv.Id,
            conv.HasName ? conv.Name : null,
            conv.HasUnreadCount ? conv.UnreadCount : 0u,
            effectiveLastMsgTs,
            conv.HasArchived && conv.Archived,
            messages);
    }

    private static MessageRecord MapWebMessage(
        string id,
        string remoteJid,
        string? senderJid,
        bool fromMe,
        ulong timestamp,
        global::Whatsapp.WebMessageInfo webMsg)
    {
        if (webMsg.Message == null && webMsg.HasMessageStubType)
        {
            string stubName = webMsg.MessageStubType.ToString();
            string stubText = webMsg.MessageStubParameters.Count > 0
                ? $"[{stubName}] {string.Join(", ", webMsg.MessageStubParameters)}"
                : $"[{stubName}]";
            return new MessageRecord(id, remoteJid, senderJid, fromMe, timestamp, MessageType.Protocol, stubText);
        }

        var (type, text, mimeType, directPath, mediaKey, fileHash, fileEncHash, fileLength, isRevoked) =
            ClassifyMessage(webMsg.Message);

        if (text is null && webMsg.HasMessageStubType)
        {
            type = MessageType.Protocol;
            string stubName = webMsg.MessageStubType.ToString();
            text = webMsg.MessageStubParameters.Count > 0
                ? $"[{stubName}] {string.Join(", ", webMsg.MessageStubParameters)}"
                : $"[{stubName}]";
        }

        string? quotedId = null;
        string? quotedSender = null;
        string? quotedText = null;
        if (webMsg.Message != null)
        {
            var ctx = ExtractContextInfo(webMsg.Message);
            if (ctx != null)
            {
                quotedId = ctx.HasStanzaId ? ctx.StanzaId : null;
                quotedSender = ctx.HasParticipant ? ctx.Participant : null;
                if (ctx.QuotedMessage != null)
                {
                    quotedText = ClassifyMessage(ctx.QuotedMessage).Text;
                }
            }
        }

        return new MessageRecord(
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
            fileEncHash,
            quotedId,
            quotedSender,
            quotedText,
            EditedTimestamp: null,
            IsRevoked: isRevoked,
            FileLength: fileLength);
    }

    private static global::Whatsapp.ContextInfo? ExtractContextInfo(global::Whatsapp.Message root)
    {
        var cur = MessageStanzas.UnwrapClassificationMessage(root);
        return cur.ExtendedTextMessage?.ContextInfo
            ?? cur.ImageMessage?.ContextInfo
            ?? cur.VideoMessage?.ContextInfo
            ?? cur.PtvMessage?.ContextInfo
            ?? cur.AudioMessage?.ContextInfo
            ?? cur.DocumentMessage?.ContextInfo
            ?? cur.StickerMessage?.ContextInfo
            ?? (cur.PollCreationMessage ?? cur.PollCreationMessageV2 ?? cur.PollCreationMessageV3 ?? cur.PollCreationMessageV5 ?? cur.PollCreationMessageV6)?.ContextInfo
            ?? cur.EventMessage?.ContextInfo
            ?? cur.LocationMessage?.ContextInfo
            ?? cur.LiveLocationMessage?.ContextInfo
            ?? cur.ContactMessage?.ContextInfo
            ?? cur.ContactsArrayMessage?.ContextInfo
            ?? cur.GroupInviteMessage?.ContextInfo
            ?? cur.ProductMessage?.ContextInfo
            ?? cur.OrderMessage?.ContextInfo
            ?? cur.ButtonsMessage?.ContextInfo
            ?? cur.TemplateMessage?.ContextInfo
            ?? cur.InteractiveMessage?.ContextInfo
            ?? cur.ListMessage?.ContextInfo
            ?? cur.NewsletterAdminInviteMessage?.ContextInfo
            ?? cur.NewsletterFollowerInviteMessageV2?.ContextInfo
            ?? cur.MusicMessage?.ContextInfo
            ?? cur.RichResponseMessage?.ContextInfo;
    }

    private static (MessageType Type, string? Text, string? MimeType, string? DirectPath, byte[]? MediaKey, byte[]? FileHash, byte[]? FileEncHash, ulong? FileLength, bool IsRevoked)
        ClassifyMessage(global::Whatsapp.Message? rawMsg)
    {
        if (rawMsg == null)
        {
            return (MessageType.Unknown, null, null, null, null, null, null, null, false);
        }

        var msg = MessageStanzas.UnwrapClassificationMessage(rawMsg);

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
                s.HasFileEncSha256 ? s.FileEncSha256.ToByteArray() : null,
                s.HasFileLength ? s.FileLength : null,
                false);
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
                img.HasFileEncSha256 ? img.FileEncSha256.ToByteArray() : null,
                img.HasFileLength ? img.FileLength : null,
                false);
        }

        var vid = msg.VideoMessage ?? msg.PtvMessage;
        if (vid != null)
        {
            string fallback = msg.PtvMessage != null ? "🎥 Video note" : "🎥 Video";
            return (
                MessageType.Video,
                vid.HasCaption ? vid.Caption : fallback,
                vid.HasMimetype ? vid.Mimetype : "video/mp4",
                vid.HasDirectPath ? vid.DirectPath : null,
                vid.HasMediaKey ? vid.MediaKey.ToByteArray() : null,
                vid.HasFileSha256 ? vid.FileSha256.ToByteArray() : null,
                vid.HasFileEncSha256 ? vid.FileEncSha256.ToByteArray() : null,
                vid.HasFileLength ? vid.FileLength : null,
                false);
        }

        if (msg.AudioMessage != null)
        {
            var a = msg.AudioMessage;
            return (
                MessageType.Audio,
                a.Ptt ? "🎤 Voice message" : "🔊 Audio",
                a.HasMimetype ? a.Mimetype : "audio/ogg",
                a.HasDirectPath ? a.DirectPath : null,
                a.HasMediaKey ? a.MediaKey.ToByteArray() : null,
                a.HasFileSha256 ? a.FileSha256.ToByteArray() : null,
                a.HasFileEncSha256 ? a.FileEncSha256.ToByteArray() : null,
                a.HasFileLength ? a.FileLength : null,
                false);
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
                d.HasFileEncSha256 ? d.FileEncSha256.ToByteArray() : null,
                d.HasFileLength ? d.FileLength : null,
                false);
        }

        if (msg.ProtocolMessage is { } pm)
        {
            bool isRevoke = pm.Type == global::Whatsapp.Message.Types.ProtocolMessage.Types.Type.Revoke;
            string? pmText = pm.EditedMessage != null
                ? ClassifyMessage(pm.EditedMessage).Text
                : (isRevoke ? "[Message deleted]" : null);
            return (MessageType.Protocol, pmText, null, null, null, null, null, null, isRevoke);
        }

        if (msg.HasConversation)
        {
            return (MessageType.Text, msg.Conversation, null, null, null, null, null, null, false);
        }

        if (msg.ExtendedTextMessage?.HasText == true)
        {
            return (MessageType.Text, msg.ExtendedTextMessage.Text, null, null, null, null, null, null, false);
        }

        if ((msg.PollCreationMessage ?? msg.PollCreationMessageV2 ?? msg.PollCreationMessageV3 ?? msg.PollCreationMessageV5 ?? msg.PollCreationMessageV6) is { } poll)
        {
            return (MessageType.Text, poll.HasName ? $"[Poll] {poll.Name}" : "[Poll]", null, null, null, null, null, null, false);
        }

        if (msg.EventMessage is { } ev)
        {
            return (MessageType.Text, ev.HasName ? $"[Event] {ev.Name}" : "[Event]", null, null, null, null, null, null, false);
        }

        if (msg.LocationMessage is { } loc)
        {
            string locLabel = loc.HasName ? loc.Name : (loc.HasAddress ? loc.Address : $"{loc.DegreesLatitude:F5},{loc.DegreesLongitude:F5}");
            return (MessageType.Text, $"[Location] {locLabel}", null, null, null, null, null, null, false);
        }

        if (msg.LiveLocationMessage is { } live)
        {
            string liveLabel = live.HasCaption ? live.Caption : $"{live.DegreesLatitude:F5},{live.DegreesLongitude:F5}";
            return (MessageType.Text, $"[Live Location] {liveLabel}", null, null, null, null, null, null, false);
        }

        if (msg.ContactMessage is { } contact)
        {
            return (MessageType.Text, contact.HasDisplayName ? $"[Contact] {contact.DisplayName}" : "[Contact]", null, null, null, null, null, null, false);
        }

        if (msg.ContactsArrayMessage is { } contacts)
        {
            return (MessageType.Text, contacts.HasDisplayName ? $"[Contacts] {contacts.DisplayName}" : $"[Contacts ({contacts.Contacts.Count})]", null, null, null, null, null, null, false);
        }

        if (msg.GroupInviteMessage is { } grpInv)
        {
            return (MessageType.Text, grpInv.HasGroupName ? $"[Group Invite] {grpInv.GroupName}" : "[Group Invite]", null, null, null, null, null, null, false);
        }

        if (msg.CallLogMesssage is { } clm)
        {
            return (MessageType.Protocol, $"[{(clm.IsVideo ? "Video" : "Voice")} call: {clm.CallOutcome}]", null, null, null, null, null, null, false);
        }

        if (msg.ScheduledCallCreationMessage is { } scc)
        {
            return (MessageType.Text, scc.HasTitle ? $"[Scheduled call] {scc.Title}" : "[Scheduled call]", null, null, null, null, null, null, false);
        }

        if (msg.ReactionMessage is { } rx)
        {
            return (MessageType.Protocol, rx.HasText && rx.Text.Length > 0 ? $"[Reaction] {rx.Text}" : "[Reaction removed]", null, null, null, null, null, null, false);
        }

        if (msg.ButtonsMessage is { } bm)
        {
            string? bText = bm.HasContentText ? bm.ContentText : (bm.HasText ? bm.Text : null);
            return (MessageType.Text, bText ?? "[Buttons message]", null, null, null, null, null, null, false);
        }

        if (msg.ListMessage is { } lm)
        {
            string? lText = lm.HasTitle ? lm.Title : (lm.HasDescription ? lm.Description : null);
            return (MessageType.Text, lText ?? "[List message]", null, null, null, null, null, null, false);
        }

        if (msg.InteractiveMessage is { } im)
        {
            string? iText = im.Body?.HasText == true ? im.Body.Text : (im.Header?.HasTitle == true ? im.Header.Title : null);
            return (MessageType.Text, iText ?? "[Interactive message]", null, null, null, null, null, null, false);
        }

        if (msg.ProductMessage is { } prod)
        {
            string? pText = prod.Product?.HasTitle == true ? prod.Product.Title : (prod.HasBody ? prod.Body : null);
            return (MessageType.Text, pText is not null ? $"[Product] {pText}" : "[Product]", null, null, null, null, null, null, false);
        }

        if (msg.OrderMessage is { } ord)
        {
            string? oText = ord.HasOrderTitle ? ord.OrderTitle : (ord.HasMessage ? ord.Message : (ord.HasOrderId ? ord.OrderId : null));
            return (MessageType.Text, oText is not null ? $"[Order] {oText}" : "[Order]", null, null, null, null, null, null, false);
        }

        if (msg.PollUpdateMessage is not null || msg.KeepInChatMessage is not null || msg.PinInChatMessage is not null || msg.SenderKeyDistributionMessage is not null)
        {
            return (MessageType.Protocol, null, null, null, null, null, null, null, false);
        }

        return (MessageType.Unknown, null, null, null, null, null, null, null, false);
    }
}
