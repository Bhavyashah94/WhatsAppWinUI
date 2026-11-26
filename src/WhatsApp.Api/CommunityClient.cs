using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Api;

public sealed class CommunityConfigurationException : Exception
{
    public Jid CreatedCommunityJid { get; }
    public string Step { get; }

    public CommunityConfigurationException(Jid createdCommunityJid, string step, Exception inner)
        : base($"Community '{createdCommunityJid}' created, but post-create step '{step}' failed: {inner.Message}", inner)
    {
        CreatedCommunityJid = createdCommunityJid;
        Step = step;
    }
}

public sealed class CommunityMembershipApprovalRequiredException : Exception
{
    public Jid SubgroupJid { get; }

    public CommunityMembershipApprovalRequiredException(Jid subgroupJid)
        : base($"Subgroup '{subgroupJid}' requires membership approval before joining.")
    {
        SubgroupJid = subgroupJid;
    }
}

public sealed record LinkSubgroupsResult(
    IReadOnlyList<Jid> LinkedJids,
    IReadOnlyList<LinkedGroupEntryResult> FailedGroups);

/// <summary>
/// High-level <c>w:g2</c> and <c>w:mex</c> Community management client:
/// parent community creation/deactivation, Default Announcement Group (<c>CAG</c>) discovery,
/// linked subgroup management, member counts, suggestions, and cascading participant removal.
/// </summary>
public sealed class CommunityClient
{
    private readonly IqClient _iqClient;
    private readonly MexClient _mexClient;
    private readonly GroupClient _groups;
    private readonly IWhatsAppBackend _backend;
    private readonly ILidPnCache _lidPnCache;

    public CommunityClient(
        IqClient iqClient,
        MexClient mexClient,
        GroupClient groups,
        IWhatsAppBackend backend,
        ILidPnCache lidPnCache)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _mexClient = mexClient ?? throw new ArgumentNullException(nameof(mexClient));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
    }

    public async Task<GroupMetadata> CreateCommunityAsync(CreateCommunityOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        string iqId = _iqClient.NextRequestId();
        Node iq = CommunityIq.BuildCreateCommunity(iqId, options);
        Node resp = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        GroupMetadata created = CommunityIq.ParseCreateCommunityResponse(resp, options);

        if (!string.IsNullOrEmpty(options.Description))
        {
            try
            {
                await _groups.SetDescriptionAsync(created.Id, options.Description, previousDescriptionId: null, ct).ConfigureAwait(false);
                created = created with { Description = options.Description };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new CommunityConfigurationException(created.Id, "SetDescription", ex);
            }
        }

        return created;
    }

    public Task<GroupMetadata> CreateSubgroupAsync(CreateSubgroupOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var groupOpts = new GroupCreateOptions(
            Subject: options.Name,
            Participants: options.Participants.Select(j => new GroupParticipantOptions(j)).ToArray(),
            LinkedParent: options.ParentJid,
            HiddenGroup: options.Visibility == SubgroupVisibility.Hidden);
        return _groups.CreateGroupAsync(groupOpts, ct);
    }

    public async Task DeactivateCommunityAsync(Jid communityJid, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = CommunityIq.BuildDeactivateCommunity(iqId, communityJid);
        await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        _groups.InvalidateCache(communityJid);
    }

    public Task<IReadOnlyList<ParticipantChangeResult>> RemoveParticipantsAsync(
        Jid communityJid,
        IReadOnlyList<Jid> participants,
        CancellationToken ct = default) =>
        _groups.RemoveParticipantsAsync(communityJid, participants, includeLinkedGroups: true, ct);

    public Task<LinkSubgroupsResult> LinkSubgroupsAsync(Jid communityJid, IReadOnlyList<Jid> subgroupJids, CancellationToken ct = default) =>
        LinkSubgroupsAsync(communityJid, subgroupJids.Select(j => new LinkSubgroupOption(j)).ToArray(), ct);

    public async Task<LinkSubgroupsResult> LinkSubgroupsAsync(
        Jid communityJid,
        IReadOnlyList<LinkSubgroupOption> subgroups,
        CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = CommunityIq.BuildLinkSubgroups(iqId, communityJid, subgroups);
        Node resp = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        IReadOnlyList<LinkedGroupEntryResult> parsed = CommunityIq.ParseLinkSubgroupsResponse(resp);
        return PartitionLinkedResults(parsed);
    }

    public async Task<LinkSubgroupsResult> UnlinkSubgroupsAsync(
        Jid communityJid,
        IReadOnlyList<Jid> subgroupJids,
        bool removeOrphanMembers = false,
        CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = CommunityIq.BuildUnlinkSubgroups(iqId, communityJid, subgroupJids, removeOrphanMembers);
        Node resp = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        IReadOnlyList<LinkedGroupEntryResult> parsed = CommunityIq.ParseUnlinkSubgroupsResponse(resp);
        return PartitionLinkedResults(parsed);
    }

    public async Task<IReadOnlyList<CommunitySubgroupInfo>> GetSubgroupsAsync(Jid communityJid, CancellationToken ct = default)
    {
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchAllSubgroups,
            MexStanzas.BuildFetchAllSubgroupsVariables(communityJid, CommunityIq.SubgroupQueryContext),
            ct: ct).ConfigureAwait(false);

        return CommunityIq.ParseSubgroupsMexResponse(resp.Data);
    }

    public async Task<CommunitySubgroupInfo?> FindAnnouncementGroupAsync(Jid communityJid, CancellationToken ct = default)
    {
        IReadOnlyList<CommunitySubgroupInfo> subgroups = await GetSubgroupsAsync(communityJid, ct).ConfigureAwait(false);
        return subgroups.FirstOrDefault(s => s.IsDefaultSubGroup);
    }

    public async Task<IReadOnlyList<SubgroupParticipantCountInfo>> GetSubgroupParticipantCountsAsync(Jid communityJid, CancellationToken ct = default)
    {
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.QuerySubgroupParticipantCount,
            MexStanzas.BuildQuerySubgroupParticipantCountVariables(communityJid, CommunityIq.SubgroupQueryContext),
            ct: ct).ConfigureAwait(false);

        return CommunityIq.ParseSubgroupParticipantCountsMexResponse(resp.Data);
    }

    public async Task<IReadOnlyList<SubgroupSuggestionInfo>> GetSubgroupSuggestionsAsync(Jid communityJid, CancellationToken ct = default)
    {
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchSubgroupSuggestions,
            MexStanzas.BuildFetchSubgroupSuggestionsVariables(communityJid, CommunityIq.SubgroupQueryContext),
            ct: ct).ConfigureAwait(false);

        return CommunityIq.ParseSubgroupSuggestionsMexResponse(resp.Data);
    }

    public async Task<IReadOnlyList<GroupMetadata>> GetParticipatingCommunitiesAsync(CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await _iqClient.SendIqAsync(GroupManagementIq.BuildParticipating(iqId, includeParticipants: true), IqClient.DefaultTimeout, ct)
            .ConfigureAwait(false);
        return CommunityIq.ParseParticipatingCommunitiesResponse(resp);
    }

    public async Task<IReadOnlyList<GroupOverview>> GetParticipatingCommunityOverviewsAsync(CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await _iqClient.SendIqAsync(GroupManagementIq.BuildParticipating(iqId, includeParticipants: false, includeDescription: false), IqClient.DefaultTimeout, ct)
            .ConfigureAwait(false);
        return CommunityIq.ParseParticipatingCommunityOverviewsResponse(resp);
    }

    public async Task<GroupMetadata> QueryLinkedGroupAsync(Jid communityJid, Jid subgroupJid, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await _iqClient.SendIqAsync(CommunityIq.BuildQueryLinkedGroup(iqId, communityJid, subgroupJid), IqClient.DefaultTimeout, ct)
            .ConfigureAwait(false);
        return CommunityIq.ParseQueryLinkedGroupResponse(resp);
    }

    public async Task<GroupMetadata> JoinSubgroupAsync(Jid communityJid, Jid subgroupJid, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await _iqClient.SendIqAsync(CommunityIq.BuildJoinLinkedGroup(iqId, communityJid, subgroupJid), IqClient.DefaultTimeout, ct)
            .ConfigureAwait(false);

        JoinGroupOutcome outcome = CommunityIq.ParseJoinLinkedGroupResponse(resp, subgroupJid);
        if (outcome.Kind == JoinGroupOutcomeKind.PendingApproval)
        {
            throw new CommunityMembershipApprovalRequiredException(outcome.GroupJid);
        }

        return await QueryLinkedGroupAsync(communityJid, outcome.GroupJid, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GroupParticipantInfo>> GetLinkedGroupsParticipantsAsync(Jid communityJid, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node resp = await _iqClient.SendIqAsync(CommunityIq.BuildGetLinkedGroupsParticipants(iqId, communityJid), IqClient.DefaultTimeout, ct)
            .ConfigureAwait(false);

        IReadOnlyList<GroupParticipantInfo> participants = CommunityIq.ParseLinkedGroupsParticipantsResponse(resp);
        var mappings = new List<LidPnMappingRecord>();
        foreach (GroupParticipantInfo p in participants)
        {
            if (p.Jid.IsLid() && p.PhoneNumber is Jid pn && pn.IsPn())
            {
                _lidPnCache.Learn(pn.ToNonAdString(), p.Jid.ToNonAdString(), LidPnLearningSource.PeerJid);
                mappings.Add(new LidPnMappingRecord(pn.ToNonAdString(), p.Jid.ToNonAdString()));
            }
            else if (p.Jid.IsPn() && p.Lid is Jid lid && lid.IsLid())
            {
                _lidPnCache.Learn(p.Jid.ToNonAdString(), lid.ToNonAdString(), LidPnLearningSource.PeerJid);
                mappings.Add(new LidPnMappingRecord(p.Jid.ToNonAdString(), lid.ToNonAdString()));
            }
        }
        if (mappings.Count > 0)
        {
            await _backend.Protocol.UpsertLidPnMappingsAsync(mappings, ct).ConfigureAwait(false);
        }

        return participants;
    }

    private static LinkSubgroupsResult PartitionLinkedResults(IReadOnlyList<LinkedGroupEntryResult> entries)
    {
        var ok = new List<Jid>(entries.Count);
        var failed = new List<LinkedGroupEntryResult>();
        foreach (LinkedGroupEntryResult entry in entries)
        {
            if (entry.IsOk) ok.Add(entry.GroupJid);
            else failed.Add(entry);
        }
        return new LinkSubgroupsResult(ok, failed);
    }
}
