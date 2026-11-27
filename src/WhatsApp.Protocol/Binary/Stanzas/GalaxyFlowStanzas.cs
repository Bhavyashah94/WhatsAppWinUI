using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace WhatsApp.Protocol.Binary.Stanzas;

public sealed record GalaxyFlowEndpointPublicKey(
    string KeyPem,
    string Signature);

public sealed record GalaxyFlowMetadata(
    string FlowId,
    string? FlowName = null,
    string? State = null,
    IReadOnlyList<string>? Categories = null,
    string? CreationSource = null,
    string? WwwProxySecret = null,
    string? FlowTokenSignature = null);

public sealed record GalaxyFlowDataResult(
    IReadOnlyList<GalaxyFlowMetadata> Flows,
    GalaxyFlowEndpointPublicKey? EndpointPublicKey);

public sealed record EncryptedFlowRequestPayload(
    [property: JsonPropertyName("encrypted_flow_data")] string EncryptedFlowDataBase64,
    [property: JsonPropertyName("encrypted_aes_key")] string EncryptedAesKeyBase64,
    [property: JsonPropertyName("initial_vector")] string InitialVectorBase64);

/// <summary>
/// MEX variable builders, response parsers, native-flow response JSON builders, and RSA-OAEP / AES-GCM
/// endpoint crypto helpers for WhatsApp Flows (<c>galaxy_message</c>), <c>address_message</c>, and
/// <c>call_permission_request</c>.
/// </summary>
public static class GalaxyFlowStanzas
{
    public const string DefaultFlowsHost = "https://web.whatsapp.com";

    public static Uri BuildFlowJsonUri(string flowId, string? baseOrigin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        foreach (char c in flowId)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                throw new ArgumentException($"Invalid WhatsApp Flow ID '{flowId}'.", nameof(flowId));
            }
        }
        string origin = string.IsNullOrWhiteSpace(baseOrigin) ? DefaultFlowsHost : baseOrigin.TrimEnd('/');
        return new Uri($"{origin}/flows/json/{flowId}", UriKind.Absolute);
    }

    public static object BuildGetFlowDataVariables(Jid businessJid, string flowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        return new
        {
            request = new
            {
                extensions = new
                {
                    biz_jid = businessJid.ToNonAdString(),
                    flow_id = flowId
                }
            }
        };
    }

    public static GalaxyFlowDataResult ParseGetFlowDataResponse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("xwa_extensions_get_flow_data", out JsonElement root)
            || root.ValueKind != JsonValueKind.Object)
        {
            return new GalaxyFlowDataResult(Array.Empty<GalaxyFlowMetadata>(), null);
        }

        GalaxyFlowEndpointPublicKey? pubKey = null;
        if (root.TryGetProperty("endpoint_public_key", out JsonElement pkEl) && pkEl.ValueKind == JsonValueKind.Object)
        {
            string? key = pkEl.TryGetProperty("key", out JsonElement kEl) && kEl.ValueKind == JsonValueKind.String ? kEl.GetString() : null;
            string? sig = pkEl.TryGetProperty("signature", out JsonElement sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
            if (!string.IsNullOrEmpty(key))
            {
                pubKey = new GalaxyFlowEndpointPublicKey(key, sig ?? string.Empty);
            }
        }

        var flows = new List<GalaxyFlowMetadata>();
        if (root.TryGetProperty("extensions_flow_data", out JsonElement arrEl) && arrEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in arrEl.EnumerateArray())
            {
                string? flowId = item.TryGetProperty("flow_id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                if (string.IsNullOrEmpty(flowId))
                {
                    continue;
                }

                string? flowName = null;
                string? state = null;
                string? creationSource = null;
                string? wwwProxySecret = null;
                string? flowTokenSignature = null;
                List<string>? categories = null;

                if (item.TryGetProperty("extensions", out JsonElement extEl)
                    && extEl.ValueKind == JsonValueKind.Object
                    && extEl.TryGetProperty("metadata", out JsonElement metaEl)
                    && metaEl.ValueKind == JsonValueKind.Object)
                {
                    flowName = GetString(metaEl, "flow_name");
                    state = GetString(metaEl, "state");
                    creationSource = GetString(metaEl, "creation_source");
                    wwwProxySecret = GetString(metaEl, "www_proxy_secret");
                    flowTokenSignature = GetString(metaEl, "flow_token_signature");
                    if (metaEl.TryGetProperty("categories", out JsonElement catEl) && catEl.ValueKind == JsonValueKind.Array)
                    {
                        categories = [];
                        foreach (JsonElement c in catEl.EnumerateArray())
                        {
                            if (c.ValueKind == JsonValueKind.String && c.GetString() is { } cs)
                            {
                                categories.Add(cs);
                            }
                        }
                    }
                }

                flows.Add(new GalaxyFlowMetadata(
                    flowId,
                    flowName,
                    state,
                    categories?.ToArray(),
                    creationSource,
                    wwwProxySecret,
                    flowTokenSignature));
            }
        }

        return new GalaxyFlowDataResult(flows.ToArray(), pubKey);
    }

    public static EncryptedFlowRequestPayload EncryptFlowEndpointPayload(
        string payloadJson,
        string endpointPublicKeyPem,
        ReadOnlySpan<byte> aesKey = default,
        ReadOnlySpan<byte> iv = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointPublicKeyPem);

        byte[] keyBytes = aesKey.IsEmpty ? RandomNumberGenerator.GetBytes(16) : aesKey.ToArray();
        byte[] ivBytes = iv.IsEmpty ? RandomNumberGenerator.GetBytes(16) : iv.ToArray();

        if (keyBytes.Length is not (16 or 32))
        {
            throw new ArgumentException("Flows AES key must be 16 or 32 bytes.", nameof(aesKey));
        }
        if (ivBytes.Length is not (12 or 16))
        {
            throw new ArgumentException("Flows IV must be 12 or 16 bytes.", nameof(iv));
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(payloadJson);
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(forEncryption: true, new AeadParameters(new KeyParameter(keyBytes), 128, ivBytes));
        byte[] combined = new byte[cipher.GetOutputSize(plaintext.Length)];
        int len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, combined, 0);
        cipher.DoFinal(combined, len);

        byte[] encryptedAesKey;
        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(endpointPublicKeyPem);
            encryptedAesKey = rsa.Encrypt(keyBytes, RSAEncryptionPadding.OaepSHA256);
        }

        return new EncryptedFlowRequestPayload(
            Convert.ToBase64String(combined),
            Convert.ToBase64String(encryptedAesKey),
            Convert.ToBase64String(ivBytes));
    }

    public static string DecryptFlowEndpointResponse(
        string encryptedResponseBase64,
        ReadOnlySpan<byte> aesKey,
        ReadOnlySpan<byte> requestIv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedResponseBase64);
        byte[] raw = Convert.FromBase64String(encryptedResponseBase64);
        if (raw.Length < 16)
        {
            throw new InvalidDataException("Encrypted Flows response shorter than 16-byte authentication tag.");
        }

        byte[] responseIv = new byte[requestIv.Length];
        for (int i = 0; i < requestIv.Length; i++)
        {
            responseIv[i] = (byte)~requestIv[i];
        }

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(forEncryption: false, new AeadParameters(new KeyParameter(aesKey.ToArray()), 128, responseIv));
        byte[] plaintext = new byte[cipher.GetOutputSize(raw.Length)];
        int len = cipher.ProcessBytes(raw, 0, raw.Length, plaintext, 0);
        int finalLen = cipher.DoFinal(plaintext, len);

        return Encoding.UTF8.GetString(plaintext, 0, len + finalLen);
    }

    public static string BuildFlowResponseParamsJson(
        string flowToken,
        IReadOnlyDictionary<string, object?> formValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowToken);
        ArgumentNullException.ThrowIfNull(formValues);

        var merged = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["flow_token"] = flowToken
        };
        foreach (var kv in formValues)
        {
            if (!string.Equals(kv.Key, "flow_token", StringComparison.Ordinal))
            {
                merged[kv.Key] = kv.Value;
            }
        }
        return JsonSerializer.Serialize(merged);
    }

    public static string BuildAddressResponseParamsJson(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return JsonSerializer.Serialize(new { values });
    }

    public static string BuildCallPermissionResponseParamsJson(
        string responseStatus,
        long? expirationTimestampSeconds = null,
        string? source = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseStatus);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["response"] = responseStatus.ToLowerInvariant()
        };
        if (expirationTimestampSeconds is long exp)
        {
            payload["expiration_timestamp"] = exp;
        }
        if (!string.IsNullOrWhiteSpace(source))
        {
            payload["response_source"] = source;
        }
        return JsonSerializer.Serialize(payload);
    }

    private static string? GetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
