using Whatsapp;
using WhatsApp.Api;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Cli;

internal static class MessagingAndPresenceHandlers
{
    public static async Task<CommandResult> HandleSendAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 3)
        {
            await ctx.Err.WriteLineAsync("Usage: send text <jid> <text...> | send media <image|video|audio|document|sticker> <jid> <file> [caption...]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        if (string.Equals(args[1], "media", StringComparison.OrdinalIgnoreCase))
        {
            return await HistoryAndMediaHandlers.HandleSendMediaAsync(ctx, args, ct).ConfigureAwait(false);
        }

        if (string.Equals(args[1], "contact", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleSendContactAsync(ctx, args, ct).ConfigureAwait(false);
        }

        if (string.Equals(args[1], "live-location", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleSendLiveLocationAsync(ctx, args, ct).ConfigureAwait(false);
        }

        if (string.Equals(args[1], "scheduled-call", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleSendScheduledCallAsync(ctx, args, ct).ConfigureAwait(false);
        }

        int startIdx = string.Equals(args[1], "text", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var (pos, opts, flags) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: startIdx,
            valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--mention" },
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "--all", "--mention-all", "--mention-members", "--preview", "--spoiler"
            });

        if (pos.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: send text <jid> <text...> [--mention <jidOrPhone>...] [--all|--mention-members] [--preview] [--spoiler]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid toJid = CliCommandHelpers.ParseTargetJid(pos[0]);
        string text = string.Join(' ', pos.Skip(1));

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
        IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);
        bool mentionAll = flags.Contains("--all") || flags.Contains("--mention-all");
        bool mentionMembers = flags.Contains("--mention-members");
        (string finalText, Jid[]? mentionedJids) = await ResolveMentionsAsync(ctx, toJid, text, opts, mentionAll, mentionMembers, participants, ct).ConfigureAwait(false);

        SendMessageResult res;
        if (flags.Contains("--spoiler"))
        {
            Message inner = (mentionedJids is { Length: > 0 } || mentionAll)
                ? RichMessageBuilders.BuildMentionText(finalText, mentionedJids?.Select(j => j.ToNonAdString()).ToArray(), mentionAll)
                : new Message { Conversation = finalText };
            res = await sender.SendSpoilerAsync(toJid, inner, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
        }
        else if (flags.Contains("--preview"))
        {
            res = await sender.SendTextWithLinkPreviewAsync(
                toJid, finalText, groupParticipants: participants, mentionedJids: mentionedJids, mentionAll: mentionAll, awaitServerAck: true, ct: ct).ConfigureAwait(false);
        }
        else
        {
            res = await sender.SendTextAsync(
                toJid, finalText, participants, mentionedJids: mentionedJids, mentionAll: mentionAll, awaitServerAck: true, ct: ct).ConfigureAwait(false);
        }
        await ctx.Out.WriteLineAsync($"Sent text to {res.ChatJid}: msgId={res.MessageId}, devices={res.TargetDeviceCount}").ConfigureAwait(false);
        return new CommandResult(0);
    }

    private static async Task<CommandResult> HandleSendLiveLocationAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 5
            || !double.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lat)
            || !double.TryParse(args[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lng))
        {
            await ctx.Err.WriteLineAsync("Usage: send live-location <jid> <lat> <lng> [caption...]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid toJid = CliCommandHelpers.ParseTargetJid(args[2]);
        string? caption = args.Count > 5 ? string.Join(' ', args.Skip(5)) : null;
        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
        IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);

        SendMessageResult res = await sender.SendLiveLocationAsync(toJid, lat, lng, caption, groupParticipants: participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync($"Sent live location ({lat}, {lng}) to {res.ChatJid}: msgId={res.MessageId}").ConfigureAwait(false);
        return new CommandResult(0);
    }

    private static async Task<CommandResult> HandleSendScheduledCallAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 4 || !long.TryParse(args[3], out long unixMs))
        {
            await ctx.Err.WriteLineAsync("Usage: send scheduled-call <jid> <unixMs> [title...]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid toJid = CliCommandHelpers.ParseTargetJid(args[2]);
        string? title = args.Count > 4 ? string.Join(' ', args.Skip(4)) : null;
        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
        IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);

        SendMessageResult res = await sender.SendScheduledCallAsync(toJid, unixMs, title: title, groupParticipants: participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync($"Sent scheduled call to {res.ChatJid}: msgId={res.MessageId}").ConfigureAwait(false);
        return new CommandResult(0);
    }

    private static async Task<CommandResult> HandleSendContactAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 5)
        {
            await ctx.Err.WriteLineAsync("Usage: send contact <jid> <phone> <displayName...>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid toJid = CliCommandHelpers.ParseTargetJid(args[2]);
        string phone = args[3];
        string displayName = string.Join(' ', args.Skip(4));
        string vcard = RichMessageBuilders.BuildVCard(displayName, phone);

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
        IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);

        SendMessageResult res = await sender.SendContactAsync(toJid, displayName, vcard, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync($"Sent contact \"{displayName}\" to {res.ChatJid}: msgId={res.MessageId}").ConfigureAwait(false);
        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandleReplyAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var (pos, opts, flags) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: 1,
            valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--mention" },
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--from-me", "--all", "--mention-all", "--mention-members" });

        if (pos.Count < 3)
        {
            await ctx.Err.WriteLineAsync("Usage: reply <jid> <msgId> <text...> [--from-me] [--mention <jidOrPhone>...] [--all|--mention-members]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid toJid = CliCommandHelpers.ParseTargetJid(pos[0]);
        string targetId = pos[1];
        string replyText = string.Join(' ', pos.Skip(2));
        bool? fromMeOverride = flags.Contains("--from-me") ? true : null;
        bool mentionAll = flags.Contains("--all") || flags.Contains("--mention-all");
        bool mentionMembers = flags.Contains("--mention-members");

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
        var (quotedKey, stored) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, toJid, targetId, ct, fromMeOverride).ConfigureAwait(false);
        IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);
        (string finalText, Jid[]? mentionedJids) = await ResolveMentionsAsync(ctx, toJid, replyText, opts, mentionAll, mentionMembers, participants, ct).ConfigureAwait(false);

        if (toJid.IsGroup() && string.IsNullOrEmpty(quotedKey.Participant))
        {
            if (!string.IsNullOrEmpty(stored?.SenderJid))
            {
                quotedKey.Participant = stored.SenderJid;
            }
            else if (quotedKey.FromMe)
            {
                DeviceCredentials? creds = await ctx.Runtime.Backend.Device.LoadAsync(ct).ConfigureAwait(false);
                bool preferLid = participants?.Any(p => p.IsLid()) == true;
                Jid? ownSender = preferLid
                    ? (creds?.Lid?.ToNonAd() ?? creds?.Pn?.ToNonAd())
                    : (creds?.Pn?.ToNonAd() ?? creds?.Lid?.ToNonAd());
                if (ownSender is Jid os)
                {
                    quotedKey.Participant = os.ToString();
                }
            }
        }
        else if (!toJid.IsGroup())
        {
            quotedKey.ClearParticipant();
        }

        var quotedMessage = new Message
        {
            Conversation = string.IsNullOrWhiteSpace(stored?.Text) ? string.Empty : stored.Text
        };

        SendMessageResult res = await sender.SendReplyTextAsync(
            toJid,
            finalText,
            quotedKey,
            quotedMessage,
            participants,
            mentionedJids: mentionedJids,
            mentionAll: mentionAll,
            awaitServerAck: true,
            ct: ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync($"Sent reply to {res.ChatJid} (quoted={quotedKey.Id}): msgId={res.MessageId}, devices={res.TargetDeviceCount}").ConfigureAwait(false);
        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandleEditAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 4)
        {
            await ctx.Err.WriteLineAsync("Usage: edit <jid> <msgId> <newText...>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid toJid = CliCommandHelpers.ParseTargetJid(args[1]);
        string targetId = args[2];
        string newText = string.Join(' ', args.Skip(3));

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
        var (origKey, stored) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, toJid, targetId, ct, fromMeOverride: true).ConfigureAwait(false);
        IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);

        var newContent = new Message { Conversation = newText };
        SendMessageResult res = await sender.SendMessageEditAsync(
            toJid,
            origKey,
            newContent,
            preferSecretEncrypted: false,
            originalSenderJid: stored?.SenderJid,
            groupParticipants: participants,
            awaitServerAck: true,
            ct: ct).ConfigureAwait(false);

        await ctx.Out.WriteLineAsync($"Sent edit for {origKey.Id} to {res.ChatJid}: msgId={res.MessageId}, devices={res.TargetDeviceCount}").ConfigureAwait(false);
        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandleRevokeAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var (pos, _, flags) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: 1,
            valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--admin" });

        if (pos.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: revoke <jid> <msgId> [--admin]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid toJid = CliCommandHelpers.ParseTargetJid(pos[0]);
        string targetId = pos[1];
        bool isAdmin = flags.Contains("--admin");

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
        var (targetKey, _) = await CliCommandHelpers.ResolveMessageKeyAsync(
            ctx,
            toJid,
            targetId,
            ct,
            fromMeOverride: isAdmin ? false : null).ConfigureAwait(false);
        IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);

        SendMessageResult res = await sender.SendRevokeAsync(toJid, targetKey, isAdmin, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync($"Sent {(isAdmin ? "admin-" : string.Empty)}revoke for {targetKey.Id} to {res.ChatJid}: msgId={res.MessageId}").ConfigureAwait(false);
        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandleReactAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var (pos, _, flags) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: 1,
            valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--encrypted", "--from-me" });

        if (pos.Count < 3)
        {
            await ctx.Err.WriteLineAsync("Usage: react <jid> <msgId> <emoji|remove> [--encrypted] [--from-me]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid toJid = CliCommandHelpers.ParseTargetJid(pos[0]);
        string targetId = pos[1];
        string emoji = string.Equals(pos[2], "remove", StringComparison.OrdinalIgnoreCase) ? string.Empty : pos[2];
        bool encrypted = flags.Contains("--encrypted");
        bool? fromMeOverride = flags.Contains("--from-me") ? true : null;

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
        var (targetKey, stored) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, toJid, targetId, ct, fromMeOverride).ConfigureAwait(false);
        IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);

        SendMessageResult res = await sender.SendReactionAsync(
            toJid,
            targetKey,
            emoji,
            encrypted,
            stored?.SenderJid,
            participants,
            awaitServerAck: true,
            ct: ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync($"Sent reaction '{(emoji == string.Empty ? "<removed>" : emoji)}' on {targetKey.Id} to {res.ChatJid}: msgId={res.MessageId}").ConfigureAwait(false);
        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandlePinOrKeepAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        bool isPin,
        CancellationToken ct)
    {
        var (pos, opts, flags) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: 1,
            valueOptions: isPin
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--duration" }
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--from-me" });

        string cmdName = isPin ? "pin" : "keep";
        if (pos.Count < 3)
        {
            await ctx.Err.WriteLineAsync(isPin
                ? "Usage: pin <jid> <msgId> <true|false> [--duration 24h|7d|30d|secs] [--from-me]"
                : "Usage: keep <jid> <msgId> <true|false> [--from-me]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid toJid = CliCommandHelpers.ParseTargetJid(pos[0]);
        string targetId = pos[1];
        if (!bool.TryParse(pos[2], out bool enable))
        {
            throw new ArgumentException($"Invalid boolean '{pos[2]}'. Expected true or false.");
        }

        uint durationSeconds = 604_800u;
        if (isPin && opts.TryGetValue("--duration", out var durVals))
        {
            string rawDur = durVals[^1].ToLowerInvariant();
            durationSeconds = rawDur switch
            {
                "24h" or "1d" => 86_400u,
                "7d" => 604_800u,
                "30d" => 2_592_000u,
                _ when uint.TryParse(rawDur, out uint parsedSec) => parsedSec,
                _ => throw new ArgumentException($"Invalid pin duration '{durVals[^1]}'. Use 24h, 7d, 30d, or seconds.")
            };
        }

        bool? fromMeOverride = flags.Contains("--from-me") ? true : null;
        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
        var (targetKey, stored) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, toJid, targetId, ct, fromMeOverride).ConfigureAwait(false);
        IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);

        if (toJid.IsGroup() && string.IsNullOrEmpty(targetKey.Participant))
        {
            if (!string.IsNullOrEmpty(stored?.SenderJid))
            {
                targetKey.Participant = stored.SenderJid;
            }
            else if (targetKey.FromMe)
            {
                DeviceCredentials? creds = await ctx.Runtime.Backend.Device.LoadAsync(ct).ConfigureAwait(false);
                bool preferLid = participants?.Any(p => p.IsLid()) == true;
                Jid? ownSender = preferLid
                    ? (creds?.Lid?.ToNonAd() ?? creds?.Pn?.ToNonAd())
                    : (creds?.Pn?.ToNonAd() ?? creds?.Lid?.ToNonAd());
                if (ownSender is Jid os)
                {
                    targetKey.Participant = os.ToString();
                }
            }
        }

        SendMessageResult res = isPin
            ? await sender.SendPinInChatAsync(toJid, targetKey, enable, participants, awaitServerAck: true, durationSeconds: durationSeconds, ct: ct).ConfigureAwait(false)
            : await sender.SendKeepInChatAsync(toJid, targetKey, enable, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);

        await ctx.Out.WriteLineAsync($"Sent {cmdName}={enable} for {targetKey.Id} to {res.ChatJid}: msgId={res.MessageId}").ConfigureAwait(false);
        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandlePollAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: poll create <jid> <question> --opt <a> --opt <b> [--multi] | poll vote <jid> <msgId> --opt <a> [--creator <jid>]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        string sub = args[1].ToLowerInvariant();
        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");

        if (sub == "create")
        {
            var (pos, opts, flags) = CliCommandHelpers.ParseOptions(
                args,
                startIndex: 2,
                valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--opt", "--selectable" },
                flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--multi" });

            if (pos.Count < 2 || !opts.TryGetValue("--opt", out var pollOptions) || pollOptions.Count < 2)
            {
                await ctx.Err.WriteLineAsync("Usage: poll create <jid> <question> --opt <a> --opt <b> [--multi]").ConfigureAwait(false);
                return new CommandResult(1);
            }

            Jid toJid = CliCommandHelpers.ParseTargetJid(pos[0]);
            string question = string.Join(' ', pos.Skip(1));
            uint selectable = opts.TryGetValue("--selectable", out var selVals) && uint.TryParse(selVals[^1], out uint s)
                ? s
                : (flags.Contains("--multi") ? (uint)pollOptions.Count : 1u);

            IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);
            SendMessageResult res = await sender.SendPollAsync(toJid, question, pollOptions, selectable, groupParticipants: participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Created poll \"{question}\" in {res.ChatJid}: msgId={res.MessageId}, devices={res.TargetDeviceCount}").ConfigureAwait(false);
            return new CommandResult(0);
        }

        if (sub == "vote")
        {
            var (pos, opts, flags) = CliCommandHelpers.ParseOptions(
                args,
                startIndex: 2,
                valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--opt", "--creator" },
                flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--from-me" });

            if (pos.Count < 2 || !opts.TryGetValue("--opt", out var selected))
            {
                await ctx.Err.WriteLineAsync("Usage: poll vote <jid> <msgId> --opt <option> [--creator <jid>]").ConfigureAwait(false);
                return new CommandResult(1);
            }

            Jid toJid = CliCommandHelpers.ParseTargetJid(pos[0]);
            string pollId = pos[1];
            bool? fromMeOverride = flags.Contains("--from-me") ? true : null;
            var (pollKey, stored) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, toJid, pollId, ct, fromMeOverride).ConfigureAwait(false);
            string creator = await ResolveCreatorJidAsync(ctx, opts, toJid, stored, ct).ConfigureAwait(false);
            IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);

            SendMessageResult res = await sender.SendPollVoteAsync(toJid, pollKey, selected, creator, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Voted [{string.Join(", ", selected)}] on poll {pollKey.Id}: msgId={res.MessageId}").ConfigureAwait(false);
            return new CommandResult(0);
        }

        await ctx.Err.WriteLineAsync("Unknown 'poll' subcommand. Use 'create' or 'vote'.").ConfigureAwait(false);
        return new CommandResult(1);
    }

    public static async Task<CommandResult> HandleEventAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: event create <jid> <name> --start <unixSec|iso> [--end <ts>] [--desc <text>] [--loc <name>] | event rsvp <jid> <msgId> <going|not_going|maybe> [--creator <jid>]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        string sub = args[1].ToLowerInvariant();
        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");

        if (sub == "create")
        {
            var (pos, opts, _) = CliCommandHelpers.ParseOptions(
                args,
                startIndex: 2,
                valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--start", "--end", "--desc", "--loc", "--join" },
                flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            if (pos.Count < 2 || !opts.TryGetValue("--start", out var startVals))
            {
                await ctx.Err.WriteLineAsync("Usage: event create <jid> <name> --start <unixSec|iso> [--end <ts>] [--desc <text>] [--loc <name>]").ConfigureAwait(false);
                return new CommandResult(1);
            }

            Jid toJid = CliCommandHelpers.ParseTargetJid(pos[0]);
            string eventName = string.Join(' ', pos.Skip(1));
            long startSec = CliCommandHelpers.ParseTimestampSeconds(startVals[^1]);
            long? endSec = opts.TryGetValue("--end", out var endVals) ? CliCommandHelpers.ParseTimestampSeconds(endVals[^1]) : null;
            string? desc = opts.TryGetValue("--desc", out var descVals) ? descVals[^1] : null;
            string? loc = opts.TryGetValue("--loc", out var locVals) ? locVals[^1] : null;
            string? join = opts.TryGetValue("--join", out var joinVals) ? joinVals[^1] : null;

            IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);
            SendMessageResult res = await sender.SendEventAsync(toJid, eventName, desc, startSec, endSec, loc, join, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Created event \"{eventName}\" in {res.ChatJid}: msgId={res.MessageId}").ConfigureAwait(false);
            return new CommandResult(0);
        }

        if (sub == "rsvp")
        {
            var (pos, opts, flags) = CliCommandHelpers.ParseOptions(
                args,
                startIndex: 2,
                valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--creator", "--guests" },
                flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--from-me" });

            if (pos.Count < 3)
            {
                await ctx.Err.WriteLineAsync("Usage: event rsvp <jid> <msgId> <going|not_going|maybe> [--creator <jid>] [--guests N]").ConfigureAwait(false);
                return new CommandResult(1);
            }

            Jid toJid = CliCommandHelpers.ParseTargetJid(pos[0]);
            string eventId = pos[1];
            Message.Types.EventResponseMessage.Types.EventResponseType respType = pos[2].ToLowerInvariant() switch
            {
                "going" => Message.Types.EventResponseMessage.Types.EventResponseType.Going,
                "not_going" or "notgoing" or "no" => Message.Types.EventResponseMessage.Types.EventResponseType.NotGoing,
                "maybe" => Message.Types.EventResponseMessage.Types.EventResponseType.Maybe,
                _ => throw new ArgumentException($"Unknown RSVP response '{pos[2]}'. Use going, not_going, or maybe.")
            };

            int extraGuests = opts.TryGetValue("--guests", out var gVals) && int.TryParse(gVals[^1], out int g) ? g : 0;
            bool? fromMeOverride = flags.Contains("--from-me") ? true : null;
            var (eventKey, stored) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, toJid, eventId, ct, fromMeOverride).ConfigureAwait(false);
            string creator = await ResolveCreatorJidAsync(ctx, opts, toJid, stored, ct).ConfigureAwait(false);
            IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, toJid, ct).ConfigureAwait(false);

            SendMessageResult res = await sender.SendEventResponseAsync(toJid, eventKey, respType, creator, extraGuests, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Sent event RSVP={respType} on {eventKey.Id}: msgId={res.MessageId}").ConfigureAwait(false);
            return new CommandResult(0);
        }

        await ctx.Err.WriteLineAsync("Unknown 'event' subcommand. Use 'create' or 'rsvp'.").ConfigureAwait(false);
        return new CommandResult(1);
    }

    public static async Task<CommandResult> HandleCommentAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 4)
        {
            await ctx.Err.WriteLineAsync("Usage: comment <groupJid> <parentMsgId> <text...>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid groupJid = CliCommandHelpers.ParseTargetJid(args[1]);
        string parentId = args[2];
        string text = string.Join(' ', args.Skip(3));

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        CommentClient comments = orch.ActiveComments ?? throw new InvalidOperationException("CommentClient not active.");
        var (parentKey, _) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, groupJid, parentId, ct).ConfigureAwait(false);
        IReadOnlyList<Jid> participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, groupJid, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Comments require a @g.us group JID, got '{groupJid}'.");

        SendMessageResult res = await comments.SendTextCommentAsync(groupJid, parentKey, text, participants, ct: ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync($"Sent comment on {parentKey.Id} in {res.ChatJid}: msgId={res.MessageId}").ConfigureAwait(false);
        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandlePresenceAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: presence <available|unavailable> [pushName] | presence sub <jid>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        PresenceClient presence = orch.ActivePresence ?? throw new InvalidOperationException("PresenceClient not active.");

        string sub = args[1].ToLowerInvariant();
        if (sub == "sub" || sub == "subscribe")
        {
            if (args.Count < 3)
            {
                await ctx.Err.WriteLineAsync("Usage: presence sub <jid>").ConfigureAwait(false);
                return new CommandResult(1);
            }
            Jid target = CliCommandHelpers.ParseTargetJid(args[2]);
            await presence.SubscribeAsync(target, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Subscribed to presence updates for {target}.").ConfigureAwait(false);
            return new CommandResult(0);
        }

        PresenceStatus status = sub switch
        {
            "available" or "online" => PresenceStatus.Available,
            "unavailable" or "offline" => PresenceStatus.Unavailable,
            _ => throw new ArgumentException($"Unknown presence status '{args[1]}'. Use available, unavailable, or sub <jid>.")
        };

        string? pushName = args.Count > 2 ? string.Join(' ', args.Skip(2)) : null;
        await presence.SetAvailabilityAsync(status, pushName, ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync($"Set availability -> {status}.").ConfigureAwait(false);
        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandleTypingAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 3)
        {
            await ctx.Err.WriteLineAsync("Usage: typing <jid> <composing|recording|paused>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid target = CliCommandHelpers.ParseTargetJid(args[1]);
        ChatActivity activity = args[2].ToLowerInvariant() switch
        {
            "composing" or "typing" => ChatActivity.Typing,
            "recording" or "audio" => ChatActivity.RecordingAudio,
            "paused" or "idle" or "stop" => ChatActivity.Idle,
            _ => throw new ArgumentException($"Unknown activity '{args[2]}'. Use composing, recording, or paused.")
        };

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        PresenceClient presence = orch.ActivePresence ?? throw new InvalidOperationException("PresenceClient not active.");
        await presence.SendChatStateAsync(target, activity, ct: ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync($"Sent chat state {activity} -> {target}.").ConfigureAwait(false);
        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandleReadAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var (pos, _, flags) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: 1,
            valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--self", "--played" });

        if (pos.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: read <jid> <msgId> [extraIds...] [--self] [--played]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid chatJid = CliCommandHelpers.ParseTargetJid(pos[0]);
        string primaryId = pos[1];
        IReadOnlyList<string>? extraIds = pos.Count > 2 ? pos.Skip(2).ToArray() : null;
        bool isSelf = flags.Contains("--self");
        bool played = flags.Contains("--played");

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageReceiver receiver = orch.ActiveMessageReceiver ?? throw new InvalidOperationException("MessageReceiver not active.");
        var (_, stored) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, chatJid, primaryId, ct).ConfigureAwait(false);

        Jid? participant = chatJid.IsGroup() && !string.IsNullOrEmpty(stored?.SenderJid)
            ? Jid.Parse(stored.SenderJid)
            : null;

        await receiver.SendReadReceiptAsync(primaryId, chatJid, isSelf, played, participant, extraIds, ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync($"Sent {(played ? "played" : "read")}{(isSelf ? "-self" : string.Empty)} receipt for {primaryId} in {chatJid}.").ConfigureAwait(false);
        return new CommandResult(0);
    }

    private static async Task<(string Text, Jid[]? MentionedJids)> ResolveMentionsAsync(
        CliCommandContext ctx,
        Jid targetChat,
        string text,
        IReadOnlyDictionary<string, List<string>> opts,
        bool mentionAll,
        bool mentionMembers,
        IReadOnlyList<Jid>? participants,
        CancellationToken ct)
    {
        bool hasExplicitMentions = opts.TryGetValue("--mention", out var rawMentions) && rawMentions.Count > 0;
        if (!hasExplicitMentions && !mentionAll && !mentionMembers)
        {
            return (text, null);
        }

        GroupMetadata? groupMeta = null;
        if (targetChat.IsGroup() && ctx.Runtime.Orchestrator?.ActiveGroups is { } groups)
        {
            if (!groups.TryGetCached(targetChat, out groupMeta) || groupMeta is null)
            {
                groupMeta = await groups.QueryMetadataAsync(targetChat, bypassCache: false, ct).ConfigureAwait(false);
            }
        }

        var resolved = new List<Jid>();
        var autoPrefixes = new List<string>();
        if (mentionAll && !text.Contains("@all", StringComparison.OrdinalIgnoreCase) && !text.Contains("@everyone", StringComparison.OrdinalIgnoreCase))
        {
            autoPrefixes.Add("@all");
        }

        if (hasExplicitMentions)
        {
            foreach (string raw in rawMentions!)
            {
                Jid parsed = CliCommandHelpers.ParseTargetJid(raw);
                if (groupMeta is not null)
                {
                    string bare = parsed.ToNonAdString();
                    GroupParticipantInfo? match = groupMeta.Participants.FirstOrDefault(p =>
                        string.Equals(p.Jid.ToNonAdString(), bare, StringComparison.OrdinalIgnoreCase)
                        || (p.PhoneNumber is Jid pn && string.Equals(pn.ToNonAdString(), bare, StringComparison.OrdinalIgnoreCase))
                        || (p.Lid is Jid lid && string.Equals(lid.ToNonAdString(), bare, StringComparison.OrdinalIgnoreCase)));
                    if (match is not null)
                    {
                        parsed = match.Jid.ToNonAd();
                    }
                }

                if (!resolved.Contains(parsed))
                {
                    resolved.Add(parsed);
                }
                string tagToken = $"@{parsed.User}";
                if (!text.Contains(tagToken, StringComparison.OrdinalIgnoreCase))
                {
                    autoPrefixes.Add(tagToken);
                }
            }
        }

        if (mentionMembers && participants is { Count: > 0 })
        {
            foreach (Jid member in participants)
            {
                Jid bareMember = member.ToNonAd();
                if (!resolved.Contains(bareMember))
                {
                    resolved.Add(bareMember);
                }
            }
        }

        string updatedText = autoPrefixes.Count > 0 ? $"{string.Join(' ', autoPrefixes)} {text}" : text;
        return (updatedText, resolved.Count > 0 ? resolved.ToArray() : null);
    }

    private static async Task<string> ResolveCreatorJidAsync(
        CliCommandContext ctx,
        IReadOnlyDictionary<string, List<string>> opts,
        Jid chatJid,
        MessageRecord? stored,
        CancellationToken ct)
    {
        if (opts.TryGetValue("--creator", out var creatorVals))
        {
            return CliCommandHelpers.ParseTargetJid(creatorVals[^1]).ToNonAdString();
        }
        if (stored is not null)
        {
            if (stored.FromMe)
            {
                DeviceCredentials? creds = await ctx.Runtime.Backend.Device.LoadAsync(ct).ConfigureAwait(false);
                if (chatJid.IsLid() && creds?.Lid is Jid lid) return lid.ToNonAdString();
                if (creds?.Pn is Jid pn) return pn.ToNonAdString();
                if (creds?.Lid is Jid anyLid) return anyLid.ToNonAdString();
            }
            if (!string.IsNullOrEmpty(stored.SenderJid))
            {
                return stored.SenderJid;
            }
        }
        return chatJid.ToNonAdString();
    }

    public static async Task<CommandResult> HandleBotAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: bot <list|modes|profile <personaId>|ask <chatOrBotJid> <prompt...> [--bot <jid>] [--persona <id>] [--with-bot]>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        string sub = args[1].ToLowerInvariant();
        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        BotClient bots = orch.ActiveBots ?? throw new InvalidOperationException("BotClient not active.");

        switch (sub)
        {
            case "list":
            {
                BotList list = await bots.GetBotListAsync(ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Bot list v={(int)list.Version}, bhash={list.BHash ?? "-"}, default={list.DefaultBot?.Jid.ToString() ?? "-"} (persona={list.DefaultBot?.PersonaId ?? "-"}):").ConfigureAwait(false);
                foreach (BotListSection sec in list.Sections)
                {
                    await ctx.Out.WriteLineAsync($"  Section '{sec.Name}' ({sec.SectionType}): {sec.Bots.Count} bots").ConfigureAwait(false);
                    foreach (BotListEntry b in sec.Bots)
                    {
                        await ctx.Out.WriteLineAsync($"    {b.Jid} | persona={b.PersonaId} | default={b.IsDefault}").ConfigureAwait(false);
                    }
                }
                return new CommandResult(0);
            }

            case "modes":
            {
                try
                {
                    IReadOnlyList<DynamicAiMode> modes = await bots.GetDynamicAiModesAsync(ct).ConfigureAwait(false);
                    await ctx.Out.WriteLineAsync($"Dynamic AI modes ({modes.Count}):").ConfigureAwait(false);
                    foreach (DynamicAiMode m in modes)
                    {
                        await ctx.Out.WriteLineAsync($"  {m.ModeId} | {m.Title ?? "-"} | exp={m.IsExperimental} | {m.Subtitle ?? "-"}").ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is MexException or IqException)
                {
                    await ctx.Out.WriteLineAsync($"Dynamic AI modes unavailable over Noise MEX ({ex.Message}): WAWebFetchDynamicAIModesQuery ({MexStanzas.FetchDynamicAiModes.Id}) is a web.whatsapp.com browser Relay query requiring Web cookies.").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }

            case "profile":
            {
                BotList list = await bots.GetBotListAsync(ct: ct).ConfigureAwait(false);
                var targets = new List<(Jid BotJid, string? PersonaId)>();
                if (args.Count < 3)
                {
                    if (list.DefaultBot is { } def)
                    {
                        targets.Add((def.Jid, def.PersonaId));
                    }
                    else if (list.Sections.SelectMany(s => s.Bots).FirstOrDefault() is { } first)
                    {
                        targets.Add((first.Jid, first.PersonaId));
                    }
                }
                else
                {
                    foreach (string raw in args.Skip(2))
                    {
                        if (Jid.TryParse(raw, out Jid parsedJid) && parsedJid.IsBot())
                        {
                            string? matchPersona = list.Sections.SelectMany(s => s.Bots)
                                .FirstOrDefault(b => b.Jid.ToNonAd() == parsedJid.ToNonAd())?.PersonaId;
                            targets.Add((parsedJid, matchPersona));
                        }
                        else
                        {
                            BotListEntry? match = list.Sections.SelectMany(s => s.Bots)
                                .FirstOrDefault(b => string.Equals(b.PersonaId, raw, StringComparison.OrdinalIgnoreCase));
                            targets.Add((match?.Jid ?? Jid.Parse("867051314767696@bot"), raw));
                        }
                    }
                }

                foreach (var (bJid, pid) in targets)
                {
                    UsyncBotProfileInfo? p = await bots.GetUsyncBotProfileAsync(bJid, pid, ct).ConfigureAwait(false);
                    if (p is null)
                    {
                        await ctx.Out.WriteLineAsync($"BotProfile {bJid} (persona={pid ?? "-"}): <not returned>").ConfigureAwait(false);
                        continue;
                    }
                    await ctx.Out.WriteLineAsync($"BotProfile {bJid} | persona={p.PersonaId} | name={p.Name} | category={p.Category} | default={p.IsDefault} | metaCreated={p.IsMetaCreated?.ToString() ?? "-"}").ConfigureAwait(false);
                    await ctx.Out.WriteLineAsync($"  desc={p.Description} | attrs={p.Attributes}").ConfigureAwait(false);
                    if (p.Prompts.Count > 0)
                    {
                        await ctx.Out.WriteLineAsync($"  prompts: {string.Join(" | ", p.Prompts.Select(x => $"{x.Emoji} {x.Text}".Trim()))}").ConfigureAwait(false);
                    }
                    if (p.Commands.Count > 0)
                    {
                        await ctx.Out.WriteLineAsync($"  commands: {string.Join(", ", p.Commands.Select(c => $"{c.Name} ({c.Description})"))}").ConfigureAwait(false);
                    }
                }
                return new CommandResult(0);
            }

            case "ask":
            {
                var (pos, opts, flags) = CliCommandHelpers.ParseOptions(
                    args,
                    startIndex: 2,
                    valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--bot", "--persona" },
                    flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--with-bot" });
                if (pos.Count < 2)
                {
                    await ctx.Err.WriteLineAsync("Usage: bot ask <chatOrBotJid> <prompt...> [--bot <jid>] [--persona <id>] [--with-bot]").ConfigureAwait(false);
                    return new CommandResult(1);
                }

                Jid target = CliCommandHelpers.ParseTargetJid(pos[0]);
                string prompt = string.Join(' ', pos.Skip(1));
                Jid botJid = opts.TryGetValue("--bot", out var bv)
                    ? CliCommandHelpers.ParseTargetJid(bv[^1])
                    : Jid.Parse("867051314767696@bot");
                string? personaId = opts.TryGetValue("--persona", out var pv) ? pv[^1] : null;

                IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, target, ct).ConfigureAwait(false);
                if (participants is not null && flags.Contains("--with-bot") && !participants.Contains(botJid))
                {
                    participants = participants.Append(botJid).ToArray();
                }

                string finalPrompt = target.IsGroup() && !prompt.Contains($"@{botJid.User}", StringComparison.Ordinal)
                    ? $"@{botJid.User} {prompt}"
                    : prompt;

                SendMessageResult res = await bots.SendBotPromptAsync(
                    target,
                    finalPrompt,
                    personaId: personaId,
                    mentionedBotJid: target.IsGroup() ? botJid : null,
                    groupParticipants: participants,
                    awaitServerAck: true,
                    ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Sent bot prompt to {res.ChatJid}: msgId={res.MessageId}, devices={res.TargetDeviceCount}, bot={botJid}, withBot={flags.Contains("--with-bot")}").ConfigureAwait(false);
                return new CommandResult(0);
            }

            default:
                await ctx.Err.WriteLineAsync($"Unknown bot subcommand '{sub}'.").ConfigureAwait(false);
                return new CommandResult(1);
        }
    }

    public static async Task<CommandResult> HandleInteractAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 4)
        {
            await ctx.Err.WriteLineAsync("Usage: interact <list|select|button|location|phone|address|callperm|flow> <jid> <msgId> [args...]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        string sub = args[1].ToLowerInvariant();
        Jid chatJid = CliCommandHelpers.ParseTargetJid(args[2]);
        string msgId = args[3];

        var (pos, opts, flags) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: 4,
            valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "--title", "--desc", "--flow", "--params", "--name", "--address", "--field", "--token", "--exp"
            },
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--template", "--fetch" });

        var (quotedKey, stored) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, chatJid, msgId, ct).ConfigureAwait(false);
        if (!chatJid.IsGroup())
        {
            quotedKey.ClearParticipant();
        }
        InteractiveMessageContent? content = InteractiveMessageParser.TryFromJson(stored?.InteractiveJson);

        if (sub == "list")
        {
            if (content is null)
            {
                await ctx.Out.WriteLineAsync($"Message {msgId} in {chatJid} has no stored interactive menu/buttons.").ConfigureAwait(false);
                return new CommandResult(0);
            }

            await ctx.Out.WriteLineAsync($"Interactive ({content.Kind}) for {msgId}: title=\"{content.Title ?? "-"}\", button=\"{content.ButtonText ?? "-"}\"").ConfigureAwait(false);
            if (!string.IsNullOrEmpty(content.Body))
            {
                await ctx.Out.WriteLineAsync($"  Body: {content.Body}").ConfigureAwait(false);
            }
            if (content.Sections is { Count: > 0 })
            {
                int rowNum = 1;
                foreach (InteractiveSection sec in content.Sections)
                {
                    if (!string.IsNullOrEmpty(sec.Title))
                    {
                        await ctx.Out.WriteLineAsync($"  Section: {sec.Title}").ConfigureAwait(false);
                    }
                    foreach (InteractiveRow r in sec.Rows)
                    {
                        string desc = string.IsNullOrEmpty(r.Description) ? string.Empty : $" — {r.Description}";
                        await ctx.Out.WriteLineAsync($"    {rowNum++}. [{r.Id}] {r.Title}{desc}").ConfigureAwait(false);
                    }
                }
            }
            if (content.Order is { } ord)
            {
                string amt = ord.TotalAmount.HasValue ? $"{ord.Currency}{ord.TotalAmount:0.##}" : "-";
                await ctx.Out.WriteLineAsync($"  Order: ref={ord.ReferenceId ?? "-"}, amount={amt}, status={ord.Status ?? "-"}, payStatus={ord.PaymentStatus ?? "-"}, desc={ord.Description ?? "-"}").ConfigureAwait(false);
                if (!string.IsNullOrEmpty(ord.PaymentUrl))
                {
                    await ctx.Out.WriteLineAsync($"  PaymentLink: {ord.PaymentUrl}").ConfigureAwait(false);
                }
            }
            if (content.Flow is { } fl)
            {
                await ctx.Out.WriteLineAsync($"  Flow: id={fl.FlowId ?? "-"}, cta=\"{fl.FlowCta ?? "-"}\", action={fl.FlowAction ?? "-"}, mode={fl.Mode ?? "-"}, token={(fl.FlowToken is not null ? "<present>" : "-")}").ConfigureAwait(false);
            }
            if (content.Booking is { } bk)
            {
                await ctx.Out.WriteLineAsync($"  Booking: start={bk.StartDatetime ?? "-"}, end={bk.EndDatetime ?? "-"}, loc={bk.Location ?? "-"}, url={bk.BookingUrl ?? "-"}").ConfigureAwait(false);
            }
            if (content.PaymentReminder is { } prm)
            {
                await ctx.Out.WriteLineAsync($"  PaymentReminder: title={prm.Title ?? "-"}, amount={prm.Currency}{prm.AmountDue ?? "-"}, due={prm.DueDate ?? "-"}, overdue={prm.IsOverdue}, url={prm.Url ?? "-"}").ConfigureAwait(false);
            }
            if (content.Address is { } addr)
            {
                await ctx.Out.WriteLineAsync($"  AddressPrompt: savedAddresses={addr.SavedAddressesJson?.Count ?? 0}, hasDefaults={addr.SavedValuesJson is not null}").ConfigureAwait(false);
            }
            if (content.Catalog is { } cat)
            {
                await ctx.Out.WriteLineAsync($"  Catalog: biz={cat.BusinessPhoneNumber ?? "-"}, shop={cat.ShopId ?? "-"}, collection={cat.CollectionId ?? "-"}, thumbProduct={cat.ThumbnailProductRetailerId ?? "-"}").ConfigureAwait(false);
            }
            if (content.Buttons is { Count: > 0 })
            {
                for (int i = 0; i < content.Buttons.Count; i++)
                {
                    InteractiveButton b = content.Buttons[i];
                    string extra = b.Url is not null
                        ? $" url={b.Url}"
                        : (b.CopyCode is not null
                            ? $" copy_code={b.CopyCode}"
                            : (b.PhoneNumber is not null ? $" tel={b.PhoneNumber}" : (b.NativeFlowName is not null ? $" flow={b.NativeFlowName}" : string.Empty)));
                    string cardInfo = b.CardIndex is uint ci ? $" card={ci}" : string.Empty;
                    await ctx.Out.WriteLineAsync($"    {i + 1}. ({b.Kind}) id={b.Id ?? "-"} text=\"{b.DisplayText}\"{extra}{cardInfo}").ConfigureAwait(false);
                }
            }
            return new CommandResult(0);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
        IReadOnlyList<Jid>? participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, chatJid, ct).ConfigureAwait(false);
        var quotedMsg = new Message { Conversation = string.IsNullOrWhiteSpace(stored?.Text) ? string.Empty : stored.Text };

        if (sub == "location")
        {
            if (pos.Count < 2
                || !double.TryParse(pos[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lat)
                || !double.TryParse(pos[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lng))
            {
                await ctx.Err.WriteLineAsync("Usage: interact location <jid> <msgId> <lat> <lng> [--name ...] [--address ...]").ConfigureAwait(false);
                return new CommandResult(1);
            }
            string? locName = opts.TryGetValue("--name", out var nv) ? nv[^1] : null;
            string? locAddr = opts.TryGetValue("--address", out var av) ? av[^1] : null;
            SendMessageResult locRes = await sender.SendLocationResponseAsync(
                chatJid, lat, lng, locName, locAddr, null, quotedKey, quotedMsg, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Sent location ({lat}, {lng}) to {locRes.ChatJid}: msgId={locRes.MessageId}").ConfigureAwait(false);
            return new CommandResult(0);
        }

        if (sub == "phone")
        {
            if (pos.Count < 1)
            {
                await ctx.Err.WriteLineAsync("Usage: interact phone <jid> <msgId> <phone> [displayName...]").ConfigureAwait(false);
                return new CommandResult(1);
            }
            string phone = pos[0];
            string displayName = pos.Count > 1 ? string.Join(' ', pos.Skip(1)) : (opts.TryGetValue("--name", out var nv) ? nv[^1] : phone);
            SendMessageResult phoneRes = await sender.SendPhoneNumberShareAsync(
                chatJid, displayName, phone, quotedKey, quotedMsg, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Shared phone \"{displayName}\" ({phone}) to {phoneRes.ChatJid}: msgId={phoneRes.MessageId}").ConfigureAwait(false);
            return new CommandResult(0);
        }

        if (sub == "address")
        {
            Dictionary<string, string?> fields = ParseKeyValueFields(opts);
            if (fields.Count == 0)
            {
                await ctx.Err.WriteLineAsync("Usage: interact address <jid> <msgId> --field name=Jane --field city=Mumbai ...").ConfigureAwait(false);
                return new CommandResult(1);
            }
            string? bodyText = opts.TryGetValue("--title", out var btv) ? btv[^1] : null;
            SendMessageResult addrRes = await sender.SendAddressResponseAsync(
                chatJid, fields, bodyText, quotedKey, quotedMsg, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Sent address_message response to {addrRes.ChatJid}: msgId={addrRes.MessageId}").ConfigureAwait(false);
            return new CommandResult(0);
        }

        if (sub == "callperm")
        {
            if (pos.Count < 1)
            {
                await ctx.Err.WriteLineAsync("Usage: interact callperm <jid> <msgId> <accept|reject> [--exp <unixSec>]").ConfigureAwait(false);
                return new CommandResult(1);
            }
            long? expSec = opts.TryGetValue("--exp", out var ev) && long.TryParse(ev[^1], out long exp) ? exp : null;
            string? bodyText = opts.TryGetValue("--title", out var btv) ? btv[^1] : null;
            SendMessageResult cpRes = await sender.SendCallPermissionResponseAsync(
                chatJid, pos[0], expSec, bodyText, quotedKey, quotedMsg, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Sent call_permission_request response ({pos[0]}) to {cpRes.ChatJid}: msgId={cpRes.MessageId}").ConfigureAwait(false);
            return new CommandResult(0);
        }

        if (sub == "flow")
        {
            if (flags.Contains("--fetch"))
            {
                string? flowId = pos.Count > 0 ? pos[0] : content?.Flow?.FlowId;
                if (string.IsNullOrWhiteSpace(flowId))
                {
                    await ctx.Err.WriteLineAsync("Flow ID not found on message; pass <flowId> explicitly.").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                FlowClient flows = orch.ActiveFlows ?? throw new InvalidOperationException("FlowClient not active.");
                GalaxyFlowDataResult flowData = await flows.GetFlowDataAsync(chatJid, flowId, ct).ConfigureAwait(false);
                GalaxyFlowMetadata? meta = flowData.Flows.FirstOrDefault(f => f.FlowId == flowId) ?? flowData.Flows.FirstOrDefault();
                await ctx.Out.WriteLineAsync($"Flow {flowId}: name={meta?.FlowName ?? "-"}, state={meta?.State ?? "-"}, hasEndpointKey={flowData.EndpointPublicKey is not null}").ConfigureAwait(false);
            }

            Dictionary<string, string?> rawFields = ParseKeyValueFields(opts);
            if (rawFields.Count > 0)
            {
                string? flowToken = opts.TryGetValue("--token", out var tv) ? tv[^1] : content?.Flow?.FlowToken;
                if (string.IsNullOrWhiteSpace(flowToken))
                {
                    await ctx.Err.WriteLineAsync("Flow token missing; pass --token <flowToken>.").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                var formValues = rawFields.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
                string? bodyText = opts.TryGetValue("--title", out var btv) ? btv[^1] : null;
                SendMessageResult flowRes = await sender.SendFlowResponseAsync(
                    chatJid, flowToken, formValues, bodyText, quotedKey, quotedMsg, participants, awaitServerAck: true, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Sent galaxy_message flow response to {flowRes.ChatJid}: msgId={flowRes.MessageId}").ConfigureAwait(false);
                return new CommandResult(0);
            }

            if (!flags.Contains("--fetch"))
            {
                await ctx.Err.WriteLineAsync("Usage: interact flow <jid> <msgId> [--fetch] [--field k=v ...] [--token <flowToken>]").ConfigureAwait(false);
                return new CommandResult(1);
            }
            return new CommandResult(0);
        }

        if (pos.Count < 1 && !opts.ContainsKey("--flow"))
        {
            await ctx.Err.WriteLineAsync($"Usage: interact {sub} <jid> <msgId> <idOr1BasedIndex>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        if (sub == "select")
        {
            string selector = pos[0];
            InteractiveRow? matched = ResolveListRow(content, selector);
            string rowId = matched?.Id ?? selector;
            string title = opts.TryGetValue("--title", out var tv) ? tv[^1] : (matched?.Title ?? selector);
            string? desc = opts.TryGetValue("--desc", out var dv) ? dv[^1] : matched?.Description;

            SendMessageResult res = await sender.SendListResponseAsync(
                chatJid,
                rowId,
                title,
                desc,
                quotedKey,
                quotedMsg,
                participants,
                awaitServerAck: true,
                ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Sent list_response \"{title}\" (id={rowId}) to {res.ChatJid}: msgId={res.MessageId}, devices={res.TargetDeviceCount}").ConfigureAwait(false);
            return new CommandResult(0);
        }

        if (sub == "button")
        {
            if (opts.TryGetValue("--flow", out var fv))
            {
                string flowName = fv[^1];
                string flowParams = opts.TryGetValue("--params", out var pv) ? pv[^1] : "{}";
                string? bodyText = pos.Count > 0 ? string.Join(' ', pos) : (opts.TryGetValue("--title", out var btv) ? btv[^1] : null);
                SendMessageResult flowRes = await sender.SendNativeFlowResponseAsync(
                    chatJid,
                    flowName,
                    flowParams,
                    bodyText,
                    quotedKey: quotedKey,
                    quotedMessage: quotedMsg,
                    groupParticipants: participants,
                    awaitServerAck: true,
                    ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Sent native_flow_response '{flowName}' to {flowRes.ChatJid}: msgId={flowRes.MessageId}").ConfigureAwait(false);
                return new CommandResult(0);
            }

            string selector = pos[0];
            InteractiveButton? matchedBtn = ResolveButton(content, selector);
            string btnId = matchedBtn?.Id ?? selector;
            string display = opts.TryGetValue("--title", out var tv) ? tv[^1] : (matchedBtn?.DisplayText ?? selector);
            bool isQuickReply = string.Equals(matchedBtn?.Kind, "quick_reply", StringComparison.OrdinalIgnoreCase)
                || string.Equals(matchedBtn?.NativeFlowName, "quick_reply", StringComparison.OrdinalIgnoreCase);
            bool isTemplate = flags.Contains("--template")
                || string.Equals(content?.Kind, "template", StringComparison.OrdinalIgnoreCase)
                || string.Equals(content?.Kind, "carousel", StringComparison.OrdinalIgnoreCase)
                || isQuickReply;

            if (string.Equals(matchedBtn?.Kind, "url", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.Err.WriteLineAsync($"Button '{display}' is a URL button ({matchedBtn?.Url}); open the URL instead of sending a reply.").ConfigureAwait(false);
                return new CommandResult(1);
            }
            if (string.Equals(matchedBtn?.Kind, "cta_copy", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.Out.WriteLineAsync($"Copy code for '{display}': {matchedBtn?.CopyCode ?? "-"}").ConfigureAwait(false);
                return new CommandResult(0);
            }

            if (!isQuickReply && matchedBtn is { NativeFlowName.Length: > 0 } && !string.Equals(content?.Kind, "buttons", StringComparison.OrdinalIgnoreCase))
            {
                string paramsJson = !string.IsNullOrWhiteSpace(matchedBtn.NativeFlowParamsJson) ? matchedBtn.NativeFlowParamsJson : "{}";
                SendMessageResult nfRes = await sender.SendNativeFlowResponseAsync(
                    chatJid,
                    matchedBtn.NativeFlowName,
                    paramsJson,
                    display,
                    quotedKey: quotedKey,
                    quotedMessage: quotedMsg,
                    groupParticipants: participants,
                    awaitServerAck: true,
                    ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Sent native_flow_response '{matchedBtn.NativeFlowName}' to {nfRes.ChatJid}: msgId={nfRes.MessageId}").ConfigureAwait(false);
                return new CommandResult(0);
            }

            SendMessageResult btnRes = isTemplate
                ? await sender.SendTemplateButtonReplyAsync(
                    chatJid,
                    btnId,
                    display,
                    matchedBtn?.Index ?? 0,
                    matchedBtn?.CardIndex,
                    quotedKey: quotedKey,
                    quotedMessage: quotedMsg,
                    groupParticipants: participants,
                    awaitServerAck: true,
                    ct: ct).ConfigureAwait(false)
                : await sender.SendButtonsResponseAsync(
                    chatJid,
                    btnId,
                    display,
                    quotedKey,
                    quotedMsg,
                    participants,
                    awaitServerAck: true,
                    ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Sent {(isTemplate ? "template_button_reply" : "buttons_response")} \"{display}\" (id={btnId}) to {btnRes.ChatJid}: msgId={btnRes.MessageId}").ConfigureAwait(false);
            return new CommandResult(0);
        }

        await ctx.Err.WriteLineAsync($"Unknown interact subcommand '{sub}'.").ConfigureAwait(false);
        return new CommandResult(1);
    }

    private static InteractiveRow? ResolveListRow(InteractiveMessageContent? content, string selector)
    {
        if (content?.Sections is null) return null;
        InteractiveRow[] all = content.Sections.SelectMany(s => s.Rows).ToArray();
        if (int.TryParse(selector, out int idx1) && idx1 >= 1 && idx1 <= all.Length)
        {
            return all[idx1 - 1];
        }
        return all.FirstOrDefault(r => string.Equals(r.Id, selector, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(r => string.Equals(r.Title, selector, StringComparison.OrdinalIgnoreCase));
    }

    private static InteractiveButton? ResolveButton(InteractiveMessageContent? content, string selector)
    {
        if (content?.Buttons is null) return null;
        if (int.TryParse(selector, out int idx1) && idx1 >= 1 && idx1 <= content.Buttons.Count)
        {
            return content.Buttons[idx1 - 1];
        }
        return content.Buttons.FirstOrDefault(b => string.Equals(b.Id, selector, StringComparison.OrdinalIgnoreCase))
            ?? content.Buttons.FirstOrDefault(b => string.Equals(b.DisplayText, selector, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string?> ParseKeyValueFields(Dictionary<string, List<string>> opts)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!opts.TryGetValue("--field", out var list))
        {
            return result;
        }
        foreach (string item in list)
        {
            int eq = item.IndexOf('=');
            if (eq > 0)
            {
                result[item[..eq].Trim()] = item[(eq + 1)..];
            }
        }
        return result;
    }
}
