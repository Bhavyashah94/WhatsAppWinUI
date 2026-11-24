namespace WhatsApp.Protocol.Binary.Stanzas;

/// <summary>
/// Protocol acknowledgment (<c>&lt;ack&gt;</c>) stanza builder.
/// </summary>
public static class AckStanza
{
    public static Node Build(
        string stanzaId,
        string classType,
        Jid to,
        string? type = null,
        Jid? participant = null,
        string? errorCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stanzaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(classType);

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = stanzaId,
            ["class"] = classType,
            ["to"] = to
        };

        if (!string.IsNullOrWhiteSpace(type))
        {
            attrs["type"] = type;
        }
        if (participant is Jid p)
        {
            attrs["participant"] = p;
        }
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            attrs["error"] = errorCode;
        }

        return new Node("ack", attrs);
    }
}

/// <summary>
/// Delivery / read / played / retry <c>&lt;receipt&gt;</c> stanza builder.
/// </summary>
public static class ReceiptStanza
{
    public const string TypeRead = "read";
    public const string TypeReadSelf = "read-self";
    public const string TypePlayed = "played";
    public const string TypePlayedSelf = "played-self";
    public const string TypeRetry = "retry";

    /// <summary>
    /// Builds a single or aggregated <c>&lt;receipt&gt;</c> stanza. Pass <paramref name="type"/> = <c>null</c>
    /// for a standard delivery receipt.
    /// </summary>
    public static Node Build(
        string messageId,
        Jid to,
        string? type = null,
        Jid? participant = null,
        Jid? recipient = null,
        IReadOnlyList<string>? additionalMessageIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = messageId,
            ["to"] = to
        };

        if (!string.IsNullOrWhiteSpace(type))
        {
            attrs["type"] = type;
        }
        if (participant is Jid p)
        {
            attrs["participant"] = p;
        }
        if (recipient is Jid r)
        {
            attrs["recipient"] = r;
        }

        if (additionalMessageIds is { Count: > 0 })
        {
            var items = new Node[additionalMessageIds.Count];
            for (int i = 0; i < additionalMessageIds.Count; i++)
            {
                items[i] = new Node("item", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
                {
                    ["id"] = additionalMessageIds[i]
                });
            }
            return new Node("receipt", attrs, new Node[] { new("list", items) });
        }

        return new Node("receipt", attrs);
    }
}
