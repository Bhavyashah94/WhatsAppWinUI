using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Protocol.Abstractions;

/// <summary>
/// Base type for strongly-typed events emitted by the WhatsApp API runtime to client consumers.
/// </summary>
public abstract record WhatsAppEvent;

public sealed record QrCodeEvent(string QrData, TimeSpan Timeout) : WhatsAppEvent;
public sealed record PairSuccessEvent(string PnJid, string? LidJid) : WhatsAppEvent;
public sealed record ConnectedEvent(string PnJid, string? LidJid) : WhatsAppEvent;
public sealed record DisconnectedEvent(string Reason, bool WillReconnect) : WhatsAppEvent;
public sealed record OfflineSyncPreviewEvent(int ExpectedCount) : WhatsAppEvent;
public sealed record OfflineSyncCompletedEvent(int ProcessedCount) : WhatsAppEvent;
public sealed record HistorySyncReceivedEvent(HistorySyncBatch Batch, string? PeerSessionId = null) : WhatsAppEvent;
public sealed record MessageReceivedEvent(MessageRecord Message) : WhatsAppEvent;
public sealed record ReceiptReceivedEvent(string ChatJid, string SenderJid, IReadOnlyList<string> MessageIds, string Type) : WhatsAppEvent;
public sealed record AppStateUpdatedEvent(string Collection, ulong NewVersion, int MutationCount) : WhatsAppEvent;
