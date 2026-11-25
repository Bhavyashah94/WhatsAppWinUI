using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Api;

/// <summary>
/// Sends peer <c>HISTORY_SYNC_ON_DEMAND</c> (<c>syncType = ON_DEMAND</c>) backfill requests anchored
/// cleanly on <see cref="IMessageHistoryStore.GetMessageRangeAsync"/> / oldest known chat cursor.
/// </summary>
public sealed class PdoHistoryClient
{
    private readonly IWhatsAppBackend _backend;
    private readonly MessageSender _messageSender;
    private readonly DeviceCredentials _credentials;

    public PdoHistoryClient(
        IWhatsAppBackend backend,
        MessageSender messageSender,
        DeviceCredentials credentials)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public Task<SendMessageResult> RequestHistoryOnDemandAsync(
        Jid chatJid,
        string oldestMsgId,
        bool oldestMsgFromMe,
        long oldestMsgTimestampMs,
        int count = 50,
        bool supportInlineResponse = true,
        bool initialReverseHistory = false,
        string? requestMessageId = null,
        CancellationToken ct = default)
    {
        Jid ownPeerTarget = _credentials.Lid?.ToNonAd()
            ?? _credentials.Pn?.ToNonAd()
            ?? throw new InvalidOperationException("Device credentials have neither LID nor PN assigned.");

        Message pdoMsg = MessageStanzas.BuildHistorySyncOnDemandMessage(
            chatJid,
            oldestMsgId,
            oldestMsgFromMe,
            oldestMsgTimestampMs,
            count,
            accountLid: _credentials.Lid?.ToNonAd(),
            supportInlineResponse: supportInlineResponse,
            initialReverseHistory: initialReverseHistory);

        return _messageSender.SendDmAsync(
            ownPeerTarget,
            pdoMsg,
            messageId: requestMessageId,
            includeReportingToken: false,
            category: "peer",
            ct: ct);
    }

    /// <summary>
    /// Automatically determines the oldest synced message cursor for <paramref name="chatJid"/>
    /// from <see cref="IMessageHistoryStore"/> and dispatches an on-demand backfill request.
    /// </summary>
    public async Task<SendMessageResult?> RequestNextOlderPageAsync(
        Jid chatJid,
        int count = 50,
        CancellationToken ct = default)
    {
        string chatBare = chatJid.ToNonAdString();
        ActiveMessageRange? range = await _backend.History.GetMessageRangeAsync(chatBare, ct).ConfigureAwait(false);
        ulong? beforeTs = range is not null ? range.MinTimestamp + 1 : null;

        IReadOnlyList<MessageRecord> msgs = await _backend.History.GetMessagesAsync(chatBare, beforeTs, limit: 200, ct)
            .ConfigureAwait(false);
        MessageRecord? oldest = msgs.LastOrDefault();
        if (oldest is null)
        {
            return null;
        }

        return await RequestHistoryOnDemandAsync(
            chatJid,
            oldest.Id,
            oldest.FromMe,
            checked((long)(oldest.Timestamp * 1000UL)),
            count,
            ct: ct).ConfigureAwait(false);
    }
}
