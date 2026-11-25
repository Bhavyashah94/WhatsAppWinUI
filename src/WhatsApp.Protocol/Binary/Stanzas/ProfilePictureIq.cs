using System.Globalization;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum ProfilePictureType
{
    Preview = 0,
    Full = 1,
    Cover = 2,
}

public enum ProfilePictureLookupStatus
{
    Found = 0,
    Unchanged = 1,
    NotFound = 2,
    NotAuthorized = 3,
}

public sealed record ProfilePictureInfo(
    string Id,
    string? Url,
    string? DirectPath,
    string? Hash,
    ProfilePictureType Type,
    bool HasStaging = false,
    byte[]? BlobBytes = null,
    IReadOnlyList<string>? AvatarUrls = null);

public sealed record ProfilePictureLookupResult(
    ProfilePictureLookupStatus Status,
    ProfilePictureInfo? Picture = null);

public sealed class ProfilePictureIqException : Exception
{
    public int Code { get; }
    public string? ErrorType { get; }
    public int? BackoffSeconds { get; }

    public ProfilePictureIqException(int code, string message, string? errorType = null, int? backoffSeconds = null)
        : base(message)
    {
        Code = code;
        ErrorType = errorType;
        BackoffSeconds = backoffSeconds;
    }
}

/// <summary>
/// Profile picture &amp; avatar IQ builders/parsers (<c>xmlns="w:profile:picture"</c>, community <c>w:g2</c>,
/// and own status text <c>xmlns="status"</c>).
/// Matches live <c>WASmaxProfilePictureGetRPC</c> and <c>wacore/src/iq/contacts.rs</c>.
/// </summary>
public static class ProfilePictureIq
{
    public const string Xmlns = "w:profile:picture";
    public const string CommunityXmlns = "w:g2";
    public const string StatusXmlns = "status";

    public static string ToWireType(ProfilePictureType type) => type switch
    {
        ProfilePictureType.Preview => "preview",
        ProfilePictureType.Full => "image",
        ProfilePictureType.Cover => "cover",
        _ => "preview"
    };

    public static ProfilePictureType ParseWireType(string? raw) => raw switch
    {
        "image" => ProfilePictureType.Full,
        "cover" => ProfilePictureType.Cover,
        _ => ProfilePictureType.Preview
    };

    public static Node BuildGet(
        string iqId,
        Jid targetJid,
        ProfilePictureType pictureType = ProfilePictureType.Preview,
        string? existingId = null,
        Jid? commonGid = null,
        string? invite = null,
        string? personaId = null,
        int? size = null,
        byte[]? tcToken = null,
        bool isCommunity = false,
        string query = "url")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);

        var picAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["type"] = ToWireType(pictureType),
            ["query"] = string.IsNullOrWhiteSpace(query) ? "url" : query
        };
        if (!string.IsNullOrEmpty(existingId))
        {
            picAttrs["id"] = existingId;
        }
        if (size is int s && s > 0)
        {
            picAttrs["size"] = s.ToString(CultureInfo.InvariantCulture);
        }

        if (isCommunity)
        {
            picAttrs["parent_group_jid"] = targetJid;
            var picNode = new Node("picture", picAttrs);
            return StanzaBuilder.Iq(iqId, "get", CommunityXmlns, targetJid, new Node("pictures", new[] { picNode }));
        }

        if (commonGid is Jid cg)
        {
            picAttrs["common_gid"] = cg;
        }
        if (!string.IsNullOrEmpty(invite))
        {
            picAttrs["invite"] = invite;
        }
        if (!string.IsNullOrEmpty(personaId))
        {
            picAttrs["persona_id"] = personaId;
        }

        Node pictureNode = tcToken is { Length: > 0 }
            ? new Node("picture", picAttrs, new Node[] { new("tctoken", tcToken) })
            : new Node("picture", picAttrs);

        var iqAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = iqId,
            ["type"] = "get",
            ["xmlns"] = Xmlns,
            ["to"] = StanzaBuilder.ServerPn,
            ["target"] = targetJid
        };
        return new Node("iq", iqAttrs, new[] { pictureNode });
    }

    public static ProfilePictureLookupResult ParseGetResponse(Node responseIq, bool hadExistingId = false)
    {
        ArgumentNullException.ThrowIfNull(responseIq);

        Node? pictureNode = responseIq.GetChild("pictures")?.GetChild("picture")
            ?? responseIq.GetChild("picture");

        if (pictureNode is null)
        {
            // Also check live WASmaxProfilePictureGetRPC <avatar> children on root <iq>
            var avatarUrls = responseIq.GetChildren("avatar")
                .Select(a => a.GetAttr("url"))
                .Where(u => !string.IsNullOrEmpty(u))
                .Cast<string>()
                .ToArray();
            if (avatarUrls.Length > 0)
            {
                return new ProfilePictureLookupResult(
                    ProfilePictureLookupStatus.Found,
                    new ProfilePictureInfo(string.Empty, avatarUrls[0], null, null, ProfilePictureType.Full, AvatarUrls: avatarUrls));
            }

            return new ProfilePictureLookupResult(
                hadExistingId ? ProfilePictureLookupStatus.Unchanged : ProfilePictureLookupStatus.NotFound);
        }

        if (pictureNode.GetChild("error") is { } errNode)
        {
            string codeStr = errNode.GetAttr("code") ?? "0";
            int code = int.TryParse(codeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : 0;
            if (code == 404)
            {
                return new ProfilePictureLookupResult(ProfilePictureLookupStatus.NotFound);
            }
            if (code is 401 or 403)
            {
                return new ProfilePictureLookupResult(ProfilePictureLookupStatus.NotAuthorized);
            }

            string text = errNode.GetAttr("text") ?? "unknown error";
            string? errType = errNode.GetAttr("type");
            int? backoff = int.TryParse(errNode.GetAttr("backoff"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) ? b : null;
            throw new ProfilePictureIqException(code, $"Profile picture error {code}: {text}", errType, backoff);
        }

        string? status = pictureNode.GetAttr("status");
        switch (status)
        {
            case "304":
                return new ProfilePictureLookupResult(ProfilePictureLookupStatus.Unchanged);
            case "204":
                return new ProfilePictureLookupResult(ProfilePictureLookupStatus.NotFound);
            case "405":
                throw new ProfilePictureIqException(405, "Profile picture query not allowed (405).");
            case "500":
                throw new ProfilePictureIqException(500, "Profile picture server error (500).");
        }

        string? id = pictureNode.GetAttr("id");
        if (string.IsNullOrEmpty(id))
        {
            return new ProfilePictureLookupResult(
                hadExistingId ? ProfilePictureLookupStatus.Unchanged : ProfilePictureLookupStatus.NotFound);
        }

        string? url = pictureNode.GetAttr("url");
        byte[]? blobBytes = pictureNode.Content is NodeContent.BytesContent bc && bc.Bytes.Length > 0
            ? bc.Bytes
            : null;
        if (string.IsNullOrEmpty(url) && blobBytes is null)
        {
            return new ProfilePictureLookupResult(
                hadExistingId ? ProfilePictureLookupStatus.Unchanged : ProfilePictureLookupStatus.NotFound);
        }

        bool hasStaging = string.Equals(pictureNode.GetAttr("has_staging"), "true", StringComparison.OrdinalIgnoreCase);
        return new ProfilePictureLookupResult(
            ProfilePictureLookupStatus.Found,
            new ProfilePictureInfo(
                id,
                url,
                pictureNode.GetAttr("direct_path"),
                pictureNode.GetAttr("hash"),
                ParseWireType(pictureNode.GetAttr("type")),
                hasStaging,
                blobBytes));
    }

    public static Node BuildSetOwn(string iqId, byte[]? jpegBytes) => BuildSetInternal(iqId, target: null, jpegBytes);

    public static Node BuildSetGroup(string iqId, Jid groupJid, byte[]? jpegBytes) => BuildSetInternal(iqId, groupJid, jpegBytes);

    private static Node BuildSetInternal(string iqId, Jid? target, byte[]? jpegBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = iqId,
            ["type"] = "set",
            ["xmlns"] = Xmlns,
            ["to"] = StanzaBuilder.ServerPn
        };
        if (target is Jid t)
        {
            attrs["target"] = t;
        }

        // Empty/null deletes picture per WAWebSendProfilePictureJob.
        if (jpegBytes is { Length: > 0 })
        {
            var picNode = new Node("picture", new Dictionary<string, NodeValue> { ["type"] = "image" }, jpegBytes);
            return new Node("iq", attrs, new[] { picNode });
        }

        return new Node("iq", attrs);
    }

    public static string? ParseSetResponse(Node responseIq)
    {
        ArgumentNullException.ThrowIfNull(responseIq);
        return responseIq.GetChild("picture")?.GetAttr("id");
    }

    public static Node BuildSetStatusText(string iqId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iqId);
        ArgumentNullException.ThrowIfNull(text);
        return StanzaBuilder.Iq(iqId, "set", StatusXmlns, StanzaBuilder.ServerPn, new Node("status", text));
    }
}
