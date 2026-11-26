using System.Text;
using System.Text.Json;
using Whatsapp;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum BotListVersion
{
    V2 = 2,
    V3 = 3,
}

public enum BotSectionType
{
    All,
    Category,
    Featured,
}

public enum BotSectionDisplayType
{
    Default,
    Banner,
    Carousel,
}

public enum BotThemeMode
{
    Light,
    Dark,
}

public enum BotEditType
{
    First,
    Inner,
    Last,
}

public sealed record BotStanzaInfo(
    BotEditType? EditType,
    string? EditTargetId,
    long? SenderTimestampMs,
    string? BizBotType = null,
    string? BodyType = null);

public sealed record BotDefault(
    Jid Jid,
    string PersonaId);

public sealed record BotListEntry(
    Jid Jid,
    string PersonaId,
    bool IsDefault = false);

public sealed record BotTheme(
    BotThemeMode? Mode,
    string? PrimaryColor,
    string? SecondaryColor,
    string? BackgroundUrl);

public sealed record BotListSection(
    string Name,
    BotSectionType SectionType,
    BotSectionDisplayType DisplayType,
    bool ShowMore,
    string? Category,
    BotTheme? Theme,
    IReadOnlyList<BotListEntry> Bots);

public sealed record BotList(
    BotListVersion Version,
    string? BHash,
    BotDefault? DefaultBot,
    IReadOnlyList<BotListSection> Sections)
{
    public IReadOnlyList<BotListEntry> Flatten()
    {
        var list = new List<BotListEntry>();
        foreach (BotListSection section in Sections)
        {
            foreach (BotListEntry bot in section.Bots)
            {
                list.Add(bot);
            }
        }
        return list;
    }
}

public sealed record BotGqlProfile(
    string Id,
    string? Jid,
    bool? IsMetaCreated,
    string? CreatorName,
    string? CreatorProfileUri,
    string? Name,
    string? Description,
    IReadOnlyList<string> IcebreakerPrompts,
    bool? PosingAsProfessional);

public sealed record DynamicAiMode(
    string ModeId,
    string? Title,
    string? Subtitle,
    bool IsExperimental);

public sealed record BotCertificateRevocationList(
    string CrlPem,
    long? NextUpdateSeconds);

public sealed record RichResponseSegment(
    AIRichResponseSubMessageType Type,
    string? Text,
    string? CodeLanguage = null,
    IReadOnlyList<string>? ImageUrls = null,
    IReadOnlyList<IReadOnlyList<string>>? TableRows = null,
    IReadOnlyList<string>? LatexExpressions = null,
    string? Title = null);

public sealed record ParsedBotRichResponse(
    AIRichResponseMessageType MessageType,
    string SummaryText,
    IReadOnlyList<RichResponseSegment> Segments,
    byte[]? UnifiedResponseData = null);

/// <summary>
/// <c>xmlns="bot"</c> IQ directory builders/parsers, Meta AI MEX GraphQL payload decoders,
/// and <see cref="AIRichResponseMessage"/> structured extractors matching live <c>web.whatsapp.com</c>
/// and <c>wacore/src/iq/bot.rs</c>.
/// </summary>
public static class BotStanzas
{
    public const string BotXmlns = "bot";

    public static BotEditType? ParseBotEditType(string? wire) => wire?.ToLowerInvariant() switch
    {
        "first" => BotEditType.First,
        "inner" => BotEditType.Inner,
        "last" => BotEditType.Last,
        _ => null
    };

    /// <summary>
    /// Builds <c>&lt;iq xmlns="bot" type="get" to="s.whatsapp.net"&gt;&lt;bot v="2"/&gt;&lt;/iq&gt;</c>
    /// (or <c>v="3"</c> with <paramref name="bhash"/> when requesting delta updates).
    /// </summary>
    public static Node BuildGetBotList(string iqId, string? bhash = null)
    {
        var botAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(bhash))
        {
            botAttrs["v"] = "3";
            botAttrs["bhash"] = bhash;
        }
        else
        {
            botAttrs["v"] = "2";
        }

        return StanzaBuilder.Iq(
            iqId,
            "get",
            BotXmlns,
            StanzaBuilder.ServerPn,
            new Node("bot", botAttrs));
    }

    public static BotList ParseGetBotListResponse(Node iqResponse)
    {
        ArgumentNullException.ThrowIfNull(iqResponse);
        Node botNode = iqResponse.GetChild("bot")
            ?? throw new InvalidDataException("Missing <bot> child in get-bot-list IQ response.");

        string? vStr = botNode.GetAttr("v");
        BotListVersion version = vStr switch
        {
            "2" or null => BotListVersion.V2,
            "3" => BotListVersion.V3,
            _ => throw new InvalidDataException($"Unsupported bot list version '{vStr}'.")
        };

        string? bhash = botNode.GetAttr("bhash");
        if (version == BotListVersion.V3 && string.IsNullOrEmpty(bhash))
        {
            throw new InvalidDataException("Bot list v3 response requires non-empty bhash attribute.");
        }

        BotDefault? defaultBot = null;
        if (botNode.GetChild("default") is { } defaultNode)
        {
            string? jidStr = defaultNode.GetAttr("jid");
            if (string.IsNullOrEmpty(jidStr) || !Jid.TryParse(jidStr, out Jid defJid))
            {
                throw new InvalidDataException($"Invalid default bot JID '{jidStr}'.");
            }
            string personaId = defaultNode.GetAttr("persona_id") ?? string.Empty;
            defaultBot = new BotDefault(defJid, personaId);
        }
        else if (version == BotListVersion.V2)
        {
            throw new InvalidDataException("Bot list v2 response requires <default> child.");
        }

        var sections = new List<BotListSection>();
        foreach (Node secNode in botNode.GetChildren("section"))
        {
            string name = secNode.GetAttr("name") ?? string.Empty;
            BotSectionType secType = (secNode.GetAttr("type") ?? "category") switch
            {
                "all" => BotSectionType.All,
                "featured" => BotSectionType.Featured,
                _ => BotSectionType.Category
            };
            BotSectionDisplayType displayType = (secNode.GetAttr("display_type") ?? "default") switch
            {
                "banner" => BotSectionDisplayType.Banner,
                "carousel" => BotSectionDisplayType.Carousel,
                _ => BotSectionDisplayType.Default
            };
            bool showMore = string.Equals(secNode.GetAttr("show_more"), "true", StringComparison.OrdinalIgnoreCase);
            string? category = secNode.GetAttr("category");

            BotTheme? theme = null;
            if (secNode.GetChild("theme") is { } themeNode)
            {
                BotThemeMode? mode = themeNode.GetAttr("mode") switch
                {
                    "light" => BotThemeMode.Light,
                    "dark" => BotThemeMode.Dark,
                    _ => null
                };
                theme = new BotTheme(
                    mode,
                    themeNode.GetAttr("primary_color"),
                    themeNode.GetAttr("secondary_color"),
                    themeNode.GetAttr("background_url"));
            }

            var bots = new List<BotListEntry>();
            foreach (Node entryNode in secNode.GetChildren("bot"))
            {
                string? botJidStr = entryNode.GetAttr("jid");
                if (string.IsNullOrEmpty(botJidStr) || !Jid.TryParse(botJidStr, out Jid entryJid))
                {
                    throw new InvalidDataException($"Invalid bot entry JID '{botJidStr}' in section '{name}'.");
                }
                string personaId = entryNode.GetAttr("persona_id") ?? string.Empty;
                bool isDefault = string.Equals(entryNode.GetAttr("default"), "true", StringComparison.OrdinalIgnoreCase);
                bots.Add(new BotListEntry(entryJid, personaId, isDefault));
            }

            sections.Add(new BotListSection(name, secType, displayType, showMore, category, theme, bots));
        }

        return new BotList(version, bhash, defaultBot, sections);
    }

    public static object BuildFetchBotProfilesVariables(IReadOnlyList<string> personaIds)
    {
        ArgumentNullException.ThrowIfNull(personaIds);
        return new { ids = personaIds };
    }

    public static IReadOnlyList<BotGqlProfile> ParseBotProfilesResponse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var doc = JsonDocument.Parse(json);
        return ParseBotProfilesElement(doc.RootElement);
    }

    public static IReadOnlyList<BotGqlProfile> ParseBotProfilesElement(JsonElement root)
    {
        if (!TryGetDataProperty(root, "xfb_fetch_genai_personas", out JsonElement arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BotGqlProfile>();
        }

        var profiles = new List<BotGqlProfile>();
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string id = item.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? string.Empty
                : string.Empty;
            string? jid = item.TryGetProperty("jid", out JsonElement jidEl) && jidEl.ValueKind == JsonValueKind.String
                ? jidEl.GetString()
                : null;
            bool? isMetaCreated = item.TryGetProperty("is_meta_created", out JsonElement imc) && imc.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? imc.GetBoolean()
                : null;

            string? creatorName = null;
            string? creatorUri = null;
            if (item.TryGetProperty("creator", out JsonElement creator) && creator.ValueKind == JsonValueKind.Object)
            {
                creatorName = creator.TryGetProperty("name", out JsonElement cn) && cn.ValueKind == JsonValueKind.String ? cn.GetString() : null;
                creatorUri = creator.TryGetProperty("profile_uri", out JsonElement cu) && cu.ValueKind == JsonValueKind.String ? cu.GetString() : null;
            }

            string? name = null;
            string? desc = null;
            bool? posing = null;
            var icebreakers = new List<string>();
            if (item.TryGetProperty("latest_published_version_for_viewer", out JsonElement ver) && ver.ValueKind == JsonValueKind.Object)
            {
                name = ver.TryGetProperty("name", out JsonElement vn) && vn.ValueKind == JsonValueKind.String ? vn.GetString() : null;
                desc = ver.TryGetProperty("description", out JsonElement vd) && vd.ValueKind == JsonValueKind.String ? vd.GetString() : null;
                posing = ver.TryGetProperty("posing_as_professional", out JsonElement vp) && vp.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? vp.GetBoolean()
                    : null;
                if (ver.TryGetProperty("icebreaker_prompt_list", out JsonElement ib) && ib.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement prompt in ib.EnumerateArray())
                    {
                        if (prompt.ValueKind == JsonValueKind.String && prompt.GetString() is { Length: > 0 } p)
                        {
                            icebreakers.Add(p);
                        }
                    }
                }
            }

            profiles.Add(new BotGqlProfile(id, jid, isMetaCreated, creatorName, creatorUri, name, desc, icebreakers, posing));
        }

        return profiles;
    }

    public static IReadOnlyList<DynamicAiMode> ParseDynamicAiModesResponse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var doc = JsonDocument.Parse(json);
        return ParseDynamicAiModesElement(doc.RootElement);
    }

    public static IReadOnlyList<DynamicAiMode> ParseDynamicAiModesElement(JsonElement root)
    {
        if (!TryGetDataProperty(root, "xfb_meta_ai_modes", out JsonElement arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<DynamicAiMode>();
        }

        var modes = new List<DynamicAiMode>();
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string modeId = item.TryGetProperty("mode_id", out JsonElement mid) && mid.ValueKind == JsonValueKind.String
                ? mid.GetString() ?? string.Empty
                : string.Empty;
            string? title = item.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            string? subtitle = item.TryGetProperty("subtitle", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            bool isExp = item.TryGetProperty("is_experimental", out JsonElement e) && e.ValueKind == JsonValueKind.True;
            modes.Add(new DynamicAiMode(modeId, title, subtitle, isExp));
        }
        return modes;
    }

    public static object BuildFetchBotCrlVariables(string crlName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(crlName);
        return new { crl_name = crlName };
    }

    public static BotCertificateRevocationList? ParseBotCrlResponse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var doc = JsonDocument.Parse(json);
        return ParseBotCrlElement(doc.RootElement);
    }

    public static BotCertificateRevocationList? ParseBotCrlElement(JsonElement root)
    {
        if (!TryGetDataProperty(root, "xwa2_fetch_feature_pki_crl", out JsonElement obj)
            || obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string crl = obj.TryGetProperty("crl", out JsonElement crlEl) && crlEl.ValueKind == JsonValueKind.String
            ? crlEl.GetString() ?? string.Empty
            : string.Empty;
        long? nextUpdate = obj.TryGetProperty("next_update", out JsonElement nu) && nu.TryGetInt64(out long sec)
            ? sec
            : null;
        return new BotCertificateRevocationList(crl, nextUpdate);
    }

    public static object BuildAiAgentAutoReplyVariables(string? phoneUser, string? lidUser, bool enabled)
    {
        return new
        {
            input = new
            {
                user_id = lidUser ?? phoneUser ?? string.Empty,
                phone_number = phoneUser,
                lid = lidUser,
                auto_reply_enabled = enabled
            }
        };
    }

    /// <summary>
    /// Extracts readable markdown/text and typed sub-message segments from an <see cref="AIRichResponseMessage"/>.
    /// </summary>
    public static ParsedBotRichResponse ExtractRichResponse(AIRichResponseMessage rich)
    {
        ArgumentNullException.ThrowIfNull(rich);
        var segments = new List<RichResponseSegment>(rich.Submessages.Count);
        var textParts = new List<string>();

        foreach (AIRichResponseSubMessage sub in rich.Submessages)
        {
            AIRichResponseSubMessageType type = sub.MessageType;
            switch (type)
            {
                case AIRichResponseSubMessageType.AiRichResponseCode:
                {
                    string? codeText = sub.HasMessageText ? sub.MessageText : null;
                    string? lang = sub.CodeMetadata?.HasCodeLanguage == true ? sub.CodeMetadata.CodeLanguage : null;
                    segments.Add(new RichResponseSegment(type, codeText, CodeLanguage: lang));
                    if (!string.IsNullOrEmpty(codeText))
                    {
                        textParts.Add(!string.IsNullOrEmpty(lang) ? $"```{lang}\n{codeText}\n```" : codeText);
                    }
                    break;
                }
                case AIRichResponseSubMessageType.AiRichResponseTable:
                {
                    var rows = new List<IReadOnlyList<string>>();
                    if (sub.TableMetadata is { } tm)
                    {
                        foreach (var row in tm.Rows)
                        {
                            rows.Add(row.Items.ToArray());
                        }
                    }
                    string? title = sub.TableMetadata?.HasTitle == true ? sub.TableMetadata.Title : null;
                    segments.Add(new RichResponseSegment(type, sub.HasMessageText ? sub.MessageText : null, TableRows: rows, Title: title));
                    if (sub.HasMessageText && !string.IsNullOrWhiteSpace(sub.MessageText))
                    {
                        textParts.Add(sub.MessageText);
                    }
                    else if (!string.IsNullOrEmpty(title))
                    {
                        textParts.Add(title);
                    }
                    else if (rows.Count > 0)
                    {
                        textParts.Add(string.Join("\n", rows.Select(r => string.Join(" | ", r))));
                    }
                    break;
                }
                case AIRichResponseSubMessageType.AiRichResponseInlineImage:
                {
                    var urls = new List<string>();
                    if (sub.ImageMetadata?.ImageUrl?.HasImagePreviewUrl == true)
                    {
                        urls.Add(sub.ImageMetadata.ImageUrl.ImagePreviewUrl);
                    }
                    else if (sub.ImageMetadata?.ImageUrl?.HasImageHighResUrl == true)
                    {
                        urls.Add(sub.ImageMetadata.ImageUrl.ImageHighResUrl);
                    }
                    segments.Add(new RichResponseSegment(type, sub.HasMessageText ? sub.MessageText : null, ImageUrls: urls));
                    if (sub.HasMessageText && !string.IsNullOrWhiteSpace(sub.MessageText))
                    {
                        textParts.Add(sub.MessageText);
                    }
                    break;
                }
                case AIRichResponseSubMessageType.AiRichResponseGridImage:
                {
                    var urls = new List<string>();
                    if (sub.GridImageMetadata is { } gm)
                    {
                        foreach (AIRichResponseImageURL u in gm.ImageUrls)
                        {
                            if (u.HasImagePreviewUrl) urls.Add(u.ImagePreviewUrl);
                            else if (u.HasImageHighResUrl) urls.Add(u.ImageHighResUrl);
                        }
                    }
                    segments.Add(new RichResponseSegment(type, sub.HasMessageText ? sub.MessageText : null, ImageUrls: urls));
                    if (sub.HasMessageText && !string.IsNullOrWhiteSpace(sub.MessageText))
                    {
                        textParts.Add(sub.MessageText);
                    }
                    break;
                }
                case AIRichResponseSubMessageType.AiRichResponseLatex:
                {
                    var exprs = new List<string>();
                    if (sub.LatexMetadata is { } lm)
                    {
                        foreach (var e in lm.Expressions)
                        {
                            if (e.HasLatexExpression) exprs.Add(e.LatexExpression);
                        }
                    }
                    string? rawText = sub.HasMessageText ? sub.MessageText : sub.LatexMetadata?.Text;
                    segments.Add(new RichResponseSegment(type, rawText, LatexExpressions: exprs));
                    if (!string.IsNullOrWhiteSpace(rawText))
                    {
                        textParts.Add(rawText);
                    }
                    else if (exprs.Count > 0)
                    {
                        textParts.Add(string.Join("\n", exprs));
                    }
                    break;
                }
                default:
                {
                    string? txt = sub.HasMessageText ? sub.MessageText : null;
                    segments.Add(new RichResponseSegment(type, txt));
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        textParts.Add(txt);
                    }
                    break;
                }
            }
        }

        string summary = textParts.Count > 0
            ? string.Join("\n\n", textParts)
            : (rich.UnifiedResponse?.HasData == true ? Encoding.UTF8.GetString(rich.UnifiedResponse.Data.Span) : string.Empty);

        return new ParsedBotRichResponse(
            rich.MessageType,
            summary,
            segments,
            rich.UnifiedResponse?.HasData == true ? rich.UnifiedResponse.Data.ToByteArray() : null);
    }

    private static bool TryGetDataProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out JsonElement dataObj)
                && dataObj.ValueKind == JsonValueKind.Object
                && dataObj.TryGetProperty(propertyName, out value))
            {
                return true;
            }
            if (root.TryGetProperty(propertyName, out value))
            {
                return true;
            }
        }
        value = default;
        return false;
    }
}
