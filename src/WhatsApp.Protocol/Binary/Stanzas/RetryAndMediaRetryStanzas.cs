using System.Globalization;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum RetryReason : byte
{
    UnknownError = 0,
    NoSession = 1,
    InvalidKey = 2,
    InvalidKeyId = 3,
    InvalidMessage = 4,
    InvalidSignature = 5,
    FutureMessage = 6,
    BadMac = 7,
    InvalidSession = 8,
    InvalidMsgKey = 9,
    BadBroadcastEphemeralSetting = 10,
    UnknownCompanionNoPrekey = 11,
    AdvFailure = 12,
    StatusRevokeDelay = 13,
}

public sealed record RetryPreKeyUploadBundle(
    byte[] IdentityPublicKey32,
    uint OneTimePreKeyId,
    byte[] OneTimePreKeyPublic32,
    uint SignedPreKeyId,
    byte[] SignedPreKeyPublic32,
    byte[] SignedPreKeySignature64,
    byte[]? DeviceIdentityBytes = null);

public sealed record ParsedReceiptStanza(
    string MessageId,
    Jid From,
    string Type,
    Jid? Participant,
    Jid? Recipient,
    long TimestampSeconds,
    IReadOnlyList<string> AdditionalMessageIds,
    int? RetryCount,
    uint? RegistrationId,
    PreKeyBundle? RetryKeyBundle,
    byte[]? ServerErrorEncPayload,
    byte[]? ServerErrorEncIv);

public sealed record ParsedMediaRetryNotification(
    Jid From,
    string StanzaId,
    Jid? RmrJid,
    bool FromMe,
    Jid? RmrParticipant,
    byte[] EncPayload,
    byte[] EncIv12,
    int? ErrorCode);

/// <summary>
/// Signal retry receipt &amp; MediaRetry server-error receipt / notification codecs.
/// Matches <c>WAWebSendRetryReceiptJob</c>, <c>WAWebCryptoMediaRetry</c>, and
/// <c>wacore/src/protocol/retry.rs</c>.
/// </summary>
public static class RetryAndMediaRetryStanzas
{
    public const int MaxRetryCount = 5;
    public const int MinRetryCountForKeys = 2;
    public const int ReceiptModeHidFailedDecrypt = 1;

    public static Node BuildRetryReceipt(
        string messageId,
        Jid toJid,
        long originalTimestampSeconds,
        int retryCount,
        uint registrationId,
        RetryReason reason,
        Jid? participant = null,
        Jid? recipient = null,
        RetryPreKeyUploadBundle? localKeys = null,
        bool hideFailedDecrypt = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = messageId,
            ["to"] = toJid,
            ["type"] = ReceiptStanza.TypeRetry
        };
        if (participant is Jid p)
        {
            attrs["participant"] = p;
        }
        if (recipient is Jid r)
        {
            attrs["recipient"] = r;
        }

        var retryAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["count"] = retryCount.ToString(CultureInfo.InvariantCulture),
            ["id"] = messageId,
            ["t"] = originalTimestampSeconds.ToString(CultureInfo.InvariantCulture),
            ["v"] = "1"
        };
        if (reason != RetryReason.UnknownError)
        {
            retryAttrs["error"] = ((byte)reason).ToString(CultureInfo.InvariantCulture);
        }

        var children = new List<Node>
        {
            new("retry", retryAttrs),
            new("registration", StanzaBuilder.EncodeUInt32BigEndian(registrationId))
        };

        if (hideFailedDecrypt)
        {
            children.Add(new Node("meta", new Dictionary<string, NodeValue>
            {
                ["mode"] = ReceiptModeHidFailedDecrypt.ToString(CultureInfo.InvariantCulture)
            }));
        }

        if (localKeys is not null && retryCount >= MinRetryCountForKeys)
        {
            var keysChildren = new List<Node>
            {
                new("type", new[] { PreKeyIq.DjbKeyType }),
                new("identity", localKeys.IdentityPublicKey32),
                new("key", new Node[]
                {
                    new("id", StanzaBuilder.EncodeKeyId24(localKeys.OneTimePreKeyId)),
                    new("value", localKeys.OneTimePreKeyPublic32)
                }),
                new("skey", new Node[]
                {
                    new("id", StanzaBuilder.EncodeKeyId24(localKeys.SignedPreKeyId)),
                    new("value", localKeys.SignedPreKeyPublic32),
                    new("signature", localKeys.SignedPreKeySignature64)
                })
            };
            if (localKeys.DeviceIdentityBytes is { Length: > 0 } devId)
            {
                keysChildren.Add(new Node("device-identity", devId));
            }
            children.Add(new Node("keys", keysChildren));
        }

        return new Node("receipt", attrs, new NodeContent.NodesContent(children));
    }

    public static ParsedReceiptStanza? ParseIncomingReceipt(Node receipt, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Tag != "receipt"
            || receipt.GetAttr("id") is not { Length: > 0 } msgId
            || receipt.GetAttr("from") is not { Length: > 0 } fromStr
            || !Jid.TryParse(fromStr, out Jid from))
        {
            return null;
        }

        string type = receipt.GetAttr("type") ?? "delivery";
        if (type == "delivery"
            && receipt.GetChild("error") is { } lidErr
            && string.Equals(lidErr.GetAttr("reason"), "lid", StringComparison.OrdinalIgnoreCase)
            && string.Equals(lidErr.GetAttr("type"), "feature-incapable", StringComparison.OrdinalIgnoreCase))
        {
            type = "sent";
        }
        Jid? participant = TryParseAttrJid(receipt, "participant");
        Jid? recipient = TryParseAttrJid(receipt, "recipient");
        long ts = long.TryParse(receipt.GetAttr("t"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long tVal) && tVal > 0
            ? tVal
            : (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();

        var additionalIds = new List<string>();
        if (receipt.GetChild("list") is { } listNode)
        {
            foreach (Node item in listNode.GetChildren("item"))
            {
                if (item.GetAttr("id") is { Length: > 0 } itemId)
                {
                    additionalIds.Add(itemId);
                }
            }
        }

        int? retryCount = null;
        if (receipt.GetChild("retry") is { } retryNode
            && int.TryParse(retryNode.GetAttr("count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rc))
        {
            retryCount = rc;
        }

        uint? regId = null;
        if (receipt.GetChild("registration")?.Content is NodeContent.BytesContent regBc
            && PreKeyIq.TryReadBigEndianUInt32(regBc.Bytes, out uint parsedReg))
        {
            regId = parsedReg;
        }

        PreKeyBundle? retryBundle = null;
        if (type == ReceiptStanza.TypeRetry && receipt.GetChild("keys") is not null)
        {
            Jid senderDev = participant ?? from;
            PreKeyIq.TryParseSingleBundle(senderDev, receipt, expectedAccountIdentity: null, out retryBundle);
        }

        byte[]? errPayload = null;
        byte[]? errIv = null;
        if (receipt.GetChild("encrypt") is { } encNode)
        {
            errPayload = (encNode.GetChild("enc_payload")?.Content as NodeContent.BytesContent)?.Bytes;
            errIv = (encNode.GetChild("enc_iv")?.Content as NodeContent.BytesContent)?.Bytes;
        }

        return new ParsedReceiptStanza(
            msgId,
            from,
            type,
            participant,
            recipient,
            ts,
            additionalIds,
            retryCount,
            regId,
            retryBundle,
            errPayload,
            errIv);
    }

    public static Node BuildMediaServerErrorReceipt(
        string messageId,
        Jid chatJid,
        bool fromMe,
        byte[] encryptedPayload,
        byte[] iv12,
        Jid? participant = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var rmrAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["jid"] = chatJid.ToNonAd(),
            ["from_me"] = fromMe ? "true" : "false"
        };
        if (participant is Jid p)
        {
            rmrAttrs["participant"] = p;
        }

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = messageId,
            ["to"] = StanzaBuilder.ServerPn,
            ["type"] = "server-error"
        };

        return new Node("receipt", attrs, new Node[]
        {
            new("encrypt", new Node[]
            {
                new("enc_payload", encryptedPayload),
                new("enc_iv", iv12)
            }),
            new("rmr", rmrAttrs)
        });
    }

    public static ParsedMediaRetryNotification? ParseMediaRetryNotification(Node notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.GetAttr("from") is not { Length: > 0 } fromStr || !Jid.TryParse(fromStr, out Jid from))
        {
            return null;
        }

        Node? encNode = notification.GetChild("encrypt");
        byte[]? payload = (encNode?.GetChild("enc_payload")?.Content as NodeContent.BytesContent)?.Bytes;
        byte[]? iv = (encNode?.GetChild("enc_iv")?.Content as NodeContent.BytesContent)?.Bytes;
        Node? rmrNode = notification.GetChild("rmr");
        int? errCode = notification.GetChild("error") is { } errNode
            && int.TryParse(errNode.GetAttr("code"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ec)
            ? ec
            : null;

        if (payload is null || iv is null)
        {
            return null;
        }

        return new ParsedMediaRetryNotification(
            from,
            notification.GetAttr("id") ?? string.Empty,
            TryParseAttrJid(rmrNode, "jid"),
            string.Equals(rmrNode?.GetAttr("from_me"), "true", StringComparison.OrdinalIgnoreCase),
            TryParseAttrJid(rmrNode, "participant"),
            payload,
            iv,
            errCode);
    }

    private static Jid? TryParseAttrJid(Node? node, string attrName) =>
        node?.GetAttr(attrName) is { Length: > 0 } s && Jid.TryParse(s, out Jid j) ? j : null;
}
