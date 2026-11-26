using System.Globalization;
using System.Text.Json;
using WhatsApp.Protocol.Binary;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum BusinessHourMode
{
    Open24H,
    SpecificHours,
    AppointmentOnly,
    Other
}

public sealed record BusinessHoursConfig(
    string DayOfWeek,
    BusinessHourMode Mode,
    uint? OpenTimeMinutes = null,
    uint? CloseTimeMinutes = null);

public sealed record BusinessHours(
    string? Timezone,
    IReadOnlyList<BusinessHoursConfig> Config);

public sealed record BusinessHoursUpdate(
    string? Timezone,
    string? Note,
    IReadOnlyList<BusinessHoursConfig> Config);

public sealed record BusinessCategory(string Id, string Name);

public sealed record BusinessProfile(
    Jid? Wid,
    string Description,
    string? Email,
    IReadOnlyList<string> Websites,
    IReadOnlyList<BusinessCategory> Categories,
    string? Address,
    BusinessHours BusinessHours);

public sealed record BusinessProfileUpdate(
    string? Address = null,
    double? Latitude = null,
    double? Longitude = null,
    string? Description = null,
    string? Email = null,
    IReadOnlyList<string>? Websites = null,
    IReadOnlyList<string>? Categories = null,
    BusinessHoursUpdate? BusinessHours = null);

public sealed record CoverPhotoUpload(string Id, string Token, long TimestampSeconds);

public sealed record BusinessPrice(long Amount1000, string? Currency);

public sealed record BusinessSalePrice(BusinessPrice Price, string? StartDate, string? EndDate);

public sealed record BusinessProductImage(string? Id, string? RequestImageUrl, string? OriginalImageUrl);

public sealed record BusinessProductVideo(string? Id, string? OriginalVideoUrl, string? ThumbnailUrl);

public sealed record BusinessVariantProperty(string? Name, string? Value);

public sealed record BusinessImporterAddress(
    string? Street1,
    string? Street2,
    string? City,
    string? Region,
    string? PostalCode,
    string? CountryCode);

public sealed record BusinessProduct(
    string Id,
    string? RetailerId,
    string? Name,
    string? Description,
    string? Url,
    string? ShimmedUrl,
    BusinessPrice? Price,
    BusinessSalePrice? SalePrice,
    bool? IsHidden,
    bool? IsSanctioned,
    long? MaxAvailable,
    string? Availability,
    string? ReviewStatus,
    bool? CanAppeal,
    bool? BelongsTo,
    IReadOnlyList<BusinessProductImage> Images,
    IReadOnlyList<BusinessProductVideo> Videos,
    string? ComplianceCategory,
    string? CountryCodeOrigin,
    string? ImporterName,
    BusinessImporterAddress? ImporterAddress);

public sealed record BusinessCatalog(
    IReadOnlyList<BusinessProduct> Products,
    string? AfterCursor,
    string? BeforeCursor);

public sealed record BusinessCollection(
    string? Id,
    string? Name,
    IReadOnlyList<BusinessProduct> Products,
    string? ReviewStatus,
    bool? CanAppeal,
    string? RejectReason,
    string? CommerceUrl);

public sealed record BusinessCollections(
    IReadOnlyList<BusinessCollection> Collections,
    string? AfterCursor);

public sealed record BusinessOrderProduct(
    string? Id,
    string? Name,
    BusinessPrice? Price,
    long? Quantity,
    IReadOnlyList<BusinessProductImage> Images,
    IReadOnlyList<BusinessVariantProperty> VariantProperties);

public sealed record BusinessOrderPriceDetails(
    BusinessPrice? Subtotal,
    BusinessPrice? Total,
    string? Currency);

public sealed record BusinessOrder(
    IReadOnlyList<BusinessOrderProduct> Products,
    BusinessOrderPriceDetails? PriceDetails,
    long? CreationTimestampSeconds);

public enum CatalogNonceStatus
{
    Success,
    RecoveryRequired,
    Unknown
}

/// <summary>
/// Business profile IQs (<c>xmlns="w:biz"</c>), catalog owner authentication (<c>xmlns="fb:thrift_iq"</c>),
/// and GraphQL payload builders/decoders matching <c>wacore/src/iq/business.rs</c> and <c>src/features/business.rs</c>.
/// </summary>
public static class BusinessStanzas
{
    public const string Xmlns = "w:biz";
    public const string FbThriftXmlns = "fb:thrift_iq";
    public const string ProfileReadVersion = "244";
    public const string ProfileMutationVersion = "3";
    public const int MaxWebsites = 2;
    public const int MinutesPerDay = 1440;
    public const string VisitorCatalogEndpoint = "https://graph.whatsapp.com/graphql/catalog";
    public const string OwnerCatalogEndpoint = "https://graph.facebook.com/graphql";

    // ─── w:biz Business Profile IQs ──────────────────────────────────────

    public static Node BuildGetBusinessProfileIq(string iqId, Jid targetJid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        var profileNode = new Node("profile", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["jid"] = targetJid.ToNonAd()
        });
        var bpNode = new Node(
            "business_profile",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["v"] = ProfileReadVersion },
            new[] { profileNode });
        return StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn, bpNode);
    }

    public static BusinessProfile? ParseBusinessProfileResponse(Node response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Node? bpNode = response.GetChild("business_profile");
        Node? profileNode = bpNode?.GetChild("profile");
        if (profileNode is null)
        {
            return null;
        }

        Jid? wid = profileNode.GetAttr("jid") is { Length: > 0 } jidStr && Jid.TryParse(jidStr, out Jid parsedJid)
            ? parsedJid
            : null;
        string description = ReadChildText(profileNode, "description") ?? string.Empty;
        string? address = ReadChildText(profileNode, "address");
        string? email = ReadChildText(profileNode, "email");

        var websites = new List<string>();
        foreach (Node w in profileNode.GetChildren("website"))
        {
            string? text = ReadNodeText(w);
            if (!string.IsNullOrEmpty(text))
            {
                websites.Add(text);
            }
        }

        var categories = new List<BusinessCategory>();
        if (profileNode.GetChild("categories") is { } catsNode)
        {
            foreach (Node cat in catsNode.GetChildren("category"))
            {
                string? id = cat.GetAttr("id");
                if (!string.IsNullOrEmpty(id))
                {
                    categories.Add(new BusinessCategory(id, ReadNodeText(cat) ?? string.Empty));
                }
            }
        }

        BusinessHours hours;
        if (profileNode.GetChild("business_hours") is { } bhNode)
        {
            string? tz = bhNode.GetAttr("timezone");
            var configs = new List<BusinessHoursConfig>();
            foreach (Node c in bhNode.GetChildren("business_hours_config"))
            {
                string? day = c.GetAttr("day_of_week");
                string? modeStr = c.GetAttr("mode");
                if (string.IsNullOrEmpty(day) || string.IsNullOrEmpty(modeStr))
                {
                    continue;
                }
                uint? open = uint.TryParse(c.GetAttr("open_time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint o) ? o : null;
                uint? close = uint.TryParse(c.GetAttr("close_time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint cl) ? cl : null;
                configs.Add(new BusinessHoursConfig(day, ParseHourMode(modeStr), open, close));
            }
            hours = new BusinessHours(tz, configs);
        }
        else
        {
            hours = new BusinessHours(null, Array.Empty<BusinessHoursConfig>());
        }

        return new BusinessProfile(wid, description, email, websites, categories, address, hours);
    }

    public static Node BuildUpdateBusinessProfileIq(string iqId, BusinessProfileUpdate update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentNullException.ThrowIfNull(update);

        if (update.Address is null
            && update.Latitude is null
            && update.Longitude is null
            && update.Description is null
            && update.Email is null
            && update.Websites is null
            && update.Categories is null
            && update.BusinessHours is null)
        {
            throw new ArgumentException("Business profile update cannot be completely empty.", nameof(update));
        }

        if (update.Websites is { Count: > MaxWebsites })
        {
            throw new ArgumentException($"Business profile accepts at most {MaxWebsites} websites.", nameof(update));
        }

        if (update.Latitude is double lat && (double.IsNaN(lat) || Math.Abs(lat) > 90.0))
        {
            throw new ArgumentOutOfRangeException(nameof(update), "Latitude must be finite within ±90.");
        }
        if (update.Longitude is double lon && (double.IsNaN(lon) || Math.Abs(lon) > 180.0))
        {
            throw new ArgumentOutOfRangeException(nameof(update), "Longitude must be finite within ±180.");
        }

        var children = new List<Node>();
        if (update.Address is not null) children.Add(new Node("address", update.Address));
        if (update.Latitude is double latVal) children.Add(new Node("latitude", latVal.ToString(CultureInfo.InvariantCulture)));
        if (update.Longitude is double lonVal) children.Add(new Node("longitude", lonVal.ToString(CultureInfo.InvariantCulture)));
        if (update.Description is not null) children.Add(new Node("description", update.Description));
        if (update.Email is not null) children.Add(new Node("email", update.Email));

        if (update.Websites is not null)
        {
            if (update.Websites.Count == 0)
            {
                children.Add(new Node("website"));
            }
            else
            {
                foreach (string w in update.Websites)
                {
                    children.Add(new Node("website", w));
                }
            }
        }

        if (update.Categories is not null)
        {
            Node[] catNodes = update.Categories
                .Select(id => new Node("category", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["id"] = id }))
                .ToArray();
            children.Add(new Node("categories", catNodes));
        }

        if (update.BusinessHours is { } bh)
        {
            var bhChildren = new List<Node>();
            if (!string.IsNullOrEmpty(bh.Note))
            {
                bhChildren.Add(new Node("business_hours_note", bh.Note));
            }

            foreach (BusinessHoursConfig cfg in bh.Config)
            {
                ValidateHoursConfig(cfg);
                var cfgAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
                {
                    ["day_of_week"] = cfg.DayOfWeek,
                    ["mode"] = ToWireString(cfg.Mode)
                };
                if (cfg.OpenTimeMinutes is uint o)
                {
                    cfgAttrs["open_time"] = o.ToString(CultureInfo.InvariantCulture);
                }
                if (cfg.CloseTimeMinutes is uint cl)
                {
                    cfgAttrs["close_time"] = cl.ToString(CultureInfo.InvariantCulture);
                }
                bhChildren.Add(new Node("business_hours_config", cfgAttrs));
            }

            var bhAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(bh.Timezone))
            {
                bhAttrs["timezone"] = bh.Timezone;
            }
            children.Add(new Node("business_hours", bhAttrs, bhChildren));
        }

        return BuildBusinessProfileMutationIq(iqId, children);
    }

    public static Node BuildSetCoverPhotoIq(string iqId, CoverPhotoUpload upload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentNullException.ThrowIfNull(upload);
        var cover = new Node("cover_photo", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["op"] = "update",
            ["id"] = upload.Id,
            ["ts"] = upload.TimestampSeconds.ToString(CultureInfo.InvariantCulture),
            ["token"] = upload.Token
        });
        return BuildBusinessProfileMutationIq(iqId, [cover]);
    }

    public static Node BuildRemoveCoverPhotoIq(string iqId, string photoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentException.ThrowIfNullOrWhiteSpace(photoId);
        var cover = new Node("cover_photo", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["op"] = "delete",
            ["id"] = photoId
        });
        return BuildBusinessProfileMutationIq(iqId, [cover]);
    }

    // ─── Catalog Owner Token Auth (fb:thrift_iq) ─────────────────────────

    public static Node BuildCatalogNonceRequestIq(string iqId) =>
        new("iq", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = iqId,
            ["type"] = "get",
            ["xmlns"] = FbThriftXmlns,
            ["to"] = StanzaBuilder.ServerPn,
            ["smax_id"] = "118"
        });

    public static CatalogNonceStatus ParseCatalogNonceResponse(Node response)
    {
        ArgumentNullException.ThrowIfNull(response);
        string? status = response.GetChild("result")?.GetAttr("status");
        return status switch
        {
            "Success" => CatalogNonceStatus.Success,
            "RecoveryRequired" => CatalogNonceStatus.RecoveryRequired,
            _ => CatalogNonceStatus.Unknown
        };
    }

    public static string? ParseCatalogNonceNotification(Node notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        string? val = ReadChildText(notification, "wa_ad_account_nonce");
        return string.IsNullOrWhiteSpace(val) ? null : val;
    }

    public static Node BuildCatalogTokenExchangeIq(string iqId, string nonceCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonceCode);
        var parameters = new Node("parameters", new[] { new Node("code", nonceCode) });
        return new Node("iq", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = iqId,
            ["type"] = "get",
            ["xmlns"] = FbThriftXmlns,
            ["to"] = StanzaBuilder.ServerPn,
            ["smax_id"] = "104"
        }, new[] { parameters });
    }

    public static string? ParseCatalogTokenExchangeResponse(Node response)
    {
        ArgumentNullException.ThrowIfNull(response);
        string? token = ReadChildText(response, "access_token");
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    // ─── GraphQL Variable Builders & Decoders (Catalog/Collections/Orders) ─

    public static object BuildCatalogVariables(
        Jid businessJid,
        uint limit = 10,
        uint imageWidth = 100,
        uint imageHeight = 100,
        bool allowShopSource = false,
        string? afterCursor = null,
        bool isOwner = false)
    {
        var catalogReq = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["jid"] = businessJid.ToNonAdString(),
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["width"] = imageWidth.ToString(CultureInfo.InvariantCulture),
            ["height"] = imageHeight.ToString(CultureInfo.InvariantCulture),
            ["variant_thumbnail_width"] = null,
            ["variant_thumbnail_height"] = null,
            ["allow_shop_source"] = isOwner
                ? allowShopSource
                : (allowShopSource ? "ALLOWSHOPSOURCE_TRUE" : "ALLOWSHOPSOURCE_FALSE")
        };
        if (isOwner)
        {
            catalogReq["belongs_to"] = new { };
        }
        if (!string.IsNullOrEmpty(afterCursor))
        {
            catalogReq["after"] = afterCursor;
        }

        var request = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["product_catalog"] = catalogReq
        };
        if (isOwner)
        {
            request["platform"] = "WEB";
        }
        return new { request };
    }

    public static object BuildCollectionsVariables(
        Jid businessJid,
        uint collectionLimit = 51,
        uint itemLimit = 51,
        uint imageWidth = 100,
        uint imageHeight = 100,
        string? afterCursor = null)
    {
        var collectionsReq = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["biz_jid"] = businessJid.ToNonAdString(),
            ["collection_limit"] = collectionLimit.ToString(CultureInfo.InvariantCulture),
            ["item_limit"] = itemLimit.ToString(CultureInfo.InvariantCulture),
            ["width"] = imageWidth.ToString(CultureInfo.InvariantCulture),
            ["height"] = imageHeight.ToString(CultureInfo.InvariantCulture),
            ["variant_thumbnail_width"] = null,
            ["variant_thumbnail_height"] = null
        };
        if (!string.IsNullOrEmpty(afterCursor))
        {
            collectionsReq["after"] = afterCursor;
        }
        return new { request = new { collections = collectionsReq } };
    }

    public static object BuildOrderVariables(
        Jid businessJid,
        string orderId,
        string token,
        uint imageWidth = 100,
        uint imageHeight = 100) =>
        new
        {
            request = new
            {
                order = new
                {
                    id = orderId,
                    jid = businessJid.ToNonAdString(),
                    token = new { sensitive_string_value = token },
                    image_dimensions = new { width = imageWidth, height = imageHeight }
                }
            }
        };

    public static BusinessCatalog ParseCatalogResponse(JsonElement data)
    {
        JsonElement root = data.TryGetProperty("xfb_whatsapp_catalog", out JsonElement ownerRoot)
            ? ownerRoot
            : RequireProp(data, "xwa_product_catalog_get_product_catalog", JsonValueKind.Object);
        JsonElement catalog = RequireProp(root, "product_catalog", JsonValueKind.Object);
        JsonElement prodArr = RequireProp(catalog, "products", JsonValueKind.Array);

        var products = new List<BusinessProduct>();
        foreach (JsonElement p in prodArr.EnumerateArray())
        {
            products.Add(ParseProduct(p));
        }

        string? after = null;
        string? before = null;
        if (TryGetOptionalObject(catalog, "paging", out JsonElement paging))
        {
            after = OptStr(paging, "after");
            before = OptStr(paging, "before");
        }

        return new BusinessCatalog(products, after, before);
    }

    public static BusinessCollections ParseCollectionsResponse(JsonElement data)
    {
        JsonElement root = data.TryGetProperty("xfb_whatsapp_catalog_collections", out JsonElement ownerRoot)
            ? ownerRoot
            : RequireProp(data, "xwa_product_catalog_get_collections", JsonValueKind.Object);
        JsonElement collArr = RequireProp(root, "collections", JsonValueKind.Array);

        var collections = new List<BusinessCollection>();
        foreach (JsonElement col in collArr.EnumerateArray())
        {
            if (col.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Collection entry is not an object.");
            }
            var products = new List<BusinessProduct>();
            if (col.TryGetProperty("products", out JsonElement prodEl) && prodEl.ValueKind != JsonValueKind.Null)
            {
                if (prodEl.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("collection.products is not an array.");
                }
                foreach (JsonElement p in prodEl.EnumerateArray())
                {
                    products.Add(ParseProduct(p));
                }
            }

            string? revStatus = null;
            bool? canAppeal = null;
            string? rejectReason = null;
            string? commerceUrl = null;
            if (TryGetOptionalObject(col, "status_info", out JsonElement st))
            {
                revStatus = OptStr(st, "status");
                canAppeal = OptBool(st, "can_appeal");
                rejectReason = OptStr(st, "reject_reason");
                commerceUrl = OptStr(st, "commerce_url");
            }

            collections.Add(new BusinessCollection(
                OptStr(col, "id"),
                OptStr(col, "name"),
                products,
                revStatus,
                canAppeal,
                rejectReason,
                commerceUrl));
        }

        string? after = null;
        if (TryGetOptionalObject(root, "paging", out JsonElement paging))
        {
            after = OptStr(paging, "after");
        }
        return new BusinessCollections(collections, after);
    }

    public static BusinessOrder ParseOrderResponse(JsonElement data)
    {
        JsonElement wrapper = RequireProp(data, "xwa_checkout_get_order_info", JsonValueKind.Object);
        JsonElement order = RequireProp(wrapper, "order", JsonValueKind.Object);
        JsonElement prodArr = RequireProp(order, "products", JsonValueKind.Array);

        var products = new List<BusinessOrderProduct>();
        foreach (JsonElement p in prodArr.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("order.products entry is not an object.");
            }
            string? currency = OptStr(p, "currency");
            BusinessPrice? price = p.TryGetProperty("price", out JsonElement priceEl) ? ParsePrice(priceEl, currency) : null;
            IReadOnlyList<BusinessProductImage> imgs = TryGetOptionalObject(p, "media", out JsonElement media)
                ? ParseImages(media)
                : Array.Empty<BusinessProductImage>();
            IReadOnlyList<BusinessVariantProperty> variants = TryGetOptionalObject(p, "variant_info", out JsonElement vinfo)
                ? ParseVariantProperties(vinfo)
                : Array.Empty<BusinessVariantProperty>();

            products.Add(new BusinessOrderProduct(
                OptStr(p, "id"),
                OptStr(p, "name"),
                price,
                OptInt64(p, "quantity"),
                imgs,
                variants));
        }

        BusinessOrderPriceDetails? details = null;
        if (TryGetOptionalObject(order, "price_details", out JsonElement pd))
        {
            string? currency = OptStr(pd, "currency");
            BusinessPrice? subtotal = pd.TryGetProperty("subtotal_amount", out JsonElement subEl) ? ParsePrice(subEl, currency) : null;
            BusinessPrice? total = pd.TryGetProperty("total_amount", out JsonElement totEl) ? ParsePrice(totEl, currency) : null;
            details = new BusinessOrderPriceDetails(subtotal, total, currency);
        }

        return new BusinessOrder(products, details, OptInt64(order, "creation_time_stamp"));
    }

    private static BusinessProduct ParseProduct(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Product entry is not an object.");
        }
        string id = OptStr(p, "id") ?? throw new InvalidDataException("Product entry missing id.");
        string? currency = OptStr(p, "currency");
        BusinessPrice? price = p.TryGetProperty("price", out JsonElement priceEl) ? ParsePrice(priceEl, currency) : null;

        BusinessSalePrice? salePrice = null;
        if (TryGetOptionalObject(p, "sale_price", out JsonElement saleEl)
            && saleEl.TryGetProperty("price", out JsonElement spVal)
            && ParsePrice(spVal, currency) is { } parsedSp)
        {
            string? start = OptStr(saleEl, "start_date");
            string? end = OptStr(saleEl, "end_date");
            salePrice = new BusinessSalePrice(parsedSp, start is not null && end is not null ? start : null, start is not null && end is not null ? end : null);
        }

        string? revStatus = null;
        bool? canAppeal = null;
        if (TryGetOptionalObject(p, "status_info", out JsonElement statusEl))
        {
            revStatus = OptStr(statusEl, "status");
            canAppeal = OptBool(statusEl, "can_appeal");
        }

        IReadOnlyList<BusinessProductImage> images = Array.Empty<BusinessProductImage>();
        IReadOnlyList<BusinessProductVideo> videos = Array.Empty<BusinessProductVideo>();
        if (TryGetOptionalObject(p, "media", out JsonElement mediaEl))
        {
            images = ParseImages(mediaEl);
            videos = ParseVideos(mediaEl);
        }

        string? origin = null;
        string? importerName = null;
        BusinessImporterAddress? importerAddr = null;
        if (TryGetOptionalObject(p, "compliance_info", out JsonElement compEl))
        {
            origin = OptStr(compEl, "country_code_origin");
            importerName = OptStr(compEl, "importer_name");
            if (TryGetOptionalObject(compEl, "importer_address", out JsonElement addrEl))
            {
                var candidate = new BusinessImporterAddress(
                    OptStr(addrEl, "street1"),
                    OptStr(addrEl, "street2"),
                    OptStr(addrEl, "city"),
                    OptStr(addrEl, "region"),
                    OptStr(addrEl, "postal_code"),
                    OptStr(addrEl, "country_code"));
                if (candidate.Street1 is not null || candidate.Street2 is not null || candidate.City is not null
                    || candidate.Region is not null || candidate.PostalCode is not null || candidate.CountryCode is not null)
                {
                    importerAddr = candidate;
                }
            }
        }

        return new BusinessProduct(
            id,
            OptStr(p, "retailer_id"),
            OptStr(p, "name"),
            OptStr(p, "description"),
            OptStr(p, "url"),
            OptStr(p, "shimmed_url"),
            price,
            salePrice,
            OptBool(p, "is_hidden"),
            OptBool(p, "is_sanctioned"),
            OptInt64(p, "max_available"),
            OptStr(p, "product_availability"),
            revStatus,
            canAppeal,
            OptBool(p, "belongs_to"),
            images,
            videos,
            OptStr(p, "compliance_category"),
            origin,
            importerName,
            importerAddr);
    }

    private static IReadOnlyList<BusinessProductImage> ParseImages(JsonElement media)
    {
        if (!media.TryGetProperty("images", out JsonElement arr) || arr.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<BusinessProductImage>();
        }
        if (arr.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("media.images is not an array.");
        }
        var list = new List<BusinessProductImage>();
        foreach (JsonElement img in arr.EnumerateArray())
        {
            if (img.ValueKind != JsonValueKind.Object) throw new InvalidDataException("media.images entry is not an object.");
            list.Add(new BusinessProductImage(OptStr(img, "id"), OptStr(img, "request_image_url"), OptStr(img, "original_image_url")));
        }
        return list;
    }

    private static IReadOnlyList<BusinessProductVideo> ParseVideos(JsonElement media)
    {
        if (!media.TryGetProperty("videos", out JsonElement arr) || arr.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<BusinessProductVideo>();
        }
        if (arr.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("media.videos is not an array.");
        }
        var list = new List<BusinessProductVideo>();
        foreach (JsonElement vid in arr.EnumerateArray())
        {
            if (vid.ValueKind != JsonValueKind.Object) throw new InvalidDataException("media.videos entry is not an object.");
            list.Add(new BusinessProductVideo(OptStr(vid, "id"), OptStr(vid, "original_video_url"), OptStr(vid, "thumbnail_url")));
        }
        return list;
    }

    private static IReadOnlyList<BusinessVariantProperty> ParseVariantProperties(JsonElement vinfo)
    {
        if (!vinfo.TryGetProperty("variant_properties", out JsonElement arr) || arr.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<BusinessVariantProperty>();
        }
        if (arr.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("variant_info.variant_properties is not an array.");
        }
        var list = new List<BusinessVariantProperty>();
        foreach (JsonElement prop in arr.EnumerateArray())
        {
            if (prop.ValueKind != JsonValueKind.Object) throw new InvalidDataException("variant_properties entry is not an object.");
            list.Add(new BusinessVariantProperty(OptStr(prop, "name"), OptStr(prop, "value")));
        }
        return list;
    }

    private static Node BuildBusinessProfileMutationIq(string iqId, IReadOnlyList<Node> children)
    {
        var bpNode = new Node(
            "business_profile",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["v"] = ProfileMutationVersion,
                ["mutation_type"] = "delta"
            },
            new NodeContent.NodesContent(children));
        return StanzaBuilder.Iq(iqId, "set", Xmlns, StanzaBuilder.ServerPn, bpNode);
    }

    private static void ValidateHoursConfig(BusinessHoursConfig cfg)
    {
        if (cfg.OpenTimeMinutes is uint o && o >= MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(cfg), $"open_time must be < {MinutesPerDay}");
        }
        if (cfg.CloseTimeMinutes is uint c && c >= MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(cfg), $"close_time must be < {MinutesPerDay}");
        }
        bool hasOpen = cfg.OpenTimeMinutes.HasValue;
        bool hasClose = cfg.CloseTimeMinutes.HasValue;
        if (hasOpen != hasClose)
        {
            throw new ArgumentException($"{cfg.DayOfWeek} must specify both open_time and close_time or neither.");
        }
        if (cfg.Mode == BusinessHourMode.SpecificHours && !hasOpen)
        {
            throw new ArgumentException($"{cfg.DayOfWeek} with specific_hours mode requires open_time and close_time.");
        }
        if (cfg.Mode is BusinessHourMode.Open24H or BusinessHourMode.AppointmentOnly && hasOpen)
        {
            throw new ArgumentException($"{cfg.DayOfWeek} with mode {cfg.Mode} cannot carry open_time or close_time.");
        }
    }

    private static BusinessHourMode ParseHourMode(string raw) => raw switch
    {
        "open_24h" => BusinessHourMode.Open24H,
        "specific_hours" => BusinessHourMode.SpecificHours,
        "appointment_only" => BusinessHourMode.AppointmentOnly,
        _ => BusinessHourMode.Other
    };

    private static string ToWireString(BusinessHourMode mode) => mode switch
    {
        BusinessHourMode.Open24H => "open_24h",
        BusinessHourMode.SpecificHours => "specific_hours",
        BusinessHourMode.AppointmentOnly => "appointment_only",
        _ => "specific_hours"
    };

    private static string? ReadChildText(Node parent, string childTag) =>
        parent.GetChild(childTag) is { } child ? ReadNodeText(child) : null;

    private static string? ReadNodeText(Node node) => node.Content switch
    {
        NodeContent.StringContent sc => sc.Text,
        NodeContent.BytesContent bc => System.Text.Encoding.UTF8.GetString(bc.Bytes),
        _ => null
    };

    private static JsonElement RequireProp(JsonElement obj, string prop, JsonValueKind expected)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out JsonElement el) || el.ValueKind != expected)
        {
            throw new InvalidDataException($"Missing or invalid '{prop}' (expected {expected}).");
        }
        return el;
    }

    private static bool TryGetOptionalObject(JsonElement obj, string prop, out JsonElement result)
    {
        result = default;
        if (!obj.TryGetProperty(prop, out JsonElement el) || el.ValueKind == JsonValueKind.Null)
        {
            return false;
        }
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Property '{prop}' is not an object.");
        }
        result = el;
        return true;
    }

    private static BusinessPrice? ParsePrice(JsonElement el, string? currency) =>
        ParseAmount1000(el) is long amt ? new BusinessPrice(amt, currency) : null;

    private static long? ParseAmount1000(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number when el.TryGetInt64(out long v) => v,
        JsonValueKind.String when long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long sv) => sv,
        _ => null
    };

    private static string? OptStr(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(el.GetString())
            ? el.GetString()
            : null;

    private static long? OptInt64(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out JsonElement el) ? ParseAmount1000(el) : null;

    private static bool? OptBool(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out JsonElement el)) return null;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.String)
        {
            return el.GetString() switch
            {
                "true" or "ISHIDDEN_TRUE" => true,
                "false" or "ISHIDDEN_FALSE" => false,
                _ => null
            };
        }
        return null;
    }
}
