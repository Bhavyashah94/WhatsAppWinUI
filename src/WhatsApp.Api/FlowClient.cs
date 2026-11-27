using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Api;

/// <summary>
/// WhatsApp Flows (<c>galaxy_message</c>) client handling MEX flow metadata/key lookup
/// (<c>xwa_extensions_get_flow_data</c>), Flow JSON fetching, and RSA-OAEP / AES-GCM
/// encrypted data-exchange endpoint calls.
/// </summary>
public sealed class FlowClient
{
    private readonly MexClient _mexClient;
    private readonly ILidPnCache? _lidPnCache;
    private readonly IHttpClient? _httpClient;

    public FlowClient(
        MexClient mexClient,
        ILidPnCache? lidPnCache = null,
        IHttpClient? httpClient = null)
    {
        _mexClient = mexClient ?? throw new ArgumentNullException(nameof(mexClient));
        _lidPnCache = lidPnCache;
        _httpClient = httpClient;
    }

    public async Task<GalaxyFlowDataResult> GetFlowDataAsync(
        Jid businessJid,
        string flowId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);

        Jid normalizedBiz = businessJid.ToNonAd();
        if (normalizedBiz.IsLid()
            && _lidPnCache?.TryGetPn(normalizedBiz.ToString(), out string? pnStr) == true
            && !string.IsNullOrEmpty(pnStr)
            && Jid.TryParse(pnStr, out Jid pn))
        {
            normalizedBiz = pn.ToNonAd();
        }

        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.ExtensionsGetFlowData,
            GalaxyFlowStanzas.BuildGetFlowDataVariables(normalizedBiz, flowId),
            ct: ct).ConfigureAwait(false);

        if (resp.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            throw new BusinessCatalogException("WhatsApp Flow metadata query returned no data.");
        }

        return GalaxyFlowStanzas.ParseGetFlowDataResponse(data);
    }

    public async Task<JsonElement> FetchFlowJsonAsync(
        string flowId,
        string? baseOrigin = null,
        CancellationToken ct = default)
    {
        IHttpClient http = RequireHttpClient();
        Uri uri = GalaxyFlowStanzas.BuildFlowJsonUri(flowId, baseOrigin);
        byte[] bytes = await http.GetBytesAsync(uri, IHttpClient.DefaultMaxGetBytes, ct).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    public async Task<string> SendEncryptedDataExchangeAsync(
        Uri endpointUrl,
        string payloadJson,
        string endpointPublicKeyPem,
        byte[]? aesKey = null,
        byte[]? iv = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpointUrl);
        IHttpClient http = RequireHttpClient();

        byte[] keyBytes = aesKey ?? RandomNumberGenerator.GetBytes(16);
        byte[] ivBytes = iv ?? RandomNumberGenerator.GetBytes(16);

        EncryptedFlowRequestPayload reqPayload = GalaxyFlowStanzas.EncryptFlowEndpointPayload(
            payloadJson,
            endpointPublicKeyPem,
            keyBytes,
            ivBytes);

        byte[] reqBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reqPayload));
        byte[] respBytes = await http.PostBytesAsync(endpointUrl, reqBytes, "application/json", ct).ConfigureAwait(false);
        string rawResp = Encoding.UTF8.GetString(respBytes).Trim();

        if (rawResp.StartsWith('{'))
        {
            using JsonDocument doc = JsonDocument.Parse(rawResp);
            if (doc.RootElement.TryGetProperty("encrypted_flow_data", out JsonElement efdEl) && efdEl.ValueKind == JsonValueKind.String)
            {
                rawResp = efdEl.GetString() ?? string.Empty;
            }
        }
        else if (rawResp.Length >= 2 && rawResp.StartsWith('"') && rawResp.EndsWith('"'))
        {
            rawResp = JsonSerializer.Deserialize<string>(rawResp) ?? string.Empty;
        }

        return GalaxyFlowStanzas.DecryptFlowEndpointResponse(rawResp, keyBytes, ivBytes);
    }

    private IHttpClient RequireHttpClient() =>
        _httpClient ?? throw new InvalidOperationException("WhatsApp Flows HTTP requests require an IHttpClient configured in ConnectionOrchestratorOptions.");
}
