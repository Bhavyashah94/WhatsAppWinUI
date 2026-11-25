using System.Globalization;
using System.Text;
using Google.Protobuf;
using Whatsapp;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum UsyncMode
{
    Query = 0,
    Full = 1,
    Delta = 2,
}

public enum UsyncContext
{
    Interactive = 0,
    Background = 1,
    Message = 2,
    Voip = 3,
}

public enum UsyncAddressingMode
{
    Pn = 0,
    Lid = 1,
}

[Flags]
public enum UsyncProtocolSet
{
    None = 0,
    Devices = 1 << 0,
    Lid = 1 << 1,
    Contact = 1 << 2,
    Business = 1 << 3,
    Picture = 1 << 4,
    Status = 1 << 5,
    DisappearingMode = 1 << 6,
    Username = 1 << 7,
    TextStatus = 1 << 8,
    Feature = 1 << 9,
    Bot = 1 << 10,
}

public sealed record UsyncSubprotocolError(int? Code, string? Text, int? BackoffSeconds);

public sealed record UsyncDeviceInfo(uint Id, uint? KeyIndex, bool IsHosted);

public sealed record UsyncVerifiedNameInfo(
    string? Name,
    string? Serial,
    string? Issuer,
    byte[]? CertificateBytes);

public sealed record UsyncDisappearingModeInfo(
    uint DurationSeconds,
    long? SettingTimestampSeconds,
    bool EphemeralityDisabled);

public sealed record UsyncUserSpec
{
    public Jid? Jid { get; init; }
    public Jid? PnJid { get; init; }
    public Jid? KnownLid { get; init; }
    public Jid? CommonGid { get; init; }
    public string? Phone { get; init; }
    public string? Username { get; init; }
    public string? UsernamePin { get; init; }
    public string? ContactType { get; init; }
    public string? DeviceHash { get; init; }
    public long? TimestampSeconds { get; init; }
    public long? ExpectedTimestampSeconds { get; init; }
    public byte[]? TcToken { get; init; }
    public string? PersonaId { get; init; }
}

public sealed class UsyncUserResult
{
    public Jid? Jid { get; init; }
    public Jid? PnJid { get; init; }
    public Jid? Lid { get; init; }
    public bool IsRegistered { get; init; }
    public string? ContactType { get; init; }
    public string? ContactUsername { get; init; }
    public string? Username { get; init; }
    public bool IsBusiness { get; init; }
    public UsyncVerifiedNameInfo? VerifiedName { get; init; }
    public string? PictureId { get; init; }
    public string? StatusText { get; init; }
    public string? DeviceListHash { get; init; }
    public long? KeyIndexTimestampSeconds { get; init; }
    public long? ExpectedTimestampSeconds { get; init; }
    public byte[]? SignedKeyIndexBytes { get; init; }
    public IReadOnlyList<UsyncDeviceInfo> Devices { get; init; } = Array.Empty<UsyncDeviceInfo>();
    public UsyncDisappearingModeInfo? DisappearingMode { get; init; }
    public IReadOnlyDictionary<string, UsyncSubprotocolError> Errors { get; init; } =
        new Dictionary<string, UsyncSubprotocolError>(StringComparer.Ordinal);
}

public sealed record UsyncResponse(
    IReadOnlyDictionary<string, UsyncSubprotocolError> ResultErrors,
    IReadOnlyList<UsyncUserResult> Users);

public enum UsernameLookupStatus
{
    NotFound = 0,
    Found = 1,
    KeyRequired = 2,
}

public sealed record UsernameLookupResult(
    UsernameLookupStatus Status,
    Jid? LidJid = null,
    Jid? PnJid = null,
    string? Username = null,
    bool IsBusiness = false,
    UsyncVerifiedNameInfo? VerifiedName = null);

/// <summary>
/// Typed <c>xmlns="usync"</c> IQ builder &amp; parser covering <c>devices</c> v2, <c>lid</c>,
/// <c>contact</c> (including <c>@username</c> &amp; PIN resolution), <c>business</c>, <c>picture</c>,
/// <c>status</c>, <c>disappearing_mode</c>, and <c>username</c> protocols.
/// Matches live <c>WAWebUsync</c>, <c>WAWebQueryExistsJob</c>, and <c>wacore/src/iq/usync.rs</c>.
/// </summary>
public static class UsyncProtocol
{
    public const string Xmlns = "usync";

    public static string ToWireMode(UsyncMode mode) => mode switch
    {
        UsyncMode.Full => "full",
        UsyncMode.Delta => "delta",
        _ => "query"
    };

    public static string ToWireContext(UsyncContext ctx) => ctx switch
    {
        UsyncContext.Background => "background",
        UsyncContext.Message => "message",
        UsyncContext.Voip => "voip",
        _ => "interactive"
    };

    public static Node BuildQuery(
        string iqId,
        string sid,
        UsyncMode mode,
        UsyncContext context,
        UsyncProtocolSet protocols,
        IReadOnlyList<UsyncUserSpec> users,
        UsyncAddressingMode addressingMode = UsyncAddressingMode.Pn,
        int? businessProfileVersion = null,
        IReadOnlyList<string>? features = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        ArgumentNullException.ThrowIfNull(users);
        if (protocols == UsyncProtocolSet.None)
        {
            throw new ArgumentException("At least one usync protocol flag is required.", nameof(protocols));
        }
        if (users.Count == 0)
        {
            throw new ArgumentException("At least one usync user is required.", nameof(users));
        }

        var queryChildren = new List<Node>();
        if (protocols.HasFlag(UsyncProtocolSet.Contact))
        {
            var cAttrs = addressingMode == UsyncAddressingMode.Lid
                ? new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["addressing_mode"] = "lid" }
                : null;
            queryChildren.Add(new Node("contact", cAttrs));
        }
        if (protocols.HasFlag(UsyncProtocolSet.Business))
        {
            var bizChildren = new List<Node> { new("verified_name") };
            if (businessProfileVersion is int bpv)
            {
                bizChildren.Add(new Node("profile", new Dictionary<string, NodeValue> { ["v"] = bpv.ToString(CultureInfo.InvariantCulture) }));
            }
            queryChildren.Add(new Node("business", bizChildren));
        }
        if (protocols.HasFlag(UsyncProtocolSet.Status))
        {
            queryChildren.Add(new Node("status"));
        }
        if (protocols.HasFlag(UsyncProtocolSet.TextStatus))
        {
            queryChildren.Add(new Node("text_status"));
        }
        if (protocols.HasFlag(UsyncProtocolSet.Picture))
        {
            queryChildren.Add(new Node("picture"));
        }
        if (protocols.HasFlag(UsyncProtocolSet.Devices))
        {
            queryChildren.Add(new Node("devices", new Dictionary<string, NodeValue> { ["version"] = "2" }));
        }
        if (protocols.HasFlag(UsyncProtocolSet.Lid))
        {
            queryChildren.Add(new Node("lid"));
        }
        if (protocols.HasFlag(UsyncProtocolSet.Username))
        {
            queryChildren.Add(new Node("username"));
        }
        if (protocols.HasFlag(UsyncProtocolSet.DisappearingMode))
        {
            queryChildren.Add(new Node("disappearing_mode"));
        }
        if (protocols.HasFlag(UsyncProtocolSet.Feature) && features is { Count: > 0 })
        {
            queryChildren.Add(new Node("feature", features.Select(f => new Node(f)).ToArray()));
        }
        if (protocols.HasFlag(UsyncProtocolSet.Bot))
        {
            queryChildren.Add(new Node("bot", new[] { new Node("profile", new Dictionary<string, NodeValue> { ["v"] = "1" }) }));
        }

        var userNodes = new Node[users.Count];
        for (int i = 0; i < users.Count; i++)
        {
            UsyncUserSpec u = users[i];
            var uAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal);
            if (u.Jid is Jid jid)
            {
                uAttrs["jid"] = jid.ToNonAd();
            }
            if (u.PnJid is Jid pnJid)
            {
                uAttrs["pn_jid"] = pnJid.ToNonAd();
            }
            if (u.CommonGid is Jid commonGid)
            {
                uAttrs["common_gid"] = commonGid.ToNonAd();
            }

            var uChildren = new List<Node>();
            if (u.TcToken is { Length: > 0 } tc)
            {
                uChildren.Add(new Node("tctoken", tc));
            }

            if (protocols.HasFlag(UsyncProtocolSet.Contact))
            {
                if (!string.IsNullOrEmpty(u.Phone))
                {
                    string phone = NormalizePhone(u.Phone);
                    uChildren.Add(new Node("contact", phone));
                }
                else if (!string.IsNullOrEmpty(u.Username))
                {
                    var ca = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
                    {
                        ["username"] = StripLeadingAt(u.Username)
                    };
                    if (!string.IsNullOrEmpty(u.UsernamePin))
                    {
                        ca["pin"] = u.UsernamePin;
                    }
                    if (u.KnownLid is Jid contactLid)
                    {
                        ca["lid"] = contactLid.ToNonAd();
                    }
                    uChildren.Add(new Node("contact", ca));
                }
                else if (!string.IsNullOrEmpty(u.ContactType))
                {
                    uChildren.Add(new Node("contact", new Dictionary<string, NodeValue> { ["type"] = u.ContactType }));
                }
            }

            if (protocols.HasFlag(UsyncProtocolSet.Devices)
                && (u.DeviceHash is not null || u.TimestampSeconds.HasValue || u.ExpectedTimestampSeconds.HasValue))
            {
                var dAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(u.DeviceHash))
                {
                    dAttrs["device_hash"] = u.DeviceHash;
                }
                if (u.TimestampSeconds is long ts)
                {
                    dAttrs["ts"] = ts.ToString(CultureInfo.InvariantCulture);
                }
                if (u.ExpectedTimestampSeconds is long expTs)
                {
                    dAttrs["expected_ts"] = expTs.ToString(CultureInfo.InvariantCulture);
                }
                uChildren.Add(new Node("devices", dAttrs));
            }

            if (protocols.HasFlag(UsyncProtocolSet.Lid) && u.KnownLid is Jid klid)
            {
                uChildren.Add(new Node("lid", new Dictionary<string, NodeValue> { ["jid"] = klid.ToNonAd() }));
            }

            if (protocols.HasFlag(UsyncProtocolSet.Username) && !string.IsNullOrEmpty(u.Username))
            {
                uChildren.Add(new Node("username", new Dictionary<string, NodeValue> { ["username"] = StripLeadingAt(u.Username) }));
            }

            if (protocols.HasFlag(UsyncProtocolSet.Bot) && !string.IsNullOrEmpty(u.PersonaId))
            {
                uChildren.Add(new Node("bot", new[]
                {
                    new Node("profile", new Dictionary<string, NodeValue> { ["persona_id"] = u.PersonaId })
                }));
            }

            userNodes[i] = new Node("user", uAttrs.Count > 0 ? uAttrs : null, uChildren.Count > 0 ? new NodeContent.NodesContent(uChildren) : null);
        }

        var usyncAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["sid"] = sid,
            ["index"] = "0",
            ["last"] = "true",
            ["mode"] = ToWireMode(mode),
            ["context"] = ToWireContext(context)
        };

        var usyncNode = new Node("usync", usyncAttrs, new Node[]
        {
            new("query", queryChildren),
            new("list", userNodes)
        });
        return StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn, usyncNode);
    }

    public static Node BuildUsernameLookupQuery(
        string iqId,
        string sid,
        string username,
        string? usernameKeyPin = null,
        UsyncContext context = UsyncContext.Interactive)
    {
        string bare = StripLeadingAt(username);
        if (bare.Length is < 3 or > 35)
        {
            throw new ArgumentException("Username must be between 3 and 35 characters (without @).", nameof(username));
        }

        return BuildQuery(
            iqId,
            sid,
            UsyncMode.Query,
            context,
            UsyncProtocolSet.Contact | UsyncProtocolSet.Business,
            [new UsyncUserSpec { Username = bare, UsernamePin = usernameKeyPin }],
            UsyncAddressingMode.Lid);
    }

    public static UsyncResponse ParseResponse(Node responseIq)
    {
        ArgumentNullException.ThrowIfNull(responseIq);
        Node? usyncNode = responseIq.Tag == "usync" ? responseIq : responseIq.GetChild("usync");
        if (usyncNode is null)
        {
            return new UsyncResponse(
                new Dictionary<string, UsyncSubprotocolError>(StringComparer.Ordinal),
                Array.Empty<UsyncUserResult>());
        }

        var resultErrors = new Dictionary<string, UsyncSubprotocolError>(StringComparer.Ordinal);
        if (usyncNode.GetChild("result")?.Content is NodeContent.NodesContent rc)
        {
            foreach (Node child in rc.Nodes)
            {
                if (child.GetChild("error") is { } errNode)
                {
                    resultErrors[child.Tag] = ParseError(errNode);
                }
            }
        }

        var parsedUsers = new List<UsyncUserResult>();
        Node? listNode = usyncNode.GetChild("list");
        if (listNode is not null)
        {
            foreach (Node userNode in listNode.GetChildren("user"))
            {
                parsedUsers.Add(ParseUserNode(userNode));
            }
        }

        return new UsyncResponse(resultErrors, parsedUsers);
    }

    public static UsernameLookupResult ParseUsernameLookupResponse(Node responseIq)
    {
        UsyncResponse resp = ParseResponse(responseIq);
        if (resp.ResultErrors.TryGetValue("contact", out UsyncSubprotocolError? resErr))
        {
            throw new InvalidOperationException($"usync contact error {resErr.Code}: {resErr.Text}");
        }

        if (resp.Users.Count == 0)
        {
            return new UsernameLookupResult(UsernameLookupStatus.NotFound);
        }

        UsyncUserResult user = resp.Users[0];
        if (user.Errors.TryGetValue("contact", out UsyncSubprotocolError? userErr))
        {
            throw new InvalidOperationException($"usync contact error {userErr.Code}: {userErr.Text}");
        }

        if (string.Equals(user.ContactType, "out", StringComparison.Ordinal))
        {
            return new UsernameLookupResult(UsernameLookupStatus.NotFound);
        }
        if (!string.Equals(user.ContactType, "in", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Username lookup returned unexpected contact type '{user.ContactType}'.");
        }

        string? resolvedUsername = !string.IsNullOrEmpty(user.ContactUsername) ? user.ContactUsername : user.Username;
        if (user.Jid is not Jid lidJid)
        {
            return new UsernameLookupResult(
                UsernameLookupStatus.KeyRequired,
                Username: resolvedUsername);
        }

        return new UsernameLookupResult(
            UsernameLookupStatus.Found,
            LidJid: lidJid,
            PnJid: user.PnJid,
            Username: resolvedUsername,
            IsBusiness: user.IsBusiness,
            VerifiedName: user.VerifiedName);
    }

    internal static UsyncVerifiedNameInfo? DecodeVerifiedNameFromNode(Node? verifiedNameNode)
    {
        if (verifiedNameNode is null)
        {
            return null;
        }

        string? name = verifiedNameNode.GetAttr("name")
            ?? (verifiedNameNode.GetChild("name")?.Content is NodeContent.StringContent sc ? sc.Text : null);
        string? serial = verifiedNameNode.GetAttr("serial");
        string? issuer = verifiedNameNode.GetAttr("issuer");
        byte[]? certBytes = verifiedNameNode.Content is NodeContent.BytesContent bc && bc.Bytes.Length > 0
            ? bc.Bytes
            : null;

        if (certBytes is not null)
        {
            try
            {
                VerifiedNameCertificate cert = VerifiedNameCertificate.Parser.ParseFrom(certBytes);
                if (cert.HasDetails && !cert.Details.IsEmpty)
                {
                    VerifiedNameCertificate.Types.Details details = VerifiedNameCertificate.Types.Details.Parser.ParseFrom(cert.Details);
                    if (string.IsNullOrEmpty(name) && details.HasVerifiedName)
                    {
                        name = details.VerifiedName;
                    }
                    if (string.IsNullOrEmpty(serial) && details.HasSerial)
                    {
                        serial = details.Serial.ToString(CultureInfo.InvariantCulture);
                    }
                    if (string.IsNullOrEmpty(issuer) && details.HasIssuer)
                    {
                        issuer = details.Issuer;
                    }
                }
            }
            catch (InvalidProtocolBufferException)
            {
                // Preserve whatever attributes were present.
            }
        }

        return new UsyncVerifiedNameInfo(name, serial, issuer, certBytes);
    }

    private static UsyncUserResult ParseUserNode(Node userNode)
    {
        Jid? jid = TryParseJid(userNode.GetAttr("jid"));
        Jid? pnJid = TryParseJid(userNode.GetAttr("pn_jid"));
        Jid? lid = null;
        bool isRegistered = false;
        string? contactType = null;
        string? contactUsername = null;
        string? username = null;
        bool isBusiness = false;
        UsyncVerifiedNameInfo? verifiedName = null;
        string? pictureId = null;
        string? statusText = null;
        string? deviceListHash = null;
        long? keyIndexTs = null;
        long? expectedTs = null;
        byte[]? signedKeyIndexBytes = null;
        var devices = new List<UsyncDeviceInfo>();
        UsyncDisappearingModeInfo? dm = null;
        var errors = new Dictionary<string, UsyncSubprotocolError>(StringComparer.Ordinal);

        if (userNode.GetChild("contact") is { } contactNode)
        {
            if (contactNode.GetChild("error") is { } err)
            {
                errors["contact"] = ParseError(err);
            }
            else
            {
                contactType = contactNode.GetAttr("type");
                isRegistered = string.Equals(contactType, "in", StringComparison.Ordinal);
                contactUsername = contactNode.GetAttr("username");
            }
        }

        if (userNode.GetChild("lid") is { } lidNode)
        {
            if (lidNode.GetChild("error") is { } err)
            {
                errors["lid"] = ParseError(err);
            }
            else
            {
                lid = TryParseJid(lidNode.GetAttr("val"));
            }
        }

        if (userNode.GetChild("business") is { } bizNode)
        {
            if (bizNode.GetChild("error") is { } err)
            {
                errors["business"] = ParseError(err);
            }
            else
            {
                isBusiness = true;
                if (pnJid is null && TryParseJid(bizNode.GetAttr("pn_jid")) is { } bizPn)
                {
                    pnJid = bizPn;
                }
                verifiedName = DecodeVerifiedNameFromNode(bizNode.GetChild("verified_name"));
            }
        }

        if (userNode.GetChild("picture") is { } picNode)
        {
            if (picNode.GetChild("error") is { } err)
            {
                errors["picture"] = ParseError(err);
            }
            else
            {
                pictureId = picNode.GetAttr("id");
            }
        }

        if (userNode.GetChild("status") is { } statusNode)
        {
            if (statusNode.GetChild("error") is { } err)
            {
                errors["status"] = ParseError(err);
            }
            else
            {
                statusText = statusNode.Content switch
                {
                    NodeContent.StringContent sc => sc.Text,
                    NodeContent.BytesContent bc => Encoding.UTF8.GetString(bc.Bytes),
                    _ => null
                };
            }
        }

        if (userNode.GetChild("username") is { } usernameNode)
        {
            if (usernameNode.GetChild("error") is { } err)
            {
                errors["username"] = ParseError(err);
            }
            else if (usernameNode.Content is NodeContent.StringContent sc && !string.IsNullOrEmpty(sc.Text))
            {
                username = sc.Text;
            }
        }

        if (userNode.GetChild("disappearing_mode") is { } dmNode)
        {
            if (dmNode.GetChild("error") is { } err)
            {
                errors["disappearing_mode"] = ParseError(err);
            }
            else
            {
                uint duration = uint.TryParse(dmNode.GetAttr("duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint d) ? d : 0u;
                long? ts = long.TryParse(dmNode.GetAttr("t"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) ? t : null;
                bool disabled = string.Equals(dmNode.GetAttr("ephemerality_disabled"), "true", StringComparison.OrdinalIgnoreCase);
                dm = new UsyncDisappearingModeInfo(duration, ts, disabled);
            }
        }

        if (userNode.GetChild("devices") is { } devNode)
        {
            if (devNode.GetChild("error") is { } err)
            {
                errors["devices"] = ParseError(err);
            }
            else
            {
                if (devNode.GetChild("key-index-list") is { } kiNode)
                {
                    keyIndexTs = long.TryParse(kiNode.GetAttr("ts"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long tsVal) ? tsVal : null;
                    expectedTs = long.TryParse(kiNode.GetAttr("expected_ts"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long expVal) ? expVal : null;
                    if (kiNode.Content is NodeContent.BytesContent bc && bc.Bytes.Length > 0)
                    {
                        signedKeyIndexBytes = bc.Bytes;
                    }
                }

                if (devNode.GetChild("device-list") is { } dlNode)
                {
                    deviceListHash = dlNode.GetAttr("hash");
                    foreach (Node d in dlNode.GetChildren("device"))
                    {
                        if (uint.TryParse(d.GetAttr("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id))
                        {
                            uint? ki = uint.TryParse(d.GetAttr("key-index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint kiVal) ? kiVal : null;
                            bool hosted = string.Equals(d.GetAttr("is_hosted"), "true", StringComparison.OrdinalIgnoreCase);
                            devices.Add(new UsyncDeviceInfo(id, ki, hosted));
                        }
                    }
                }
            }
        }

        return new UsyncUserResult
        {
            Jid = jid,
            PnJid = pnJid,
            Lid = lid,
            IsRegistered = isRegistered,
            ContactType = contactType,
            ContactUsername = contactUsername,
            Username = username,
            IsBusiness = isBusiness,
            VerifiedName = verifiedName,
            PictureId = pictureId,
            StatusText = statusText,
            DeviceListHash = deviceListHash,
            KeyIndexTimestampSeconds = keyIndexTs,
            ExpectedTimestampSeconds = expectedTs,
            SignedKeyIndexBytes = signedKeyIndexBytes,
            Devices = devices,
            DisappearingMode = dm,
            Errors = errors
        };
    }

    private static UsyncSubprotocolError ParseError(Node errNode)
    {
        int? code = int.TryParse(errNode.GetAttr("code"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : null;
        string? text = errNode.GetAttr("text");
        int? backoff = int.TryParse(errNode.GetAttr("backoff"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) ? b : null;
        return new UsyncSubprotocolError(code, text, backoff);
    }

    private static Jid? TryParseJid(string? raw) =>
        !string.IsNullOrEmpty(raw) && Jid.TryParse(raw, out Jid jid) ? jid : null;

    private static string NormalizePhone(string raw)
    {
        string trimmed = raw.Trim();
        return trimmed.StartsWith('+') ? trimmed : $"+{trimmed}";
    }

    private static string StripLeadingAt(string raw) =>
        raw.Trim().TrimStart('@');
}
