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
