using WhatsApp.Api;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Cli;

internal static class GroupAndContactHandlers
{
    public static async Task<CommandResult> HandleUsyncAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: usync <phoneOrJid>...").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        UsyncClient usync = orch.ActiveUsync ?? throw new InvalidOperationException("UsyncClient not active.");

        var phones = new List<string>();
        var jids = new List<Jid>();
        foreach (string raw in args.Skip(1))
        {
            if (Jid.TryParse(raw, out Jid parsed))
            {
                jids.Add(parsed.ToNonAd());
            }
            else
            {
                string digits = new(raw.Where(char.IsDigit).ToArray());
                if (string.IsNullOrEmpty(digits))
                {
                    throw new ArgumentException($"Invalid phone number or JID '{raw}'.");
                }
                phones.Add(digits);
            }
        }

        if (phones.Count > 0)
        {
            IReadOnlyList<UsyncUserResult> phoneRes = await usync.LookupContactsAsync(phones, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Usync phone lookup ({phoneRes.Count}):").ConfigureAwait(false);
            foreach (UsyncUserResult u in phoneRes)
            {
                await ctx.Out.WriteLineAsync(FormatUsyncResult(u)).ConfigureAwait(false);
            }
        }

        if (jids.Count > 0)
        {
            IReadOnlyDictionary<string, UsyncUserResult> info = await usync.GetUserInfoAsync(jids, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Usync user info ({info.Count}):").ConfigureAwait(false);
            foreach (UsyncUserResult u in info.Values)
            {
                await ctx.Out.WriteLineAsync(FormatUsyncResult(u)).ConfigureAwait(false);
            }
        }

        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandleContactsAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count >= 2 && string.Equals(args[1], "search", StringComparison.OrdinalIgnoreCase))
        {
            var (pos, opts, _) = CliCommandHelpers.ParseOptions(
                args,
                startIndex: 2,
                valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--limit" },
                flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            string query = pos.Count > 0 ? string.Join(' ', pos) : string.Empty;
            int limit = opts.TryGetValue("--limit", out var limVals) && int.TryParse(limVals[^1], out int parsed) ? parsed : 50;

            IReadOnlyList<ContactRecord> contacts = await ctx.Runtime.Backend.History
                .SearchContactsAsync(query, limit, ct)
                .ConfigureAwait(false);

            await ctx.Out.WriteLineAsync($"Contacts ({contacts.Count}):").ConfigureAwait(false);
            foreach (ContactRecord c in contacts)
            {
                string label = !string.IsNullOrWhiteSpace(c.Name)
                    ? c.Name
                    : (!string.IsNullOrWhiteSpace(c.Pushname) ? $"~{c.Pushname}" : "<unnamed>");
                string ab = c.IsAddressBookContact ? "[AB]" : "[push]";
                await ctx.Out.WriteLineAsync($"  {label} ({c.Jid}) {ab} phone={c.PhoneNumber ?? "-"}").ConfigureAwait(false);
            }
            return new CommandResult(0);
        }

        if (args.Count >= 4 && string.Equals(args[1], "save", StringComparison.OrdinalIgnoreCase))
        {
            Jid pn = CliCommandHelpers.ParseTargetJid(args[2]);
            string fullName = string.Join(' ', args.Skip(3));
            ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
            ContactClient contacts = orch.ActiveContacts ?? throw new InvalidOperationException("ContactClient requires synced AppState.");
            await contacts.SaveContactAsync(pn, fullName, ct: ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Saved address book contact \"{fullName}\" ({pn}).").ConfigureAwait(false);
            return new CommandResult(0);
        }

        if (args.Count >= 3 && string.Equals(args[1], "delete", StringComparison.OrdinalIgnoreCase))
        {
            Jid pn = CliCommandHelpers.ParseTargetJid(args[2]);
            ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
            ContactClient contacts = orch.ActiveContacts ?? throw new InvalidOperationException("ContactClient requires synced AppState.");
            await contacts.DeleteContactAsync(pn, ct).ConfigureAwait(false);
            await ctx.Out.WriteLineAsync($"Deleted address book contact {pn}.").ConfigureAwait(false);
            return new CommandResult(0);
        }

        await ctx.Err.WriteLineAsync("Usage: contacts <search [query] [--limit N]|save <phone> <fullName...>|delete <phone>>").ConfigureAwait(false);
        return new CommandResult(1);
    }

    public static async Task<CommandResult> HandleAvatarAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var (pos, opts, flags) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: 1,
            valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--out" },
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--full" });

        if (pos.Count < 1)
        {
            await ctx.Err.WriteLineAsync("Usage: avatar <jid> [--full] [--out <file>]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        Jid target = CliCommandHelpers.ParseTargetJid(pos[0]);
        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        ProfilePictureClient pics = orch.ActiveProfilePictures ?? throw new InvalidOperationException("ProfilePictureClient not active.");

        ProfilePictureType picType = flags.Contains("--full") ? ProfilePictureType.Full : ProfilePictureType.Preview;
        ProfilePictureLookupResult lookup = await pics.GetProfilePictureAsync(target, picType, ct: ct).ConfigureAwait(false);
        await ctx.Out.WriteLineAsync($"Avatar {target}: status={lookup.Status}, id={lookup.Picture?.Id ?? "<none>"}, url={lookup.Picture?.Url ?? "<none>"}").ConfigureAwait(false);

        if (opts.TryGetValue("--out", out var outVals) && lookup.Picture is not null)
        {
            byte[]? bytes = await pics.DownloadProfilePictureBytesAsync(lookup.Picture, ct).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                string outPath = Path.GetFullPath(outVals[^1]);
                string? dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(outPath, bytes, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Saved avatar ({bytes.Length:N0} bytes) -> {outPath}").ConfigureAwait(false);
            }
            else
            {
                await ctx.Out.WriteLineAsync("Avatar metadata returned no image bytes.").ConfigureAwait(false);
            }
        }

        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandleGroupAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await PrintGroupUsageAsync(ctx.Err).ConfigureAwait(false);
            return new CommandResult(1);
        }

        string sub = args[1].ToLowerInvariant();
        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        GroupClient groups = orch.ActiveGroups ?? throw new InvalidOperationException("GroupClient not active.");

        switch (sub)
        {
            case "list":
            {
                IReadOnlyList<GroupMetadata> joined = await groups.GetJoinedGroupsAsync(includeParticipants: true, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Joined groups ({joined.Count}):").ConfigureAwait(false);
                foreach (GroupMetadata g in joined.OrderBy(x => x.Subject ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                {
                    string subj = string.IsNullOrWhiteSpace(g.Subject) ? "<no subject>" : g.Subject;
                    await ctx.Out.WriteLineAsync($"  {subj} ({g.Id}) | participants={g.Participants.Count} | announce={g.IsAnnouncement} | locked={g.IsLocked}").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }

            case "info":
            {
                if (args.Count < 3)
                {
                    await ctx.Err.WriteLineAsync("Usage: group info <groupJid>").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                Jid gid = CliCommandHelpers.ParseTargetJid(args[2]);
                GroupMetadata meta = await groups.QueryMetadataAsync(gid, bypassCache: true, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Group '{gid}' not found.");

                await ctx.Out.WriteLineAsync($"Group: {meta.Subject ?? "<none>"} ({meta.Id})").ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Creator: {meta.Creator?.ToString() ?? "?"} (pn={meta.CreatorPn?.ToString() ?? "-"}) | Created: {CliCommandHelpers.FormatTimestamp(meta.CreationTime ?? 0)} | Mode: {meta.AddressingMode}").ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Flags: locked={meta.IsLocked}, announce={meta.IsAnnouncement}, approval={meta.MembershipApproval}, addMode={meta.MemberAddMode?.ToString() ?? "default"}, linkMode={meta.MemberLinkMode?.ToString() ?? "default"}, noFwd={meta.NoFrequentlyForwarded}, reports={meta.AllowAdminReports}, history={meta.HasGroupHistory}, limitShare={meta.IsLimitSharingEnabled}, ephemeral={meta.Ephemeral?.ExpirationSeconds?.ToString() ?? "off"}").ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(meta.Description))
                {
                    await ctx.Out.WriteLineAsync($"Description (id={meta.DescriptionId ?? "-"}): {CliCommandHelpers.Truncate(meta.Description, 200)}").ConfigureAwait(false);
                }
                await ctx.Out.WriteLineAsync($"Participants ({meta.Participants.Count}):").ConfigureAwait(false);
                foreach (GroupParticipantInfo p in meta.Participants.Take(30))
                {
                    await ctx.Out.WriteLineAsync($"  {p.Jid} [{p.Role}] pn={p.PhoneNumber?.ToString() ?? "-"}").ConfigureAwait(false);
                }
                if (meta.Participants.Count > 30)
                {
                    await ctx.Out.WriteLineAsync($"  ... and {meta.Participants.Count - 30} more").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }

            case "create":
            {
                var (pos, opts, _) = CliCommandHelpers.ParseOptions(
                    args,
                    startIndex: 2,
                    valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "--desc", "--add-mode", "--link-mode", "--approval", "--ephemeral"
                    },
                    flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                if (pos.Count < 2)
                {
                    await ctx.Err.WriteLineAsync("Usage: group create <subject> <memberJid>... [--desc <text>] [--add-mode admin|all] [--link-mode admin|all] [--approval on|off] [--ephemeral <sec|off>]").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                string subject = pos[0];
                var members = pos.Skip(1)
                    .Select(s =>
                    {
                        Jid memberJid = CliCommandHelpers.ParseTargetJid(s);
                        Jid? pnJid = memberJid.IsLid()
                            && orch.LidPnCache.TryGetPn(memberJid.ToNonAdString(), out string? pnStr)
                            && !string.IsNullOrEmpty(pnStr)
                            && Jid.TryParse(pnStr, out Jid parsedPn)
                                ? parsedPn
                                : null;
                        return new GroupParticipantOptions(memberJid, pnJid);
                    })
                    .ToArray();
                string? desc = opts.TryGetValue("--desc", out var dv) ? dv[^1] : null;
                MemberAddMode addMode = opts.TryGetValue("--add-mode", out var amv) && amv[^1].Equals("admin", StringComparison.OrdinalIgnoreCase)
                    ? MemberAddMode.AdminAdd
                    : MemberAddMode.AllMemberAdd;
                MemberLinkMode linkMode = opts.TryGetValue("--link-mode", out var lmv) && lmv[^1].Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? MemberLinkMode.AllMemberLink
                    : MemberLinkMode.AdminLink;
                MembershipApprovalMode approvalMode = opts.TryGetValue("--approval", out var apv) && apv[^1].Equals("on", StringComparison.OrdinalIgnoreCase)
                    ? MembershipApprovalMode.On
                    : MembershipApprovalMode.Off;
                uint? ephemeral = opts.TryGetValue("--ephemeral", out var epv)
                    ? (epv[^1].Equals("off", StringComparison.OrdinalIgnoreCase) ? 0u : uint.Parse(epv[^1], System.Globalization.CultureInfo.InvariantCulture))
                    : 0u;

                GroupMetadata created = await groups.CreateGroupAsync(
                    new GroupCreateOptions(
                        subject,
                        members,
                        LinkMode: linkMode,
                        AddMode: addMode,
                        ApprovalMode: approvalMode,
                        EphemeralExpirationSeconds: ephemeral,
                        Description: desc),
                    ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Created group: {created.Subject} ({created.Id}), participants={created.Participants.Count}").ConfigureAwait(false);
                return new CommandResult(0);
            }

            case "pic":
            case "picture":
            {
                if (args.Count < 4)
                {
                    await ctx.Err.WriteLineAsync("Usage: group pic <groupJid> <jpegFile|--clear>").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                Jid gid = CliCommandHelpers.ParseTargetJid(args[2]);
                ProfilePictureClient pics = orch.ActiveProfilePictures ?? throw new InvalidOperationException("ProfilePictureClient not active.");
                bool clear = string.Equals(args[3], "--clear", StringComparison.OrdinalIgnoreCase);
                byte[]? jpegBytes = clear ? null : await File.ReadAllBytesAsync(Path.GetFullPath(args[3]), ct).ConfigureAwait(false);
                string? picId = await pics.SetGroupProfilePictureAsync(gid, jpegBytes, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync(clear
                    ? $"Cleared group picture for {gid}."
                    : $"Updated group picture for {gid}: id={picId ?? "<ok>"}, bytes={jpegBytes!.Length:N0}").ConfigureAwait(false);
                return new CommandResult(0);
            }

            case "settings":
            {
                var (pos, opts, _) = CliCommandHelpers.ParseOptions(
                    args,
                    startIndex: 2,
                    valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "--locked", "--announce", "--approval", "--add-mode", "--link-mode",
                        "--ephemeral", "--no-fwd", "--reports", "--limit-share", "--history"
                    },
                    flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                if (pos.Count < 1 || opts.Count == 0)
                {
                    await ctx.Err.WriteLineAsync(
                        "Usage: group settings <groupJid> [--locked true|false] [--announce true|false] [--approval on|off] " +
                        "[--add-mode admin|all] [--link-mode admin|all] [--ephemeral <sec|off>] [--no-fwd true|false] " +
                        "[--reports true|false] [--limit-share true|false] [--history true|false]").ConfigureAwait(false);
                    return new CommandResult(1);
                }

                Jid gid = CliCommandHelpers.ParseTargetJid(pos[0]);
                var applied = new List<string>();
                if (opts.TryGetValue("--locked", out var lockedVals))
                {
                    bool val = ParseToggle(lockedVals[^1]);
                    await groups.SetLockedAsync(gid, val, ct).ConfigureAwait(false);
                    applied.Add($"locked={val}");
                }
                if (opts.TryGetValue("--announce", out var annVals))
                {
                    bool val = ParseToggle(annVals[^1]);
                    await groups.SetAnnouncementAsync(gid, val, ct).ConfigureAwait(false);
                    applied.Add($"announce={val}");
                }
                if (opts.TryGetValue("--approval", out var appVals))
                {
                    MembershipApprovalMode mode = ParseToggle(appVals[^1]) ? MembershipApprovalMode.On : MembershipApprovalMode.Off;
                    await groups.SetMembershipApprovalAsync(gid, mode, ct).ConfigureAwait(false);
                    applied.Add($"approval={mode}");
                }
                if (opts.TryGetValue("--add-mode", out var addVals))
                {
                    MemberAddMode mode = addVals[^1].Equals("admin", StringComparison.OrdinalIgnoreCase)
                        ? MemberAddMode.AdminAdd : MemberAddMode.AllMemberAdd;
                    await groups.SetMemberAddModeAsync(gid, mode, ct).ConfigureAwait(false);
                    applied.Add($"addMode={mode}");
                }
                if (opts.TryGetValue("--link-mode", out var linkVals))
                {
                    MemberLinkMode mode = linkVals[^1].Equals("admin", StringComparison.OrdinalIgnoreCase)
                        ? MemberLinkMode.AdminLink : MemberLinkMode.AllMemberLink;
                    await groups.SetMemberLinkModeAsync(gid, mode, ct).ConfigureAwait(false);
                    applied.Add($"linkMode={mode}");
                }
                if (opts.TryGetValue("--ephemeral", out var ephVals))
                {
                    uint? exp = ephVals[^1].Equals("off", StringComparison.OrdinalIgnoreCase)
                        ? 0u
                        : uint.Parse(ephVals[^1], System.Globalization.CultureInfo.InvariantCulture);
                    await groups.SetEphemeralAsync(gid, exp, ct: ct).ConfigureAwait(false);
                    applied.Add($"ephemeral={(exp > 0 ? exp.Value.ToString() : "off")}");
                }
                if (opts.TryGetValue("--no-fwd", out var nfVals))
                {
                    bool val = ParseToggle(nfVals[^1]);
                    await groups.SetNoFrequentlyForwardedAsync(gid, val, ct).ConfigureAwait(false);
                    applied.Add($"noFwd={val}");
                }
                if (opts.TryGetValue("--reports", out var repVals))
                {
                    bool val = ParseToggle(repVals[^1]);
                    await groups.SetAllowAdminReportsAsync(gid, val, ct).ConfigureAwait(false);
                    applied.Add($"reports={val}");
                }
                if (opts.TryGetValue("--limit-share", out var lsVals))
                {
                    bool val = ParseToggle(lsVals[^1]);
                    await groups.SetLimitSharingAsync(gid, val, ct).ConfigureAwait(false);
                    applied.Add($"limitShare={val}");
                }
                if (opts.TryGetValue("--history", out var histVals))
                {
                    bool val = ParseToggle(histVals[^1]);
                    await groups.SetGroupHistoryAsync(gid, val, ct).ConfigureAwait(false);
                    applied.Add($"history={val}");
                }

                await ctx.Out.WriteLineAsync($"Updated group settings for {gid}: {string.Join(", ", applied)}").ConfigureAwait(false);
                return new CommandResult(0);
            }

            case "subject":
            {
                if (args.Count < 4)
                {
                    await ctx.Err.WriteLineAsync("Usage: group subject <groupJid> <subject...>").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                Jid gid = CliCommandHelpers.ParseTargetJid(args[2]);
                string subject = string.Join(' ', args.Skip(3));
                await groups.SetSubjectAsync(gid, subject, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Updated group subject for {gid} -> \"{subject}\".").ConfigureAwait(false);
                return new CommandResult(0);
            }

            case "desc":
            {
                if (args.Count < 3)
                {
                    await ctx.Err.WriteLineAsync("Usage: group desc <groupJid> [description...]").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                Jid gid = CliCommandHelpers.ParseTargetJid(args[2]);
                string? desc = args.Count > 3 ? string.Join(' ', args.Skip(3)) : null;
                await groups.SetDescriptionAsync(gid, desc, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Updated group description for {gid}.").ConfigureAwait(false);
                return new CommandResult(0);
            }

            case "invite":
            {
                if (args.Count < 3)
                {
                    await ctx.Err.WriteLineAsync("Usage: group invite <groupJid> [--reset]").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                Jid gid = CliCommandHelpers.ParseTargetJid(args[2]);
                bool reset = args.Skip(3).Any(a => string.Equals(a, "--reset", StringComparison.OrdinalIgnoreCase));
                string link = await groups.GetInviteLinkAsync(gid, reset, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Invite link for {gid}: {link}").ConfigureAwait(false);
                return new CommandResult(0);
            }

            case "leave":
            {
                if (args.Count < 3)
                {
                    await ctx.Err.WriteLineAsync("Usage: group leave <groupJid>").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                Jid gid = CliCommandHelpers.ParseTargetJid(args[2]);
                await groups.LeaveAsync(gid, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Left group {gid}.").ConfigureAwait(false);
                return new CommandResult(0);
            }

            case "tag":
            case "label":
            {
                if (args.Count < 3)
                {
                    await ctx.Err.WriteLineAsync("Usage: group tag <groupJid> [label...]").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                Jid gid = CliCommandHelpers.ParseTargetJid(args[2]);
                string label = args.Count > 3 ? string.Join(' ', args.Skip(3)) : string.Empty;
                SendMessageResult res = await groups.UpdateMemberLabelAsync(gid, label, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Updated member tag for {gid} -> \"{label}\" (msgId={res.MessageId}, devices={res.TargetDeviceCount}).").ConfigureAwait(false);
                return new CommandResult(0);
            }

            case "add":
            case "remove":
            case "promote":
            case "demote":
            {
                if (args.Count < 4)
                {
                    await ctx.Err.WriteLineAsync($"Usage: group {sub} <groupJid> <memberJid>...").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                Jid gid = CliCommandHelpers.ParseTargetJid(args[2]);
                Jid[] members = args.Skip(3).Select(CliCommandHelpers.ParseTargetJid).ToArray();
                IReadOnlyList<ParticipantChangeResult> results = sub switch
                {
                    "add" => await groups.AddParticipantsAsync(gid, members, ct).ConfigureAwait(false),
                    "remove" => await groups.RemoveParticipantsAsync(gid, members, ct: ct).ConfigureAwait(false),
                    "promote" => await groups.PromoteParticipantsAsync(gid, members, ct).ConfigureAwait(false),
                    _ => await groups.DemoteParticipantsAsync(gid, members, ct).ConfigureAwait(false),
                };
                foreach (ParticipantChangeResult r in results)
                {
                    await ctx.Out.WriteLineAsync($"  {r.Jid}: status={r.Status ?? "ok"}").ConfigureAwait(false);
                }
                return new CommandResult(0);
            }

            default:
                await PrintGroupUsageAsync(ctx.Err).ConfigureAwait(false);
                return new CommandResult(1);
        }
    }

    private static string FormatUsyncResult(UsyncUserResult u) =>
        $"  jid={u.Jid?.ToString() ?? "-"}, lid={u.Lid?.ToString() ?? "-"}, reg={u.IsRegistered}, biz={u.IsBusiness}, pic={u.PictureId ?? "-"}, status=\"{CliCommandHelpers.Truncate(u.StatusText, 40)}\"";

    public static async Task<CommandResult> HandleSyncdAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        SyncdCoordinator syncd = orch.ActiveSyncd ?? throw new InvalidOperationException("SyncdCoordinator is not active.");
        string[] collections = args.Count > 1
            ? args.Skip(1).ToArray()
            : [
                AppStateCollections.CriticalBlock,
                AppStateCollections.CriticalUnblockLow,
                AppStateCollections.RegularHigh,
                AppStateCollections.Regular,
                AppStateCollections.RegularLow
            ];

        IReadOnlyDictionary<string, IReadOnlyList<WhatsApp.Protocol.AppState.DecodedAppStateMutation>> result =
            await syncd.SyncCollectionsAsync(collections, validateMacs: true, ct).ConfigureAwait(false);

        foreach (var kv in result.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            await ctx.Out.WriteLineAsync($"  {kv.Key}: {kv.Value.Count} mutations applied").ConfigureAwait(false);
        }
        return new CommandResult(0);
    }

    public static async Task<CommandResult> HandleChatActionAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var (pos, _, flags) = CliCommandHelpers.ParseOptions(
            args,
            startIndex: 1,
            valueOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            flagOptions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--delete-media", "--delete-starred" });

        if (pos.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: chat <delete|clear|pin|unpin|archive|unarchive|mute|unmute|read|unread|lock|unlock> <jid> [--delete-media] [--delete-starred]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        string action = pos[0].ToLowerInvariant();
        Jid chatJid = CliCommandHelpers.ParseTargetJid(pos[1]);
        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        ChatActionsClient chatActions = orch.ActiveChatActions ?? throw new InvalidOperationException("ChatActionsClient is not active.");
        bool deleteMedia = flags.Contains("--delete-media");
        bool deleteStarred = flags.Contains("--delete-starred");

        switch (action)
        {
            case "delete":
            {
                Whatsapp.SyncActionValue.Types.SyncActionMessageRange range = await BuildMessageRangeAsync(ctx, chatJid, ct).ConfigureAwait(false);
                await chatActions.DeleteChatAsync(chatJid, deleteMedia, range, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Sent deleteChat AppState mutation for {chatJid} (deleteMedia={deleteMedia}, lastMsgTs={range.LastMessageTimestamp}, lastSysTs={range.LastSystemMessageTimestamp}).").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "clear":
            {
                Whatsapp.SyncActionValue.Types.SyncActionMessageRange range = await BuildMessageRangeAsync(ctx, chatJid, ct).ConfigureAwait(false);
                await chatActions.ClearChatAsync(chatJid, new ClearChatOptions(deleteStarred, deleteMedia), range, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Sent clearChat AppState mutation for {chatJid}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "pin":
                await chatActions.PinChatAsync(chatJid, ct).ConfigureAwait(false);
                break;
            case "unpin":
                await chatActions.UnpinChatAsync(chatJid, ct).ConfigureAwait(false);
                break;
            case "archive":
                await chatActions.ArchiveChatAsync(chatJid, await BuildMessageRangeAsync(ctx, chatJid, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                break;
            case "unarchive":
                await chatActions.UnarchiveChatAsync(chatJid, await BuildMessageRangeAsync(ctx, chatJid, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                break;
            case "mute":
                await chatActions.MuteChatAsync(chatJid, ct).ConfigureAwait(false);
                break;
            case "unmute":
                await chatActions.UnmuteChatAsync(chatJid, ct).ConfigureAwait(false);
                break;
            case "read":
                await chatActions.MarkChatAsReadAsync(chatJid, true, await BuildMessageRangeAsync(ctx, chatJid, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                break;
            case "unread":
                await chatActions.MarkChatAsReadAsync(chatJid, false, await BuildMessageRangeAsync(ctx, chatJid, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                break;
            case "lock":
                await chatActions.LockChatAsync(chatJid, true, ct).ConfigureAwait(false);
                break;
            case "unlock":
                await chatActions.LockChatAsync(chatJid, false, ct).ConfigureAwait(false);
                break;
            case "star" when pos.Count >= 3:
            case "unstar" when pos.Count >= 3:
            {
                var (key, rec) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, chatJid, pos[2], ct).ConfigureAwait(false);
                Jid? participant = chatJid.IsGroup() && Jid.TryParse(key.Participant, out Jid pj) ? pj : null;
                await chatActions.StarMessageAsync(
                    chatJid,
                    new ChatMessageRef(key.Id, key.FromMe, participant, rec is not null ? checked((long)rec.Timestamp) : 0),
                    action == "star",
                    ct).ConfigureAwait(false);
                break;
            }
            case "delete-msg" when pos.Count >= 3:
            {
                var (key, rec) = await CliCommandHelpers.ResolveMessageKeyAsync(ctx, chatJid, pos[2], ct).ConfigureAwait(false);
                Jid? participant = chatJid.IsGroup() && Jid.TryParse(key.Participant, out Jid pj) ? pj : null;
                await chatActions.DeleteMessageForMeAsync(
                    chatJid,
                    new ChatMessageRef(key.Id, key.FromMe, participant, rec is not null ? checked((long)rec.Timestamp) : 0),
                    deleteMedia,
                    ct).ConfigureAwait(false);
                break;
            }
            case "disappearing" when pos.Count >= 3 && uint.TryParse(pos[2], out uint ephemSecs):
                await chatActions.SetDirectChatDisappearingAsync(chatJid, ephemSecs, ct).ConfigureAwait(false);
                break;
            case "favorites":
            {
                Jid[] favJids = pos.Skip(1).Select(CliCommandHelpers.ParseTargetJid).ToArray();
                await chatActions.SetFavoritesAsync(favJids, ct).ConfigureAwait(false);
                break;
            }
            default:
                await ctx.Err.WriteLineAsync($"Unknown chat action '{action}'.").ConfigureAwait(false);
                return new CommandResult(1);
        }

        await ctx.Out.WriteLineAsync($"Sent chat '{action}' AppState mutation for {chatJid}.").ConfigureAwait(false);
        return new CommandResult(0);
    }

    private static async Task<Whatsapp.SyncActionValue.Types.SyncActionMessageRange> BuildMessageRangeAsync(
        CliCommandContext ctx,
        Jid chatJid,
        CancellationToken ct)
    {
        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        IReadOnlyList<MessageRecord> msgs = await ctx.Runtime.Backend.History
            .GetMessagesAsync(chatJid.ToNonAdString(), beforeTimestamp: null, limit: 5, ct: ct)
            .ConfigureAwait(false);

        long lastMsgTs = msgs.Count > 0 ? msgs.Max(static m => checked((long)m.Timestamp)) : nowSec;
        var range = new Whatsapp.SyncActionValue.Types.SyncActionMessageRange
        {
            LastMessageTimestamp = lastMsgTs,
            LastSystemMessageTimestamp = Math.Max(lastMsgTs, nowSec)
        };

        foreach (MessageRecord m in msgs)
        {
            var key = new Whatsapp.MessageKey
            {
                RemoteJid = chatJid.ToNonAdString(),
                Id = m.Id,
                FromMe = m.FromMe
            };
            if (!m.FromMe && chatJid.IsGroup() && !string.IsNullOrWhiteSpace(m.SenderJid))
            {
                key.Participant = m.SenderJid;
            }
            range.Messages.Add(new Whatsapp.SyncActionValue.Types.SyncActionMessage
            {
                Key = key,
                Timestamp = checked((long)m.Timestamp)
            });
        }

        return range;
    }

    private static bool ParseToggle(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("on", StringComparison.OrdinalIgnoreCase)
        || value.Equals("1", StringComparison.Ordinal);

    private static Task PrintGroupUsageAsync(TextWriter err) =>
        err.WriteLineAsync("Usage: group <list|info|create|pic|settings|subject|desc|tag|invite|leave|add|remove|promote|demote> ...");
}
