using System.Globalization;
using System.Text.Json;
using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum NewsletterVerification
{
    Unverified,
    Verified,
    Other
}

public enum NewsletterState
{
    Active,
    Suspended,
    Geosuspended,
    Deleted,
    NonExisting,
    Other
}

public enum NewsletterRole
{
    Owner,
    Admin,
    Subscriber,
    Guest
}

public enum NewsletterMessageKind
{
    Text,
    Media,
    Poll,
    Other
}

public sealed record NewsletterAdminProfile(
    string? Id,
    string Name,
    string? PictureId,
    string? PictureDirectPath);

public sealed record NewsletterMetadata(
    Jid Jid,
    string Name,
    string? Description,
    ulong SubscriberCount,
    NewsletterVerification Verification,
    string VerificationRaw,
    NewsletterState State,
    string StateRaw,
    string? PictureDirectPath,
    string? PreviewDirectPath,
    string? InviteCode,
    string? Handle,
    ulong? CreationTimeSeconds,
    NewsletterRole? Role,
    bool? Muted,
    bool? FollowerActivityMuted);

public sealed record NewsletterAdminInfo(
    uint? AdminCount,
    NewsletterAdminProfile? AdminProfile,
    bool? AdminProfilesEnabled);

public sealed record NewsletterFollower(
    Jid Jid,
    Jid? PhoneJid,
    string? DisplayName,
    string? Username,
    NewsletterRole? Role,
    ulong? FollowTimeSeconds,
    NewsletterAdminProfile? AdminProfile);

public sealed record NewsletterDirectoryEntry(
    Jid Jid,
    string Name,
    string? Description,
    ulong SubscriberCount,
    NewsletterVerification Verification,
    string? PictureDirectPath,
    string? InviteCode,
    string? Handle,
    ulong? CreationTimeSeconds,
    ulong? LastStatusSentTime,
    string? LastStatusServerId);

public sealed record NewsletterDirectoryCategoryPreview(
    string Category,
    string CategoryTitle,
    IReadOnlyList<NewsletterDirectoryEntry> Newsletters);

public sealed record NewsletterDirectoryPage(
    IReadOnlyList<NewsletterDirectoryEntry> Newsletters,
    string? StartCursor,
    string? EndCursor,
    bool HasNextPage,
    bool HasPreviousPage);

public sealed record NewsletterReactionCount(string Code, ulong Count);

public sealed record NewsletterPollVote(byte[] OptionHash32, ulong Count);

public sealed record NewsletterMessage(
    string? MessageId,
    ulong ServerId,
    ulong TimestampSeconds,
    NewsletterMessageKind MessageKind,
    string MessageTypeRaw,
    string? EditAttribute,
    bool IsSender,
    Message? Message,
    string? MediaType,
    IReadOnlyList<NewsletterReactionCount> Reactions,
    IReadOnlyList<NewsletterPollVote> Votes,
    ulong? ForwardsCount,
    ulong? ViewsCount,
    ulong? ResponsesCount,
    ulong? OriginalTimestampSeconds,
    ulong? LastEditTimestampMs,
    string? PollType,
    string? ContentType,
    string? QuestionType,
    string? MessageAssociationType,
    bool IsWamoSub,
    NewsletterAdminProfile? AdminProfile,
    byte[]? Rcat);

public sealed record NewsletterMyReaction(string Code, ulong TimestampSeconds);

public sealed record NewsletterMyPollVote(ulong TimestampSeconds, IReadOnlyList<byte[]> OptionHashes32);

public sealed record NewsletterMyAddOns(
    ulong ServerId,
    NewsletterMyReaction? Reaction,
    NewsletterMyPollVote? PollVote);

public sealed record NewsletterLiveUpdateMessage(
    ulong ServerId,
    IReadOnlyList<NewsletterReactionCount> Reactions,
    IReadOnlyList<NewsletterPollVote> Votes,
    ulong? ForwardsCount);

public sealed record ParsedNewsletterLiveUpdateNotification(
    Jid ChannelJid,
    IReadOnlyList<NewsletterLiveUpdateMessage> Messages,
    long TimestampSeconds);

/// <summary>
/// Newsletter (Channel) <c>xmlns="newsletter"</c> IQ builders/parsers, MEX variable builders
/// and JSON response decoders matching <c>wacore/src/iq/newsletter.rs</c> and
/// <c>src/features/newsletter.rs</c>.
/// </summary>
public static class NewsletterStanzas
{
    public const string Xmlns = "newsletter";
    public const string EditAttrAdminEdit = "3";
    public const string EditAttrAdminRevoke = "8";
    public const ulong DefaultLiveUpdateDurationSeconds = 300;
    public const int MaxPollVoteOptions = 1000;

    // ─── Standard xmlns="newsletter" IQs ──────────────────────────────────

    /// <summary>
    /// Builds `<iq to="s.whatsapp.net" type="get" xmlns="newsletter"><messages type="jid" jid="..." count="..." [before="..."]/></iq>`.
    /// </summary>
    public static Node BuildGetMessagesIq(string iqId, Jid channelJid, uint count, ulong? beforeServerId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        EnsureNewsletterJid(channelJid);

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["type"] = "jid",
            ["jid"] = channelJid.ToNonAd(),
            ["count"] = count.ToString(CultureInfo.InvariantCulture)
        };
        if (beforeServerId is ulong before)
        {
            attrs["before"] = before.ToString(CultureInfo.InvariantCulture);
        }

        return StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn, new Node("messages", attrs));
    }

    public static IReadOnlyList<NewsletterMessage> ParseMessagesResponse(Node response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Node messagesNode = response.GetChild("messages")
            ?? throw new InvalidDataException("Missing <messages> in newsletter response.");

        var list = new List<NewsletterMessage>();
        foreach (Node msgNode in messagesNode.GetChildren("message"))
        {
            if (!TryParseUInt64(msgNode.GetAttr("server_id"), out ulong serverId))
            {
                continue;
            }

            string? msgId = msgNode.GetAttr("id");
            if (string.IsNullOrEmpty(msgId))
            {
                msgId = null;
            }

            _ = TryParseUInt64(msgNode.GetAttr("t"), out ulong ts);
            string typeRaw = msgNode.GetAttr("type") ?? "text";
            NewsletterMessageKind kind = typeRaw switch
            {
                "text" => NewsletterMessageKind.Text,
                "media" => NewsletterMessageKind.Media,
                "poll" => NewsletterMessageKind.Poll,
                _ => NewsletterMessageKind.Other
            };

            string? editAttr = msgNode.GetAttr("edit");
            bool isSender = string.Equals(msgNode.GetAttr("is_sender"), "true", StringComparison.OrdinalIgnoreCase);

            Node? ptNode = msgNode.GetChild("plaintext");
            Message? decoded = null;
            if (ptNode?.Content is NodeContent.BytesContent bc && bc.Bytes.Length > 0)
            {
                decoded = Message.Parser.ParseFrom(bc.Bytes);
            }
            string? mediaType = ptNode?.GetAttr("mediatype");

            Node? metaNode = msgNode.GetChild("meta");
            ulong? originalTs = TryParseOptionalUInt64(metaNode?.GetAttr("original_msg_t"));
            ulong? editTsMs = TryParseOptionalUInt64(metaNode?.GetAttr("msg_edit_t"));
            string? pollType = kind == NewsletterMessageKind.Poll ? metaNode?.GetAttr("polltype") : metaNode?.GetAttr("polltype");
            string? contentType = metaNode?.GetAttr("contenttype");
            string? questionType = metaNode?.GetAttr("questiontype");
            string? assocType = metaNode?.GetAttr("message_association_type");
            bool isWamoSub = string.Equals(metaNode?.GetAttr("is_wamo_sub"), "true", StringComparison.OrdinalIgnoreCase);
            NewsletterAdminProfile? adminProfile = metaNode?.GetChild("admin_profile") is { } apNode
                ? ParseAdminProfileNode(apNode)
                : null;

            byte[]? rcat = msgNode.GetChild("rcat")?.Content switch
            {
                NodeContent.BytesContent rbc => rbc.Bytes,
                _ => null
            };

            list.Add(new NewsletterMessage(
                msgId,
                serverId,
                ts,
                kind,
                typeRaw,
                editAttr,
                isSender,
                decoded,
                mediaType,
                ParseReactionCounts(msgNode),
                ParsePollVotes(msgNode),
                ParseCountChild(msgNode, "forwards_count"),
                ParseCountChild(msgNode, "views_count"),
                ParseCountChild(msgNode, "responses_count"),
                originalTs,
                editTsMs,
                pollType,
                contentType,
                questionType,
                assocType,
                isWamoSub,
                adminProfile,
                rcat));
        }

        return list;
    }

    public static Node BuildGetMyAddOnsIq(string iqId, Jid channelJid, uint limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        EnsureNewsletterJid(channelJid);

        var myAddOns = new Node(
            "my_addons",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
                ["jid"] = channelJid.ToNonAd()
            });
        return StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn, myAddOns);
    }

    public static IReadOnlyList<NewsletterMyAddOns> ParseMyAddOnsResponse(Node response, Jid expectedChannelJid)
    {
        ArgumentNullException.ThrowIfNull(response);
        EnsureNewsletterJid(expectedChannelJid);

        Node root = response.GetChild("my_addons")
            ?? throw new InvalidDataException("Missing <my_addons> child in response.");

        var result = new List<NewsletterMyAddOns>();
        Jid targetNorm = expectedChannelJid.ToNonAd();
        foreach (Node group in root.GetChildren("messages"))
        {
            string? jidAttr = group.GetAttr("jid");
            if (string.IsNullOrEmpty(jidAttr) || !Jid.TryParse(jidAttr, out Jid groupJid))
            {
                throw new InvalidDataException("Newsletter <my_addons>/<messages> missing or invalid jid attribute.");
            }
            if (groupJid.ToNonAd() != targetNorm)
            {
                continue;
            }

            foreach (Node msgNode in group.GetChildren("message"))
            {
                if (!TryParseUInt64(msgNode.GetAttr("server_id"), out ulong serverId))
                {
                    throw new InvalidDataException("<message> in <my_addons> missing or invalid server_id.");
                }

                NewsletterMyReaction? rx = null;
                if (msgNode.GetChild("reaction") is { } rxNode)
                {
                    string code = rxNode.GetAttr("code")
                        ?? throw new InvalidDataException("<reaction> in <my_addons> missing code.");
                    if (!TryParseUInt64(rxNode.GetAttr("t"), out ulong rxTs))
                    {
                        throw new InvalidDataException("<reaction> in <my_addons> missing or invalid t.");
                    }
                    rx = new NewsletterMyReaction(code, rxTs);
                }

                NewsletterMyPollVote? vote = null;
                if (msgNode.GetChild("votes") is { } votesNode)
                {
                    if (!TryParseUInt64(votesNode.GetAttr("t"), out ulong voteTs))
                    {
                        throw new InvalidDataException("<votes> in <my_addons> missing or invalid t.");
                    }
                    var hashes = new List<byte[]>();
                    foreach (Node v in votesNode.GetChildren("vote"))
                    {
                        if (v.Content is not NodeContent.BytesContent bc || bc.Bytes.Length != 32)
                        {
                            throw new InvalidDataException("<vote> in <my_addons> is not a 32-byte option hash.");
                        }
                        hashes.Add(bc.Bytes);
                    }
                    vote = new NewsletterMyPollVote(voteTs, hashes);
                }

                result.Add(new NewsletterMyAddOns(serverId, rx, vote));
            }
        }

        return result;
    }

    public static Node BuildSubscribeLiveUpdatesIq(string iqId, Jid channelJid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        EnsureNewsletterJid(channelJid);

        return StanzaBuilder.Iq(iqId, "set", Xmlns, channelJid.ToNonAd(), new Node("live_updates"));
    }

    public static ulong ParseLiveUpdatesDurationSeconds(Node response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Node? liveNode = response.Tag == "live_updates" ? response : response.GetChild("live_updates");
        return TryParseUInt64(liveNode?.GetAttr("duration"), out ulong seconds)
            ? seconds
            : DefaultLiveUpdateDurationSeconds;
    }

    public static ParsedNewsletterLiveUpdateNotification? ParseLiveUpdateNotification(
        Node notification,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.GetAttr("from") is not { Length: > 0 } fromStr
            || !Jid.TryParse(fromStr, out Jid channelJid)
            || !channelJid.IsNewsletter())
        {
            return null;
        }

        long ts = NotificationStanzas.ParseTimestampSeconds(notification, time);
        Node? liveUpdates = notification.GetChild("live_updates");
        Node? messagesNode = liveUpdates?.GetChild("messages");
        if (messagesNode is null)
        {
            return null;
        }

        var updates = new List<NewsletterLiveUpdateMessage>();
        foreach (Node msgNode in messagesNode.GetChildren("message"))
        {
            if (!TryParseUInt64(msgNode.GetAttr("server_id"), out ulong serverId))
            {
                continue;
            }
            updates.Add(new NewsletterLiveUpdateMessage(
                serverId,
                ParseReactionCounts(msgNode),
                ParsePollVotes(msgNode),
                ParseCountChild(msgNode, "forwards_count")));
        }

        return updates.Count == 0
            ? null
            : new ParsedNewsletterLiveUpdateNotification(channelJid.ToNonAd(), updates, ts);
    }

    public static IReadOnlyList<NewsletterReactionCount> ParseReactionCounts(Node msgNode)
    {
        Node? reactionsNode = msgNode.GetChild("reactions");
        if (reactionsNode is null)
        {
            return Array.Empty<NewsletterReactionCount>();
        }

        var list = new List<NewsletterReactionCount>();
        foreach (Node r in reactionsNode.GetChildren("reaction"))
        {
            string? code = r.GetAttr("code");
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }
            ulong count = TryParseUInt64(r.GetAttr("count"), out ulong c) ? c : 0;
            list.Add(new NewsletterReactionCount(code, count));
        }
        return list;
    }

    public static IReadOnlyList<NewsletterPollVote> ParsePollVotes(Node msgNode)
    {
        Node? votesNode = msgNode.GetChild("votes");
        if (votesNode is null)
        {
            return Array.Empty<NewsletterPollVote>();
        }

        var list = new List<NewsletterPollVote>();
        foreach (Node v in votesNode.GetChildren("vote"))
        {
            if (v.Content is not NodeContent.BytesContent bc || bc.Bytes.Length != 32)
            {
                continue;
            }
            if (!TryParseUInt64(v.GetAttr("count"), out ulong count))
            {
                continue;
            }
            list.Add(new NewsletterPollVote(bc.Bytes, count));
        }
        return list;
    }

    // ─── MEX Variables & Decoders ────────────────────────────────────────

    public static object BuildListSubscribedVariables(bool fetchStatusMetadata = false, bool fetchWamoSub = false) =>
        new { fetch_status_metadata = fetchStatusMetadata, fetch_wamo_sub = fetchWamoSub };

    public static object BuildFetchNewsletterVariables(
        string key,
        string keyType = "JID",
        string viewRole = "GUEST") =>
        new
        {
            input = new { key, type = keyType, view_role = viewRole },
            fetch_viewer_metadata = true,
            fetch_full_image = true,
            fetch_creation_time = true,
            fetch_pinned_messages = false,
            fetch_status_metadata = false,
            fetch_wamo_sub = false
        };

    public static object BuildCreateNewsletterVariables(string name, string? description = null, byte[]? pictureJpeg = null) =>
        new
        {
            input = new
            {
                name,
                description,
                picture = pictureJpeg is { Length: > 0 } ? Convert.ToBase64String(pictureJpeg) : null
            }
        };

    public static object BuildUpdateNewsletterVariables(
        Jid channelJid,
        string? name = null,
        string? description = null,
        byte[]? pictureJpeg = null,
        bool clearPicture = false) =>
        new
        {
            newsletter_id = channelJid.ToNonAdString(),
            updates = new
            {
                name,
                description,
                picture = clearPicture
                    ? string.Empty
                    : (pictureJpeg is { Length: > 0 } ? Convert.ToBase64String(pictureJpeg) : null)
            }
        };

    public static object BuildNewsletterIdVariables(Jid channelJid) =>
        new { newsletter_id = channelJid.ToNonAdString() };

    public static object BuildNewsletterUserActionVariables(Jid channelJid, Jid userLid) =>
        new { newsletter_id = channelJid.ToNonAdString(), user_id = userLid.ToNonAdString() };

    public static object BuildFetchFollowersVariables(Jid channelJid, uint count) =>
        new { input = new { newsletter_id = channelJid.ToNonAdString(), count } };

    public static object BuildUpdateUserSettingVariables(Jid channelJid, string settingType, bool enabled) =>
        new
        {
            input = new
            {
                newsletter_id = channelJid.ToNonAdString(),
                type = settingType,
                value = enabled ? "ON" : "OFF"
            }
        };

    public static object BuildPinMessagesVariables(Jid channelJid, IReadOnlyList<string> serverIds) =>
        new
        {
            newsletter_id = channelJid.ToNonAdString(),
            input = new { message_ids = serverIds }
        };

    public static object BuildDirectoryCategoriesPreviewVariables(
        IReadOnlyList<string>? categories = null,
        string? countryCode = null,
        int? perCategoryLimit = null,
        bool fetchStatusMetadata = false) =>
        new
        {
            fetch_status_metadata = fetchStatusMetadata,
            input = new
            {
                categories,
                country_code = countryCode,
                per_category_limit = perCategoryLimit?.ToString(CultureInfo.InvariantCulture)
            }
        };

    public static object BuildDirectoryListVariables(
        string? view = null,
        IReadOnlyList<string>? categories = null,
        string? countryCodes = null,
        int? limit = null,
        string? startCursor = null,
        bool fetchStatusMetadata = false) =>
        new
        {
            fetch_status_metadata = fetchStatusMetadata,
            input = new
            {
                view,
                limit,
                start_cursor = startCursor,
                filters = new
                {
                    categories = categories ?? Array.Empty<string>(),
                    country_codes = ToCountryCodesArray(countryCodes) ?? Array.Empty<string>()
                }
            }
        };

    public static object BuildDirectorySearchVariables(
        string searchText,
        IReadOnlyList<string>? categories = null,
        int? limit = null,
        string? startCursor = null,
        bool fetchStatusMetadata = false) =>
        new
        {
            fetch_status_metadata = fetchStatusMetadata,
            input = new
            {
                search_text = searchText,
                categories = categories ?? Array.Empty<string>(),
                limit,
                start_cursor = startCursor
            }
        };

    public static object BuildRecommendedNewslettersVariables(
        string? countryCodes = null,
        int? limit = null,
        bool fetchStatusMetadata = false) =>
        new
        {
            fetch_status_metadata = fetchStatusMetadata,
            input = new
            {
                country_codes = ToCountryCodesArray(countryCodes) ?? ["US"],
                limit
            }
        };

    public static object BuildSimilarNewslettersVariables(
        Jid channelJid,
        string? countryCodes = null,
        int? limit = null,
        bool fetchStatusMetadata = false) =>
        new
        {
            fetch_status_metadata = fetchStatusMetadata,
            input = new
            {
                newsletter_id = channelJid.ToNonAdString(),
                country_codes = ToCountryCodesArray(countryCodes) ?? Array.Empty<string>(),
                limit
            }
        };

    private static string[]? ToCountryCodesArray(string? countryCodes) =>
        string.IsNullOrWhiteSpace(countryCodes)
            ? null
            : countryCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static IReadOnlyList<NewsletterMetadata> ParseSubscribedNewsletters(JsonElement? data)
    {
        JsonElement root = RequireDataObject(data);
        if (!root.TryGetProperty("xwa2_newsletter_subscribed", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Missing xwa2_newsletter_subscribed array in MEX response.");
        }

        var list = new List<NewsletterMetadata>();
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (IsAbsentNewsletterNode(item))
            {
                continue;
            }
            list.Add(ParseNewsletterMetadata(item));
        }
        return list;
    }

    public static NewsletterMetadata? ParseSingleNewsletter(JsonElement? data, string fieldName)
    {
        JsonElement root = RequireDataObject(data);
        if (!root.TryGetProperty(fieldName, out JsonElement node))
        {
            throw new InvalidDataException($"Missing '{fieldName}' in MEX newsletter response.");
        }
        return IsAbsentNewsletterNode(node) ? null : ParseNewsletterMetadata(node);
    }

    public static NewsletterAdminInfo ParseAdminInfo(JsonElement? data)
    {
        JsonElement admin = RequireFieldObject(data, "xwa2_newsletter_admin");
        uint? adminCount = GetJsonUInt64(admin, "admin_count") is ulong ac && ac <= uint.MaxValue ? (uint)ac : null;
        NewsletterAdminProfile? profile = admin.TryGetProperty("admin_profile", out JsonElement ap) && ap.ValueKind == JsonValueKind.Object
            ? ParseAdminProfileJson(ap)
            : null;
        bool? enabled = admin.TryGetProperty("admin_settings", out JsonElement settings)
            && settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty("admin_profiles_enabled", out JsonElement ape)
            && ape.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ape.GetBoolean()
                : null;
        return new NewsletterAdminInfo(adminCount, profile, enabled);
    }

    public static IReadOnlyList<NewsletterFollower> ParseFollowers(JsonElement? data)
    {
        JsonElement followersObj = RequireFieldObject(data, "xwa2_newsletter_followers");
        if (!followersObj.TryGetProperty("followers", out JsonElement conn)
            || conn.ValueKind != JsonValueKind.Object
            || !conn.TryGetProperty("edges", out JsonElement edges)
            || edges.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<NewsletterFollower>();
        }

        var list = new List<NewsletterFollower>();
        foreach (JsonElement edge in edges.EnumerateArray())
        {
            if (!edge.TryGetProperty("node", out JsonElement node) || node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string? idStr = GetJsonString(node, "id");
            if (string.IsNullOrEmpty(idStr) || !Jid.TryParse(idStr, out Jid jid))
            {
                continue;
            }

            Jid? phoneJid = GetJsonString(node, "pn") is { Length: > 0 } pnStr && Jid.TryParse(pnStr, out Jid pn)
                ? pn
                : null;
            string? displayName = GetJsonString(node, "display_name");
            string? username = node.TryGetProperty("username_info", out JsonElement ui) && ui.ValueKind == JsonValueKind.Object
                ? GetJsonString(ui, "username")
                : null;
            NewsletterRole? role = ParseNewsletterRole(GetJsonString(edge, "role"));
            ulong? followTime = GetJsonUInt64(edge, "follow_time");
            NewsletterAdminProfile? ap = edge.TryGetProperty("admin_profile", out JsonElement apEl) && apEl.ValueKind == JsonValueKind.Object
                ? ParseAdminProfileJson(apEl)
                : null;

            list.Add(new NewsletterFollower(jid, phoneJid, displayName, username, role, followTime, ap));
        }
        return list;
    }

    public static IReadOnlyList<NewsletterDirectoryCategoryPreview> ParseDirectoryCategoriesPreview(JsonElement? data)
    {
        JsonElement container = RequireFieldObject(data, "xwa2_newsletters_directory_category_preview");
        if (!container.TryGetProperty("result", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<NewsletterDirectoryCategoryPreview>();
        }

        var list = new List<NewsletterDirectoryCategoryPreview>();
        foreach (JsonElement item in results.EnumerateArray())
        {
            string cat = GetJsonString(item, "category") ?? string.Empty;
            string title = GetJsonString(item, "category_title") ?? string.Empty;
            var entries = new List<NewsletterDirectoryEntry>();
            if (item.TryGetProperty("newsletters", out JsonElement nls) && nls.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement nl in nls.EnumerateArray())
                {
                    if (TryParseDirectoryEntry(nl, out NewsletterDirectoryEntry? entry) && entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
            }
            list.Add(new NewsletterDirectoryCategoryPreview(cat, title, entries));
        }
        return list;
    }

    public static NewsletterDirectoryPage ParseDirectoryPage(JsonElement? data, string fieldName)
    {
        JsonElement container = RequireFieldObject(data, fieldName);
        var entries = new List<NewsletterDirectoryEntry>();
        if (container.TryGetProperty("result", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in arr.EnumerateArray())
            {
                if (TryParseDirectoryEntry(item, out NewsletterDirectoryEntry? entry) && entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }

        string? startCursor = null;
        string? endCursor = null;
        bool hasNext = false;
        bool hasPrev = false;
        if (container.TryGetProperty("page_info", out JsonElement pi) && pi.ValueKind == JsonValueKind.Object)
        {
            startCursor = GetJsonString(pi, "startCursor");
            endCursor = GetJsonString(pi, "endCursor");
            hasNext = ParseJsonBoolish(pi, "hasNextPage");
            hasPrev = ParseJsonBoolish(pi, "hasPreviousPage");
        }

        return new NewsletterDirectoryPage(entries, startCursor, endCursor, hasNext, hasPrev);
    }

    public static NewsletterMetadata ParseNewsletterMetadata(JsonElement value)
    {
        string? jidStr = GetJsonString(value, "id");
        if (string.IsNullOrEmpty(jidStr) || !Jid.TryParse(jidStr, out Jid jid))
        {
            throw new InvalidDataException($"Invalid or missing newsletter id '{jidStr}'.");
        }

        JsonElement thread = value.TryGetProperty("thread_metadata", out JsonElement tm) && tm.ValueKind == JsonValueKind.Object
            ? tm
            : default;

        string name = GetNestedText(thread, "name") ?? string.Empty;
        string? description = GetNestedText(thread, "description");
        if (string.IsNullOrEmpty(description))
        {
            description = null;
        }

        ulong subCount = thread.ValueKind == JsonValueKind.Object ? (GetJsonUInt64(thread, "subscribers_count") ?? 0) : 0;
        string verRaw = thread.ValueKind == JsonValueKind.Object ? (GetJsonString(thread, "verification") ?? "UNVERIFIED") : "UNVERIFIED";
        NewsletterVerification ver = ParseVerification(verRaw);

        string stateRaw = value.TryGetProperty("state", out JsonElement stObj) && stObj.ValueKind == JsonValueKind.Object
            ? (GetJsonString(stObj, "type") ?? "ACTIVE")
            : "ACTIVE";
        NewsletterState state = ParseState(stateRaw);

        string? picPath = GetPictureDirectPath(thread, "picture");
        string? prevPath = GetPictureDirectPath(thread, "preview");
        string? invite = thread.ValueKind == JsonValueKind.Object ? GetJsonString(thread, "invite") : null;
        string? handle = thread.ValueKind == JsonValueKind.Object ? GetJsonString(thread, "handle") : null;
        ulong? creationTime = thread.ValueKind == JsonValueKind.Object ? GetJsonUInt64(thread, "creation_time") : null;

        NewsletterRole? role = null;
        bool? muted = null;
        bool? followerMuted = null;
        if (value.TryGetProperty("viewer_metadata", out JsonElement vm) && vm.ValueKind == JsonValueKind.Object)
        {
            role = ParseNewsletterRole(GetJsonString(vm, "role"));
            if (vm.TryGetProperty("settings", out JsonElement settings) && settings.ValueKind == JsonValueKind.Array)
            {
                muted = ParseMuteSetting(settings, "MUTE_ADMIN_ACTIVITY");
                followerMuted = ParseMuteSetting(settings, "MUTE_FOLLOWER_ACTIVITY");
            }
        }

        return new NewsletterMetadata(
            jid,
            name,
            description,
            subCount,
            ver,
            verRaw,
            state,
            stateRaw,
            picPath,
            prevPath,
            invite,
            handle,
            creationTime,
            role,
            muted,
            followerMuted);
    }

    private static bool TryParseDirectoryEntry(JsonElement item, out NewsletterDirectoryEntry? entry)
    {
        entry = null;
        if (IsAbsentNewsletterNode(item))
        {
            return false;
        }
        string? idStr = GetJsonString(item, "id");
        if (string.IsNullOrEmpty(idStr) || !Jid.TryParse(idStr, out Jid jid))
        {
            return false;
        }

        JsonElement thread = item.TryGetProperty("thread_metadata", out JsonElement tm) && tm.ValueKind == JsonValueKind.Object
            ? tm
            : default;
        string name = GetNestedText(thread, "name") ?? string.Empty;
        string? desc = GetNestedText(thread, "description");
        if (string.IsNullOrEmpty(desc)) desc = null;
        ulong subs = thread.ValueKind == JsonValueKind.Object ? (GetJsonUInt64(thread, "subscribers_count") ?? 0) : 0;
        NewsletterVerification ver = ParseVerification(thread.ValueKind == JsonValueKind.Object ? GetJsonString(thread, "verification") : null);
        string? pic = GetPictureDirectPath(thread, "picture");
        string? invite = thread.ValueKind == JsonValueKind.Object ? GetJsonString(thread, "invite") : null;
        string? handle = thread.ValueKind == JsonValueKind.Object ? GetJsonString(thread, "handle") : null;
        ulong? creationTime = thread.ValueKind == JsonValueKind.Object ? GetJsonUInt64(thread, "creation_time") : null;

        ulong? lastStatusTime = null;
        string? lastStatusServerId = null;
        if (item.TryGetProperty("status_metadata", out JsonElement sm) && sm.ValueKind == JsonValueKind.Object)
        {
            lastStatusTime = GetJsonUInt64(sm, "last_status_sent_time");
            lastStatusServerId = GetJsonString(sm, "last_status_server_id");
        }

        entry = new NewsletterDirectoryEntry(jid, name, desc, subs, ver, pic, invite, handle, creationTime, lastStatusTime, lastStatusServerId);
        return true;
    }

    private static void EnsureNewsletterJid(Jid channelJid)
    {
        if (!channelJid.IsNewsletter())
        {
            throw new ArgumentException($"Expected a @newsletter JID, got '{channelJid}'.", nameof(channelJid));
        }
    }

    private static bool IsAbsentNewsletterNode(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || value.ValueKind != JsonValueKind.Object
        || !value.TryGetProperty("id", out JsonElement idEl)
        || idEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static JsonElement RequireDataObject(JsonElement? data) =>
        data is { ValueKind: JsonValueKind.Object } obj
            ? obj
            : throw new InvalidDataException("MEX newsletter response missing data object.");

    private static JsonElement RequireFieldObject(JsonElement? data, string fieldName)
    {
        JsonElement root = RequireDataObject(data);
        if (!root.TryGetProperty(fieldName, out JsonElement el) || el.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"MEX newsletter response missing '{fieldName}' object.");
        }
        return el;
    }

    private static NewsletterAdminProfile ParseAdminProfileNode(Node node)
    {
        Node? pic = node.GetChild("picture");
        string? name = node.GetChild("name")?.Content switch
        {
            NodeContent.StringContent sc => sc.Text,
            NodeContent.BytesContent bc => System.Text.Encoding.UTF8.GetString(bc.Bytes),
            _ => string.Empty
        };
        return new NewsletterAdminProfile(
            node.GetAttr("id"),
            name ?? string.Empty,
            pic?.GetAttr("id"),
            pic?.GetAttr("direct_path"));
    }

    private static NewsletterAdminProfile? ParseAdminProfileJson(JsonElement ap)
    {
        string? name = GetJsonString(ap, "name");
        if (name is null)
        {
            return null;
        }
        string? picId = null;
        string? picPath = null;
        if (ap.TryGetProperty("picture", out JsonElement pic) && pic.ValueKind == JsonValueKind.Object)
        {
            picId = GetJsonString(pic, "id");
            picPath = GetJsonString(pic, "direct_path");
        }
        return new NewsletterAdminProfile(GetJsonString(ap, "id"), name, picId, picPath);
    }

    private static ulong? ParseCountChild(Node node, string tag)
    {
        Node? child = node.GetChild(tag);
        return child is null ? null : TryParseOptionalUInt64(child.GetAttr("count"));
    }

    private static NewsletterRole? ParseNewsletterRole(string? raw) => raw?.ToUpperInvariant() switch
    {
        "OWNER" => NewsletterRole.Owner,
        "ADMIN" => NewsletterRole.Admin,
        "SUBSCRIBER" => NewsletterRole.Subscriber,
        "GUEST" => NewsletterRole.Guest,
        _ => null
    };

    private static NewsletterState ParseState(string? raw) => raw?.ToUpperInvariant() switch
    {
        "ACTIVE" => NewsletterState.Active,
        "SUSPENDED" => NewsletterState.Suspended,
        "GEOSUSPENDED" => NewsletterState.Geosuspended,
        "DELETED" => NewsletterState.Deleted,
        "NON_EXISTING" => NewsletterState.NonExisting,
        _ => NewsletterState.Other
    };

    private static NewsletterVerification ParseVerification(string? raw) => raw?.ToUpperInvariant() switch
    {
        "VERIFIED" => NewsletterVerification.Verified,
        "UNVERIFIED" or null => NewsletterVerification.Unverified,
        _ => NewsletterVerification.Other
    };

    private static bool? ParseMuteSetting(JsonElement settings, string muteType)
    {
        foreach (JsonElement s in settings.EnumerateArray())
        {
            if (string.Equals(GetJsonString(s, "type"), muteType, StringComparison.Ordinal))
            {
                return GetJsonString(s, "value") switch
                {
                    "ON" => true,
                    "OFF" => false,
                    _ => null
                };
            }
        }
        return null;
    }

    private static string? GetNestedText(JsonElement obj, string prop)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(prop, out JsonElement child)
            && child.ValueKind == JsonValueKind.Object)
        {
            return GetJsonString(child, "text");
        }
        return null;
    }

    private static string? GetPictureDirectPath(JsonElement obj, string prop)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(prop, out JsonElement child)
            && child.ValueKind == JsonValueKind.Object)
        {
            return GetJsonString(child, "direct_path");
        }
        return null;
    }

    private static string? GetJsonString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static ulong? GetJsonUInt64(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out JsonElement el))
        {
            return null;
        }
        if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt64(out ulong n))
        {
            return n;
        }
        if (el.ValueKind == JsonValueKind.String && ulong.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong s))
        {
            return s;
        }
        return null;
    }

    private static bool ParseJsonBoolish(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out JsonElement el))
        {
            return false;
        }
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b)) return b;
        return false;
    }

    private static bool TryParseUInt64(string? text, out ulong value) =>
        ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static ulong? TryParseOptionalUInt64(string? text) =>
        TryParseUInt64(text, out ulong v) ? v : null;
}
