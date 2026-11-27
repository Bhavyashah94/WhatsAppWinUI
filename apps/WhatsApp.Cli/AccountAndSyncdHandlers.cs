using System.Globalization;
using WhatsApp.Api;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Cli;

internal static class AccountAndSyncdHandlers
{
    public static async Task<CommandResult> HandlePrivacyAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: privacy <get|set <cat> <val>|blacklist <add|remove> <dhash> <jid...>|default-disappearing <sec>>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        PrivacyClient privacy = orch.ActivePrivacy ?? throw new InvalidOperationException("PrivacyClient is not active.");
        string sub = args[1].ToLowerInvariant();

        switch (sub)
        {
            case "get":
            {
                PrivacySettingsSnapshot snap = await privacy.GetPrivacySettingsAsync(ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Privacy settings ({snap.Settings.Count}):").ConfigureAwait(false);
                foreach (PrivacySettingEntry s in snap.Settings)
                {
                    string dhash = string.IsNullOrEmpty(s.Dhash) ? string.Empty : $" dhash={s.Dhash}";
                    await ctx.Out.WriteLineAsync($"  {s.CategoryRaw} = {s.ValueRaw}{dhash}").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }
            case "set" when args.Count >= 4:
            {
                PrivacyCategory cat = ParsePrivacyCategory(args[2]);
                PrivacyValue val = ParsePrivacyValue(args[3]);
                SetPrivacyResult res = await privacy.SetPrivacySettingAsync(cat, val, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Updated privacy {cat}={val} (dhash={res.Dhash ?? "-"})").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "blacklist" when args.Count >= 5:
            {
                DisallowedListAction act = args[2].Equals("remove", StringComparison.OrdinalIgnoreCase)
                    ? DisallowedListAction.Remove
                    : DisallowedListAction.Add;
                string dhash = args[3];
                DisallowedListUserEntry[] users = args.Skip(4)
                    .Select(s => new DisallowedListUserEntry(act, CliCommandHelpers.ParseTargetJid(s)))
                    .ToArray();
                SetPrivacyResult res = await privacy.SetDisallowedListAsync(PrivacyCategory.Last, dhash, users, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Updated disallowed list ({act} {users.Length}): dhash={res.Dhash ?? "-"}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "default-disappearing" when args.Count >= 3 && uint.TryParse(args[2], out uint secs):
            {
                await privacy.SetDefaultDisappearingModeAsync(secs, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Set default disappearing timer to {secs}s.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            default:
                await ctx.Err.WriteLineAsync("Usage: privacy <get|set <cat> <val>|blacklist <add|remove> <dhash> <jid...>|default-disappearing <sec>>").ConfigureAwait(false);
                return new CommandResult(1);
        }
    }

    public static async Task<CommandResult> HandleProfileAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: profile <pushname <name...>|about <text...> [--emoji <e>]|clear-about>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        ProfileClient profile = orch.ActiveProfile ?? throw new InvalidOperationException("ProfileClient is not active.");
        string sub = args[1].ToLowerInvariant();

        switch (sub)
        {
            case "pushname" when args.Count >= 3:
            {
                string name = string.Join(' ', args.Skip(2));
                PushNameOutcome res = await profile.SetPushNameAsync(name, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Updated push name to \"{name}\" (synced={res.Synced}).").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "about" when args.Count >= 3:
            {
                var (pos, opts, _) = CliCommandHelpers.ParseOptions(
                    args, 2, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--emoji" }, new HashSet<string>());
                string? emoji = opts.TryGetValue("--emoji", out var ev) ? ev[^1] : null;
                string? text = pos.Count > 0 ? string.Join(' ', pos) : null;
                await profile.SetTextStatusAsync(text, emoji, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Updated text status: \"{text ?? string.Empty}\" {emoji ?? string.Empty}".Trim()).ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "clear-about":
            {
                await profile.ClearTextStatusAsync(ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync("Cleared text status.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            default:
                await ctx.Err.WriteLineAsync("Usage: profile <pushname <name...>|about <text...> [--emoji <e>]|clear-about>").ConfigureAwait(false);
                return new CommandResult(1);
        }
    }

    public static async Task<CommandResult> HandleBlockAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: block <list|add <jid>|remove <jid>|check <jid>>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        BlockingClient blocking = orch.ActiveBlocking ?? throw new InvalidOperationException("BlockingClient is not active.");
        string sub = args[1].ToLowerInvariant();

        switch (sub)
        {
            case "list":
            {
                IReadOnlyList<BlocklistEntry> list = await blocking.GetBlocklistAsync(ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Blocked contacts ({list.Count}):").ConfigureAwait(false);
                foreach (BlocklistEntry item in list)
                {
                    await ctx.Out.WriteLineAsync($"  {item.Jid}").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }
            case "add" when args.Count >= 3:
            {
                Jid target = CliCommandHelpers.ParseTargetJid(args[2]);
                await blocking.BlockAsync(target, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Blocked {target}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "remove" when args.Count >= 3:
            {
                Jid target = CliCommandHelpers.ParseTargetJid(args[2]);
                await blocking.UnblockAsync(target, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Unblocked {target}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "check" when args.Count >= 3:
            {
                Jid target = CliCommandHelpers.ParseTargetJid(args[2]);
                bool isBlocked = await blocking.IsBlockedAsync(target, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"{target}: blocked={isBlocked}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            default:
                await ctx.Err.WriteLineAsync("Usage: block <list|add <jid>|remove <jid>|check <jid>>").ConfigureAwait(false);
                return new CommandResult(1);
        }
    }

    public static async Task<CommandResult> HandleLabelAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 3)
        {
            await ctx.Err.WriteLineAsync("Usage: label <create <id> <name> [--color N]|delete <id>|chat <add|remove> <labelId> <jid>|msg <add|remove> <labelId> <jid> <msgId>|reorder <id...>>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        LabelsClient labels = orch.ActiveLabels ?? throw new InvalidOperationException("LabelsClient requires synced AppState.");
        string sub = args[1].ToLowerInvariant();

        switch (sub)
        {
            case "create" when args.Count >= 4:
            {
                var (pos, opts, _) = CliCommandHelpers.ParseOptions(
                    args, 2, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--color" }, new HashSet<string>());
                int color = opts.TryGetValue("--color", out var cv) && int.TryParse(cv[^1], out int c) ? c : 0;
                string labelId = pos[0];
                string name = string.Join(' ', pos.Skip(1));
                await labels.CreateOrUpdateLabelAsync(labelId, name, color, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Saved label [{labelId}] \"{name}\" (color={color}).").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "delete" when args.Count >= 3:
                await labels.DeleteLabelAsync(args[2], ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Deleted label [{args[2]}].").ConfigureAwait(false);
                return new CommandResult(0);
            case "chat" when args.Count >= 5:
            {
                bool add = !args[2].Equals("remove", StringComparison.OrdinalIgnoreCase);
                Jid chatJid = CliCommandHelpers.ParseTargetJid(args[4]);
                if (add) await labels.AddChatLabelAsync(args[3], chatJid, ct).ConfigureAwait(false);
                else await labels.RemoveChatLabelAsync(args[3], chatJid, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Updated label [{args[3]}] on chat {chatJid} (add={add}).").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "msg" when args.Count >= 6:
            {
                bool add = !args[2].Equals("remove", StringComparison.OrdinalIgnoreCase);
                Jid chatJid = CliCommandHelpers.ParseTargetJid(args[4]);
                string msgId = args[5];
                if (add) await labels.AddMessageLabelAsync(args[3], chatJid, msgId, ct).ConfigureAwait(false);
                else await labels.RemoveMessageLabelAsync(args[3], chatJid, msgId, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Updated label [{args[3]}] on message {msgId} in {chatJid} (add={add}).").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "reorder" when args.Count >= 3:
            {
                int[] ids = args.Skip(2).Select(int.Parse).ToArray();
                await labels.ReorderLabelsAsync(ids, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Reordered {ids.Length} labels.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            default:
                await ctx.Err.WriteLineAsync("Unknown label subcommand.").ConfigureAwait(false);
                return new CommandResult(1);
        }
    }

    public static async Task<CommandResult> HandleQuickReplyAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 3)
        {
            await ctx.Err.WriteLineAsync("Usage: quickreply <set <id> <shortcut> <message...>|delete <id>>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        QuickRepliesClient qr = orch.ActiveQuickReplies ?? throw new InvalidOperationException("QuickRepliesClient requires synced AppState.");
        string sub = args[1].ToLowerInvariant();

        if (sub == "set" && args.Count >= 5)
        {
            string id = args[2];
            string shortcut = args[3];
            string message = string.Join(' ', args.Skip(4));
            await qr.SetQuickReplyAsync(id, shortcut, message, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Saved quick reply [{id}] /{shortcut}.").ConfigureAwait(false);
            return new CommandResult(0);
        }
        if (sub == "delete")
        {
            await qr.DeleteQuickReplyAsync(args[2], ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Deleted quick reply [{args[2]}].").ConfigureAwait(false);
            return new CommandResult(0);
        }

        await ctx.Err.WriteLineAsync("Usage: quickreply <set <id> <shortcut> <message...>|delete <id>>").ConfigureAwait(false);
        return new CommandResult(1);
    }

    public static async Task<CommandResult> HandleSafetyAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: safety <fingerprint <jid>|remove-device <deviceJid> [--reason ...]|rotate-spk|resync-appstate <collection>|report <jid> <msgId>>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        AccountSafetyClient safety = orch.ActiveAccountSafety ?? throw new InvalidOperationException("AccountSafetyClient is not active.");
        string sub = args[1].ToLowerInvariant();

        switch (sub)
        {
            case "fingerprint" when args.Count >= 3:
            {
                Jid peer = CliCommandHelpers.ParseTargetJid(args[2]);
                SafetyNumberFingerprint? fp = await safety.ComputeSafetyNumberAsync(peer, ct).ConfigureAwait(false);
                if (fp is null)
                {
                    await ctx.Out.WriteLineAsync($"No stored identity key for {peer}.").ConfigureAwait(false);
                }
                else
                {
                    await ctx.Out.WriteLineAsync($"Safety number for {peer}: {fp.DisplayableDigits}").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }
            case "remove-device" when args.Count >= 3:
            {
                var (pos, opts, _) = CliCommandHelpers.ParseOptions(
                    args, 2, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--reason" }, new HashSet<string>());
                Jid dev = CliCommandHelpers.ParseTargetJid(pos[0]);
                string reason = opts.TryGetValue("--reason", out var rv) ? rv[^1] : "user_initiated";
                await safety.RemoveCompanionDeviceAsync(dev, reason, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Removed companion device {dev} (reason={reason}).").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "rotate-spk":
            {
                uint newId = await safety.RotateSignedPreKeyAsync(ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Rotated Signed PreKey to id={newId}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "resync-appstate" when args.Count >= 3:
            {
                SyncdCoordinator syncd = orch.ActiveSyncd ?? throw new InvalidOperationException("SyncdCoordinator required.");
                var mutations = await safety.ResyncAppStateCollectionAsync(syncd, args[2], ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Resynced AppState collection '{args[2]}': {mutations.Count} mutations.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "report" when args.Count >= 4:
            {
                Jid chat = CliCommandHelpers.ParseTargetJid(args[2]);
                string msgId = args[3];
                var (key, rec) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, chat, msgId, ct).ConfigureAwait(false);
                SpamReportResult res = await safety.ReportMessageAsync(new SpamReportRequest(
                    key.Id,
                    rec is not null ? checked((long)rec.Timestamp) : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    FromJid: chat,
                    GroupJid: chat.IsGroup() ? chat : null), ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Submitted spam report for {msgId} in {chat}: reportId={res.ReportId ?? "-"}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            default:
                await ctx.Err.WriteLineAsync("Unknown safety subcommand.").ConfigureAwait(false);
                return new CommandResult(1);
        }
    }

    public static async Task<CommandResult> HandleDraftAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 3)
        {
            await ctx.Err.WriteLineAsync("Usage: draft <set <jid> <text...>|clear <jid>>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
        string sub = args[1].ToLowerInvariant();
        Jid chat = CliCommandHelpers.ParseTargetJid(args[2]);

        if (sub == "clear")
        {
            SendMessageResult res = await sender.SendDraftSyncAsync(
                chat, null, Whatsapp.DraftMessageSync.Types.ClearReason.UserCleared, ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Cleared draft sync for {chat}: msgId={res.MessageId}").ConfigureAwait(false);
            return new CommandResult(0);
        }
        if (sub == "set" && args.Count >= 4)
        {
            string text = string.Join(' ', args.Skip(3));
            SendMessageResult res = await sender.SendDraftSyncAsync(chat, text, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Synced draft for {chat}: msgId={res.MessageId}").ConfigureAwait(false);
            return new CommandResult(0);
        }

        await ctx.Err.WriteLineAsync("Usage: draft <set <jid> <text...>|clear <jid>>").ConfigureAwait(false);
        return new CommandResult(1);
    }

    private static PrivacyCategory ParsePrivacyCategory(string s) => s.ToLowerInvariant() switch
    {
        "last" => PrivacyCategory.Last,
        "online" => PrivacyCategory.Online,
        "profile" => PrivacyCategory.Profile,
        "status" => PrivacyCategory.Status,
        "groupadd" => PrivacyCategory.GroupAdd,
        "readreceipts" => PrivacyCategory.ReadReceipts,
        "calladd" => PrivacyCategory.CallAdd,
        "messages" => PrivacyCategory.Messages,
        "defense" or "defensemode" => PrivacyCategory.DefenseMode,
        _ => Enum.TryParse<PrivacyCategory>(s, true, out var cat) ? cat : PrivacyCategory.Other
    };

    private static PrivacyValue ParsePrivacyValue(string s) => s.ToLowerInvariant() switch
    {
        "all" => PrivacyValue.All,
        "contacts" => PrivacyValue.Contacts,
        "none" => PrivacyValue.None,
        "blacklist" or "contact_blacklist" => PrivacyValue.ContactBlacklist,
        "match_last_seen" => PrivacyValue.MatchLastSeen,
        "known" => PrivacyValue.Known,
        "screen" => PrivacyValue.Screen,
        "off" => PrivacyValue.Off,
        "on" or "on_standard" => PrivacyValue.OnStandard,
        _ => Enum.TryParse<PrivacyValue>(s, true, out var val) ? val : PrivacyValue.Other
    };
}
