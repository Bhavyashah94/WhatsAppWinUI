using System.Text.Json;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Api;

public sealed class MexException : Exception
{
    public string OperationName { get; }
    public IReadOnlyList<MexGraphQLError> Errors { get; }

    public MexException(string operationName, string message, IReadOnlyList<MexGraphQLError>? errors = null)
        : base(message)
    {
        OperationName = operationName;
        Errors = errors ?? Array.Empty<MexGraphQLError>();
    }
}

/// <summary>
/// Executes MEX (<c>xmlns="w:mex"</c>) persisted GraphQL queries and mutations over <see cref="IqClient"/>.
/// </summary>
public sealed class MexClient
{
    private readonly IqClient _iqClient;

    public MexClient(IqClient iqClient)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
    }

    public async Task<MexExecutionResponse> ExecuteAsync(
        MexDoc doc,
        object variables,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(variables);

        string iqId = _iqClient.NextRequestId();
        Node iq = MexStanzas.BuildQuery(iqId, doc, variables);
        Node resp = await _iqClient.SendIqAsync(iq, timeout ?? IqClient.DefaultTimeout, ct).ConfigureAwait(false);

        MexExecutionResponse parsed = MexStanzas.ParseResponse(resp);
        if (parsed.HasFatalErrors)
        {
            string firstMsg = parsed.Errors.FirstOrDefault()?.Message ?? "Unknown MEX GraphQL error";
            throw new MexException(doc.Name, $"MEX GraphQL '{doc.Name}' returned error(s): {firstMsg}", parsed.Errors);
        }

        return parsed;
    }

    public static string? TryGetString(JsonElement? data, string property1, string property2)
    {
        if (data is not { ValueKind: JsonValueKind.Object } root)
        {
            return null;
        }
        if (root.TryGetProperty(property1, out JsonElement el1)
            && el1.ValueKind == JsonValueKind.Object
            && el1.TryGetProperty(property2, out JsonElement el2)
            && el2.ValueKind == JsonValueKind.String)
        {
            return el2.GetString();
        }
        return null;
    }
}
