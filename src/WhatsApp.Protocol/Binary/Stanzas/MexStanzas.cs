using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhatsApp.Protocol.Binary;

namespace WhatsApp.Protocol.Binary.Stanzas;

/// <summary>
/// Persisted GraphQL document descriptor for WhatsApp's MEX (<c>xmlns="w:mex"</c>) protocol.
/// </summary>
public sealed record MexDoc(string Name, string Id);

public sealed record MexGraphQLErrorExtensions(
    int? ErrorCode,
    bool? IsSummary,
    bool? IsRetryable,
    string? Severity);

public sealed record MexGraphQLError(
    string Message,
    MexGraphQLErrorExtensions? Extensions);

public sealed record MexExecutionResponse(
    JsonElement? Data,
    IReadOnlyList<MexGraphQLError> Errors,
    string RawJson)
{
    public bool HasFatalErrors
    {
        get
        {
            if (Errors.Count == 0)
            {
                return false;
            }

            if (Data is not { ValueKind: JsonValueKind.Object })
            {
                return true;
            }

            foreach (MexGraphQLError err in Errors)
            {
                if (err.Extensions?.ErrorCode is not null)
                {
                    return true;
                }
            }

            return true;
        }
    }
}

/// <summary>
/// MEX (Meta Exchange) GraphQL IQ stanza builder and parser (<c>xmlns="w:mex"</c>).
/// Matches <c>wacore/src/iq/mex.rs</c> and live <c>web.whatsapp.com</c> persisted GraphQL doc IDs.
/// </summary>
public static class MexStanzas
{
    public const string Xmlns = "w:mex";

    /// <summary>Live <c>WAWebMexUpdateGroupPropertyJobMutation</c> persisted query ID.</summary>
    public static readonly MexDoc UpdateGroupProperty = new(
        "WAWebMexUpdateGroupPropertyJobMutation",
        "9418211574894172");

    /// <summary>Live <c>WAWebMexFetchGroupInfoJobQuery</c> persisted query ID.</summary>
    public static readonly MexDoc FetchGroupInfo = new(
        "WAWebMexFetchGroupInfoJobQuery",
        "28997152336593340");

    /// <summary>Live <c>WAWebMexFetchAllSubgroupsJobQuery</c> persisted query ID.</summary>
    public static readonly MexDoc FetchAllSubgroups = new(
        "WAWebMexFetchAllSubgroupsJobQuery",
        "9935467776504344");

    /// <summary>Live <c>WAWebMexQuerySubgroupParticipantCountJobQuery</c> persisted query ID.</summary>
    public static readonly MexDoc QuerySubgroupParticipantCount = new(
        "WAWebMexQuerySubgroupParticipantCountJobQuery",
        "24079399904996141");

    /// <summary>Live <c>WAWebMexFetchSubgroupSuggestionsJobQuery</c> persisted query ID.</summary>
    public static readonly MexDoc FetchSubgroupSuggestions = new(
        "WAWebMexFetchSubgroupSuggestionsJobQuery",
        "23972005349071865");

    /// <summary>Live <c>WAWebMexTransferCommunityOwnershipJobMutation</c> persisted query ID.</summary>
    public static readonly MexDoc TransferCommunityOwnership = new(
        "WAWebMexTransferCommunityOwnershipJobMutation",
        "29643783178598899");

    /// <summary>Live <c>WAWebMexUpdateTextStatusJobMutation</c> persisted query ID.</summary>
    public static readonly MexDoc UpdateTextStatus = new(
        "WAWebMexUpdateTextStatusJobMutation",
        "9152604461510864");

    // ─── Phase 9A: Newsletter (Channel) Persisted Queries & Mutations ───
    public static readonly MexDoc FetchSubscribedNewsletters = new(
        "WAWebMexFetchAllNewslettersMetadataJobQuery",
        "25399611239711790");

    public static readonly MexDoc FetchNewsletter = new(
        "WAWebMexFetchNewsletterJobQuery",
        "27456920720571478");

    public static readonly MexDoc FetchNewsletterDehydrated = new(
        "WAWebMexFetchNewsletterDehydratedJobQuery",
        "26944199458535748");

    public static readonly MexDoc CreateNewsletter = new(
        "WAWebMexCreateNewsletterJobMutation",
        "25149874324715067");

    public static readonly MexDoc JoinNewsletter = new(
        "WAWebMexJoinNewsletterJobMutation",
        "24404358912487870");

    public static readonly MexDoc LeaveNewsletter = new(
        "WAWebMexLeaveNewsletterJobMutation",
        "9767147403369991");

    public static readonly MexDoc UpdateNewsletter = new(
        "WAWebMexUpdateNewsletterJobMutation",
        "24250201037901610");

    public static readonly MexDoc DeleteNewsletter = new(
        "WAWebMexDeleteNewsletterJobMutation",
        "30062808666639665");

    public static readonly MexDoc ChangeNewsletterOwner = new(
        "WAWebMexChangeNewsletterOwnerJobMutation",
        "9546742745432473");

    public static readonly MexDoc DemoteNewsletterAdmin = new(
        "WAWebMexDemoteNewsletterAdminJobMutation",
        "9880997548630971");

    public static readonly MexDoc FetchNewsletterAdminInfo = new(
        "WAWebMexFetchNewsletterAdminInfoJobQuery",
        "26278439461859188");

    public static readonly MexDoc FetchNewsletterAdminCapabilities = new(
        "WAWebMexFetchNewsletterAdminCapabilitiesJobQuery",
        "9801384413216421");

    public static readonly MexDoc CreateNewsletterAdminInvite = new(
        "WAWebMexCreateNewsletterAdminInviteJobMutation",
        "9387141988078609");

    public static readonly MexDoc AcceptNewsletterAdminInvite = new(
        "WAWebMexAcceptNewsletterAdminInviteJobMutation",
        "9580828702035549");

    public static readonly MexDoc RevokeNewsletterAdminInvite = new(
        "WAWebMexRevokeNewsletterAdminInviteJobMutation",
        "9656078347839416");

    public static readonly MexDoc FetchNewsletterFollowers = new(
        "WAWebMexFetchNewsletterFollowersJobQuery",
        "27472091235714801");

    public static readonly MexDoc UpdateNewsletterUserSetting = new(
        "WAWebMexUpdateNewsletterUserSettingJobMutation",
        "31938993655691868");

    public static readonly MexDoc UpdateNewsletterAdminProfile = new(
        "WAWebMexUpdateNewsletterAdminProfileJobMutation",
        "27871895895845117");

    public static readonly MexDoc UpdateNewsletterAdminProfileSetting = new(
        "WAWebMexUpdateNewsletterAdminProfileSettingJobMutation",
        "28226671310350649");

    public static readonly MexDoc FetchNewsletterDirectoryCategoriesPreview = new(
        "WAWebMexFetchNewsletterDirectoryCategoriesPreviewJobQuery",
        "35266481849605779");

    public static readonly MexDoc FetchNewsletterDirectoryList = new(
        "WAWebMexFetchNewsletterDirectoryListJobQuery",
        "26125047313831973");

    public static readonly MexDoc FetchNewsletterDirectorySearchResults = new(
        "WAWebMexFetchNewsletterDirectorySearchResultsJobQuery",
        "26301059626252132");

    public static readonly MexDoc FetchRecommendedNewsletters = new(
        "WAWebMexFetchRecommendedNewslettersJobQuery",
        "25806748772361516");

    public static readonly MexDoc FetchSimilarNewsletters = new(
        "WAWebMexFetchSimilarNewslettersJobQuery",
        "26217043484590756");

    public static readonly MexDoc NewsletterPinMessages = new(
        "WAWebMexNewsletterPinMessagesJobMutation",
        "27165709459706559");

    public static readonly MexDoc NewsletterUnpinMessages = new(
        "WAWebMexNewsletterUnpinMessagesJobMutation",
        "28007176042216937");

    public static readonly MexDoc NewsletterAddPaidPartnershipLabel = new(
        "WAWebMexNewsletterAddPaidPartnershipLabelJobMutation",
        "26102375079404865");

    public static readonly MexDoc NewsletterLabelAiContent = new(
        "WAWebMexNewsletterLabelAiContentJobMutation",
        "27909718265289596");

    // ─── Phase 9B: Business Suite, Catalogs & Orders ─────────────────────
    public static readonly MexDoc BizQueryCatalog = new(
        "WAWebQueryCatalogQuery",
        "29516483177936279");

    public static readonly MexDoc BizQueryOwnerCatalog = new(
        "WAWebBizCatalogManagementFetchCatalogQuery",
        "9957894520961099");

    public static readonly MexDoc BizQueryCollections = new(
        "WAWebQueryProductCollectionsQuery",
        "29398139243143451");

    public static readonly MexDoc BizQueryOwnerCollections = new(
        "WAWebBizCatalogManagementFetchCollectionsQuery",
        "9687699931342731");

    public static readonly MexDoc BizQueryCatalogProduct = new(
        "WAWebQueryCatalogProductQuery",
        "28846496438335971");

    public static readonly MexDoc BizQueryOrder = new(
        "WAWebBizQueryOrderJobQuery",
        "26593811266898374");

    public static readonly MexDoc BizCreateOrder = new(
        "WAWebBizCreateOrderJobMutation",
        "26486627094287046");

    public static readonly MexDoc EditBizProfile = new(
        "WAWebEditBizProfileMutation",
        "26652989367627867");

    // ─── Phase 9C: Meta AI & Bots ────────────────────────────────────────
    public static readonly MexDoc FetchBotProfilesGql = new(
        "WAWebFetchBotProfilesGQLQuery",
        "26368585139502858");

    public static readonly MexDoc FetchBotCertificateRevocationList = new(
        "WAWebMexFetchBotCertificateRevocationListQuery",
        "35807917542188393");

    public static readonly MexDoc FetchDynamicAiModes = new(
        "WAWebFetchDynamicAIModesQuery",
        "25335662402775799");

    public static readonly MexDoc FetchGroupInfoIncludBots = new(
        "WAWebMexFetchGroupInfoIncludBotsJobQuery",
        "28713479051667101");

    public static readonly MexDoc AiAgentAutoReplyControl = new(
        "WAWebAiAgentAutoReplyControlMutation",
        "27338647792432014");

    public static Node BuildQuery(string iqId, MexDoc doc, object variables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(variables);

        string payloadJson = variables is string rawJson
            ? rawJson
            : JsonSerializer.Serialize(new { variables });

        var queryNode = new Node(
            "query",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["query_id"] = doc.Id
            },
            payloadJson);

        return StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn, queryNode);
    }

    public static MexExecutionResponse ParseResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);

        Node resultNode = responseNode.Tag == "result"
            ? responseNode
            : responseNode.GetChild("result")
                ?? throw new InvalidDataException("Missing <result> child in MEX response.");

        string rawJson = resultNode.Content switch
        {
            NodeContent.StringContent sc => sc.Text,
            NodeContent.BytesContent bc => Encoding.UTF8.GetString(bc.Bytes),
            _ => throw new InvalidDataException("MEX <result> node has no text or byte payload.")
        };

        using JsonDocument doc = JsonDocument.Parse(rawJson);
        JsonElement root = doc.RootElement;

        JsonElement? data = null;
        if (root.TryGetProperty("data", out JsonElement d) && d.ValueKind != JsonValueKind.Null && d.ValueKind != JsonValueKind.Undefined)
        {
            data = d.Clone();
        }

        var errors = new List<MexGraphQLError>();
        if (root.TryGetProperty("errors", out JsonElement errsEl) && errsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement errEl in errsEl.EnumerateArray())
            {
                string msg = errEl.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? string.Empty
                    : string.Empty;

                MexGraphQLErrorExtensions? ext = null;
                if (errEl.TryGetProperty("extensions", out JsonElement extEl) && extEl.ValueKind == JsonValueKind.Object)
                {
                    int? errorCode = extEl.TryGetProperty("error_code", out JsonElement ec) && ec.TryGetInt32(out int eci) ? eci : null;
                    bool? isSummary = extEl.TryGetProperty("is_summary", out JsonElement isSum) && isSum.ValueKind is JsonValueKind.True or JsonValueKind.False ? isSum.GetBoolean() : null;
                    bool? isRetryable = extEl.TryGetProperty("is_retryable", out JsonElement isRet) && isRet.ValueKind is JsonValueKind.True or JsonValueKind.False ? isRet.GetBoolean() : null;
                    string? severity = extEl.TryGetProperty("severity", out JsonElement sev) && sev.ValueKind == JsonValueKind.String ? sev.GetString() : null;
                    ext = new MexGraphQLErrorExtensions(errorCode, isSummary, isRetryable, severity);
                }

                errors.Add(new MexGraphQLError(msg, ext));
            }
        }

        return new MexExecutionResponse(data, errors, rawJson);
    }

    public static object BuildUpdateMemberLinkModeVariables(Jid groupJid, MemberLinkMode mode) =>
        new
        {
            group_id = groupJid.ToNonAdString(),
            update = new
            {
                member_link_mode = mode == MemberLinkMode.AllMemberLink ? "ALL_MEMBER_LINK" : "ADMIN_LINK"
            }
        };

    public static object BuildUpdateMemberShareHistoryModeVariables(Jid groupJid, MemberShareHistoryMode mode) =>
        new
        {
            group_id = groupJid.ToNonAdString(),
            update = new
            {
                member_share_group_history_mode = mode == MemberShareHistoryMode.AllMemberShare
                    ? "ALL_MEMBER_SHARE"
                    : "ADMIN_SHARE"
            }
        };

    public static object BuildUpdateLimitSharingVariables(Jid groupJid, bool enabled, string trigger = "CHAT_SETTING") =>
        new
        {
            group_id = groupJid.ToNonAdString(),
            update = new
            {
                limit_sharing = new
                {
                    limit_sharing_enabled = enabled,
                    limit_sharing_trigger = trigger
                }
            }
        };

    public static object BuildFetchAllSubgroupsVariables(Jid communityJid, string queryContext = "INTERACTIVE", string? subGroupHintId = null) =>
        subGroupHintId is null
            ? new { group_id = communityJid.ToNonAdString(), query_context = queryContext }
            : new { group_id = communityJid.ToNonAdString(), query_context = queryContext, sub_group_hint_id = subGroupHintId };

    public static object BuildQuerySubgroupParticipantCountVariables(Jid communityJid, string queryContext = "INTERACTIVE", string? subGroupHintId = null) =>
        new
        {
            input = subGroupHintId is null
                ? (object)new { group_jid = communityJid.ToNonAdString(), query_context = queryContext }
                : new { group_jid = communityJid.ToNonAdString(), query_context = queryContext, sub_group_jid_hint = subGroupHintId }
        };

    public static object BuildFetchSubgroupSuggestionsVariables(Jid communityJid, string queryContext = "INTERACTIVE") =>
        new
        {
            group_id = communityJid.ToNonAdString(),
            query_context = queryContext
        };
}
