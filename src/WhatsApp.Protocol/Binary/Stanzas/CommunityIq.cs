using System.Globalization;
using System.Text.Json;
using WhatsApp.Protocol.Binary;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum SubgroupVisibility
{
    Visible = 0,
    Hidden = 1
}

public sealed record LinkSubgroupOption(
    Jid GroupJid,
    SubgroupVisibility Visibility = SubgroupVisibility.Visible);

public sealed record CreateCommunityOptions(
    string Name,
    string? Description = null,
    bool Closed = true,
    bool AllowNonAdminSubGroupCreation = false,
    bool CreateGeneralChat = true)
{
    public void Validate()
    {
        if (string.IsNullOrEmpty(Name) || Name.Length > GroupManagementIq.SubjectMaxLength)
        {
            throw new ArgumentException($"Community name must be 1..{GroupManagementIq.SubjectMaxLength} characters.", nameof(Name));
        }
        if (Description is { Length: > GroupManagementIq.DescriptionMaxLength })
        {
            throw new ArgumentException($"Community description exceeds {GroupManagementIq.DescriptionMaxLength} characters.", nameof(Description));
        }
    }
}

public sealed record CreateSubgroupOptions(
    string Name,
    IReadOnlyList<Jid> Participants,
    Jid ParentJid,
    SubgroupVisibility Visibility = SubgroupVisibility.Visible);

public sealed record LinkedGroupEntryResult(Jid GroupJid, uint? ErrorCode = null)
{
    public bool IsOk => ErrorCode is null;
}

public sealed record CommunitySubgroupInfo(
    Jid Id,
    string Subject,
    uint? ParticipantCount = null,
    ulong? CreationTime = null,
    Jid? Owner = null,
    bool IsDefaultSubGroup = false,
    bool IsGeneralChat = false,
    bool IsHiddenGroup = false);

public sealed record SubgroupParticipantCountInfo(Jid SubgroupJid, uint TotalParticipantsCount);

public sealed record SubgroupSuggestionInfo(
    Jid GroupJid,
    string Subject,
    string? Description = null,
    ulong? CreationTime = null,
    Jid? Creator = null,
    bool IsExistingGroup = false,
    uint? TotalParticipantsCount = null);

/// <summary>
/// <c>xmlns="w:g2"</c> and <c>w:mex</c> Community protocol builders &amp; parsers (CAG, subgroups,
/// link/unlink, linked participant directory, suggestions) matching
/// <c>wacore/src/iq/{groups,community_create,mex_operations}.rs</c> and live <c>web.whatsapp.com</c>.
/// </summary>
public static class CommunityIq
{
    public const string SubgroupQueryContext = "INTERACTIVE";

    public static Node BuildCreateCommunity(string iqId, CreateCommunityOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var children = new List<Node>();
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

        var createNode = new Node(
            "create",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["subject"] = options.Name },
            children);

        return StanzaBuilder.Iq(iqId, "set", GroupManagementIq.Xmlns, StanzaBuilder.ServerGroup, createNode);
    }

    public static GroupMetadata ParseCreateCommunityResponse(Node responseNode, CreateCommunityOptions? requestOptions = null)
    {
        GroupMetadata info = GroupManagementIq.ParseCreateGroupResponse(responseNode);
        if (info.ParentGroupJid is null && requestOptions is not null)
        {
            info = info with
            {
                IsParentGroup = true,
                ParentMembershipApprovalRequired = info.ParentMembershipApprovalRequired || requestOptions.Closed,
                AllowNonAdminSubGroupCreation = info.AllowNonAdminSubGroupCreation || requestOptions.AllowNonAdminSubGroupCreation
            };
        }
        return info;
    }

    public static Node BuildDeactivateCommunity(string iqId, Jid communityJid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        return StanzaBuilder.Iq(iqId, "set", GroupManagementIq.Xmlns, communityJid.ToNonAd(), new Node("delete_parent"));
    }

    public static Node BuildLinkSubgroups(string iqId, Jid communityJid, IReadOnlyList<LinkSubgroupOption> subgroups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentNullException.ThrowIfNull(subgroups);

        var groupNodes = new Node[subgroups.Count];
        for (int i = 0; i < subgroups.Count; i++)
        {
            LinkSubgroupOption item = subgroups[i];
            var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = item.GroupJid.ToNonAd() };
            groupNodes[i] = item.Visibility == SubgroupVisibility.Hidden
                ? new Node("group", attrs, new[] { new Node("hidden_group") })
                : new Node("group", attrs);
        }

        var linkNode = new Node("link", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["link_type"] = "sub_group" }, groupNodes);
        var linksNode = new Node("links", null, new[] { linkNode });
        return StanzaBuilder.Iq(iqId, "set", GroupManagementIq.Xmlns, communityJid.ToNonAd(), linksNode);
    }

    public static IReadOnlyList<LinkedGroupEntryResult> ParseLinkSubgroupsResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node links = responseNode.Tag == "links"
            ? responseNode
            : responseNode.GetChild("links") ?? throw new InvalidDataException("Missing <links> child.");
        Node link = links.GetChild("link") ?? throw new InvalidDataException("Missing <link> child.");
        return ParseLinkedEntries(link);
    }

    public static Node BuildUnlinkSubgroups(
        string iqId,
        Jid communityJid,
        IReadOnlyList<Jid> subgroupJids,
        bool removeOrphanMembers = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentNullException.ThrowIfNull(subgroupJids);

        var groupNodes = new Node[subgroupJids.Count];
        for (int i = 0; i < subgroupJids.Count; i++)
        {
            var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = subgroupJids[i].ToNonAd() };
            if (removeOrphanMembers)
            {
                attrs["remove_orphaned_members"] = "true";
            }
            groupNodes[i] = new Node("group", attrs);
        }

        var unlinkNode = new Node("unlink", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["unlink_type"] = "sub_group" }, groupNodes);
        return StanzaBuilder.Iq(iqId, "set", GroupManagementIq.Xmlns, communityJid.ToNonAd(), unlinkNode);
    }

    public static IReadOnlyList<LinkedGroupEntryResult> ParseUnlinkSubgroupsResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node unlink = responseNode.Tag == "unlink"
            ? responseNode
            : responseNode.GetChild("unlink") ?? throw new InvalidDataException("Missing <unlink> child.");
        return ParseLinkedEntries(unlink);
    }

    public static Node BuildQueryLinkedGroup(string iqId, Jid communityJid, Jid subgroupJid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        var q = new Node("query_linked", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["type"] = "sub_group",
            ["jid"] = subgroupJid.ToNonAd()
        });
        return StanzaBuilder.Iq(iqId, "get", GroupManagementIq.Xmlns, communityJid.ToNonAd(), q);
    }

    public static GroupMetadata ParseQueryLinkedGroupResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node linked = responseNode.Tag == "linked_group"
            ? responseNode
            : responseNode.GetChild("linked_group") ?? throw new InvalidDataException("Missing <linked_group> child.");
        Node groupNode = linked.GetChild("group") ?? throw new InvalidDataException("Missing <group> inside <linked_group>.");
        return GroupManagementIq.ParseGroupNode(groupNode);
    }

    public static Node BuildJoinLinkedGroup(string iqId, Jid communityJid, Jid subgroupJid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        var joinNode = new Node("join_linked_group", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["jid"] = subgroupJid.ToNonAd()
        });
        return StanzaBuilder.Iq(iqId, "set", GroupManagementIq.Xmlns, communityJid.ToNonAd(), joinNode);
    }

    public static JoinGroupOutcome ParseJoinLinkedGroupResponse(Node responseNode, Jid subgroupJid)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        if (responseNode.Content is null)
        {
            return new JoinGroupOutcome(JoinGroupOutcomeKind.Joined, subgroupJid.ToNonAd());
        }

        if (responseNode.GetChild("linked_group") is { } lg
            && lg.GetChild("group") is { } gNode
            && gNode.GetAttr("id") is { Length: > 0 } idStr)
        {
            return new JoinGroupOutcome(JoinGroupOutcomeKind.Joined, GroupManagementIq.ParseGroupId(idStr));
        }

        return GroupManagementIq.ParseJoinGroupResponse(responseNode, subgroupJid.ToNonAd());
    }

    public static Node BuildGetLinkedGroupsParticipants(string iqId, Jid communityJid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        return StanzaBuilder.Iq(iqId, "get", GroupManagementIq.Xmlns, communityJid.ToNonAd(), new Node("linked_groups_participants"));
    }

    public static IReadOnlyList<GroupParticipantInfo> ParseLinkedGroupsParticipantsResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node container = responseNode.Tag == "linked_groups_participants"
            ? responseNode
            : responseNode.GetChild("linked_groups_participants")
                ?? throw new InvalidDataException("Missing <linked_groups_participants> child.");

        var result = new List<GroupParticipantInfo>();
        CollectParticipantNodes(container, result);
        if (result.Count > 0)
        {
            return result;
        }

        foreach (Node groupNode in container.GetChildren("group"))
        {
            CollectParticipantNodes(groupNode, result);
        }
        return result;
    }

    public static IReadOnlyList<GroupMetadata> ParseParticipatingCommunitiesResponse(Node responseNode) =>
        GroupManagementIq.ParseParticipatingResponse(responseNode)
            .Where(g => g.IsParentGroup)
            .ToArray();

    public static IReadOnlyList<GroupOverview> ParseParticipatingCommunityOverviewsResponse(Node responseNode) =>
        ParseParticipatingCommunitiesResponse(responseNode)
            .Select(g => new GroupOverview(g.Id, g.Subject, g.IsParentGroup, g.IsDefaultSubGroup, g.IsGeneralChat, g.ParentGroupJid, g.Size))
            .ToArray();

    public static IReadOnlyList<CommunitySubgroupInfo> ParseSubgroupsMexResponse(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("xwa2_group_query_by_id", out JsonElement queryById)
            || queryById.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("MEX fetch_all_subgroups response missing 'xwa2_group_query_by_id'.");
        }

        var subgroups = new List<CommunitySubgroupInfo>();
        if (queryById.TryGetProperty("default_sub_group", out JsonElement defSub)
            && defSub.ValueKind == JsonValueKind.Object
            && TryParseSubgroupJsonNode(defSub, isDefault: true, out CommunitySubgroupInfo? defInfo))
        {
            subgroups.Add(defInfo!);
        }

        if (queryById.TryGetProperty("sub_groups", out JsonElement subGroups)
            && subGroups.TryGetProperty("edges", out JsonElement edges)
            && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement edge in edges.EnumerateArray())
            {
                if (edge.TryGetProperty("node", out JsonElement node)
                    && node.ValueKind == JsonValueKind.Object
                    && TryParseSubgroupJsonNode(node, isDefault: false, out CommunitySubgroupInfo? item))
                {
                    subgroups.Add(item!);
                }
            }
        }

        return subgroups;
    }

    public static IReadOnlyList<SubgroupParticipantCountInfo> ParseSubgroupParticipantCountsMexResponse(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("xwa2_group_query_by_id", out JsonElement queryById)
            || queryById.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("MEX query_subgroup_participant_count response missing 'xwa2_group_query_by_id'.");
        }

        var counts = new List<SubgroupParticipantCountInfo>();
        if (queryById.TryGetProperty("sub_groups", out JsonElement subGroups)
            && subGroups.TryGetProperty("edges", out JsonElement edges)
            && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement edge in edges.EnumerateArray())
            {
                if (!edge.TryGetProperty("node", out JsonElement node) || node.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                if (node.TryGetProperty("id", out JsonElement idEl)
                    && idEl.ValueKind == JsonValueKind.String
                    && Jid.TryParse(idEl.GetString()!, out Jid gid))
                {
                    uint count = TryReadJsonUInt(node, "total_participants_count")
                        ?? TryReadJsonUInt(node, "participants_count")
                        ?? 0;
                    counts.Add(new SubgroupParticipantCountInfo(gid, count));
                }
            }
        }
        return counts;
    }

    public static IReadOnlyList<SubgroupSuggestionInfo> ParseSubgroupSuggestionsMexResponse(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("xwa2_group_query_by_id", out JsonElement queryById)
            || queryById.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<SubgroupSuggestionInfo>();
        }

        var suggestions = new List<SubgroupSuggestionInfo>();
        if (queryById.TryGetProperty("sub_group_suggestions", out JsonElement sugObj)
            && sugObj.TryGetProperty("edges", out JsonElement edges)
            && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement edge in edges.EnumerateArray())
            {
                if (!edge.TryGetProperty("node", out JsonElement node) || node.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                if (!node.TryGetProperty("id", out JsonElement idEl)
                    || idEl.ValueKind != JsonValueKind.String
                    || !Jid.TryParse(idEl.GetString()!, out Jid sgJid))
                {
                    continue;
                }

                string subject = TryReadSubject(node) ?? string.Empty;
                string? desc = node.TryGetProperty("description", out JsonElement descEl) && descEl.ValueKind == JsonValueKind.Object
                    && descEl.TryGetProperty("value", out JsonElement dv) && dv.ValueKind == JsonValueKind.String
                    ? dv.GetString()
                    : null;
                ulong? created = TryReadJsonULong(node, "creation_time");
                Jid? creator = node.TryGetProperty("creator", out JsonElement crEl) && TryParseJsonJid(crEl, out Jid cr) ? cr : null;
                bool existing = node.TryGetProperty("is_existing_group", out JsonElement exEl) && exEl.ValueKind == JsonValueKind.True;
                uint? count = TryReadJsonUInt(node, "total_participants_count");

                suggestions.Add(new SubgroupSuggestionInfo(sgJid, subject, desc, created, creator, existing, count));
            }
        }
        return suggestions;
    }

    private static IReadOnlyList<LinkedGroupEntryResult> ParseLinkedEntries(Node parent)
    {
        var list = new List<LinkedGroupEntryResult>();
        foreach (Node g in parent.GetChildren("group"))
        {
            string jidStr = g.GetAttr("jid") ?? g.GetAttr("id") ?? throw new InvalidDataException("Linked <group> missing 'jid'.");
            Jid jid = GroupManagementIq.ParseGroupId(jidStr);
            uint? err = uint.TryParse(g.GetAttr("error"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint code) ? code : null;
            list.Add(new LinkedGroupEntryResult(jid, err));
        }
        return list;
    }

    private static void CollectParticipantNodes(Node container, List<GroupParticipantInfo> dest)
    {
        foreach (Node p in container.GetChildren("participant"))
        {
            if (p.GetAttr("jid") is { Length: > 0 } jStr && Jid.TryParse(jStr, out Jid jid))
            {
                Jid? pn = p.GetAttr("phone_number") is { Length: > 0 } pnStr && Jid.TryParse(pnStr, out Jid pnj) ? pnj : null;
                Jid? lid = p.GetAttr("lid") is { Length: > 0 } lidStr && Jid.TryParse(lidStr, out Jid lj) ? lj : null;
                dest.Add(new GroupParticipantInfo(
                    jid,
                    pn,
                    lid,
                    p.GetAttr("username") ?? p.GetAttr("participant_username"),
                    GroupManagementIq.ParseParticipantRole(p.GetAttr("type"))));
            }
        }
    }

    private static bool TryParseSubgroupJsonNode(JsonElement node, bool isDefault, out CommunitySubgroupInfo? info)
    {
        info = null;
        if (!node.TryGetProperty("id", out JsonElement idEl)
            || idEl.ValueKind != JsonValueKind.String
            || !Jid.TryParse(idEl.GetString()!, out Jid gid))
        {
            return false;
        }

        string subject = TryReadSubject(node) ?? string.Empty;
        uint? count = TryReadJsonUInt(node, "participants_count") ?? TryReadJsonUInt(node, "total_participants_count");
        ulong? creation = TryReadJsonULong(node, "creation") ?? TryReadJsonULong(node, "creation_time");
        if (creation is null && node.TryGetProperty("subject", out JsonElement subjObj) && subjObj.ValueKind == JsonValueKind.Object)
        {
            creation = TryReadJsonULong(subjObj, "creation_time");
        }

        Jid? owner = null;
        if (node.TryGetProperty("creator", out JsonElement cr) && TryParseJsonJid(cr, out Jid cj)) owner = cj;
        else if (node.TryGetProperty("owner", out JsonElement ow) && TryParseJsonJid(ow, out Jid oj)) owner = oj;

        bool isGeneral = false;
        bool isHidden = false;
        if (node.TryGetProperty("properties", out JsonElement props) && props.ValueKind == JsonValueKind.Object)
        {
            isGeneral = TryReadJsonBool(props, "general_chat") ?? false;
            isHidden = TryReadJsonBool(props, "hidden_group") ?? false;
        }

        info = new CommunitySubgroupInfo(gid, subject, count, creation, owner, isDefault, isGeneral, isHidden);
        return true;
    }

    private static string? TryReadSubject(JsonElement node)
    {
        if (!node.TryGetProperty("subject", out JsonElement subj)) return null;
        if (subj.ValueKind == JsonValueKind.String) return subj.GetString();
        if (subj.ValueKind == JsonValueKind.Object && subj.TryGetProperty("value", out JsonElement v) && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }
        return null;
    }

    private static bool TryParseJsonJid(JsonElement el, out Jid jid)
    {
        if (el.ValueKind == JsonValueKind.String && Jid.TryParse(el.GetString()!, out jid))
        {
            return true;
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in new[] { "id", "lid", "pn" })
            {
                if (el.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String && Jid.TryParse(v.GetString()!, out jid))
                {
                    return true;
                }
            }
        }
        jid = default;
        return false;
    }

    private static uint? TryReadJsonUInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out JsonElement el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out uint u)) return u;
        if (el.ValueKind == JsonValueKind.String && uint.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint us)) return us;
        return null;
    }

    private static ulong? TryReadJsonULong(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out JsonElement el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt64(out ulong u)) return u;
        if (el.ValueKind == JsonValueKind.String && ulong.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong us)) return us;
        return null;
    }

    private static bool? TryReadJsonBool(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out JsonElement el)) return null;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.String)
        {
            return el.GetString() switch
            {
                "true" or "1" => true,
                "false" or "0" => false,
                _ => null
            };
        }
        return null;
    }
}
