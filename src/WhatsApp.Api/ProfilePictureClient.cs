using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Api;

/// <summary>
/// Runtime client for fetching, uploading, and deleting profile pictures (<c>w:profile:picture</c>
/// and <c>w:g2</c> community avatars) and own status text (<c>xmlns="status"</c>), automatically attaching
/// stored Trusted Contact (<c>tctoken</c>) credentials from <see cref="IProtocolStore"/>.
/// </summary>
public sealed class ProfilePictureClient
{
    private readonly IqClient _iqClient;
    private readonly IWhatsAppBackend _backend;
    private readonly IHttpClient? _httpClient;

    public ProfilePictureClient(
        IqClient iqClient,
        IWhatsAppBackend backend,
        IHttpClient? httpClient = null)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _httpClient = httpClient;
    }

    public async Task<ProfilePictureLookupResult> GetProfilePictureAsync(
        Jid targetJid,
        ProfilePictureType type = ProfilePictureType.Preview,
        string? existingId = null,
        Jid? commonGid = null,
        string? invite = null,
        string? personaId = null,
        int? size = null,
        bool isCommunity = false,
        CancellationToken ct = default)
    {
        Jid bare = targetJid.ToNonAd();
        byte[]? tcToken = isCommunity
            ? null
            : await _backend.Protocol.GetTcTokenAsync(bare.ToString(), ct).ConfigureAwait(false);

        string iqId = _iqClient.NextRequestId();
        Node iq = ProfilePictureIq.BuildGet(
            iqId,
            bare,
            type,
            existingId,
            commonGid,
            invite,
            personaId,
            size,
            tcToken,
            isCommunity);

        Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        return ProfilePictureIq.ParseGetResponse(response, hadExistingId: !string.IsNullOrEmpty(existingId));
    }

    public async Task<byte[]?> DownloadProfilePictureBytesAsync(ProfilePictureInfo info, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.BlobBytes is { Length: > 0 } inlineBytes)
        {
            return inlineBytes;
        }
        if (string.IsNullOrWhiteSpace(info.Url))
        {
            return null;
        }
        if (_httpClient is null)
        {
            throw new InvalidOperationException("IHttpClient is required to download remote profile picture URLs.");
        }

        return await _httpClient.GetBytesAsync(new Uri(info.Url), ct: ct).ConfigureAwait(false);
    }

    public async Task<string?> SetOwnProfilePictureAsync(byte[] jpegBytes, CancellationToken ct = default)
    {
        if (jpegBytes is null || jpegBytes.Length == 0)
        {
            throw new ArgumentException("Use RemoveOwnProfilePictureAsync to remove profile picture.", nameof(jpegBytes));
        }

        string iqId = _iqClient.NextRequestId();
        Node iq = ProfilePictureIq.BuildSetOwn(iqId, jpegBytes);
        Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        return ProfilePictureIq.ParseSetResponse(response);
    }

    public async Task RemoveOwnProfilePictureAsync(CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = ProfilePictureIq.BuildSetOwn(iqId, null);
        _ = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
    }

    public async Task<string?> SetGroupProfilePictureAsync(Jid groupJid, byte[]? jpegBytes, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = ProfilePictureIq.BuildSetGroup(iqId, groupJid, jpegBytes);
        Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        return ProfilePictureIq.ParseSetResponse(response);
    }

    public async Task SetStatusTextAsync(string text, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = ProfilePictureIq.BuildSetStatusText(iqId, text);
        _ = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
    }
}
