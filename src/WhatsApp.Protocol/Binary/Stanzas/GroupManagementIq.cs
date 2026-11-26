using System.Globalization;
using System.Security.Cryptography;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum ParticipantType
{
    Member = 0,
    Admin = 1,
    SuperAdmin = 2
}

public enum MemberLinkMode
{
    AdminLink = 0,
    AllMemberLink = 1
}

public enum MemberAddMode
{
    AdminAdd = 0,
    AllMemberAdd = 1
}

public enum MembershipApprovalMode
{
    Off = 0,
    On = 1
}

public enum MemberShareHistoryMode
{
    AdminShare = 0,
    AllMemberShare = 1
}

public enum GroupAppealStatus
{
    NoAppeal = 0,
    InReview = 1,
    Approved = 2,
    Rejected = 3
}

public enum GroupPictureType
{
    Preview = 0,
    Image = 1
}

public enum JoinGroupOutcomeKind
{
    Joined = 0,
    PendingApproval = 1
}

public enum BatchGroupLookupStatus
{
    Found = 0,
    Truncated = 1,
    Forbidden = 2,
    NotFound = 3
}

public sealed record GrowthLockInfo(string LockType, ulong Expiration);

public sealed record GroupEphemeralSettings(uint? ExpirationSeconds, uint? Trigger);

public sealed record GroupParticipantDetails(
    string? ParticipantLabel = null,
    ulong? ParticipantLabelMtime = null,
    ulong? JoinTime = null,
    bool? GroupHistorySent = null,
    string? DisplayName = null,
    bool IsAddressable = true);

public sealed record GroupParticipantInfo(
    Jid Jid,
    Jid? PhoneNumber = null,
    Jid? Lid = null,
    string? Username = null,
    ParticipantType Role = ParticipantType.Member,
    GroupParticipantDetails? Details = null)
{
    public bool IsAdmin => Role is ParticipantType.Admin or ParticipantType.SuperAdmin;
}

public sealed record GroupParticipantOptions(
    Jid Jid,
    Jid? PhoneNumber = null,
    byte[]? PrivacyToken = null);

public sealed record GroupCreateOptions(
    string Subject,
    IReadOnlyList<GroupParticipantOptions> Participants,
    MemberLinkMode? LinkMode = MemberLinkMode.AdminLink,
    MemberAddMode? AddMode = MemberAddMode.AllMemberAdd,
    MembershipApprovalMode? ApprovalMode = MembershipApprovalMode.Off,
    uint? EphemeralExpirationSeconds = 0,
    bool IsParent = false,
    bool Closed = false,
    bool AllowNonAdminSubGroupCreation = false,
    bool CreateGeneralChat = false,
    Jid? LinkedParent = null,
    bool HiddenGroup = false,
    string? Description = null);

public sealed record GroupMetadata(
    Jid Id,
    string? Subject,
    string AddressingMode,
    IReadOnlyList<GroupParticipantInfo> Participants,
    string? Notify = null,
    Jid? Creator = null,
    Jid? CreatorPn = null,
    string? CreatorUsername = null,
    string? CreatorCountryCode = null,
    ulong? CreationTime = null,
    string? ParticipantVersionId = null,
    string? AdminVersionId = null,
    string? OpenThreadId = null,
    bool HasMissingParticipantIdentification = false,
    ulong? SubjectTime = null,
    Jid? SubjectOwner = null,
    Jid? SubjectOwnerPn = null,
    string? SubjectOwnerUsername = null,
    string? Description = null,
    string? DescriptionId = null,
    Jid? DescriptionOwner = null,
    Jid? DescriptionOwnerPn = null,
    string? DescriptionOwnerUsername = null,
    ulong? DescriptionTime = null,
    bool IsLocked = false,
    bool IsAnnouncement = false,
    GroupEphemeralSettings? Ephemeral = null,
    bool MembershipApproval = false,
    MemberAddMode? MemberAddMode = null,
    MemberLinkMode? MemberLinkMode = null,
    uint? Size = null,
    bool IsParentGroup = false,
    bool ParentMembershipApprovalRequired = false,
    Jid? ParentGroupJid = null,
    bool IsDefaultSubGroup = false,
    bool IsGeneralChat = false,
    bool AllowNonAdminSubGroupCreation = false,
    bool NoFrequentlyForwarded = false,
    MemberShareHistoryMode? ShareHistoryMode = null,
    GrowthLockInfo? GrowthLocked = null,
    bool IsSuspended = false,
    bool SuspensionCanAutoFile = false,
    GroupAppealStatus? AppealStatus = null,
    ulong? AppealUpdateTime = null,
    bool IsSupportGroup = false,
    bool AllowAdminReports = false,
    bool IsHiddenGroup = false,
    bool IsIncognito = false,
    bool HasGroupHistory = false,
    bool IsAutoAddDisabled = false,
    bool HasCapi = false,
    uint? EvolutionVersion = null,
    bool HasGroupSafetyCheck = false,
    bool ParticipantLabelEnabled = false,
    bool IsLimitSharingEnabled = false,
    uint? LimitSharingTrigger = null)
{
    public bool MatchesParticipant(Jid userJid)
    {
        string bare = userJid.ToNonAdString();
        return Participants.Any(p =>
            string.Equals(p.Jid.ToNonAdString(), bare, StringComparison.OrdinalIgnoreCase)
            || (p.PhoneNumber is Jid pn && string.Equals(pn.ToNonAdString(), bare, StringComparison.OrdinalIgnoreCase))
            || (p.Lid is Jid lid && string.Equals(lid.ToNonAdString(), bare, StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed record GroupOverview(
    Jid Id,
    string? Subject,
    bool IsParentGroup,
    bool IsDefaultSubGroup,
    bool IsGeneralChat,
    Jid? ParentGroupJid,
    uint? Size);

public sealed record GroupQueryResult(bool IsModified, GroupMetadata? Metadata);

public sealed record AddRequestInfo(string Code, ulong Expiration);

public sealed record ParticipantChangeResult(
    Jid Jid,
    string? Status,
    string? Error,
    Jid? PhoneNumber = null,
    string? Username = null,
    AddRequestInfo? AddRequest = null)
{
    public bool IsOk => string.IsNullOrEmpty(Error);
}

public sealed record MembershipRequest(Jid Jid, ulong? RequestTime);

public sealed record JoinGroupOutcome(JoinGroupOutcomeKind Kind, Jid GroupJid);

public sealed record BatchGroupMetadataEntry(
    BatchGroupLookupStatus Status,
    Jid GroupJid,
    GroupMetadata? Metadata = null,
    uint? TruncatedSize = null);

public sealed record GroupProfilePictureEntry(
    Jid GroupJid,
    GroupPictureType PictureType,
    string? Id = null,
    string? Url = null,
    string? DirectPath = null,
    string? Error = null);

public sealed record GroupMessageReporter(
    Jid Jid,
    ulong Timestamp,
    Jid? PhoneNumber = null,
    string? Username = null);

public sealed record ReportedGroupMessage(string MessageId, IReadOnlyList<GroupMessageReporter> Reporters);

public sealed record ReportedGroupMessagesResult(string? AddressingMode, IReadOnlyList<ReportedGroupMessage> Reports);

/// <summary>
/// Full <c>xmlns="w:g2"</c> group management IQ builders and response parsers matching
/// <c>wacore/src/iq/groups.rs</c> and live <c>web.whatsapp.com</c> stanzas.
/// </summary>
public static class GroupManagementIq
{
    public const string Xmlns = "w:g2";
    public const int SubjectMaxLength = 100;
    public const int DescriptionMaxLength = 2048;
    public const int BatchGroupInfoLimit = 10_000;
    public const int BatchProfilePicturesLimit = 1_000;
    public const uint MaxParticipantLimit = 19_999;
    public const uint MaxEphemeralTrigger = 20;

    public static string ToWireString(this ParticipantType role) => role switch
    {
        ParticipantType.Admin => "admin",
        ParticipantType.SuperAdmin => "superadmin",
        _ => "member"
    };

    public static ParticipantType ParseParticipantRole(string? wire) => wire switch
    {
        "admin" => ParticipantType.Admin,
        "superadmin" => ParticipantType.SuperAdmin,
        _ => ParticipantType.Member
    };

    public static string ToWireString(this MemberLinkMode mode) => mode switch
    {
        MemberLinkMode.AllMemberLink => "all_member_link",
        _ => "admin_link"
    };

    public static MemberLinkMode? ParseMemberLinkMode(string? wire) => wire switch
    {
        "admin_link" => MemberLinkMode.AdminLink,
        "all_member_link" => MemberLinkMode.AllMemberLink,
        _ => null
    };

    public static string ToWireString(this MemberAddMode mode) => mode switch
    {
        MemberAddMode.AdminAdd => "admin_add",
        _ => "all_member_add"
    };

    public static MemberAddMode? ParseMemberAddMode(string? wire) => wire switch
    {
        "admin_add" => MemberAddMode.AdminAdd,
        "all_member_add" => MemberAddMode.AllMemberAdd,
        _ => null
    };

    public static string ToWireString(this MembershipApprovalMode mode) => mode switch
    {
        MembershipApprovalMode.On => "on",
        _ => "off"
    };

    public static string ToWireString(this MemberShareHistoryMode mode) => mode switch
    {
        MemberShareHistoryMode.AllMemberShare => "all_member_share",
        _ => "admin_share"
    };

    public static MemberShareHistoryMode? ParseShareHistoryMode(string? wire) => wire switch
    {
        "admin_share" => MemberShareHistoryMode.AdminShare,
        "all_member_share" => MemberShareHistoryMode.AllMemberShare,
        _ => null
    };

    public static GroupAppealStatus? ParseAppealStatus(string? wire) => wire switch
    {
        "approved" => GroupAppealStatus.Approved,
        "in_review" => GroupAppealStatus.InReview,
        "none" => GroupAppealStatus.NoAppeal,
        "rejected" => GroupAppealStatus.Rejected,
        _ => null
    };

    public static Node BuildQueryGroup(string iqId, Jid groupJid, string? phash = null)
    {
        var queryAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["request"] = "interactive" };
        if (!string.IsNullOrEmpty(phash))
        {
            queryAttrs["phash"] = phash;
        }
        return StanzaBuilder.Iq(iqId, "get", Xmlns, groupJid.ToNonAd(), new Node("query", queryAttrs));
    }

    public static GroupQueryResult ParseQueryGroupResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node? groupNode = responseNode.Tag is "group" or "community"
            ? responseNode
            : responseNode.GetChild("group") ?? responseNode.GetChild("community");
        if (groupNode is null)
        {
            return new GroupQueryResult(IsModified: false, Metadata: null);
        }
        return new GroupQueryResult(IsModified: true, ParseGroupNode(groupNode));
    }

    public static Node BuildParticipating(string iqId, bool includeParticipants = true, bool includeDescription = true)
    {
        var children = new List<Node>();
        if (includeParticipants) children.Add(new Node("participants"));
        if (includeDescription) children.Add(new Node("description"));
        return StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerGroup, new Node("participating", null, new NodeContent.NodesContent(children)));
    }

    public static IReadOnlyList<GroupMetadata> ParseParticipatingResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        var list = new List<GroupMetadata>();
        CollectGroupNodes(responseNode, "groups", "group", isCommunity: false, list);
        CollectGroupNodes(responseNode, "communities", "community", isCommunity: true, list);
        return list;
    }

    public static IReadOnlyList<GroupOverview> ParseParticipatingOverviewResponse(Node responseNode) =>
        ParseParticipatingResponse(responseNode)
            .Select(g => new GroupOverview(g.Id, g.Subject, g.IsParentGroup, g.IsDefaultSubGroup, g.IsGeneralChat, g.ParentGroupJid, g.Size))
            .ToArray();

    public static Node BuildCreateGroup(string iqId, GroupCreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Subject.Length > SubjectMaxLength)
        {
            throw new ArgumentException($"Group subject exceeds {SubjectMaxLength} characters.", nameof(options));
        }
        if (options.Description is { Length: > DescriptionMaxLength })
        {
            throw new ArgumentException($"Group description exceeds {DescriptionMaxLength} characters.", nameof(options));
        }

        var children = new List<Node>();
        if (options.LinkMode is { } lm)
        {
            children.Add(new Node("member_link_mode", lm.ToWireString()));
        }
        if (options.AddMode is { } am)
        {
            children.Add(new Node("member_add_mode", am.ToWireString()));
        }

        foreach (GroupParticipantOptions p in options.Participants)
        {
            var pAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = p.Jid };
            if (p.Jid.IsLid() && p.PhoneNumber is Jid pn)
            {
                pAttrs["phone_number"] = pn;
            }
            if (p.PrivacyToken is { Length: > 0 } priv)
            {
                children.Add(new Node("participant", pAttrs, new[] { new Node("privacy", Convert.ToHexStringLower(priv)) }));
            }
            else
            {
                children.Add(new Node("participant", pAttrs));
            }
        }

        if (options.EphemeralExpirationSeconds is uint exp)
        {
            children.Add(new Node("ephemeral", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["expiration"] = exp.ToString(CultureInfo.InvariantCulture)
            }));
        }

        if (options.ApprovalMode is { } approval)
        {
            children.Add(new Node("membership_approval_mode", null, new[]
            {
                new Node("group_join", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["state"] = approval.ToWireString() })
            }));
        }

        if (options.LinkedParent is Jid parentJid)
        {
            children.Add(new Node("linked_parent", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = parentJid.ToNonAd() }));
        }
        else if (options.IsParent)
        {
            var parentAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal);
            if (options.Closed)
            {
                parentAttrs["default_membership_approval_mode"] = "request_required";
            }
            children.Add(new Node("parent", parentAttrs));
            if (options.AllowNonAdminSubGroupCreation)
            {
                children.Add(new Node("allow_non_admin_sub_group_creation"));
            }
            if (options.CreateGeneralChat)
            {
                children.Add(new Node("create_general_chat"));
            }
        }

        if (options.HiddenGroup)
        {
            children.Add(new Node("hidden_group"));
        }

        if (!string.IsNullOrEmpty(options.Description))
        {
            children.Add(new Node(
                "description",
                new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["id"] = GenerateDescriptionId() },
                new[] { new Node("body", options.Description) }));
        }

        var createNode = new Node(
            "create",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["subject"] = options.Subject },
            children);
        return StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerGroup, createNode);
    }

    public static GroupMetadata ParseCreateGroupResponse(Node responseNode, GroupCreateOptions? requestOptions = null)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node groupNode = responseNode.Tag is "group" or "community"
            ? responseNode
            : responseNode.GetChild("group") ?? responseNode.GetChild("community")
                ?? throw new InvalidDataException("Missing <group> or <community> in create response.");

        GroupMetadata info = ParseGroupNode(groupNode);
        if (requestOptions is not null)
        {
            bool isLinkedSub = info.ParentGroupJid is not null || requestOptions.LinkedParent is not null;
            if (requestOptions.IsParent && !isLinkedSub)
            {
                info = info with
                {
                    IsParentGroup = true,
                    ParentMembershipApprovalRequired = info.ParentMembershipApprovalRequired || requestOptions.Closed,
                    AllowNonAdminSubGroupCreation = info.AllowNonAdminSubGroupCreation || requestOptions.AllowNonAdminSubGroupCreation
                };
            }
            if (requestOptions.HiddenGroup)
            {
                info = info with { IsHiddenGroup = true };
            }
        }
        return info;
    }

    public static Node BuildLeaveGroup(string iqId, Jid groupJid)
    {
        var gNode = new Node("group", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["id"] = groupJid.ToNonAd() });
        return StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerGroup, new Node("leave", null, new[] { gNode }));
    }

    public static Node BuildSetSubject(string iqId, Jid groupJid, string subject)
    {
        if (subject.Length > SubjectMaxLength)
        {
            throw new ArgumentException($"Group subject exceeds {SubjectMaxLength} characters.", nameof(subject));
        }
        return StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node("subject", subject));
    }

    public static Node BuildSetDescription(string iqId, Jid groupJid, string? description, string? previousId = null, string? descriptionId = null)
    {
        if (description is { Length: > DescriptionMaxLength })
        {
            throw new ArgumentException($"Group description exceeds {DescriptionMaxLength} characters.", nameof(description));
        }

        string id = string.IsNullOrEmpty(descriptionId) ? GenerateDescriptionId() : descriptionId;
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["id"] = id };
        if (!string.IsNullOrEmpty(previousId))
        {
            attrs["prev"] = previousId;
        }

        Node descNode;
        if (description is null)
        {
            attrs["delete"] = "true";
            descNode = new Node("description", attrs);
        }
        else
        {
            descNode = new Node("description", attrs, new[] { new Node("body", description) });
        }

        return StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), descNode);
    }

    public static Node BuildAddParticipants(string iqId, Jid groupJid, IReadOnlyList<GroupParticipantOptions> participants)
    {
        var pNodes = new List<Node>(participants.Count);
        foreach (GroupParticipantOptions p in participants)
        {
            var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = p.Jid.ToNonAd() };
            if (p.PrivacyToken is { Length: > 0 } priv)
            {
                pNodes.Add(new Node("participant", attrs, new[] { new Node("privacy", Convert.ToHexStringLower(priv)) }));
            }
            else
            {
                pNodes.Add(new Node("participant", attrs));
            }
        }
        return StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node("add", null, pNodes));
    }

    public static Node BuildRemoveParticipants(string iqId, Jid groupJid, IReadOnlyList<Jid> participants, bool includeLinkedGroups = false) =>
        BuildParticipantActionIq(iqId, groupJid, "remove", participants, includeLinkedGroups);

    public static Node BuildPromoteParticipants(string iqId, Jid groupJid, IReadOnlyList<Jid> participants) =>
        BuildParticipantActionIq(iqId, groupJid, "promote", participants, includeLinkedGroups: false);

    public static Node BuildDemoteParticipants(string iqId, Jid groupJid, IReadOnlyList<Jid> participants) =>
        BuildParticipantActionIq(iqId, groupJid, "demote", participants, includeLinkedGroups: false);

    public static Node BuildCancelMembershipRequests(string iqId, Jid groupJid, IReadOnlyList<Jid> participants) =>
        BuildParticipantActionIq(iqId, groupJid, "cancel_membership_requests", participants, includeLinkedGroups: false);

    public static Node BuildRevokeRequestCode(string iqId, Jid groupJid, IReadOnlyList<Jid> participants) =>
        BuildParticipantActionIq(iqId, groupJid, "revoke", participants, includeLinkedGroups: false);

    public static IReadOnlyList<ParticipantChangeResult> ParseParticipantActionResponse(Node responseNode, string actionTag)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node container = responseNode.Tag == actionTag
            ? responseNode
            : responseNode.GetChild(actionTag)
                ?? throw new InvalidDataException($"Missing <{actionTag}> child in response.");

        var results = new List<ParticipantChangeResult>();
        foreach (Node p in container.GetChildren("participant"))
        {
            if (p.GetAttr("jid") is not { Length: > 0 } jidStr || !Jid.TryParse(jidStr, out Jid jid))
            {
                throw new InvalidDataException("<participant> missing required 'jid' attribute.");
            }
            AddRequestInfo? addReq = null;
            if (p.GetChild("add_request") is { } arNode)
            {
                string code = arNode.GetAttr("code") ?? throw new InvalidDataException("<add_request> missing 'code' attribute.");
                if (!ulong.TryParse(arNode.GetAttr("expiration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong exp))
                {
                    throw new InvalidDataException("<add_request> invalid 'expiration' attribute.");
                }
                addReq = new AddRequestInfo(code, exp);
            }

            Jid? pn = TryParseAttrJid(p, "phone_number");
            results.Add(new ParticipantChangeResult(jid, p.GetAttr("type"), p.GetAttr("error"), pn, p.GetAttr("username"), addReq));
        }
        return results;
    }

    public static Node BuildGetInviteLink(string iqId, Jid groupJid, bool reset = false) =>
        StanzaBuilder.Iq(iqId, reset ? "set" : "get", Xmlns, groupJid.ToNonAd(), new Node("invite"));

    public static string ParseInviteLinkResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node inv = responseNode.Tag == "invite"
            ? responseNode
            : responseNode.GetChild("invite") ?? throw new InvalidDataException("Missing <invite> child.");
        string code = inv.GetAttr("code") ?? throw new InvalidDataException("<invite> missing 'code' attribute.");
        return $"https://chat.whatsapp.com/{code}";
    }

    public static Node BuildGetInviteInfo(string iqId, string inviteCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteCode);
        return StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerGroup,
            new Node("invite", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["code"] = inviteCode }));
    }

    public static Node BuildAcceptInvite(string iqId, string inviteCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteCode);
        return StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerGroup,
            new Node("invite", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["code"] = inviteCode }));
    }

    public static Node BuildAcceptInviteV4(string iqId, Jid groupJid, string code, long expiration, Jid adminJid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["expiration"] = expiration.ToString(CultureInfo.InvariantCulture),
            ["admin"] = adminJid.ToNonAd()
        };
        return StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node("accept", attrs));
    }

    public static JoinGroupOutcome ParseJoinGroupResponse(Node responseNode, Jid? fallbackGroupJid = null)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        if (responseNode.GetChild("membership_approval_request") is { } reqNode)
        {
            Jid reqJid = TryParseAttrJid(reqNode, "jid") ?? fallbackGroupJid
                ?? throw new InvalidDataException("Missing group JID in membership_approval_request.");
            return new JoinGroupOutcome(JoinGroupOutcomeKind.PendingApproval, reqJid);
        }
        Node? target = responseNode.GetChild("group") ?? responseNode.GetChild("community");
        if (target is not null)
        {
            string? idStr = target.GetAttr("id") ?? target.GetAttr("jid");
            Jid jid = ParseGroupId(idStr ?? throw new InvalidDataException("Missing group id in join response."));
            return new JoinGroupOutcome(JoinGroupOutcomeKind.Joined, jid);
        }
        if (responseNode.GetChild("membership_requests_action") is not null)
        {
            return new JoinGroupOutcome(JoinGroupOutcomeKind.PendingApproval, fallbackGroupJid ?? default);
        }
        if (fallbackGroupJid is Jid fb)
        {
            return new JoinGroupOutcome(JoinGroupOutcomeKind.Joined, fb);
        }
        throw new InvalidDataException("Unrecognized group join IQ response.");
    }

    public static Node BuildGetMembershipRequests(string iqId, Jid groupJid) =>
        StanzaBuilder.Iq(iqId, "get", Xmlns, groupJid.ToNonAd(), new Node("membership_approval_requests"));

    public static IReadOnlyList<MembershipRequest> ParseMembershipRequestsResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node reqs = responseNode.Tag == "membership_approval_requests"
            ? responseNode
            : responseNode.GetChild("membership_approval_requests")
                ?? throw new InvalidDataException("Missing <membership_approval_requests>.");

        var list = new List<MembershipRequest>();
        foreach (Node child in reqs.GetChildren("membership_approval_request"))
        {
            string jidStr = child.GetAttr("jid") ?? throw new InvalidDataException("Missing 'jid' on membership request.");
            if (!Jid.TryParse(jidStr, out Jid jid))
            {
                throw new InvalidDataException($"Invalid JID '{jidStr}' in membership request.");
            }
            ulong? rt = ulong.TryParse(child.GetAttr("request_time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong v) ? v : null;
            list.Add(new MembershipRequest(jid, rt));
        }
        return list;
    }

    public static Node BuildMembershipRequestAction(string iqId, Jid groupJid, bool approve, IReadOnlyList<Jid> participants)
    {
        string actionTag = approve ? "approve" : "reject";
        var pNodes = participants.Select(j => new Node("participant", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = j.ToNonAd() })).ToArray();
        var actionNode = new Node("membership_requests_action", null, new[] { new Node(actionTag, null, pNodes) });
        return StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), actionNode);
    }

    public static IReadOnlyList<ParticipantChangeResult> ParseMembershipRequestActionResponse(Node responseNode, bool approve)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node outer = responseNode.Tag == "membership_requests_action"
            ? responseNode
            : responseNode.GetChild("membership_requests_action")
                ?? throw new InvalidDataException("Missing <membership_requests_action>.");
        return ParseParticipantActionResponse(outer, approve ? "approve" : "reject");
    }

    public static Node BuildSetLocked(string iqId, Jid groupJid, bool locked) =>
        StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node(locked ? "locked" : "unlocked"));

    public static Node BuildSetAnnouncement(string iqId, Jid groupJid, bool announce) =>
        StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node(announce ? "announcement" : "not_announcement"));

    public static Node BuildSetEphemeral(string iqId, Jid groupJid, uint? expirationSeconds, uint? trigger = null)
    {
        Node child;
        if (expirationSeconds is uint exp && exp > 0)
        {
            var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["expiration"] = exp.ToString(CultureInfo.InvariantCulture)
            };
            if (trigger is uint tr && tr <= MaxEphemeralTrigger)
            {
                attrs["trigger"] = tr.ToString(CultureInfo.InvariantCulture);
            }
            child = new Node("ephemeral", attrs);
        }
        else
        {
            child = new Node("not_ephemeral");
        }
        return StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), child);
    }

    public static Node BuildSetMembershipApproval(string iqId, Jid groupJid, MembershipApprovalMode mode)
    {
        var gj = new Node("group_join", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["state"] = mode.ToWireString() });
        return StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node("membership_approval_mode", null, new[] { gj }));
    }

    public static Node BuildSetMemberAddMode(string iqId, Jid groupJid, MemberAddMode mode) =>
        StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node("member_add_mode", mode.ToWireString()));

    public static Node BuildSetNoFrequentlyForwarded(string iqId, Jid groupJid, bool enabled) =>
        StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node(enabled ? "no_frequently_forwarded" : "frequently_forwarded_ok"));

    public static Node BuildSetAllowAdminReports(string iqId, Jid groupJid, bool allow) =>
        StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node(allow ? "allow_admin_reports" : "not_allow_admin_reports"));

    public static Node BuildSetGroupHistory(string iqId, Jid groupJid, bool enabled) =>
        StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node(enabled ? "group_history" : "no_group_history"));

    public static Node BuildAcknowledgeGroup(string iqId, Jid groupJid) =>
        StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node("ack"));

    public static Node BuildBatchGetGroupInfo(string iqId, IReadOnlyList<Jid> groupJids)
    {
        if (groupJids.Count > BatchGroupInfoLimit)
        {
            throw new ArgumentException($"Batch query exceeds {BatchGroupInfoLimit} groups.", nameof(groupJids));
        }
        var children = groupJids.Select(j => new Node("group", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = j.ToNonAd() })).ToArray();
        return StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerGroup, new Node("query", null, children));
    }

    public static IReadOnlyList<BatchGroupMetadataEntry> ParseBatchGetGroupInfoResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node container = responseNode.Tag == "groups"
            ? responseNode
            : responseNode.GetChild("groups")
                ?? throw new InvalidDataException("Missing <groups> child in batch group info response.");

        var results = new List<BatchGroupMetadataEntry>();
        foreach (Node g in container.GetChildren("group"))
        {
            string idStr = g.GetAttr("id") ?? throw new InvalidDataException("<group> missing 'id' attribute.");
            Jid gid = ParseGroupId(idStr);
            if (g.GetAttr("error") is { Length: > 0 } errCode)
            {
                BatchGroupLookupStatus status = errCode switch
                {
                    "403" => BatchGroupLookupStatus.Forbidden,
                    "404" => BatchGroupLookupStatus.NotFound,
                    _ => throw new InvalidDataException($"Unexpected batch group error code '{errCode}'.")
                };
                results.Add(new BatchGroupMetadataEntry(status, gid));
                continue;
            }

            if (string.Equals(g.GetAttr("truncated"), "true", StringComparison.OrdinalIgnoreCase))
            {
                if (!uint.TryParse(g.GetAttr("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint sz))
                {
                    throw new InvalidDataException("Truncated batch group missing 'size' attribute.");
                }
                results.Add(new BatchGroupMetadataEntry(BatchGroupLookupStatus.Truncated, gid, TruncatedSize: sz));
                continue;
            }

            results.Add(new BatchGroupMetadataEntry(BatchGroupLookupStatus.Found, gid, ParseGroupNode(g)));
        }
        return results;
    }

    public static Node BuildGetGroupProfilePictures(string iqId, IReadOnlyList<(Jid GroupJid, GroupPictureType PictureType)> groups)
    {
        if (groups.Count > BatchProfilePicturesLimit)
        {
            throw new ArgumentException($"Batch picture query exceeds {BatchProfilePicturesLimit} groups.", nameof(groups));
        }

        var children = groups.Select(g => new Node(
            "group",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = g.GroupJid.ToNonAd() },
            new[]
            {
                new Node("picture", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
                {
                    ["query"] = "url",
                    ["type"] = g.PictureType == GroupPictureType.Image ? "image" : "preview"
                })
            })).ToArray();

        return StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerGroup, new Node("pictures", null, children));
    }

    public static IReadOnlyList<GroupProfilePictureEntry> ParseGetGroupProfilePicturesResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node container = responseNode.Tag == "pictures"
            ? responseNode
            : responseNode.GetChild("pictures")
                ?? throw new InvalidDataException("Missing <pictures> child.");

        var list = new List<GroupProfilePictureEntry>();
        foreach (Node g in container.GetChildren("group"))
        {
            Jid gid = ParseGroupId(g.GetAttr("jid") ?? g.GetAttr("id") ?? throw new InvalidDataException("<group> missing jid/id."));
            Node? pic = g.GetChild("picture");
            GroupPictureType pt = string.Equals(pic?.GetAttr("type"), "image", StringComparison.OrdinalIgnoreCase)
                ? GroupPictureType.Image
                : GroupPictureType.Preview;
            string? error = g.GetAttr("error") ?? pic?.GetAttr("error");
            list.Add(new GroupProfilePictureEntry(gid, pt, pic?.GetAttr("id"), pic?.GetAttr("url"), pic?.GetAttr("direct_path"), error));
        }
        return list;
    }

    public static Node BuildReportMessages(string iqId, Jid groupJid, IReadOnlyList<string> messageIds)
    {
        var children = messageIds.Select(id =>
            new Node("report", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["message_id"] = id })).ToArray();
        return StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node("reports", null, children));
    }

    public static Node BuildGetReportedMessages(string iqId, Jid groupJid) =>
        StanzaBuilder.Iq(iqId, "get", Xmlns, groupJid.ToNonAd(), new Node("reports"));

    public static ReportedGroupMessagesResult ParseGetReportedMessagesResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        string? mode = responseNode.GetAttr("addressing_mode");
        Node reportsNode = responseNode.Tag == "reports"
            ? responseNode
            : responseNode.GetChild("reports")
                ?? throw new InvalidDataException("Missing <reports> child.");

        var reports = new List<ReportedGroupMessage>();
        foreach (Node rep in reportsNode.GetChildren("report"))
        {
            string msgId = rep.GetAttr("message_id") ?? throw new InvalidDataException("<report> missing 'message_id'.");
            var reporters = new List<GroupMessageReporter>();
            foreach (Node rr in rep.GetChildren("reporter"))
            {
                if (TryParseAttrJid(rr, "jid") is not Jid rJid)
                {
                    throw new InvalidDataException($"Reporter of {msgId} missing valid 'jid'.");
                }
                if (!ulong.TryParse(rr.GetAttr("timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong ts))
                {
                    throw new InvalidDataException($"Reporter of {msgId} missing valid 'timestamp'.");
                }
                reporters.Add(new GroupMessageReporter(rJid, ts, TryParseAttrJid(rr, "phone_number"), rr.GetAttr("username")));
            }
            reports.Add(new ReportedGroupMessage(msgId, reporters));
        }
        return new ReportedGroupMessagesResult(mode, reports);
    }

    public static GroupMetadata ParseGroupNode(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string idStr = node.GetAttr("id") ?? throw new InvalidDataException("Group node missing 'id' attribute.");
        Jid id = ParseGroupId(idStr);
        string addressingMode = node.GetAttr("addressing_mode") ?? "pn";

        var participants = new List<GroupParticipantInfo>();
        foreach (Node p in node.GetChildren("participant"))
        {
            if (p.GetAttr("jid") is not { Length: > 0 } pjStr || !Jid.TryParse(pjStr, out Jid pj))
            {
                throw new InvalidDataException("Group <participant> missing valid 'jid'.");
            }
            GroupParticipantDetails? details = ParseParticipantDetails(p);
            participants.Add(new GroupParticipantInfo(
                pj,
                TryParseAttrJid(p, "phone_number"),
                TryParseAttrJid(p, "lid"),
                p.GetAttr("username") ?? p.GetAttr("participant_username"),
                ParseParticipantRole(p.GetAttr("type")),
                details));
        }

        Node? descNode = node.GetChild("description");
        string? desc = descNode?.GetChild("body")?.Content switch
        {
            NodeContent.StringContent sc => sc.Text,
            NodeContent.BytesContent bc => System.Text.Encoding.UTF8.GetString(bc.Bytes),
            _ => descNode?.Content switch
            {
                NodeContent.StringContent sc => sc.Text,
                _ => null
            }
        };

        GroupEphemeralSettings? eph = null;
        if (node.GetChild("ephemeral") is { } ephNode)
        {
            eph = new GroupEphemeralSettings(
                ParseOptionalUInt(ephNode.GetAttr("expiration")),
                ParseOptionalUInt(ephNode.GetAttr("trigger")));
        }

        bool membershipApproval = string.Equals(
            node.GetChild("membership_approval_mode")?.GetChild("group_join")?.GetAttr("state"),
            "on",
            StringComparison.OrdinalIgnoreCase);

        Node? parentNode = node.GetChild("parent");
        bool isParentGroup = parentNode is not null || string.Equals(node.Tag, "community", StringComparison.OrdinalIgnoreCase);
        bool parentApproval = string.Equals(parentNode?.GetAttr("default_membership_approval_mode"), "request_required", StringComparison.OrdinalIgnoreCase);

        GrowthLockInfo? growthLocked = null;
        if (node.GetChild("growth_locked") is { } glNode)
        {
            growthLocked = new GrowthLockInfo(
                glNode.GetAttr("type") ?? string.Empty,
                ParseOptionalULong(glNode.GetAttr("expiration")) ?? 0);
        }

        Node? suspendedNode = node.GetChild("suspended");
        Node? limitNode = node.GetChild("limit_sharing_enabled");

        return new GroupMetadata(
            Id: id,
            Subject: node.GetAttr("subject"),
            AddressingMode: addressingMode,
            Participants: participants,
            Notify: node.GetAttr("notify"),
            Creator: TryParseAttrJid(node, "creator") ?? TryParseAttrJid(node, "owner"),
            CreatorPn: TryParseAttrJid(node, "creator_pn"),
            CreatorUsername: node.GetAttr("creator_username"),
            CreatorCountryCode: node.GetAttr("creator_country_code"),
            CreationTime: ParseOptionalULong(node.GetAttr("creation")),
            ParticipantVersionId: node.GetAttr("p_v_id"),
            AdminVersionId: node.GetAttr("a_v_id"),
            OpenThreadId: node.GetAttr("open_thread_id"),
            HasMissingParticipantIdentification: node.GetChild("missing_participant_identification") is not null,
            SubjectTime: ParseOptionalULong(node.GetAttr("s_t")),
            SubjectOwner: TryParseAttrJid(node, "s_o"),
            SubjectOwnerPn: TryParseAttrJid(node, "s_o_pn"),
            SubjectOwnerUsername: node.GetAttr("s_o_username"),
            Description: desc,
            DescriptionId: descNode?.GetAttr("id"),
            DescriptionOwner: descNode is not null ? TryParseAttrJid(descNode, "participant") : null,
            DescriptionOwnerPn: descNode is not null ? TryParseAttrJid(descNode, "participant_pn") : null,
            DescriptionOwnerUsername: descNode?.GetAttr("participant_username"),
            DescriptionTime: ParseOptionalULong(descNode?.GetAttr("t")),
            IsLocked: node.GetChild("locked") is not null || node.GetAttr("restrict") is "true" or "1",
            IsAnnouncement: node.GetChild("announcement") is not null || node.GetAttr("announce") is "true" or "1",
            Ephemeral: eph,
            MembershipApproval: membershipApproval,
            MemberAddMode: ParseMemberAddMode(ReadChildText(node, "member_add_mode")),
            MemberLinkMode: ParseMemberLinkMode(ReadChildText(node, "member_link_mode")),
            Size: ParseOptionalUInt(node.GetAttr("size")),
            IsParentGroup: isParentGroup,
            ParentMembershipApprovalRequired: parentApproval,
            ParentGroupJid: TryParseAttrJid(node.GetChild("linked_parent"), "jid"),
            IsDefaultSubGroup: node.GetChild("default_sub_group") is not null,
            IsGeneralChat: node.GetChild("general_chat") is not null,
            AllowNonAdminSubGroupCreation: node.GetChild("allow_non_admin_sub_group_creation") is not null,
            NoFrequentlyForwarded: node.GetChild("no_frequently_forwarded") is not null,
            ShareHistoryMode: ParseShareHistoryMode(ReadChildText(node, "member_share_group_history_mode")),
            GrowthLocked: growthLocked,
            IsSuspended: suspendedNode is not null,
            SuspensionCanAutoFile: string.Equals(suspendedNode?.GetAttr("can_auto_file"), "true", StringComparison.OrdinalIgnoreCase),
            AppealStatus: ParseAppealStatus(node.GetChild("appeal_status")?.GetAttr("type")),
            AppealUpdateTime: ParseOptionalULong(node.GetChild("appeal_update_time")?.GetAttr("value")),
            IsSupportGroup: node.GetChild("support") is not null,
            AllowAdminReports: node.GetChild("allow_admin_reports") is not null,
            IsHiddenGroup: node.GetChild("hidden_group") is not null,
            IsIncognito: node.GetChild("incognito") is not null,
            HasGroupHistory: node.GetChild("group_history") is not null,
            IsAutoAddDisabled: node.GetChild("auto_add_disabled") is not null,
            HasCapi: node.GetChild("capi") is not null,
            EvolutionVersion: ParseOptionalUInt(node.GetChild("evolution_version")?.GetAttr("value")),
            HasGroupSafetyCheck: node.GetChild("group_safety_check") is not null,
            ParticipantLabelEnabled: node.GetChild("participant_label_enabled") is not null,
            IsLimitSharingEnabled: limitNode is not null,
            LimitSharingTrigger: ParseOptionalUInt(limitNode?.GetAttr("trigger")));
    }

    private static GroupParticipantDetails? ParseParticipantDetails(Node p)
    {
        string? label = p.GetAttr("participant_label");
        ulong? labelMtime = ParseOptionalULong(p.GetAttr("participant_label_mtime"));
        ulong? joinTime = ParseOptionalULong(p.GetAttr("join_time"));
        bool? histSent = ParseOptionalBool(p.GetAttr("group_history_sent"));
        string? displayName = p.GetAttr("display_name");
        bool isAddressable = ParseOptionalBool(p.GetAttr("addressable")) ?? true;

        if (label is null && labelMtime is null && joinTime is null && histSent is null && displayName is null && isAddressable)
        {
            return null;
        }
        return new GroupParticipantDetails(label, labelMtime, joinTime, histSent, displayName, isAddressable);
    }

    private static void CollectGroupNodes(
        Node responseNode,
        string containerTag,
        string childTag,
        bool isCommunity,
        List<GroupMetadata> dest)
    {
        Node? container = responseNode.Tag == containerTag
            ? responseNode
            : responseNode.GetChild(containerTag);

        IEnumerable<Node> items = container is not null
            ? container.GetChildren(childTag)
            : (string.Equals(childTag, "group", StringComparison.Ordinal) ? responseNode.GetChildren("group") : responseNode.GetChildren("community"));

        foreach (Node g in items)
        {
            GroupMetadata parsed = ParseGroupNode(g);
            if (isCommunity && !parsed.IsParentGroup)
            {
                parsed = parsed with { IsParentGroup = true };
            }
            dest.Add(parsed);
        }
    }

    private static Node BuildParticipantActionIq(string iqId, Jid groupJid, string action, IReadOnlyList<Jid> participants, bool includeLinkedGroups)
    {
        var pNodes = participants.Select(j => new Node("participant", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = j.ToNonAd() })).ToArray();
        var attrs = includeLinkedGroups
            ? new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["linked_groups"] = "true" }
            : null;
        return StanzaBuilder.Iq(iqId, "set", Xmlns, groupJid.ToNonAd(), new Node(action, attrs, pNodes));
    }

    private static string GenerateDescriptionId()
    {
        Span<byte> buf = stackalloc byte[4];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf);
    }

    public static Jid ParseGroupId(string raw) =>
        raw.Contains('@', StringComparison.Ordinal)
            ? Jid.Parse(raw)
            : new Jid(raw, Server.Group);

    private static Jid? TryParseAttrJid(Node? node, string attr) =>
        node?.GetAttr(attr) is { Length: > 0 } s && Jid.TryParse(s, out Jid j) ? j : null;

    private static string? ReadChildText(Node parent, string childTag) =>
        parent.GetChild(childTag)?.Content switch
        {
            NodeContent.StringContent sc => sc.Text,
            NodeContent.BytesContent bc => System.Text.Encoding.UTF8.GetString(bc.Bytes),
            _ => null
        };

    private static uint? ParseOptionalUInt(string? raw) =>
        uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v) ? v : null;

    private static ulong? ParseOptionalULong(string? raw) =>
        ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong v) ? v : null;

    private static bool? ParseOptionalBool(string? raw) => raw?.ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null
    };
}
