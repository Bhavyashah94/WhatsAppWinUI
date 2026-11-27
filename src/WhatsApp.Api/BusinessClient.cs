using System.Text;
using System.Text.Json;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Api;

public sealed class BusinessCatalogException : Exception
{
    public int? ErrorCode { get; }

    public BusinessCatalogException(string message, int? errorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// WhatsApp Business client handling <c>w:biz</c> profile reads/updates, cover photos,
/// verified-name/business USync lookups, correlated catalog owner token exchange
/// (<c>fb:thrift_iq</c> + <c>wa_ad_account_nonce</c>), visitor/owner HTTP GraphQL catalogs
/// &amp; collections, and MEX <c>WAWebBizQueryOrderJobQuery</c> order inspection.
/// </summary>
public sealed class BusinessClient
{
    /// <summary>Matches live <c>web.whatsapp.com</c> AB prop <c>ctwa_ad_account_nonce_push_wait_timeout_web</c> (40s).</summary>
    public static readonly TimeSpan NonceWaitTimeout = TimeSpan.FromSeconds(40);

    private readonly IqClient _iqClient;
    private readonly MexClient _mexClient;
    private readonly UsyncClient _usync;
    private readonly DeviceCredentials _credentials;
    private readonly IHttpClient? _httpClient;
    private readonly SemaphoreSlim _ownerTokenLock = new(1, 1);
    private TaskCompletionSource<string>? _pendingNonceWaiter;
    private string? _cachedOwnerToken;

    public BusinessClient(
        IqClient iqClient,
        MexClient mexClient,
        UsyncClient usync,
        DeviceCredentials credentials,
        IHttpClient? httpClient = null)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _mexClient = mexClient ?? throw new ArgumentNullException(nameof(mexClient));
        _usync = usync ?? throw new ArgumentNullException(nameof(usync));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _httpClient = httpClient;
    }

    // ─── w:biz Profiles & Verified Names ─────────────────────────────────

    public async Task<BusinessProfile?> GetProfileAsync(Jid businessJid, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = BusinessStanzas.BuildGetBusinessProfileIq(iqId, businessJid);
        Node resp = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        return BusinessStanzas.ParseBusinessProfileResponse(resp);
    }

    public async Task UpdateProfileAsync(BusinessProfileUpdate update, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = BusinessStanzas.BuildUpdateBusinessProfileIq(iqId, update);
        await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
    }

    public async Task SetCoverPhotoAsync(CoverPhotoUpload upload, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = BusinessStanzas.BuildSetCoverPhotoIq(iqId, upload);
        await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
    }

    public async Task RemoveCoverPhotoAsync(string photoId, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = BusinessStanzas.BuildRemoveCoverPhotoIq(iqId, photoId);
        await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
    }

    public async Task<UsyncVerifiedNameInfo?> GetVerifiedNameAsync(Jid userJid, CancellationToken ct = default)
    {
        Jid bare = userJid.ToNonAd();
        IReadOnlyDictionary<string, UsyncUserResult> users = await _usync.GetUserInfoAsync(
            [bare],
            UsyncProtocolSet.Business,
            UsyncContext.Interactive,
            ct).ConfigureAwait(false);

        return users.TryGetValue(bare.ToString(), out UsyncUserResult? user)
            ? user.VerifiedName
            : users.Values.FirstOrDefault()?.VerifiedName;
    }

    // ─── Catalog & Collections (Owner + Visitor GraphQL) ──────────────────

    public bool TryResolveCatalogNonceNotification(string nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        TaskCompletionSource<string>? waiter = Volatile.Read(ref _pendingNonceWaiter);
        return waiter is not null && waiter.TrySetResult(nonce);
    }

    public async Task<BusinessCatalog> GetCatalogAsync(
        Jid businessJid,
        uint limit = 10,
        uint imageWidth = 100,
        uint imageHeight = 100,
        bool allowShopSource = false,
        string? afterCursor = null,
        string? visitorToken = null,
        CancellationToken ct = default)
    {
        bool isOwn = IsOwnBusinessJid(businessJid);
        object variables = BusinessStanzas.BuildCatalogVariables(
            businessJid,
            limit,
            imageWidth,
            imageHeight,
            allowShopSource,
            afterCursor,
            isOwner: isOwn);

        JsonElement data;
        if (isOwn)
        {
            data = await ExecuteOwnerCatalogHttpAsync(MexStanzas.BizQueryOwnerCatalog.Id, variables, ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(visitorToken))
        {
            data = await ExecuteVisitorCatalogHttpAsync(MexStanzas.BizQueryCatalog.Id, variables, visitorToken, ct).ConfigureAwait(false);
        }
        else
        {
            MexExecutionResponse mexResp = await _mexClient.ExecuteAsync(MexStanzas.BizQueryCatalog, variables, ct: ct).ConfigureAwait(false);
            data = mexResp.Data is { ValueKind: JsonValueKind.Object } d
                ? d
                : throw new BusinessCatalogException("Visitor catalog MEX query returned no data.");
        }

        return BusinessStanzas.ParseCatalogResponse(data);
    }

    public async Task<BusinessCollections> GetCollectionsAsync(
        Jid businessJid,
        uint collectionLimit = 51,
        uint itemLimit = 51,
        uint imageWidth = 100,
        uint imageHeight = 100,
        string? afterCursor = null,
        string? visitorToken = null,
        CancellationToken ct = default)
    {
        bool isOwn = IsOwnBusinessJid(businessJid);
        object variables = BusinessStanzas.BuildCollectionsVariables(
            businessJid,
            collectionLimit,
            itemLimit,
            imageWidth,
            imageHeight,
            afterCursor);

        JsonElement data;
        if (isOwn)
        {
            data = await ExecuteOwnerCatalogHttpAsync(MexStanzas.BizQueryOwnerCollections.Id, variables, ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(visitorToken))
        {
            data = await ExecuteVisitorCatalogHttpAsync(MexStanzas.BizQueryCollections.Id, variables, visitorToken, ct).ConfigureAwait(false);
        }
        else
        {
            MexExecutionResponse mexResp = await _mexClient.ExecuteAsync(MexStanzas.BizQueryCollections, variables, ct: ct).ConfigureAwait(false);
            data = mexResp.Data is { ValueKind: JsonValueKind.Object } d
                ? d
                : throw new BusinessCatalogException("Visitor collections MEX query returned no data.");
        }

        return BusinessStanzas.ParseCollectionsResponse(data);
    }

    // ─── Orders (MEX) ────────────────────────────────────────────────────

    public async Task<BusinessOrder> GetOrderAsync(
        Jid businessJid,
        string orderId,
        string token,
        uint imageWidth = 100,
        uint imageHeight = 100,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.BizQueryOrder,
            BusinessStanzas.BuildOrderVariables(businessJid, orderId, token, imageWidth, imageHeight),
            ct: ct).ConfigureAwait(false);

        if (resp.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            throw new BusinessCatalogException("Order query returned no data.");
        }
        return BusinessStanzas.ParseOrderResponse(data);
    }

    private async Task<JsonElement> ExecuteVisitorCatalogHttpAsync(
        string docId,
        object variables,
        string? visitorToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(visitorToken))
        {
            throw new BusinessCatalogException("Visitor catalog reads require a WhatsApp catalog application token.");
        }

        IHttpClient http = RequireHttpClient();
        string payload = JsonSerializer.Serialize(new
        {
            access_token = visitorToken,
            doc_id = docId,
            variables,
            lang = "en_US"
        });
        return await PostCatalogGraphQlAsync(BusinessStanzas.VisitorCatalogEndpoint, payload, http, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExecuteOwnerCatalogHttpAsync(string docId, object variables, CancellationToken ct)
    {
        IHttpClient http = RequireHttpClient();
        string token = await GetOrFetchOwnerTokenAsync(ct).ConfigureAwait(false);
        string payload = JsonSerializer.Serialize(new
        {
            access_token = token,
            doc_id = docId,
            variables,
            locale = "en_US"
        });

        try
        {
            return await PostCatalogGraphQlAsync(BusinessStanzas.OwnerCatalogEndpoint, payload, http, ct).ConfigureAwait(false);
        }
        catch (BusinessCatalogException ex) when (ex.ErrorCode == 190)
        {
            await InvalidateOwnerTokenAsync(token, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> GetOrFetchOwnerTokenAsync(CancellationToken ct)
    {
        await _ownerTokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_cachedOwnerToken))
            {
                return _cachedOwnerToken;
            }

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pendingNonceWaiter, tcs);
            try
            {
                Node nonceResp = await _iqClient.SendIqAsync(
                    BusinessStanzas.BuildCatalogNonceRequestIq(_iqClient.NextRequestId()),
                    IqClient.DefaultTimeout,
                    ct).ConfigureAwait(false);

                CatalogNonceStatus status = BusinessStanzas.ParseCatalogNonceResponse(nonceResp);
                if (status == CatalogNonceStatus.RecoveryRequired)
                {
                    throw new BusinessCatalogException("Catalog owner authentication requires account recovery in WhatsApp.");
                }
                if (status != CatalogNonceStatus.Success)
                {
                    throw new BusinessCatalogException($"Catalog owner nonce request returned unexpected status '{status}'.");
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(NonceWaitTimeout);
                string nonce = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);

                Node tokenResp = await _iqClient.SendIqAsync(
                    BusinessStanzas.BuildCatalogTokenExchangeIq(_iqClient.NextRequestId(), nonce),
                    IqClient.DefaultTimeout,
                    ct).ConfigureAwait(false);

                string token = BusinessStanzas.ParseCatalogTokenExchangeResponse(tokenResp)
                    ?? throw new BusinessCatalogException("Catalog token exchange returned empty access_token.");

                _cachedOwnerToken = token;
                return token;
            }
            finally
            {
                Volatile.Write(ref _pendingNonceWaiter, null);
            }
        }
        finally
        {
            _ownerTokenLock.Release();
        }
    }

    private async Task InvalidateOwnerTokenAsync(string staleToken, CancellationToken ct)
    {
        await _ownerTokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.Equals(_cachedOwnerToken, staleToken, StringComparison.Ordinal))
            {
                _cachedOwnerToken = null;
            }
        }
        finally
        {
            _ownerTokenLock.Release();
        }
    }

    private static async Task<JsonElement> PostCatalogGraphQlAsync(
        string endpoint,
        string jsonPayload,
        IHttpClient http,
        CancellationToken ct)
    {
        byte[] respBytes = await http.PostBytesAsync(
            new Uri(endpoint, UriKind.Absolute),
            Encoding.UTF8.GetBytes(jsonPayload),
            "application/json",
            ct).ConfigureAwait(false);

        using JsonDocument doc = JsonDocument.Parse(respBytes);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("error", out JsonElement errObj) && errObj.ValueKind == JsonValueKind.Object)
        {
            int? code = ExtractGraphQlErrorCode(errObj);
            throw new BusinessCatalogException($"Catalog GraphQL error (code={code}).", code);
        }
        if (root.TryGetProperty("errors", out JsonElement errArr) && errArr.ValueKind == JsonValueKind.Array && errArr.GetArrayLength() > 0)
        {
            int? code = null;
            foreach (JsonElement item in errArr.EnumerateArray())
            {
                int? c = ExtractGraphQlErrorCode(item);
                if (c == 190) { code = 190; break; }
                code ??= c;
            }
            throw new BusinessCatalogException($"Catalog GraphQL error(s) (code={code}).", code);
        }

        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new BusinessCatalogException("Catalog GraphQL response carried no data object.");
        }
        return data.Clone();
    }

    private static int? ExtractGraphQlErrorCode(JsonElement err)
    {
        if (err.TryGetProperty("extensions", out JsonElement ext)
            && ext.ValueKind == JsonValueKind.Object
            && ext.TryGetProperty("error_code", out JsonElement ec)
            && ec.TryGetInt32(out int extCode)
            && extCode == 190)
        {
            return 190;
        }
        if (err.TryGetProperty("code", out JsonElement c) && c.TryGetInt32(out int directCode))
        {
            return directCode;
        }
        if (err.TryGetProperty("extensions", out JsonElement ext2)
            && ext2.ValueKind == JsonValueKind.Object
            && ext2.TryGetProperty("error_code", out JsonElement ec2)
            && ec2.TryGetInt32(out int anyExtCode))
        {
            return anyExtCode;
        }
        return null;
    }

    private bool IsOwnBusinessJid(Jid target)
    {
        Jid bare = target.ToNonAd();
        return (_credentials.Pn is Jid pn && pn.ToNonAd() == bare)
            || (_credentials.Lid is Jid lid && lid.ToNonAd() == bare);
    }

    private IHttpClient RequireHttpClient() =>
        _httpClient ?? throw new InvalidOperationException("Business catalog HTTP calls require an IHttpClient configured in ConnectionOrchestratorOptions.");
}
