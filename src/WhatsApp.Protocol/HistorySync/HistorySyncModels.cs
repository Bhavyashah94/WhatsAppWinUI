using PbHistorySyncType = global::Whatsapp.HistorySync.Types.HistorySyncType;

namespace WhatsApp.Protocol.HistorySync;

public enum MessageType
{
    Text,
    Image,
    Sticker,
    Video,
    Audio,
    Document,
    Protocol,
    Unknown
}

public sealed record LidPnMappingRecord(string PnJid, string LidJid);

public sealed record PushnameRecord(string Id, string Pushname);

public sealed record InlineContactRecord(
    string? PnJid,
    string? LidJid,
    string? FullName,
    string? FirstName,
    string? Username
);

public sealed record ContactRecord(
    string Jid,
    string? Name,
    string? Pushname,
    string? PhoneNumber,
    bool IsAddressBookContact = false
);

public sealed record MessageRecord(
    string Id,
    string RemoteJid,
    string? SenderJid,
    bool FromMe,
    ulong Timestamp,
    MessageType Type,
    string? Text,
    string? MimeType = null,
    string? DirectPath = null,
    byte[]? MediaKey = null,
    byte[]? FileHash = null,
    byte[]? FileEncHash = null
);

public sealed record ConversationRecord(
    string Id,
    string? Name,
    uint UnreadCount,
    ulong LastMsgTimestamp,
    bool Archived,
    IReadOnlyList<MessageRecord> Messages
);

public sealed record HistorySyncBatch(
    PbHistorySyncType SyncType,
    uint? ChunkOrder,
    uint? Progress,
    IReadOnlyList<LidPnMappingRecord> LidMappings,
    IReadOnlyList<PushnameRecord> Pushnames,
    IReadOnlyList<InlineContactRecord> InlineContacts,
    IReadOnlyList<ConversationRecord> Conversations
);
