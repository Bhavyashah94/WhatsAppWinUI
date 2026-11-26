using System.Globalization;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;

namespace WhatsApp.Protocol.Binary.Stanzas;

public sealed record AbPropsResponse(
    string? AbKey,
    string? Hash,
    int? RefreshSeconds,
    uint? RefreshId,
    bool DeltaUpdate,
    IReadOnlyDictionary<uint, string> ExperimentProps);

public sealed record GroupPropsResponse(
    string? Hash,
    IReadOnlyDictionary<uint, string> ExperimentProps);

/// <summary>
/// Server-driven A/B experiment configuration IQ builder &amp; parser (<c>xmlns="abt"</c>).
/// Mirrors <c>wacore/src/iq/props.rs</c> and live <c>WAWebABPropsConfigs</c>.
/// </summary>
public static class AbPropsIq
{
    public const string Xmlns = "abt";
    public const string ProtocolVersion = "1";

    // Verified live WAWebABPropsConfigs + wacore::iq::abprops numeric IDs
    public const uint PropGroupHistorySend = 15_313;
    public const uint PropGroupHistoryReceive = 15_311;
    public const uint PropGroupHistoryCountLimit = 18_405;
    public const uint PropGroupHistoryTimeLimitSecs = 18_406;
    public const uint PropGroupHistorySendGroupLevel = 23_245;
    public const uint PropGroupHistoryBundleTimeLimitReceiverSecs = 25_910;
    public const uint PropGroupMaxSubject = 14_801;
    public const uint PropGroupDescriptionLength = 14_778;
    public const uint PropGroupSizeLimit = 1_304;
    public const uint PropCommunityAnnouncementGroupSizeLimit = 2_774;
    public const uint PropParentGroupLinkLimitCommunityCreation = 1_990;
    public const uint PropEventsCreateCagEnabled = 9_932;
    public const uint PropWebCommunitiesGeneralChatV2 = 8_580;

    public static Node BuildGetProps(string iqId, string? hash = null, uint? refreshId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["protocol"] = ProtocolVersion
        };
        if (!string.IsNullOrEmpty(hash))
        {
            attrs["hash"] = hash;
        }
        if (refreshId is uint rid)
        {
            attrs["refresh_id"] = rid.ToString(CultureInfo.InvariantCulture);
        }

        return StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn, new Node("props", attrs));
    }

    public static AbPropsResponse ParseResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node propsNode = responseNode.Tag == "props"
            ? responseNode
            : responseNode.GetChild("props")
                ?? throw new InvalidDataException("Missing <props> child in A/B props response.");

        var props = new Dictionary<uint, string>();
        foreach (Node child in propsNode.GetChildren("prop"))
        {
            if (child.GetAttr("config_code") is { Length: > 0 } codeStr
                && uint.TryParse(codeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint code)
                && code > 0
                && child.GetAttr("config_value") is { } val)
            {
                props[code] = val;
            }
        }

        int? refresh = int.TryParse(propsNode.GetAttr("refresh"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : null;
        uint? refreshId = uint.TryParse(propsNode.GetAttr("refresh_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint rid) ? rid : null;
        bool delta = string.Equals(propsNode.GetAttr("delta_update"), "true", StringComparison.OrdinalIgnoreCase);

        return new AbPropsResponse(
            propsNode.GetAttr("ab_key"),
            propsNode.GetAttr("hash"),
            refresh,
            refreshId,
            delta,
            props);
    }

    public static Node BuildGetGroupProps(string iqId, Jid groupJid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        return StanzaBuilder.Iq(
            iqId,
            "get",
            Xmlns,
            StanzaBuilder.ServerPn,
            new Node("props", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["group"] = groupJid.ToNonAd()
            }));
    }

    public static GroupPropsResponse ParseGroupPropsResponse(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node propsNode = responseNode.Tag == "props"
            ? responseNode
            : responseNode.GetChild("props")
                ?? throw new InvalidDataException("Missing <props> child in group A/B props response.");

        var props = new Dictionary<uint, string>();
        foreach (Node child in propsNode.GetChildren("prop"))
        {
            if (child.GetAttr("config_code") is { Length: > 0 } codeStr
                && uint.TryParse(codeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint code)
                && code > 0
                && child.GetAttr("config_value") is { } val)
            {
                if (props.TryGetValue(code, out string? prev) && !string.Equals(prev, val, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Conflicting group experiment values for code {code}.");
                }
                props[code] = val;
            }
        }

        return new GroupPropsResponse(propsNode.GetAttr("hash"), props);
    }
}

public enum MediaHostKind
{
    Primary = 0,
    Fallback = 1,
    Other = 2
}

public sealed record MediaConnHostInfo(
    string Hostname,
    MediaHostKind Kind,
    string? FallbackHostname,
    bool SupportsUpload,
    bool SupportsDownload);

public sealed record MediaConnInfo(
    string Auth,
    long TtlSeconds,
    long? AuthTtlSeconds,
    int? MaxBuckets,
    IReadOnlyList<MediaConnHostInfo> Hosts,
    DateTimeOffset FetchedAtUtc)
{
    public bool IsExpired(TimeProvider? time = null) =>
        (time ?? TimeProvider.System).GetUtcNow() >= FetchedAtUtc + TimeSpan.FromSeconds(Math.Max(0, TtlSeconds));

    public IEnumerable<string> GetOrderedHosts()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in Hosts)
        {
            if (seen.Add(h.Hostname))
            {
                yield return h.Hostname;
            }
            if (!string.IsNullOrWhiteSpace(h.FallbackHostname) && seen.Add(h.FallbackHostname))
            {
                yield return h.FallbackHostname;
            }
        }
    }
}

/// <summary>
/// Parses <c>&lt;iq xmlns="w:m"&gt;&lt;media_conn ...&gt;</c> host and auth token responses.
/// Mirrors <c>wacore/src/iq/mediaconn.rs</c>.
/// </summary>
public static class MediaConnParser
{
    public static MediaConnInfo ParseResponse(Node responseNode, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(responseNode);
        Node mc = responseNode.Tag == "media_conn"
            ? responseNode
            : responseNode.GetChild("media_conn")
                ?? throw new InvalidDataException("Missing <media_conn> child.");

        string auth = mc.GetAttr("auth")
            ?? throw new InvalidDataException("<media_conn> missing 'auth' attribute.");
        long ttl = long.TryParse(mc.GetAttr("ttl"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) ? t : 0;
        long? authTtl = long.TryParse(mc.GetAttr("auth_ttl"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long at) ? at : null;
        int? maxBuckets = int.TryParse(mc.GetAttr("max_buckets"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mb) ? mb : null;

        var primaryHosts = new List<MediaConnHostInfo>();
        var otherHosts = new List<MediaConnHostInfo>();

        foreach (Node hNode in mc.GetChildren("host"))
        {
            string? hostname = hNode.GetAttr("hostname");
            if (string.IsNullOrWhiteSpace(hostname))
            {
                continue;
            }

            string? typeStr = hNode.GetAttr("type");
            MediaHostKind kind = typeStr switch
            {
                "primary" or null => MediaHostKind.Primary,
                "fallback" => MediaHostKind.Fallback,
                _ => MediaHostKind.Other
            };

            var info = new MediaConnHostInfo(
                hostname,
                kind,
                hNode.GetAttr("fallback_hostname"),
                hNode.GetChild("upload") is not null || typeStr is null or "primary",
                hNode.GetChild("download") is not null || typeStr is null or "primary");

            if (kind == MediaHostKind.Primary)
            {
                primaryHosts.Add(info);
            }
            else
            {
                otherHosts.Add(info);
            }
        }

        primaryHosts.AddRange(otherHosts);
        return new MediaConnInfo(auth, ttl, authTtl, maxBuckets, primaryHosts, (time ?? TimeProvider.System).GetUtcNow());
    }
}

public sealed record TcTokenPolicyConfig(
    long BucketDurationSeconds = 604_800,
    long NumBuckets = 4,
    long SenderBucketDurationSeconds = 604_800,
    long SenderNumBuckets = 4)
{
    public const long MaxBucketDurationSeconds = 15_552_000; // 180 days

    public TcTokenPolicyConfig Clamped() => new(
        Math.Clamp(BucketDurationSeconds, 1, MaxBucketDurationSeconds),
        Math.Max(1, NumBuckets),
        Math.Clamp(SenderBucketDurationSeconds, 1, MaxBucketDurationSeconds),
        Math.Max(1, SenderNumBuckets));
}

public sealed record ParsedTcToken(Jid? Jid, byte[] Token, long Timestamp);

/// <summary>
/// Trusted Contact (<c>tctoken</c>) bucket expiration math and <c>xmlns="privacy"</c> IQ/notification codecs.
/// Mirrors <c>wacore/src/iq/tctoken.rs</c> and live <c>WAWebTrustedContactsUtils</c>.
/// </summary>
public static class TcTokenProtocol
{
    public const string PrivacyXmlns = "privacy";
    public const string TrustedContactType = "trusted_contact";

    public static long GetExpirationCutoffUnix(long nowUnixSeconds, TcTokenPolicyConfig? config = null)
    {
        TcTokenPolicyConfig cfg = (config ?? new TcTokenPolicyConfig()).Clamped();
        long currentBucket = nowUnixSeconds / cfg.BucketDurationSeconds;
        long expiredBucket = currentBucket - (cfg.NumBuckets - 1);
        return expiredBucket * cfg.BucketDurationSeconds;
    }

    public static bool IsTokenExpired(long tokenTimestampUnix, long nowUnixSeconds, TcTokenPolicyConfig? config = null) =>
        tokenTimestampUnix < GetExpirationCutoffUnix(nowUnixSeconds, config);

    public static bool ShouldSendNewToken(long? senderTimestampUnix, long nowUnixSeconds, TcTokenPolicyConfig? config = null)
    {
        if (senderTimestampUnix is not long ts)
        {
            return true;
        }

        TcTokenPolicyConfig cfg = (config ?? new TcTokenPolicyConfig()).Clamped();
        return (nowUnixSeconds / cfg.SenderBucketDurationSeconds) > (ts / cfg.SenderBucketDurationSeconds);
    }

    public static Node BuildIssuePrivacyTokensIq(string iqId, IReadOnlyList<Jid> jids, long nowUnixSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentNullException.ThrowIfNull(jids);

        var tokenNodes = new Node[jids.Count];
        string tsStr = nowUnixSeconds.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < jids.Count; i++)
        {
            tokenNodes[i] = new Node(
                "token",
                new Dictionary<string, NodeValue>(StringComparer.Ordinal)
                {
                    ["jid"] = jids[i],
                    ["t"] = tsStr,
                    ["type"] = TrustedContactType
                });
        }

        return StanzaBuilder.Iq(iqId, "set", PrivacyXmlns, StanzaBuilder.ServerPn, new Node("tokens", tokenNodes));
    }

    public static IReadOnlyList<ParsedTcToken> ParseTokensResponse(Node responseOrNotification)
    {
        ArgumentNullException.ThrowIfNull(responseOrNotification);
        Node? tokensNode = responseOrNotification.Tag == "tokens"
            ? responseOrNotification
            : responseOrNotification.GetChild("tokens");
        if (tokensNode is null)
        {
            return Array.Empty<ParsedTcToken>();
        }

        var list = new List<ParsedTcToken>();
        foreach (Node tokenNode in tokensNode.GetChildren("token"))
        {
            string? type = tokenNode.GetAttr("type");
            if (!string.IsNullOrEmpty(type) && !string.Equals(type, TrustedContactType, StringComparison.Ordinal))
            {
                continue;
            }

            if (!long.TryParse(tokenNode.GetAttr("t"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ts))
            {
                continue;
            }

            if (tokenNode.Content is not NodeContent.BytesContent bytes || bytes.Bytes.Length == 0)
            {
                continue;
            }

            Jid? jid = tokenNode.GetAttr("jid") is { Length: > 0 } jStr && Jid.TryParse(jStr, out Jid parsedJid)
                ? parsedJid
                : null;

            list.Add(new ParsedTcToken(jid, bytes.Bytes, ts));
        }

        return list;
    }
}

/// <summary>
/// Helpers for validating and storing <c>AppStateSyncKeyShare</c> payloads from self-sent messages.
/// </summary>
public static class AppStateKeyShareHelper
{
    public static bool IsSelfSender(Jid sender, DeviceCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        Jid senderBase = sender.ToNonAd();
        return (credentials.Pn is Jid pn && senderBase == pn.ToNonAd())
            || (credentials.Lid is Jid lid && senderBase == lid.ToNonAd());
    }

    public static async Task<int> SaveKeyShareAsync(
        Message.Types.AppStateSyncKeyShare share,
        IAppSyncStore appSyncStore,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(appSyncStore);

        int saved = 0;
        foreach (Message.Types.AppStateSyncKey key in share.Keys)
        {
            if (key.KeyId is { HasKeyId: true } && key.KeyId.KeyId.Length > 0
                && key.KeyData is { HasKeyData: true } && key.KeyData.KeyData.Length > 0)
            {
                await appSyncStore.SaveSyncKeyAsync(
                    key.KeyId.KeyId.ToByteArray(),
                    key.KeyData.KeyData.ToByteArray(),
                    ct).ConfigureAwait(false);
                saved++;
            }
        }
        return saved;
    }
}
