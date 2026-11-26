using System.Collections.Concurrent;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Api;

public sealed record SendDirectGroupResult(
    string MessageId,
    bool AckReceived,
    int EncryptedDeviceCount,
    int AddressedDeviceCount,
    string? AckErrorCode = null,
    string? AckErrorText = null);

/// <summary>
/// High-level <c>w:g2</c> and <c>w:mex</c> group management client with per-group metadata caching,
/// LID/PN participant resolution, <c>phash</c> skip optimization, live <c>w:gp2</c> notification
/// patching, and <c>UpdateMemberLabel</c> support.
/// </summary>
public sealed class GroupClient
{
    private readonly IqClient _iqClient;
    private readonly MexClient _mexClient;
    private readonly MessageSender _messageSender;
    private readonly IWhatsAppBackend _backend;
    private readonly ILidPnCache _lidPnCache;
    private readonly IGroupRegistry? _groupRegistry;
    private readonly Func<WhatsAppEvent, ValueTask>? _emitEvent;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, GroupMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);

    public GroupClient(
        IqClient iqClient,
        MexClient mexClient,
        MessageSender messageSender,
        IWhatsAppBackend backend,
        ILidPnCache lidPnCache,
        IGroupRegistry? groupRegistry = null,
        Func<WhatsAppEvent, ValueTask>? emitEvent = null,
        TimeProvider? timeProvider = null)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _mexClient = mexClient ?? throw new ArgumentNullException(nameof(mexClient));
        _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _groupRegistry = groupRegistry;
        _emitEvent = emitEvent;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryGetCached(Jid groupJid, out GroupMetadata? metadata) =>
        _cache.TryGetValue(groupJid.ToNonAdString(), out metadata);

    public void InvalidateCache(Jid groupJid) =>
        _cache.TryRemove(groupJid.ToNonAdString(), out _);

    public async Task<GroupMetadata> CreateGroupAsync(GroupCreateOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string iqId = _iqClient.NextRequestId();
        Node iq = GroupManagementIq.BuildCreateGroup(iqId, options);
        Node resp = await SendRequiredIqAsync(iq, ct).ConfigureAwait(false);
        GroupMetadata created = GroupManagementIq.ParseCreateGroupResponse(resp, options);
        await CacheAndLearnAsync(created, "create", ct).ConfigureAwait(false);
        return created;
    }

    public async Task<GroupMetadata?> QueryMetadataAsync(Jid groupJid, bool bypassCache = false, CancellationToken ct = default)
    {
        string key = groupJid.ToNonAdString();
        string? phash = null;
        if (!bypassCache && _cache.TryGetValue(key, out GroupMetadata? cached))
        {
            phash = ComputeGroupParticipantsPhash(cached);
        }

        string iqId = _iqClient.NextRequestId();
        Node iq = GroupManagementIq.BuildQueryGroup(iqId, groupJid, phash);
        Node resp = await SendRequiredIqAsync(iq, ct).ConfigureAwait(false);
        GroupQueryResult result = GroupManagementIq.ParseQueryGroupResponse(resp);

        if (!result.IsModified && _cache.TryGetValue(key, out GroupMetadata? unchanged))
        {
            return unchanged;
        }

        if (result.Metadata is { } full)
        {
            await CacheAndLearnAsync(full, "query", ct).ConfigureAwait(false);
            return full;
        }

        return null;
    }

    public async Task<IReadOnlyList<GroupMetadata>> GetJoinedGroupsAsync(bool includeParticipants = true, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = GroupManagementIq.BuildParticipating(iqId, includeParticipants, includeDescription: true);
        Node resp = await SendRequiredIqAsync(iq, ct).ConfigureAwait(false);
        IReadOnlyList<GroupMetadata> groups = GroupManagementIq.ParseParticipatingResponse(resp);
        foreach (GroupMetadata g in groups)
        {
            await CacheAndLearnAsync(g, "participating", ct).ConfigureAwait(false);
        }
        return groups;
    }

    public async Task<IReadOnlyList<GroupOverview>> GetJoinedGroupOverviewsAsync(CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = GroupManagementIq.BuildParticipating(iqId, includeParticipants: false, includeDescription: false);
        Node resp = await SendRequiredIqAsync(iq, ct).ConfigureAwait(false);
        return GroupManagementIq.ParseParticipatingOverviewResponse(resp);
    }

    public async Task<IReadOnlyList<BatchGroupMetadataEntry>> FetchMetadataBatchAsync(IReadOnlyList<Jid> groupJids, CancellationToken ct = default)
    {
        if (groupJids.Count == 0)
        {
            return Array.Empty<BatchGroupMetadataEntry>();
        }
        string iqId = _iqClient.NextRequestId();
        Node iq = GroupManagementIq.BuildBatchGetGroupInfo(iqId, groupJids);
        Node resp = await SendRequiredIqAsync(iq, ct).ConfigureAwait(false);
        IReadOnlyList<BatchGroupMetadataEntry> entries = GroupManagementIq.ParseBatchGetGroupInfoResponse(resp);
        foreach (BatchGroupMetadataEntry entry in entries)
        {
            if (entry.Status == BatchGroupLookupStatus.Found && entry.Metadata is { } meta)
            {
                await CacheAndLearnAsync(meta, "batch_query", ct).ConfigureAwait(false);
            }
        }
        return entries;
    }

    public async Task LeaveAsync(Jid groupJid, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        await SendRequiredIqAsync(GroupManagementIq.BuildLeaveGroup(iqId, groupJid), ct).ConfigureAwait(false);
        InvalidateCache(groupJid);
    }

    public async Task SetSubjectAsync(Jid groupJid, string subject, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        await SendRequiredIqAsync(GroupManagementIq.BuildSetSubject(iqId, groupJid, subject), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with { Subject = subject });
    }

    public async Task SetDescriptionAsync(Jid groupJid, string? description, string? previousDescriptionId = null, CancellationToken ct = default)
    {
        string? prevId = previousDescriptionId;
        if (prevId is null && _cache.TryGetValue(groupJid.ToNonAdString(), out GroupMetadata? cached))
        {
            prevId = cached.DescriptionId;
        }
        string iqId = _iqClient.NextRequestId();
        await SendRequiredIqAsync(GroupManagementIq.BuildSetDescription(iqId, groupJid, description, prevId), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with { Description = description });
    }

    public async Task SetLockedAsync(Jid groupJid, bool locked, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        await SendRequiredIqAsync(GroupManagementIq.BuildSetLocked(iqId, groupJid, locked), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with { IsLocked = locked });
    }

    public async Task SetAnnouncementAsync(Jid groupJid, bool announce, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        await SendRequiredIqAsync(GroupManagementIq.BuildSetAnnouncement(iqId, groupJid, announce), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with { IsAnnouncement = announce });
    }

    public async Task SetEphemeralAsync(Jid groupJid, uint? expirationSeconds, uint? trigger = null, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        await SendRequiredIqAsync(GroupManagementIq.BuildSetEphemeral(iqId, groupJid, expirationSeconds, trigger), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with
        {
            Ephemeral = expirationSeconds is > 0 ? new GroupEphemeralSettings(expirationSeconds, trigger) : null
        });
    }

    public async Task SetMembershipApprovalAsync(Jid groupJid, MembershipApprovalMode mode, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        await SendRequiredIqAsync(GroupManagementIq.BuildSetMembershipApproval(iqId, groupJid, mode), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with { MembershipApproval = mode == MembershipApprovalMode.On });
    }

    public async Task SetMemberAddModeAsync(Jid groupJid, MemberAddMode mode, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        await SendRequiredIqAsync(GroupManagementIq.BuildSetMemberAddMode(iqId, groupJid, mode), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with { MemberAddMode = mode });
    }

    public async Task SetNoFrequentlyForwardedAsync(Jid groupJid, bool enabled, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        await SendRequiredIqAsync(GroupManagementIq.BuildSetNoFrequentlyForwarded(iqId, groupJid, enabled), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with { NoFrequentlyForwarded = enabled });
    }

    public async Task SetAllowAdminReportsAsync(Jid groupJid, bool allow, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        await SendRequiredIqAsync(GroupManagementIq.BuildSetAllowAdminReports(iqId, groupJid, allow), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with { AllowAdminReports = allow });
    }

    public async Task SetGroupHistoryAsync(Jid groupJid, bool enabled, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        await SendRequiredIqAsync(GroupManagementIq.BuildSetGroupHistory(iqId, groupJid, enabled), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with { HasGroupHistory = enabled });
    }

    public async Task SetMemberLinkModeAsync(Jid groupJid, MemberLinkMode mode, CancellationToken ct = default)
    {
        await ExecuteGroupPropertyMexAsync(MexStanzas.BuildUpdateMemberLinkModeVariables(groupJid, mode), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with { MemberLinkMode = mode });
    }

    public async Task SetMemberShareHistoryModeAsync(Jid groupJid, MemberShareHistoryMode mode, CancellationToken ct = default)
    {
        await ExecuteGroupPropertyMexAsync(MexStanzas.BuildUpdateMemberShareHistoryModeVariables(groupJid, mode), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with { ShareHistoryMode = mode });
    }

    public async Task SetLimitSharingAsync(Jid groupJid, bool enabled, CancellationToken ct = default)
    {
        await ExecuteGroupPropertyMexAsync(MexStanzas.BuildUpdateLimitSharingVariables(groupJid, enabled), ct).ConfigureAwait(false);
        UpdateCached(groupJid, g => g with { IsLimitSharingEnabled = enabled });
    }

    public async Task<IReadOnlyList<ParticipantChangeResult>> AddParticipantsAsync(
        Jid groupJid,
        IReadOnlyList<Jid> participants,
        CancellationToken ct = default)
    {
        var opts = new List<GroupParticipantOptions>(participants.Count);
        foreach (Jid j in participants)
        {
            byte[]? token = await _backend.Protocol.GetTcTokenAsync(j.ToNonAdString(), ct).ConfigureAwait(false);
            opts.Add(new GroupParticipantOptions(j, PrivacyToken: token));
        }
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildAddParticipants(iqId, groupJid, opts), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseParticipantActionResponse(resp, "add");
    }

    public async Task<IReadOnlyList<ParticipantChangeResult>> RemoveParticipantsAsync(
        Jid groupJid,
        IReadOnlyList<Jid> participants,
        bool includeLinkedGroups = false,
        CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildRemoveParticipants(iqId, groupJid, participants, includeLinkedGroups), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseParticipantActionResponse(resp, "remove");
    }

    public async Task<IReadOnlyList<ParticipantChangeResult>> PromoteParticipantsAsync(Jid groupJid, IReadOnlyList<Jid> participants, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildPromoteParticipants(iqId, groupJid, participants), ct).ConfigureAwait(false);
        return resp.GetChild("promote") is not null
            ? GroupManagementIq.ParseParticipantActionResponse(resp, "promote")
            : Array.Empty<ParticipantChangeResult>();
    }

    public async Task<IReadOnlyList<ParticipantChangeResult>> DemoteParticipantsAsync(Jid groupJid, IReadOnlyList<Jid> participants, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildDemoteParticipants(iqId, groupJid, participants), ct).ConfigureAwait(false);
        return resp.GetChild("demote") is not null
            ? GroupManagementIq.ParseParticipantActionResponse(resp, "demote")
            : Array.Empty<ParticipantChangeResult>();
    }

    public async Task<string> GetInviteLinkAsync(Jid groupJid, bool reset = false, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildGetInviteLink(iqId, groupJid, reset), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseInviteLinkResponse(resp);
    }

    public async Task<GroupMetadata> GetInviteInfoAsync(string inviteCode, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildGetInviteInfo(iqId, inviteCode), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseCreateGroupResponse(resp);
    }

    public async Task<JoinGroupOutcome> JoinViaInviteAsync(string inviteCode, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildAcceptInvite(iqId, inviteCode), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseJoinGroupResponse(resp);
    }

    public async Task<JoinGroupOutcome> AcceptInviteV4Async(Jid groupJid, string code, long expiration, Jid adminJid, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildAcceptInviteV4(iqId, groupJid, code, expiration, adminJid), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseJoinGroupResponse(resp, groupJid.ToNonAd());
    }

    public async Task<IReadOnlyList<MembershipRequest>> GetMembershipRequestsAsync(Jid groupJid, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildGetMembershipRequests(iqId, groupJid), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseMembershipRequestsResponse(resp);
    }

    public async Task<IReadOnlyList<ParticipantChangeResult>> ApproveMembershipRequestsAsync(Jid groupJid, IReadOnlyList<Jid> participants, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildMembershipRequestAction(iqId, groupJid, approve: true, participants), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseMembershipRequestActionResponse(resp, approve: true);
    }

    public async Task<IReadOnlyList<ParticipantChangeResult>> RejectMembershipRequestsAsync(Jid groupJid, IReadOnlyList<Jid> participants, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildMembershipRequestAction(iqId, groupJid, approve: false, participants), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseMembershipRequestActionResponse(resp, approve: false);
    }

    public async Task<IReadOnlyList<ParticipantChangeResult>> CancelMembershipRequestsAsync(Jid groupJid, IReadOnlyList<Jid> participants, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildCancelMembershipRequests(iqId, groupJid, participants), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseParticipantActionResponse(resp, "cancel_membership_requests");
    }

    public async Task<IReadOnlyList<ParticipantChangeResult>> RevokeRequestCodeAsync(Jid groupJid, IReadOnlyList<Jid> participants, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildRevokeRequestCode(iqId, groupJid, participants), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseParticipantActionResponse(resp, "revoke");
    }

    public Task AcknowledgeGroupAsync(Jid groupJid, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        return SendRequiredIqAsync(GroupManagementIq.BuildAcknowledgeGroup(iqId, groupJid), ct);
    }

    public Task ReportMessagesAsync(Jid groupJid, IReadOnlyList<string> messageIds, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        return SendRequiredIqAsync(GroupManagementIq.BuildReportMessages(iqId, groupJid, messageIds), ct);
    }

    public async Task<ReportedGroupMessagesResult> GetReportedMessagesAsync(Jid groupJid, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildGetReportedMessages(iqId, groupJid), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseGetReportedMessagesResponse(resp);
    }

    public async Task<IReadOnlyList<GroupProfilePictureEntry>> GetGroupProfilePicturesAsync(
        IReadOnlyList<(Jid GroupJid, GroupPictureType PictureType)> groups,
        CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await SendRequiredIqAsync(GroupManagementIq.BuildGetGroupProfilePictures(iqId, groups), ct).ConfigureAwait(false);
        return GroupManagementIq.ParseGetGroupProfilePicturesResponse(resp);
    }

    public async Task<SendMessageResult> UpdateMemberLabelAsync(
        Jid groupJid,
        string label,
        IReadOnlyList<Jid>? groupParticipants = null,
        CancellationToken ct = default)
    {
        if (!groupJid.IsGroup())
        {
            throw new ArgumentException($"UpdateMemberLabelAsync requires a @g.us JID, got '{groupJid}'.", nameof(groupJid));
        }

        IReadOnlyList<Jid>? participants = groupParticipants;
        if (participants is null)
        {
            GroupMetadata? meta = TryGetCached(groupJid, out GroupMetadata? cached) && cached is not null
                ? cached
                : await QueryMetadataAsync(groupJid, bypassCache: false, ct).ConfigureAwait(false);
            participants = meta?.Participants.Select(p => p.Jid).ToArray()
                ?? throw new InvalidOperationException($"Unable to resolve participants for {groupJid}.");
        }

        long nowSecs = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        Message msg = RichMessageBuilders.BuildMemberLabelMessage(label, nowSecs);
        return await _messageSender.SendGroupMessageAsync(groupJid, msg, participants, ct: ct).ConfigureAwait(false);
    }

    public async Task ApplyGroupNotificationAsync(ParsedGroupNotification parsed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.IsDirtyNotification)
        {
            foreach (Jid dirtyGroup in parsed.DirtyGroupJids)
            {
                InvalidateCache(dirtyGroup);
            }
            return;
        }

        string key = parsed.From.ToNonAdString();
        if (parsed.Actions is null || parsed.Actions.Count == 0)
        {
            return;
        }

        bool changed = false;
        foreach (ParsedGroupAction act in parsed.Actions)
        {
            switch (act.Action)
            {
                case "modify" or "delete":
                    InvalidateCache(parsed.From);
                    changed = true;
                    break;

                case "add":
                    UpdateCached(parsed.From, g =>
                    {
                        var list = g.Participants.ToList();
                        for (int i = 0; i < act.ParticipantJids.Count; i++)
                        {
                            Jid pj = act.ParticipantJids[i];
                            Jid? pn = i < act.ParticipantPnJids.Count ? act.ParticipantPnJids[i] : null;
                            if (!list.Any(existing => string.Equals(existing.Jid.ToNonAdString(), pj.ToNonAdString(), StringComparison.OrdinalIgnoreCase)))
                            {
                                list.Add(new GroupParticipantInfo(pj, PhoneNumber: pn));
                            }
                        }
                        return g with { Participants = list, Size = (uint)list.Count };
                    });
                    changed = true;
                    break;

                case "remove":
                    var removedUsers = act.ParticipantJids.Select(p => p.User).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    UpdateCached(parsed.From, g =>
                    {
                        var remaining = g.Participants.Where(p => !removedUsers.Contains(p.Jid.User)).ToArray();
                        return g with { Participants = remaining, Size = (uint)remaining.Length };
                    });
                    changed = true;
                    break;

                case "promote" or "demote":
                    var roleUsers = act.ParticipantJids.Select(p => p.ToNonAdString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    ParticipantType nextRole = act.Action == "promote" ? ParticipantType.Admin : ParticipantType.Member;
                    UpdateCached(parsed.From, g => g with
                    {
                        Participants = g.Participants.Select(p =>
                            roleUsers.Contains(p.Jid.ToNonAdString()) ? p with { Role = nextRole } : p).ToArray()
                    });
                    changed = true;
                    break;

                case "subject" when act.Subject is not null:
                    UpdateCached(parsed.From, g => g with { Subject = act.Subject, SubjectTime = (ulong)parsed.TimestampSeconds });
                    changed = true;
                    break;

                case "description":
                    UpdateCached(parsed.From, g => g with { Description = act.Description, DescriptionId = act.DescriptionId });
                    changed = true;
                    break;

                case "locked" or "unlocked":
                    UpdateCached(parsed.From, g => g with { IsLocked = act.ToggleValue ?? (act.Action == "locked") });
                    changed = true;
                    break;

                case "announcement" or "not_announcement":
                    UpdateCached(parsed.From, g => g with { IsAnnouncement = act.ToggleValue ?? (act.Action == "announcement") });
                    changed = true;
                    break;

                case "ephemeral" or "not_ephemeral":
                    UpdateCached(parsed.From, g => g with
                    {
                        Ephemeral = act.EphemeralExpiration is > 0 ? new GroupEphemeralSettings(act.EphemeralExpiration, null) : null
                    });
                    changed = true;
                    break;

                case "membership_approval_mode" when act.ToggleValue is bool apr:
                    UpdateCached(parsed.From, g => g with { MembershipApproval = apr });
                    changed = true;
                    break;

                case "member_add_mode" when GroupManagementIq.ParseMemberAddMode(act.ModeValue) is { } addMode:
                    UpdateCached(parsed.From, g => g with { MemberAddMode = addMode });
                    changed = true;
                    break;

                case "member_link_mode" when GroupManagementIq.ParseMemberLinkMode(act.ModeValue) is { } linkMode:
                    UpdateCached(parsed.From, g => g with { MemberLinkMode = linkMode });
                    changed = true;
                    break;

                case "member_share_group_history_mode" when GroupManagementIq.ParseShareHistoryMode(act.ModeValue) is { } shareMode:
                    UpdateCached(parsed.From, g => g with { ShareHistoryMode = shareMode });
                    changed = true;
                    break;

                case "group_history" or "no_group_history":
                    UpdateCached(parsed.From, g => g with { HasGroupHistory = act.ToggleValue ?? (act.Action == "group_history") });
                    changed = true;
                    break;
            }
        }

        if (changed && _emitEvent is not null && _cache.TryGetValue(key, out GroupMetadata? updated))
        {
            await _emitEvent(new GroupMetadataUpdatedEvent(key, parsed.Action, updated.Participants.Count)).ConfigureAwait(false);
        }
    }

    private async Task ExecuteGroupPropertyMexAsync(object variables, CancellationToken ct)
    {
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(MexStanzas.UpdateGroupProperty, variables, ct: ct).ConfigureAwait(false);
        string? state = MexClient.TryGetString(resp.Data, "xwa2_group_update_property", "state");
        if (!string.Equals(state, "ACTIVE", StringComparison.Ordinal))
        {
            throw new MexException(MexStanzas.UpdateGroupProperty.Name, $"MEX group property update failed: state='{state ?? "<null>"}'.");
        }
    }

    private Task<Node> SendRequiredIqAsync(Node iq, CancellationToken ct) =>
        _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct);

    private async Task CacheAndLearnAsync(GroupMetadata meta, string reason, CancellationToken ct)
    {
        string key = meta.Id.ToNonAdString();
        _cache[key] = meta;
        _groupRegistry?.Upsert(new GroupMetadataRecord(
            key,
            meta.Subject ?? string.Empty,
            meta.Creator?.ToNonAdString(),
            meta.CreationTime ?? 0,
            meta.Description,
            meta.IsLocked,
            meta.IsAnnouncement,
            meta.Participants.Count));

        var mappings = new List<LidPnMappingRecord>();
        foreach (GroupParticipantInfo p in meta.Participants)
        {
            if (p.Jid.IsLid() && p.PhoneNumber is Jid pn && pn.IsPn())
            {
                string pnStr = pn.ToNonAdString();
                string lidStr = p.Jid.ToNonAdString();
                _lidPnCache.Learn(pnStr, lidStr, LidPnLearningSource.PeerJid);
                mappings.Add(new LidPnMappingRecord(pnStr, lidStr));
            }
            else if (p.Jid.IsPn() && p.Lid is Jid lid && lid.IsLid())
            {
                string pnStr = p.Jid.ToNonAdString();
                string lidStr = lid.ToNonAdString();
                _lidPnCache.Learn(pnStr, lidStr, LidPnLearningSource.PeerJid);
                mappings.Add(new LidPnMappingRecord(pnStr, lidStr));
            }
        }
        if (mappings.Count > 0)
        {
            await _backend.Protocol.UpsertLidPnMappingsAsync(mappings, ct).ConfigureAwait(false);
        }

        if (_emitEvent is not null)
        {
            await _emitEvent(new GroupMetadataUpdatedEvent(key, reason, meta.Participants.Count)).ConfigureAwait(false);
        }
    }

    private void UpdateCached(Jid groupJid, Func<GroupMetadata, GroupMetadata> transform)
    {
        string key = groupJid.ToNonAdString();
        _cache.AddOrUpdate(
            key,
            static (_, _) => default!,
            (k, existing, fn) =>
            {
                GroupMetadata next = fn(existing);
                _groupRegistry?.Upsert(new GroupMetadataRecord(
                    k,
                    next.Subject ?? string.Empty,
                    next.Creator?.ToNonAdString(),
                    next.CreationTime ?? 0,
                    next.Description,
                    next.IsLocked,
                    next.IsAnnouncement,
                    next.Participants.Count));
                return next;
            },
            transform);
        if (_cache.TryGetValue(key, out GroupMetadata? val) && val is null)
        {
            _cache.TryRemove(key, out _);
        }
    }

    private static string ComputeGroupParticipantsPhash(GroupMetadata meta)
    {
        var jids = meta.Participants.Select(p => p.Jid).ToArray();
        return MessageFraming.ComputeParticipantListHash(jids);
    }
}
