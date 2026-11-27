using System.Globalization;
using WhatsApp.Api;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Cli;

internal static class SocialAndBusinessHandlers
{
    public static async Task<CommandResult> HandleCommunityAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: community <list|create <name...>|subgroups <commJid>|link <commJid> <groupJid...>|unlink <commJid> <groupJid...>|join <commJid> <subgroupJid>|deactivate <commJid>>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        CommunityClient comm = orch.ActiveCommunities ?? throw new InvalidOperationException("CommunityClient is not active.");
        string sub = args[1].ToLowerInvariant();

        switch (sub)
        {
            case "list":
            {
                var list = await comm.GetParticipatingCommunityOverviewsAsync(ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Communities ({list.Count}):").ConfigureAwait(false);
                foreach (var c in list)
                {
                    await ctx.Out.WriteLineAsync($"  {c.Subject} ({c.Id}) members={c.Size?.ToString() ?? "-"}").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }
            case "create" when args.Count >= 3:
            {
                string name = string.Join(' ', args.Skip(2));
                var created = await comm.CreateCommunityAsync(new CreateCommunityOptions(name), ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Created community \"{created.Subject}\": {created.Id}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "subgroups" when args.Count >= 3:
            {
                Jid jid = CliCommandHelpers.ParseTargetJid(args[2]);
                var subs = await comm.GetSubgroupsAsync(jid, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Subgroups of {jid} ({subs.Count}):").ConfigureAwait(false);
                foreach (var s in subs)
                {
                    string tag = s.IsDefaultSubGroup ? " [CAG]" : (s.IsGeneralChat ? " [general]" : string.Empty);
                    await ctx.Out.WriteLineAsync($"  {s.Subject} ({s.Id}){tag} members={s.ParticipantCount?.ToString() ?? "-"}").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }
            case "link" when args.Count >= 4:
            {
                Jid parent = CliCommandHelpers.ParseTargetJid(args[2]);
                Jid[] groups = args.Skip(3).Select(CliCommandHelpers.ParseTargetJid).ToArray();
                LinkSubgroupsResult res = await comm.LinkSubgroupsAsync(parent, groups, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Linked {res.LinkedJids.Count} group(s) to {parent}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "unlink" when args.Count >= 4:
            {
                Jid parent = CliCommandHelpers.ParseTargetJid(args[2]);
                Jid[] groups = args.Skip(3).Select(CliCommandHelpers.ParseTargetJid).ToArray();
                LinkSubgroupsResult res = await comm.UnlinkSubgroupsAsync(parent, groups, removeOrphanMembers: true, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Unlinked {res.LinkedJids.Count} group(s) from {parent}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "join" when args.Count >= 4:
            {
                Jid parent = CliCommandHelpers.ParseTargetJid(args[2]);
                Jid subgroup = CliCommandHelpers.ParseTargetJid(args[3]);
                var joined = await comm.JoinSubgroupAsync(parent, subgroup, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Joined subgroup {joined.Subject} ({joined.Id}).").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "deactivate" when args.Count >= 3:
            {
                Jid parent = CliCommandHelpers.ParseTargetJid(args[2]);
                await comm.DeactivateCommunityAsync(parent, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Deactivated community {parent}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            default:
                await ctx.Err.WriteLineAsync("Unknown community subcommand.").ConfigureAwait(false);
                return new CommandResult(1);
        }
    }

    public static async Task<CommandResult> HandleNewsletterAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: newsletter <list|info <jidOrInvite>|directory [query]|recommended|create <name...>|join <jid>|leave <jid>|messages <jid> [--limit N]|publish <jid> <text...>|react <jid> <serverId> <emoji>|edit <jid> <serverId> <newText...>|revoke <jid> <serverId>|pin <jid> <serverId>|unpin <jid> <serverId>|mute <jid> <true|false>|subscribe <jid>>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        NewsletterClient nl = orch.ActiveNewsletters ?? throw new InvalidOperationException("NewsletterClient is not active.");
        string sub = args[1].ToLowerInvariant();

        switch (sub)
        {
            case "list":
            {
                var list = await nl.ListSubscribedAsync(ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Subscribed channels ({list.Count}):").ConfigureAwait(false);
                foreach (var n in list)
                {
                    await ctx.Out.WriteLineAsync($"  {n.Name ?? "-"} ({n.Jid}) subs={n.SubscriberCount} role={n.Role}").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }
            case "info" when args.Count >= 3:
            {
                NewsletterMetadata meta = args[2].Contains('@', StringComparison.Ordinal)
                    ? await nl.GetMetadataAsync(CliCommandHelpers.ParseTargetJid(args[2]), ct).ConfigureAwait(false)
                    : await nl.GetMetadataByInviteAsync(args[2], ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Channel {meta.Name ?? "-"} ({meta.Jid}): subs={meta.SubscriberCount}, state={meta.State}, invite={meta.InviteCode ?? "-"}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "directory":
            {
                var (pos, opts, _) = CliCommandHelpers.ParseOptions(args, 2, new HashSet<string> { "--limit", "--country" }, new HashSet<string>());
                int limit = opts.TryGetValue("--limit", out var lv) && int.TryParse(lv[^1], out int l) ? l : 20;
                string? country = opts.TryGetValue("--country", out var cv) ? cv[^1] : null;
                NewsletterDirectoryPage page = pos.Count > 0
                    ? await nl.SearchDirectoryAsync(string.Join(' ', pos), limit: limit, ct: ct).ConfigureAwait(false)
                    : await nl.GetDirectoryListAsync(countryCodes: country, limit: limit, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Directory ({page.Newsletters.Count}):").ConfigureAwait(false);
                foreach (var n in page.Newsletters)
                {
                    await ctx.Out.WriteLineAsync($"  {n.Name ?? "-"} ({n.Jid}) subs={n.SubscriberCount}").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }
            case "recommended":
            {
                var (_, opts, _) = CliCommandHelpers.ParseOptions(args, 2, new HashSet<string> { "--limit", "--country" }, new HashSet<string>());
                int limit = opts.TryGetValue("--limit", out var lv) && int.TryParse(lv[^1], out int l) ? l : 20;
                string? country = opts.TryGetValue("--country", out var cv) ? cv[^1] : "IN";
                NewsletterDirectoryPage page = await nl.GetRecommendedAsync(countryCodes: country, limit: limit, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Recommended channels ({page.Newsletters.Count}):").ConfigureAwait(false);
                foreach (var n in page.Newsletters)
                {
                    await ctx.Out.WriteLineAsync($"  {n.Name ?? "-"} ({n.Jid}) subs={n.SubscriberCount}").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }
            case "create" when args.Count >= 3:
            {
                var (pos, opts, _) = CliCommandHelpers.ParseOptions(args, 2, new HashSet<string> { "--desc" }, new HashSet<string>());
                string name = string.Join(' ', pos);
                string? desc = opts.TryGetValue("--desc", out var dv) ? dv[^1] : null;
                var created = await nl.CreateAsync(name, desc, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Created channel \"{created.Name}\": {created.Jid}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "join" when args.Count >= 3:
            {
                var joined = await nl.JoinAsync(CliCommandHelpers.ParseTargetJid(args[2]), ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Joined channel {joined.Name} ({joined.Jid}).").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "leave" when args.Count >= 3:
            {
                Jid jid = CliCommandHelpers.ParseTargetJid(args[2]);
                await nl.LeaveAsync(jid, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Left channel {jid}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "messages" when args.Count >= 3:
            {
                var (_, opts, _) = CliCommandHelpers.ParseOptions(args, 3, new HashSet<string> { "--limit" }, new HashSet<string>());
                uint count = opts.TryGetValue("--limit", out var lv) && uint.TryParse(lv[^1], out uint c) ? c : 20;
                Jid jid = CliCommandHelpers.ParseTargetJid(args[2]);
                var msgs = await nl.GetMessagesAsync(jid, count, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Channel messages for {jid} ({msgs.Count}):").ConfigureAwait(false);
                foreach (var m in msgs)
                {
                    string body = m.Message?.Conversation ?? m.Message?.ExtendedTextMessage?.Text ?? $"<{m.MessageTypeRaw}>";
                    await ctx.Out.WriteLineAsync($"  [sid={m.ServerId}] ({m.MessageId}) views={m.ViewsCount}: {CliCommandHelpers.Truncate(body, 80)}").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }
            case "publish" when args.Count >= 4:
            {
                Jid jid = CliCommandHelpers.ParseTargetJid(args[2]);
                SendMessageResult res = await nl.PublishTextAsync(jid, string.Join(' ', args.Skip(3)), ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Published to {res.ChatJid}: msgId={res.MessageId}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "react" when args.Count >= 5 && ulong.TryParse(args[3], out ulong reactSid):
            {
                string id = await nl.SendReactionAsync(CliCommandHelpers.ParseTargetJid(args[2]), reactSid, args[4], ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Sent channel reaction {args[4]} to sid={reactSid}: id={id}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "edit" when args.Count >= 5:
            {
                await nl.EditMessageAsync(CliCommandHelpers.ParseTargetJid(args[2]), args[3], new Whatsapp.Message { Conversation = string.Join(' ', args.Skip(4)) }, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Edited channel msgId={args[3]}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "revoke" when args.Count >= 4:
            {
                await nl.RevokeMessageAsync(CliCommandHelpers.ParseTargetJid(args[2]), args[3], ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Revoked channel msgId={args[3]}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "pin" when args.Count >= 4 && ulong.TryParse(args[3], out ulong pinSid):
                await nl.PinMessagesAsync(CliCommandHelpers.ParseTargetJid(args[2]), [pinSid], ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Pinned channel sid={pinSid}.").ConfigureAwait(false);
                return new CommandResult(0);
            case "unpin" when args.Count >= 4 && ulong.TryParse(args[3], out ulong unpinSid):
                await nl.UnpinMessagesAsync(CliCommandHelpers.ParseTargetJid(args[2]), [unpinSid], ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Unpinned channel sid={unpinSid}.").ConfigureAwait(false);
                return new CommandResult(0);
            case "mute" when args.Count >= 4:
            {
                bool muted = args[3].Equals("true", StringComparison.OrdinalIgnoreCase);
                await nl.SetFollowerMuteAsync(CliCommandHelpers.ParseTargetJid(args[2]), muted, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Set channel mute={muted}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "subscribe" when args.Count >= 3:
            {
                ulong secs = await nl.SubscribeLiveUpdatesAsync(CliCommandHelpers.ParseTargetJid(args[2]), ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Subscribed to live channel updates for {secs}s.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            default:
                await ctx.Err.WriteLineAsync("Unknown newsletter subcommand.").ConfigureAwait(false);
                return new CommandResult(1);
        }
    }

    public static async Task<CommandResult> HandleStatusPostAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 3)
        {
            await ctx.Err.WriteLineAsync("Usage: status-post <text <jid...> --text <msg>|revoke <msgId> <jid...>|react <authorJid> <msgId> <emoji>|group <groupJid> <text...>|mute <userJid> <true|false>>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        StatusClient status = orch.ActiveStatus ?? throw new InvalidOperationException("StatusClient is not active.");
        string sub = args[1].ToLowerInvariant();

        switch (sub)
        {
            case "text":
            {
                var (pos, opts, _) = CliCommandHelpers.ParseOptions(args, 2, new HashSet<string> { "--text", "--bg" }, new HashSet<string>());
                string body = opts.TryGetValue("--text", out var tv) ? tv[^1] : string.Empty;
                if (pos.Count == 0 || string.IsNullOrWhiteSpace(body))
                {
                    await ctx.Err.WriteLineAsync("Usage: status-post text <recipientJid...> --text <message> [--bg AARRGGBB]").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                uint bg = opts.TryGetValue("--bg", out var bv) && uint.TryParse(bv[^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint b)
                    ? b : 0xFF1E6E4F;
                Jid[] recipients = pos.Select(CliCommandHelpers.ParseTargetJid).ToArray();
                SendMessageResult res = await status.SendTextStatusAsync(body, recipients, bg, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Posted status update to {recipients.Length} recipients: msgId={res.MessageId}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "revoke" when args.Count >= 4:
            {
                Jid[] recipients = args.Skip(3).Select(CliCommandHelpers.ParseTargetJid).ToArray();
                SendMessageResult res = await status.RevokeStatusAsync(args[2], recipients, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Revoked status {args[2]}: revokeMsgId={res.MessageId}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "react" when args.Count >= 5:
            {
                Jid author = CliCommandHelpers.ParseTargetJid(args[2]);
                var key = new Whatsapp.MessageKey { RemoteJid = "status@broadcast", Id = args[3], Participant = author.ToNonAdString() };
                SendMessageResult res = await status.SendStatusReactionAsync(key, args[4], ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Sent status reaction {args[4]} to {author}: msgId={res.MessageId}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "group" when args.Count >= 4:
            {
                Jid groupJid = CliCommandHelpers.ParseTargetJid(args[2]);
                IReadOnlyList<Jid> participants = await CliCommandHelpers.ResolveGroupParticipantsIfNeededAsync(ctx, groupJid, ct).ConfigureAwait(false)
                    ?? Array.Empty<Jid>();
                SendMessageResult res = await status.SendGroupStatusV2Async(
                    groupJid, new Whatsapp.Message { Conversation = string.Join(' ', args.Skip(3)) }, participants, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Posted group status to {groupJid}: msgId={res.MessageId}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "mute" when args.Count >= 4:
            {
                SyncdCoordinator syncd = orch.ActiveSyncd ?? throw new InvalidOperationException("SyncdCoordinator required.");
                bool muted = args[3].Equals("true", StringComparison.OrdinalIgnoreCase);
                Jid user = CliCommandHelpers.ParseTargetJid(args[2]);
                await status.SetUserStatusMutedAsync(syncd, user, muted, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Set status mute for {user}: {muted}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            default:
                await ctx.Err.WriteLineAsync("Unknown status-post subcommand.").ConfigureAwait(false);
                return new CommandResult(1);
        }
    }

    public static async Task<CommandResult> HandleBroadcastAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: broadcast <list|send <broadcastJid> <text...>>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        BroadcastClient bc = orch.ActiveBroadcasts ?? throw new InvalidOperationException("BroadcastClient is not active.");
        string sub = args[1].ToLowerInvariant();

        if (sub == "list")
        {
            var lists = await bc.GetBroadcastListsAsync(ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Broadcast lists ({lists.Count}):").ConfigureAwait(false);
            foreach (var b in lists)
            {
                await ctx.Out.WriteLineAsync($"  {b.Name ?? "-"} ({b.ListJid}) recipients={b.RecipientJids.Count}").ConfigureAwait(false);
            }
            return new CommandResult(0);
        }
        if (sub == "send" && args.Count >= 4)
        {
            Jid listJid = CliCommandHelpers.ParseTargetJid(args[2]);
            var lists = await bc.GetBroadcastListsAsync(ct).ConfigureAwait(false);
            IReadOnlyList<Jid> recipients = lists.FirstOrDefault(l => l.ListJid == listJid)?.RecipientJids ?? Array.Empty<Jid>();
            SendMessageResult res = await bc.SendBroadcastTextAsync(listJid, string.Join(' ', args.Skip(3)), recipients, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Sent broadcast to {listJid}: msgId={res.MessageId}").ConfigureAwait(false);
            return new CommandResult(0);
        }

        await ctx.Err.WriteLineAsync("Usage: broadcast <list|send <broadcastJid> <text...>>").ConfigureAwait(false);
        return new CommandResult(1);
    }

    public static async Task<CommandResult> HandleBusinessAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 3)
        {
            await ctx.Err.WriteLineAsync("Usage: business <profile <jid>|catalog <jid> [--limit N] [--token ...]|collections <jid> [--token ...]|order <bizJid> <orderId> <token>|send-order <toJid> <orderId> <token> <sellerJid> <count> [--title ...] [--amount <1000ths>] [--currency <code>]>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        BusinessClient biz = orch.ActiveBusiness ?? throw new InvalidOperationException("BusinessClient is not active.");
        string sub = args[1].ToLowerInvariant();

        switch (sub)
        {
            case "profile":
            {
                BusinessProfile? prof = await biz.GetProfileAsync(CliCommandHelpers.ParseTargetJid(args[2]), ct).ConfigureAwait(false);
                if (prof is null)
                {
                    await ctx.Out.WriteLineAsync("No business profile found.").ConfigureAwait(false);
                    return new CommandResult(0);
                }
                await ctx.Out.WriteLineAsync($"Business {prof.Wid}: desc=\"{CliCommandHelpers.Truncate(prof.Description, 80)}\", email={prof.Email ?? "-"}, addr={prof.Address ?? "-"}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "catalog":
            {
                var (_, opts, _) = CliCommandHelpers.ParseOptions(args, 3, new HashSet<string> { "--limit", "--token" }, new HashSet<string>());
                uint limit = opts.TryGetValue("--limit", out var lv) && uint.TryParse(lv[^1], out uint l) ? l : 10;
                string? token = opts.TryGetValue("--token", out var tv) ? tv[^1] : null;
                BusinessCatalog cat = await biz.GetCatalogAsync(CliCommandHelpers.ParseTargetJid(args[2]), limit: limit, visitorToken: token, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Catalog ({cat.Products.Count} products):").ConfigureAwait(false);
                foreach (BusinessProduct p in cat.Products)
                {
                    string price = p.Price is { } pr ? $"{pr.Currency} {pr.Amount1000 / 1000m:0.##}" : "-";
                    await ctx.Out.WriteLineAsync($"  [{p.Id}] {p.Name ?? "-"} — {price} ({p.Availability ?? "-"})").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }
            case "collections":
            {
                var (_, opts, _) = CliCommandHelpers.ParseOptions(args, 3, new HashSet<string> { "--token" }, new HashSet<string>());
                string? token = opts.TryGetValue("--token", out var tv) ? tv[^1] : null;
                BusinessCollections cols = await biz.GetCollectionsAsync(CliCommandHelpers.ParseTargetJid(args[2]), visitorToken: token, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Collections ({cols.Collections.Count}):").ConfigureAwait(false);
                foreach (var c in cols.Collections)
                {
                    await ctx.Out.WriteLineAsync($"  [{c.Id}] {c.Name ?? "-"} ({c.Products.Count} items)").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }
            case "order" when args.Count >= 5:
            {
                BusinessOrder ord = await biz.GetOrderAsync(CliCommandHelpers.ParseTargetJid(args[2]), args[3], args[4], ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Order {args[3]}: {ord.Products.Count} item(s), currency={ord.PriceDetails?.Currency ?? "-"}, total={ord.PriceDetails?.Total?.Amount1000 / 1000m:0.##}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "send-order" when args.Count >= 7 && int.TryParse(args[6], out int itemCount):
            {
                var (_, opts, _) = CliCommandHelpers.ParseOptions(args, 7, new HashSet<string> { "--title", "--amount", "--currency" }, new HashSet<string>());
                long? amt1000 = opts.TryGetValue("--amount", out var av) && long.TryParse(av[^1], out long a) ? a : null;
                string? currency = opts.TryGetValue("--currency", out var cv) ? cv[^1] : null;
                string? title = opts.TryGetValue("--title", out var tv) ? tv[^1] : null;
                MessageSender sender = orch.ActiveMessageSender ?? throw new InvalidOperationException("MessageSender not active.");
                SendMessageResult res = await sender.SendOrderAsync(
                    CliCommandHelpers.ParseTargetJid(args[2]),
                    args[3],
                    args[4],
                    CliCommandHelpers.ParseTargetJid(args[5]),
                    itemCount,
                    amt1000,
                    currency,
                    title,
                    awaitServerAck: true,
                    ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Sent order {args[3]} to {res.ChatJid}: msgId={res.MessageId}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            default:
                await ctx.Err.WriteLineAsync("Unknown business subcommand.").ConfigureAwait(false);
                return new CommandResult(1);
        }
    }
}
