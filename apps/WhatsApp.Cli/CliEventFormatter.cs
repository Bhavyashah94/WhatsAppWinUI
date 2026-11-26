using System.Text.Json;
using WhatsApp.Protocol.Abstractions;

namespace WhatsApp.Cli;

public static class CliEventFormatter
{
    public static string FormatHuman(WhatsAppEvent evt) => evt switch
    {
        QrCodeEvent qr => $"[PAIR:QR] Ref={QrConsoleRenderer.RedactString(qr.QrData, 4)} (expires in {qr.Timeout.TotalSeconds:0}s)",
        PairCodeEvent pc => $"[PAIR:CODE] Enter code on phone: {pc.Code} (phone=***{QrConsoleRenderer.RedactString(pc.PhoneNumber)}, expires in {pc.Timeout.TotalSeconds:0}s)",
        PairCodeRefreshRequestedEvent => "[PAIR:CODE] Refresh requested by server.",
        PairSuccessEvent ps => $"[PAIR:OK] Linked PN={ps.PnJid}, LID={ps.LidJid ?? "<pending>"}",
        PairErrorEvent pe => $"[PAIR:ERR] Code={pe.ErrorCode} ({pe.ErrorText}) PN={pe.PnJid ?? "?"}",
        PairQrExhaustedEvent => "[PAIR:EXHAUSTED] QR rotations exhausted; run 'pair qr' again.",
        ConnectedEvent c => $"[CONN:UP] Connected as PN={c.PnJid}, LID={c.LidJid ?? "<none>"}",
        DisconnectedEvent d => $"[CONN:DOWN] Reason=\"{d.Reason}\", WillReconnect={d.WillReconnect}",
        OfflineSyncPreviewEvent p => $"[SYNC:OFFLINE_PREVIEW] Expected={p.ExpectedCount} (msgs={p.Messages}, app={p.AppDataChanges}, notif={p.Notifications}, rcpt={p.Receipts}, calls={p.Calls})",
        OfflineSyncCompletedEvent c => $"[SYNC:OFFLINE_DONE] Processed={c.ProcessedCount}, TimedOut={c.TimedOut}",
        HistorySyncReceivedEvent h => $"[HISTORY] Type={h.Batch.SyncType}, Chunk={h.Batch.ChunkOrder}, Progress={h.Batch.Progress}%, Convs={h.Batch.Conversations.Count}, Msgs={h.Batch.Conversations.Sum(c => c.Messages.Count)}",
        AppStateUpdatedEvent a => $"[SYNCD] Collection={a.Collection}, Version={a.NewVersion}, Mutations={a.MutationCount}",
        DirtyStateEvent ds => $"[DIRTY] Type={ds.DirtyType}, Ts={ds.Timestamp}",
        MessageReceivedEvent m => $"[MSG] Chat={m.Message.RemoteJid}, From={m.Message.SenderJid ?? "?"}, Id={m.Message.Id}, Type={m.Message.Type}, Text=\"{Truncate(m.Message.Text, 80)}\"",
        MessageAddOnEvent ma => $"[MSG:ADDON] Kind={ma.Kind}, Chat={ma.ChatJid}, From={ma.SenderJid}, Target={ma.TargetMessageId}, Payload=\"{Truncate(ma.PayloadText, 60)}\"",
        DecryptionFailureEvent df => $"[DECRYPT:FAIL] Id={df.MessageId}, Chat={df.ChatJid}, From={df.SenderJid}, Reason={df.Reason}, Retry={df.RetryCount}, SentRetry={df.SentRetryReceipt}, Detail=\"{Truncate(df.Detail, 120)}\"",
        MessageRetrySentEvent mr => $"[RETRY:SENT] Id={mr.MessageId}, To={mr.RecipientDeviceJid}, Retry={mr.RetryCount}, Group={mr.WasGroupResend}",
        MediaRetryNotificationEvent mrm => $"[MEDIA:RETRY] Id={mrm.StanzaId}, Status={mrm.Status}, DirectPath={mrm.NewDirectPath ?? "<none>"}",
        DeviceNotificationEvent dn => $"[DEV:NOTIF] User={dn.UserJid}, Op={dn.Operation}, Devices=[{string.Join(',', dn.DeviceIds)}]",
        IdentityChangeEvent ic => $"[ID:CHANGE] User={ic.UserJid}, Lid={ic.LidJid ?? "<none>"}, Implicit={ic.Implicit}",
        GroupNotificationEvent gn => $"[GRP:NOTIF] Group={gn.GroupJid}, Action={gn.Action}, Actor={gn.ActorJid ?? "?"}, Subject={gn.Subject ?? ""}",
        ReceiptReceivedEvent r => $"[RCPT] Chat={r.ChatJid}, From={r.SenderJid}, Type={r.Type}, Count={r.MessageIds.Count}",
        StreamReplacedEvent => "[STREAM:409] Replaced by another session (WillReconnect=false).",
        LoggedOutEvent lo => $"[AUTH:LOGOUT] Code={lo.ReasonCode}, OnConnect={lo.OnConnect}, Header={lo.Header ?? ""}",
        TemporaryBanEvent tb => $"[AUTH:BAN] SubCode={tb.SubCode}, Expire={tb.Expire}, Msg={tb.Message}",
        ClientOutdatedEvent co => $"[AUTH:OUTDATED] {co.Message}",
        ConnectFailureEvent cf => $"[CONN:FAIL] Code={cf.ReasonCode}, Msg={cf.Message}",
        IncomingCallReceivedEvent ic => $"[CALL:IN] Action={ic.Call.ActionKind}, Id={ic.Call.CallId}, From={ic.Call.From}, Video={ic.Call.IsVideo}",
        GroupCallUpdatedEvent gu => $"[CALL:GROUP] Id={gu.Update.CallId}, Tx={gu.Update.TransactionId}, Peers={gu.Update.Participants.Count}, Media={gu.Update.Media}",
        GroupCallRekeyReceivedEvent gr => $"[CALL:REKEY] Id={gr.CallId}, Tx={gr.TransactionId}, From={gr.SenderDeviceJid}",
        _ => $"[EVENT:{evt.GetType().Name}]"
    };

    public static string FormatJson(WhatsAppEvent evt, DateTimeOffset timestamp)
    {
        object payload = new
        {
            ts = timestamp.ToString("O"),
            type = evt.GetType().Name,
            summary = FormatHuman(evt)
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLen ? value : value[..maxLen] + "...";
    }
}
