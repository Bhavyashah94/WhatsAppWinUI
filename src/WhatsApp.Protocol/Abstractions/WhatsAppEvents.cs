using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Protocol.Abstractions;

/// <summary>
/// Base type for strongly-typed events emitted by the WhatsApp API runtime to client consumers.
/// </summary>
public abstract record WhatsAppEvent;

public sealed record QrCodeEvent(string QrData, TimeSpan Timeout) : WhatsAppEvent;
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
