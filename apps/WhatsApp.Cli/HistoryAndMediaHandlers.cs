using System.Globalization;
using Google.Protobuf;
using WhatsApp.Api;
using WhatsApp.Protocol;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using Whatsapp;

namespace WhatsApp.Cli;

internal static class HistoryAndMediaHandlers
{
    public static async Task<CommandResult> HandleChatsAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2 || !string.Equals(args[1], "list", StringComparison.OrdinalIgnoreCase))
        {
            await ctx.Err.WriteLineAsync("Usage: chats list [limit] [--archived|--active] [--offset N]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        var (pos, opts, flags) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: 2,
            valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--offset", "--limit" },
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--archived", "--active" });

        int limit = 25;
        if (opts.TryGetValue("--limit", out var limVals) && int.TryParse(limVals[^1], out int optLim))
        {
            limit = optLim;
        }
        else if (pos.Count > 0 && int.TryParse(pos[0], out int posLim))
        {
            limit = posLim;
        }

        int offset = opts.TryGetValue("--offset", out var offVals) && int.TryParse(offVals[^1], out int parsedOff)
            ? parsedOff
            : 0;

        bool? archived = flags.Contains("--archived")
            ? true
            : flags.Contains("--active") ? false : null;

        IReadOnlyList<ChatSummary> chats = await ctx.Runtime.Backend.History
            .GetChatsAsync(limit, offset, archived, ct)
            .ConfigureAwait(false);

        await ctx.Out.WriteLineAsync($"Chats ({chats.Count}, offset={offset}):").ConfigureAwait(false);
        foreach (ChatSummary c in chats)
        {
            string tags = string.Concat(
                c.Pinned ? "[PIN]" : string.Empty,
                c.Archived ? "[ARC]" : string.Empty,
                c.MuteEndTimestamp is not null && c.MuteEndTimestamp != 0 ? "[MUTE]" : string.Empty);
            string tagCol = tags.Length > 0 ? $" {tags}" : string.Empty;
            string name = string.IsNullOrWhiteSpace(c.Name) ? "<unnamed>" : c.Name;
            string ts = CliCommandHelpers.FormatTimestamp(c.LastMsgTimestamp);
            string snippet = CliCommandHelpers.Truncate(c.LastMessageText, 50);
            string msgPart = c.LastMessageType == MessageType.Unknown && string.IsNullOrEmpty(snippet)
                ? string.Empty
                : $" | [{c.LastMessageType}] {snippet}";
            await ctx.Out.WriteLineAsync(
                $"  {name} ({c.ChatJid}){tagCol} | unread={c.UnreadCount} | last={ts}{msgPart}")
                .ConfigureAwait(false);
        }

        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandleHistoryAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var (pos, opts, _) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: 1,
            valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--limit", "--before-ts" },
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        if (pos.Count < 1)
        {
            await ctx.Err.WriteLineAsync("Usage: history <jid> [limit] [--before-ts <unixSec>]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid chatJid = CliCommandHelpers.ParseTargetJid(pos[0]);
        int limit = 25;
        if (opts.TryGetValue("--limit", out var limVals) && int.TryParse(limVals[^1], out int optLim))
        {
            limit = optLim;
        }
        else if (pos.Count > 1 && int.TryParse(pos[1], out int posLim))
        {
            limit = posLim;
        }

        ulong? beforeTs = null;
        if (opts.TryGetValue("--before-ts", out var tsVals) && ulong.TryParse(tsVals[^1], out ulong parsedTs))
        {
            beforeTs = parsedTs;
        }

        IReadOnlyList<MessageRecord> msgs = await ctx.Runtime.Backend.History
            .GetMessagesAsync(chatJid.ToNonAdString(), beforeTs, limit, ct)
            .ConfigureAwait(false);

        int total = await ctx.Runtime.Backend.History.GetMessageCountAsync(chatJid.ToNonAdString(), ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync($"History for {chatJid} (showing {msgs.Count} of {total}):").ConfigureAwait(false);

        foreach (MessageRecord m in msgs.Reverse())
        {
            await PrintMessageLineAsync(ctx.Out, m, includeChat: false).ConfigureAwait(false);
        }

        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandleSearchAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var (pos, opts, _) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: 1,
            valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--chat", "--limit" },
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        if (pos.Count == 0)
        {
            await ctx.Err.WriteLineAsync("Usage: search <query...> [--chat <jid>] [--limit 25]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        string query = string.Join(' ', pos);
        string? chatJid = opts.TryGetValue("--chat", out var chatVals)
            ? CliCommandHelpers.ParseTargetJid(chatVals[^1]).ToNonAdString()
            : null;
        int limit = opts.TryGetValue("--limit", out var limVals) && int.TryParse(limVals[^1], out int parsedLim)
            ? parsedLim
            : 25;

        IReadOnlyList<MessageRecord> hits = await ctx.Runtime.Backend.History
            .SearchMessagesAsync(query, chatJid, limit, ct)
            .ConfigureAwait(false);

        await ctx.Out.WriteLineAsync($"Search \"{query}\" ({hits.Count} hits):").ConfigureAwait(false);
        foreach (MessageRecord m in hits)
        {
            await PrintMessageLineAsync(ctx.Out, m, includeChat: true).ConfigureAwait(false);
        }

        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandlePdoAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: pdo <chatJid> [count] | pdo placeholder <chatJid> <msgId>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);

        if (string.Equals(args[1], "placeholder", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 4)
            {
                await ctx.Err.WriteLineAsync("Usage: pdo placeholder <chatJid> <msgId>").ConfigureAwait(false);
                return new CommandResult(1);
            }

            Jid chatJid = CliCommandHelpers.ParseTargetJid(args[2]);
            var (key, _) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, chatJid, args[3], ct).ConfigureAwait(false);
            MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
            SendMessageResult placeholderRes = await sender.RequestPlaceholderResendAsync([key], ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Requested placeholder resend: reqMsgId={placeholderRes.MessageId}, targetKey={key.Id} ({key.RemoteJid}).").ConfigureAwait(false);
            return new CommandResult(0);
        }

        Jid targetChat = CliCommandHelpers.ParseTargetJid(args[1]);
        int count = args.Count > 2 && int.TryParse(args[2], out int parsed) ? parsed : 50;
        PdoHistoryClient pdo = orch.ActivePdoHistory ?? throw new InvalidOperationException("PdoHistoryClient not active.");
        SendMessageResult? pdoRes = await pdo.RequestNextOlderPageAsync(targetChat, count, ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync(pdoRes is not null
            ? $"Dispatched PDO backfill for {targetChat} (count={count}): reqMsgId={pdoRes.MessageId}."
            : $"No older PDO backfill dispatched for {targetChat} (no local cursor found).").ConfigureAwait(false);
        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandleMediaAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: media download <msgId> <outFile> | media reupload <msgId>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        string sub = args[1].ToLowerInvariant();
        if (sub == "download")
        {
            if (args.Count < 4)
            {
                await ctx.Err.WriteLineAsync("Usage: media download <msgId> <outFile>").ConfigureAwait(false);
                return new CommandResult(1);
            }

            string msgId = args[2];
            string outPath = Path.GetFullPath(args[3]);
            MessageRecord record = await ctx.Runtime.Backend.History.GetMessageByIdAsync(msgId, ct: ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Message '{msgId}' not found in local SQLite history.");

            ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
            MediaTransferClient media = orch.ActiveMediaTransfer ?? throw new InvalidOperationException("MediaTransferClient not active.");

            await media.DownloadMessageMediaToFileAsync(record, outPath, ct).ConfigureAwait(false);
            long outLen = new FileInfo(outPath).Length;
            string shaPrefix = await ComputeFileSha256PrefixAsync(outPath, ct).ConfigureAwait(false);

            await ctx.Out.WriteLineAsync(
                $"Downloaded {outLen:N0} bytes ({record.Type}, mime={record.MimeType ?? "?"}) -> {outPath} [sha256:{shaPrefix}]")
                .ConfigureAwait(false);
            return new CommandResult(0);
        }

        if (sub == "reupload")
        {
            if (args.Count < 3)
            {
                await ctx.Err.WriteLineAsync("Usage: media reupload <msgId>").ConfigureAwait(false);
                return new CommandResult(1);
            }

            string msgId = args[2];
            MessageRecord record = await ctx.Runtime.Backend.History.GetMessageByIdAsync(msgId, ct: ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Message '{msgId}' not found in local SQLite history.");
            if (record.MediaKey is not { Length: 32 })
            {
                throw new InvalidOperationException($"Message '{msgId}' does not have a 32-byte MediaKey stored.");
            }

            ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
            MediaReuploadCoordinator reup = orch.ActiveMediaReupload ?? throw new InvalidOperationException("MediaReuploadCoordinator not active.");
            Jid chatJid = Jid.Parse(record.RemoteJid);
            Jid? participant = !record.FromMe && chatJid.IsGroup() && !string.IsNullOrEmpty(record.SenderJid)
                ? Jid.Parse(record.SenderJid)
                : null;

            MediaRetryDecryptedResult result = await reup.RequestAsync(
                new MediaReuploadRequest(record.Id, chatJid, record.FromMe, record.MediaKey, participant),
                ct).ConfigureAwait(false);

            await ctx.Out.WriteLineAsync(
                $"Media reupload result: msgId={record.Id}, status={result.Status}, newDirectPath={result.DirectPath ?? "<none>"}")
                .ConfigureAwait(false);
            return new CommandResult(0);
        }

        await ctx.Err.WriteLineAsync($"Unknown 'media' subcommand '{args[1]}'. Use 'download' or 'reupload'.").ConfigureAwait(false);
        return new CommandResult(1);
    }

    public static async Task<CommandResult> HandleSendMediaAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 5)
        {
            await ctx.Err.WriteLineAsync("Usage: send media <image|video|audio|ptt|document|sticker> <jid> <file> [caption...] [--gif]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        string kind = args[2].ToLowerInvariant();
        WaMediaType mediaType = kind switch
        {
            "image" => WaMediaType.Image,
            "video" => WaMediaType.Video,
            "audio" => WaMediaType.Audio,
            "ptt" => WaMediaType.Ptt,
            "document" or "doc" => WaMediaType.Document,
            "sticker" => WaMediaType.Sticker,
            _ => throw new ArgumentException($"Unsupported media kind '{args[2]}'. Use image, video, audio, ptt, document, or sticker.")
        };

        var (pos, _, flags) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: 3,
            valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--gif" });

        if (pos.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: send media <image|video|audio|ptt|document|sticker> <jid> <file> [caption...] [--gif]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        bool isGif = flags.Contains("--gif");
        Jid toJid = CliCommandHelpers.ParseTargetJid(pos[0]);
        string filePath = Path.GetFullPath(pos[1]);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Media file not found: '{filePath}'.", filePath);
        }

        string? caption = pos.Count > 2 ? string.Join(' ', pos.Skip(2)) : null;
        long fileBytes = new FileInfo(filePath).Length;
        string mime = CliCommandHelpers.InferMimeType(filePath, mediaType);
        MediaHeaderMetadata hdrMeta = MediaHeaderInspector.InspectFile(filePath, mime);

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MediaTransferClient media = orch.ActiveMediaTransfer ?? throw new InvalidOperationException("MediaTransferClient not active.");
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
        IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);

        UploadedMediaResult uploaded = await media.UploadFileAsync(filePath, mediaType, ct: ct).ConfigureAwait(false);
        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Message proto = BuildUploadedMediaProto(kind, mime, Path.GetFileName(filePath), caption, uploaded, nowSec, isGif, hdrMeta);

        SendMessageResult res = await sender.SendRichMessageAsync(toJid, proto, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync(
            $"Sent {kind} to {res.ChatJid}: msgId={res.MessageId}, devices={res.TargetDeviceCount}, bytes={fileBytes:N0}")
            .ConfigureAwait(false);
        return new CommandResult(0);
    }

    private static Message BuildUploadedMediaProto(
        string kind,
        string mime,
        string fileName,
        string? caption,
        UploadedMediaResult u,
        long nowSec,
        bool isGif,
        MediaHeaderMetadata? hdrMeta = null)
    {
        switch (kind)
        {
            case "image":
            {
                Message imgWrap = StatusStanzas.BuildImageStatus(
                    u.DirectPath, mime, u.FileSha256, u.FileEncSha256, u.MediaKey32, u.FileLength, nowSec, caption: caption);
                imgWrap.ImageMessage.Url = u.Url;
                if (hdrMeta?.Width is uint iw) imgWrap.ImageMessage.Width = iw;
                if (hdrMeta?.Height is uint ih) imgWrap.ImageMessage.Height = ih;
                return imgWrap;
            }

            case "video":
            {
                var vid = new Message.Types.VideoMessage
                {
                    Url = u.Url,
                    DirectPath = u.DirectPath,
                    Mimetype = mime,
                    FileSha256 = ByteString.CopyFrom(u.FileSha256),
                    FileEncSha256 = ByteString.CopyFrom(u.FileEncSha256),
                    MediaKey = ByteString.CopyFrom(u.MediaKey32),
                    FileLength = u.FileLength,
                    MediaKeyTimestamp = nowSec,
                    Seconds = hdrMeta?.Seconds ?? 15,
                    GifPlayback = isGif
                };
                if (hdrMeta?.Width is uint vw) vid.Width = vw;
                if (hdrMeta?.Height is uint vh) vid.Height = vh;
                if (u.StreamingSidecar is { Length: > 0 } sidecar)
                {
                    vid.StreamingSidecar = ByteString.CopyFrom(sidecar);
                }
                if (!string.IsNullOrEmpty(caption))
                {
                    vid.Caption = caption;
                }
                return new Message { VideoMessage = vid };
            }

            case "audio":
            case "ptt":
            {
                var aud = new Message.Types.AudioMessage
                {
                    Url = u.Url,
                    DirectPath = u.DirectPath,
                    Mimetype = mime,
                    FileSha256 = ByteString.CopyFrom(u.FileSha256),
                    FileEncSha256 = ByteString.CopyFrom(u.FileEncSha256),
                    MediaKey = ByteString.CopyFrom(u.MediaKey32),
                    FileLength = u.FileLength,
                    MediaKeyTimestamp = nowSec,
                    Ptt = string.Equals(kind, "ptt", StringComparison.OrdinalIgnoreCase),
                    Seconds = hdrMeta?.Seconds ?? 3
                };
                if (u.StreamingSidecar is { Length: > 0 } sidecar)
                {
                    aud.StreamingSidecar = ByteString.CopyFrom(sidecar);
                }
                return new Message { AudioMessage = aud };
            }

            case "sticker":
                return new Message
                {
                    StickerMessage = new Message.Types.StickerMessage
                    {
                        Url = u.Url,
                        DirectPath = u.DirectPath,
                        Mimetype = mime,
                        FileSha256 = ByteString.CopyFrom(u.FileSha256),
                        FileEncSha256 = ByteString.CopyFrom(u.FileEncSha256),
                        MediaKey = ByteString.CopyFrom(u.MediaKey32),
                        FileLength = u.FileLength,
                        MediaKeyTimestamp = nowSec,
                        Width = 512,
                        Height = 512
                    }
                };

            default:
            {
                var doc = new Message.Types.DocumentMessage
                {
                    Url = u.Url,
                    DirectPath = u.DirectPath,
                    Mimetype = mime,
                    Title = fileName,
                    FileName = fileName,
                    FileSha256 = ByteString.CopyFrom(u.FileSha256),
                    FileEncSha256 = ByteString.CopyFrom(u.FileEncSha256),
                    MediaKey = ByteString.CopyFrom(u.MediaKey32),
                    FileLength = u.FileLength,
                    MediaKeyTimestamp = nowSec
                };
                if (!string.IsNullOrEmpty(caption))
                {
                    doc.Caption = caption;
                }
                return new Message { DocumentMessage = doc };
            }
        }
    }

    private static async Task PrintMessageLineAsync(TextWriter stdout, MessageRecord m, bool includeChat)
    {
        string ts = CliCommandHelpers.FormatTimestamp(m.Timestamp);
        string sender = m.FromMe ? "ME" : (m.SenderJid ?? "?");
        string chatCol = includeChat ? $"[{m.RemoteJid}] " : string.Empty;
        string flags = string.Concat(
            m.EditedTimestamp is not null ? " [edited]" : string.Empty,
            m.IsRevoked ? " [revoked]" : string.Empty,
            !string.IsNullOrEmpty(m.ReactionsJson) ? $" [rx:{m.ReactionsJson}]" : string.Empty,
            !string.IsNullOrEmpty(m.DirectPath) ? " [media]" : string.Empty);
        string body = m.IsRevoked
            ? "<revoked>"
            : (string.IsNullOrWhiteSpace(m.Text) ? $"<{m.Type}>" : CliCommandHelpers.Truncate(m.Text, 120));
        string interactiveTag = FormatInteractiveSummaryTag(m.InteractiveJson);
        await stdout.WriteLineAsync($"  {chatCol}[{ts}] ({m.Id}) {sender}: {body}{interactiveTag}{flags}").ConfigureAwait(false);
    }

    private static string FormatInteractiveSummaryTag(string? interactiveJson)
    {
        if (InteractiveMessageParser.TryFromJson(interactiveJson) is not { } content)
        {
            return string.Empty;
        }
        if (content.Sections is { Count: > 0 })
        {
            var rows = content.Sections.SelectMany(s => s.Rows).Select((r, idx) => $"{idx + 1}:{r.Title}");
            string btn = string.IsNullOrWhiteSpace(content.ButtonText) ? "Options" : content.ButtonText;
            return $" [list({btn}): {string.Join(" | ", rows)}]";
        }
        if (content.Cards is { Count: > 0 })
        {
            return $" [carousel: {content.Cards.Count} cards]";
        }
        if (content.Flow is { } fl)
        {
            string label = fl.FlowCta ?? fl.FlowName ?? fl.FlowId ?? "Flow";
            return $" [flow: {label}]";
        }
        if (content.Booking is { } bk)
        {
            return $" [booking: {bk.StartDatetime ?? "-"}]";
        }
        if (content.PaymentReminder is { } prm)
        {
            string amt = !string.IsNullOrWhiteSpace(prm.AmountDue) ? $" {prm.Currency}{prm.AmountDue}" : string.Empty;
            string due = !string.IsNullOrWhiteSpace(prm.DueDate) ? $" due {prm.DueDate}" : string.Empty;
            return $" [payment_reminder:{amt}{due}]";
        }
        if (content.Order is { } ord && !string.IsNullOrWhiteSpace(ord.ReferenceId))
        {
            string amt = ord.TotalAmount.HasValue ? $" {ord.Currency}{ord.TotalAmount:0.##}" : string.Empty;
            string st = !string.IsNullOrWhiteSpace(ord.Status) ? $" ({ord.Status})" : string.Empty;
            return $" [order #{ord.ReferenceId}{amt}{st}]";
        }
        if (content.Buttons is { Count: > 0 })
        {
            var btns = content.Buttons.Select((b, idx) =>
                !string.IsNullOrEmpty(b.CopyCode) ? $"{idx + 1}:{b.DisplayText}(code={b.CopyCode})" : $"{idx + 1}:{b.DisplayText}");
            return $" [{content.Kind}: {string.Join(" | ", btns)}]";
        }
        if (content.Response is { } resp)
        {
            string val = resp.SelectedDisplayText ?? resp.SelectedId ?? resp.NativeFlowName ?? content.Kind;
            return $" [{content.Kind}: {val}]";
        }
        return $" [{content.Kind}]";
    }

    private static async Task<string> ComputeFileSha256PrefixAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        byte[] fullHash = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(fullHash.AsSpan(0, 8));
    }
}
