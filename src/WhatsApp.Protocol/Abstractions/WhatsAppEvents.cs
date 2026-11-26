using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Protocol.Abstractions;

/// <summary>
/// Base type for strongly-typed events emitted by the WhatsApp API runtime to client consumers.
/// </summary>
public abstract record WhatsAppEvent;

public sealed record QrCodeEvent(string QrData, TimeSpan Timeout) : WhatsAppEvent;
public sealed record PairCodeEvent(string Code, string PhoneNumber, TimeSpan Timeout) : WhatsAppEvent;
public sealed record PairCodeRefreshRequestedEvent : WhatsAppEvent;
public sealed record PairSuccessEvent(string PnJid, string? LidJid) : WhatsAppEvent;
public sealed record PairErrorEvent(string? PnJid, string? LidJid, int ErrorCode, string ErrorText) : WhatsAppEvent;
public sealed record PairQrExhaustedEvent : WhatsAppEvent;
public sealed record ConnectedEvent(string PnJid, string? LidJid) : WhatsAppEvent;
public sealed record DisconnectedEvent(string Reason, bool WillReconnect) : WhatsAppEvent;
public sealed record OfflineSyncPreviewEvent(
    int ExpectedCount,
    int AppDataChanges = 0,
    int Messages = 0,
    int Notifications = 0,
    int Receipts = 0,
    int Calls = 0,
    int Statuses = 0) : WhatsAppEvent;
public sealed record OfflineSyncCompletedEvent(int ProcessedCount, bool TimedOut = false) : WhatsAppEvent;
public sealed record DirtyStateEvent(string DirtyType, ulong? Timestamp) : WhatsAppEvent;
public sealed record StreamErrorEvent(string? Code, string Kind) : WhatsAppEvent;
public sealed record StreamReplacedEvent : WhatsAppEvent;
public sealed record LoggedOutEvent(bool OnConnect, int ReasonCode, string? Header = null, string? Subtext = null) : WhatsAppEvent;
public sealed record TemporaryBanEvent(int? SubCode, TimeSpan? Expire, string? Message, string? Url) : WhatsAppEvent;
public sealed record ClientOutdatedEvent(string? Message) : WhatsAppEvent;
public sealed record ConnectFailureEvent(int ReasonCode, string? Message) : WhatsAppEvent;
public sealed record HistorySyncReceivedEvent(HistorySyncBatch Batch, string? PeerSessionId = null) : WhatsAppEvent;
public sealed record MessageReceivedEvent(MessageRecord Message) : WhatsAppEvent;
public sealed record ReceiptReceivedEvent(string ChatJid, string SenderJid, IReadOnlyList<string> MessageIds, string Type) : WhatsAppEvent;
public sealed record AppStateUpdatedEvent(string Collection, ulong NewVersion, int MutationCount) : WhatsAppEvent;

public sealed record DeviceNotificationEvent(
    string UserJid,
    string Operation,
    IReadOnlyList<uint> DeviceIds,
    string? ContactHash,
    long? TimestampSeconds) : WhatsAppEvent;

public sealed record IdentityChangeEvent(
    string UserJid,
    string? LidJid,
    bool Implicit,
    long TimestampSeconds) : WhatsAppEvent;

public sealed record GroupNotificationEvent(
    string GroupJid,
    string Action,
    string? ActorJid,
    IReadOnlyList<string> Participants,
    string? Subject,
    long TimestampSeconds,
    bool IsDirtyNotification = false) : WhatsAppEvent;

public sealed record PictureUpdateEvent(
    string Jid,
    string? AuthorJid,
    string? PictureId,
    string? ContactHash,
    bool Removed,
    long TimestampSeconds) : WhatsAppEvent;

public sealed record BusinessUpdateEvent(
    string Jid,
    string UpdateType,
    string? TargetJid,
    string? VerifiedName,
    string? ContactHash,
    long TimestampSeconds) : WhatsAppEvent;

public sealed record DisappearingModeUpdateEvent(
    string FromJid,
    uint DurationSeconds,
    long SettingTimestampSeconds,
    bool EphemeralityDisabled) : WhatsAppEvent;

public sealed record ContactNotificationEvent(
    string Action,
    string? OldPnJid,
    string? NewPnJid,
    string? OldLidJid,
    string? NewLidJid,
    string? ContactHash,
    long TimestampSeconds) : WhatsAppEvent;

public sealed record UserAboutUpdateEvent(
    string Jid,
    string StatusText,
    long TimestampSeconds) : WhatsAppEvent;

public sealed record MexNotificationEvent(
    string Operation,
    string RawPayloadJson,
    long TimestampSeconds) : WhatsAppEvent;

public sealed record PrivacyTokenNotificationEvent(
    string SenderJid,
    int TokenCount,
    long TimestampSeconds) : WhatsAppEvent;

public sealed record DecryptionFailureEvent(
    string MessageId,
    string ChatJid,
    string SenderJid,
    string Reason,
    int RetryCount,
    bool SentRetryReceipt) : WhatsAppEvent;

public sealed record MessageRetrySentEvent(
    string MessageId,
    string RecipientDeviceJid,
    int RetryCount,
    bool WasGroupResend) : WhatsAppEvent;

public sealed record MediaRetryNotificationEvent(
    string StanzaId,
    string Status,
    string? NewDirectPath) : WhatsAppEvent;

public sealed record PresenceUpdateEvent(
    string UserJid,
    bool Available,
    long? LastSeenSeconds) : WhatsAppEvent;

public sealed record ChatStateEvent(
    string ChatJid,
    string? ParticipantJid,
    string Activity) : WhatsAppEvent;

public sealed record MessageAddOnEvent(
    string Kind,
    string ChatJid,
    string SenderJid,
    string TargetMessageId,
    string? PayloadText,
    bool? BoolFlag,
    long TimestampSeconds) : WhatsAppEvent;

public sealed record GroupHistoryBundleReceivedEvent(
    string GroupJid,
    string SenderJid,
    string MessageId,
    int MessageCount,
    long OldestMessageTimestampInBundle,
    IReadOnlyList<string> HistoryReceivers,
    IReadOnlyList<MessageRecord> Messages,
    long TimestampSeconds) : WhatsAppEvent;

public sealed record MemberLabelUpdatedEvent(
    string GroupJid,
    string ParticipantJid,
    string Label,
    long LabelTimestampSeconds,
    long NotificationTimestampSeconds) : WhatsAppEvent;

public sealed record GroupMetadataUpdatedEvent(
    string GroupJid,
    string Reason,
    int ParticipantCount) : WhatsAppEvent;

public sealed record CommunityLinkChangedEvent(
    string CommunityJid,
    string Action,
    string? LinkKind,
    string? UnlinkReason,
    IReadOnlyList<string> SubgroupJids,
    long TimestampSeconds) : WhatsAppEvent;

public sealed record StatusPrivacyUpdatedEvent(
    int? Mode,
    IReadOnlyList<string> UserJids,
    bool ShareToFb,
    bool ShareToIg) : WhatsAppEvent;

public sealed record UserStatusMuteUpdatedEvent(
    string UserJid,
    bool Muted) : WhatsAppEvent;

public sealed record GroupStatusReceivedEvent(
    string GroupJid,
    string SenderJid,
    string MessageId,
    MessageRecord Record,
    long TimestampSeconds) : WhatsAppEvent;

// Phase 8D: Privacy, Profile, AppState Settings, ChatActions, Contacts, Stickers & Safety Events
public sealed record PrivacyUpdatedEvent(IReadOnlyList<WhatsApp.Protocol.Binary.Stanzas.PrivacySettingEntry> Categories, long TimestampSeconds) : WhatsAppEvent;

public sealed record LinkPreviewsDisabledUpdatedEvent(bool PreviewsDisabled, long TimestampMs) : WhatsAppEvent;

public sealed record UnarchiveChatsSettingUpdatedEvent(bool UnarchiveChats, long TimestampMs) : WhatsAppEvent;

public sealed record ChannelsPersonalisedRecUpdatedEvent(bool IsUserOptedOut, long TimestampMs) : WhatsAppEvent;

public sealed record PinUpdatedEvent(string ChatJid, bool Pinned, long TimestampMs) : WhatsAppEvent;

public sealed record MuteUpdatedEvent(string ChatJid, bool Muted, long? MuteEndTimestampMs, long TimestampMs) : WhatsAppEvent;

public sealed record ArchiveUpdatedEvent(string ChatJid, bool Archived, long TimestampMs) : WhatsAppEvent;

public sealed record MarkChatAsReadUpdatedEvent(string ChatJid, bool Read, long TimestampMs) : WhatsAppEvent;

public sealed record LockChatUpdatedEvent(string ChatJid, bool Locked, long TimestampMs) : WhatsAppEvent;

public sealed record StarUpdatedEvent(string ChatJid, string MessageId, bool FromMe, bool Starred, long TimestampMs) : WhatsAppEvent;

public sealed record DeleteMessageForMeUpdatedEvent(string ChatJid, string MessageId, bool FromMe, bool DeleteMedia, long TimestampMs) : WhatsAppEvent;

public sealed record ClearChatUpdatedEvent(string ChatJid, bool DeleteStarred, bool DeleteMedia, long TimestampMs) : WhatsAppEvent;

public sealed record DeleteChatUpdatedEvent(string ChatJid, bool DeleteMedia, long TimestampMs) : WhatsAppEvent;

public sealed record FavoritesUpdatedEvent(IReadOnlyList<string> FavoriteIds, long TimestampMs) : WhatsAppEvent;

public sealed record ContactUpdatedEvent(string ContactJid, bool IsLid, string? FullName, string? FirstName, string? Username, long TimestampMs) : WhatsAppEvent;

public sealed record ContactRemovedEvent(string ContactJid, long TimestampMs) : WhatsAppEvent;

public sealed record FavoriteStickerUpdatedEvent(string FileHash, bool IsFavorite, string? DirectPath, string? Mimetype, long TimestampMs) : WhatsAppEvent;

public sealed record RemoveRecentStickerUpdatedEvent(string FileHash, long? LastStickerSentTs, long TimestampMs) : WhatsAppEvent;

public sealed record SignedPreKeyRotatedEvent(uint PreviousSignedPreKeyId, uint NewSignedPreKeyId, long TimestampSeconds) : WhatsAppEvent;

public sealed record DraftUpdatedEvent(string ChatJid, string? DraftText, long TimestampMs, bool Cleared) : WhatsAppEvent;

public sealed record CommentReceivedEvent(
    string ChatJid,
    string ParentMessageId,
    string SenderJid,
    string CommentMessageId,
    MessageRecord Record,
    long TimestampSeconds) : WhatsAppEvent;
