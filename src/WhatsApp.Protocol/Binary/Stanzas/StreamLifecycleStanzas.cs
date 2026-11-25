using System.Globalization;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum ConnectFailureReason
{
    Unknown = 0,
    Generic = 400,
    LoggedOut = 401,
    TempBanned = 402,
    AccountLocked = 403,
    ClientOutdated = 405,
    UnknownLogout = 406,
    BadUserAgent = 409,
    CatExpired = 413,
    CatInvalid = 414,
    NotFound = 415,
    ClientUnknown = 418,
    InternalServerError = 500,
    Experimental = 501,
    ServiceUnavailable = 503,
}

public sealed record ConnectFailureInfo(
    int RawReasonCode,
    ConnectFailureReason Reason,
    int? Code,
    ulong? ExpireSeconds,
    string? Message,
    string? Url,
    string? Location,
    string? LogoutMessageHeader,
    string? LogoutMessageSubtext,
    string? LogoutMessageLocale)
{
    public bool IsLoggedOut =>
        Reason is ConnectFailureReason.LoggedOut
            or ConnectFailureReason.AccountLocked
            or ConnectFailureReason.UnknownLogout;

    public bool ShouldReconnect =>
        Reason is ConnectFailureReason.ServiceUnavailable
            or ConnectFailureReason.InternalServerError;
}

/// <summary>
/// Parses <c>&lt;failure&gt;</c> connect-rejection stanzas matching <c>wacore/src/stanza/connect_failure.rs</c>.
/// </summary>
public static class ConnectFailureStanza
{
    public static ConnectFailureInfo Parse(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        int rawReason = TryParseInt(node.GetAttr("reason")) ?? 0;
        var reason = Enum.IsDefined(typeof(ConnectFailureReason), rawReason)
            ? (ConnectFailureReason)rawReason
            : ConnectFailureReason.Unknown;

        return new ConnectFailureInfo(
            RawReasonCode: rawReason,
            Reason: reason,
            Code: TryParseInt(node.GetAttr("code")),
            ExpireSeconds: TryParseUlong(node.GetAttr("expire")),
            Message: node.GetAttr("message"),
            Url: node.GetAttr("url"),
            Location: node.GetAttr("location"),
            LogoutMessageHeader: node.GetAttr("logout_message_header"),
            LogoutMessageSubtext: node.GetAttr("logout_message_subtext"),
            LogoutMessageLocale: node.GetAttr("logout_message_locale"));
    }

    private static int? TryParseInt(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) ? val : null;

    private static ulong? TryParseUlong(string? raw) =>
        ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong val) ? val : null;
}

public enum StreamErrorKind
{
    Other = 0,
    Code = 1,
    ConflictReplaced = 2,
    ConflictDeviceRemoved = 3,
    Ack = 4,
    XmlNotWellFormed = 5,
}

public sealed record StreamErrorInfo(
    StreamErrorKind Kind,
    string? Code,
    string? ConflictType,
    string? AckId);

/// <summary>
/// Parses <c>&lt;stream:error&gt;</c> stanzas matching <c>WAWebHandleStreamError</c> and <c>whatsapp-rust</c>.
/// </summary>
public static class StreamErrorStanza
{
    public static StreamErrorInfo Parse(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.GetChild("conflict") is { } conflictNode)
        {
            string? ctype = conflictNode.GetAttr("type");
            var kind = string.Equals(ctype, "replaced", StringComparison.Ordinal)
                ? StreamErrorKind.ConflictReplaced
                : StreamErrorKind.ConflictDeviceRemoved;
            return new StreamErrorInfo(kind, Code: null, ConflictType: ctype, AckId: null);
        }

        if (node.GetAttr("code") is { Length: > 0 } code)
        {
            return new StreamErrorInfo(StreamErrorKind.Code, Code: code, ConflictType: null, AckId: null);
        }

        if (node.GetChild("ack") is { } ackNode)
        {
            return new StreamErrorInfo(StreamErrorKind.Ack, Code: null, ConflictType: null, AckId: ackNode.GetAttr("id"));
        }

        if (node.GetChild("xml-not-well-formed") is not null)
        {
            return new StreamErrorInfo(StreamErrorKind.XmlNotWellFormed, Code: null, ConflictType: null, AckId: null);
        }

        return new StreamErrorInfo(StreamErrorKind.Other, Code: null, ConflictType: null, AckId: null);
    }
}

public sealed record OfflinePreviewInfo(
    int Total,
    int AppDataChanges,
    int Messages,
    int Notifications,
    int Receipts,
    int Calls,
    int Statuses);

public sealed record InfoBulletinPayload(
    IReadOnlyList<DirtyBit> DirtyBits,
    byte[]? EdgeRoutingInfo,
    OfflinePreviewInfo? OfflinePreview,
    int? OfflineCompletedCount);

/// <summary>
/// Parses inbound <c>&lt;ib&gt;</c> bulletin children (<c>dirty</c>, <c>edge_routing</c>,
/// <c>offline_preview</c>, and <c>offline</c>).
/// </summary>
public static class InfoBulletinStanza
{
    public static InfoBulletinPayload Parse(Node ibNode)
    {
        ArgumentNullException.ThrowIfNull(ibNode);

        var dirtyBits = new List<DirtyBit>();
        byte[]? edgeRouting = null;
        OfflinePreviewInfo? preview = null;
        int? offlineCount = null;

        if (ibNode.Content is NodeContent.NodesContent children)
        {
            foreach (Node child in children.Nodes)
            {
                switch (child.Tag)
                {
                    case "dirty":
                        if (child.GetAttr("type") is { Length: > 0 } dirtyType)
                        {
                            ulong? ts = ulong.TryParse(
                                child.GetAttr("timestamp"),
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out ulong parsedTs)
                                ? parsedTs
                                : null;
                            dirtyBits.Add(new DirtyBit(dirtyType, ts));
                        }
                        break;

                    case "edge_routing":
                        if (child.GetChild("routing_info")?.Content is NodeContent.BytesContent routingBytes
                            && routingBytes.Bytes.Length > 0)
                        {
                            edgeRouting = routingBytes.Bytes;
                        }
                        break;

                    case "offline_preview":
                        preview = new OfflinePreviewInfo(
                            Total: ParseIntAttr(child, "count"),
                            AppDataChanges: ParseIntAttr(child, "appdata"),
                            Messages: ParseIntAttr(child, "message"),
                            Notifications: ParseIntAttr(child, "notification"),
                            Receipts: ParseIntAttr(child, "receipt"),
                            Calls: ParseIntAttr(child, "call"),
                            Statuses: ParseIntAttr(child, "status"));
                        break;

                    case "offline":
                        offlineCount = ParseIntAttr(child, "count");
                        break;
                }
            }
        }

        return new InfoBulletinPayload(dirtyBits, edgeRouting, preview, offlineCount);
    }

    private static int ParseIntAttr(Node node, string attr) =>
        int.TryParse(node.GetAttr(attr), NumberStyles.Integer, CultureInfo.InvariantCulture, out int val)
            ? val
            : 0;
}
