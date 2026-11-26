using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Api;

/// <summary>
/// Runtime client for WhatsApp privacy settings (<c>xmlns="privacy"</c>) including
/// disallowed lists (<c>contact_blacklist</c> with <c>dhash</c> and LID addressing)
/// and account default disappearing timer (<c>xmlns="disappearing_mode"</c>).
/// </summary>
public sealed class PrivacyClient
{
    private readonly IqClient _iqClient;

    public PrivacyClient(IqClient iqClient)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
    }

    public async Task<PrivacySettingsSnapshot> GetPrivacySettingsAsync(CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = PrivacyProtocol.BuildGetPrivacy(iqId);
        Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        return PrivacyProtocol.ParseGetPrivacyResponse(response);
    }

    public async Task<SetPrivacyResult> SetPrivacySettingAsync(
        PrivacyCategory category,
        PrivacyValue value,
        CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = PrivacyProtocol.BuildSetPrivacy(iqId, category, value);
        Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        return PrivacyProtocol.ParseSetPrivacyResponse(response);
    }

    public async Task<SetPrivacyResult> SetDisallowedListAsync(
        PrivacyCategory category,
        string dhash,
        IReadOnlyList<DisallowedListUserEntry> users,
        CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = PrivacyProtocol.BuildSetPrivacyWithDisallowedList(iqId, category, dhash, users);
        Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        return PrivacyProtocol.ParseSetPrivacyResponse(response);
    }

    public async Task SetDefaultDisappearingModeAsync(uint durationSeconds, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = PrivacyProtocol.BuildSetDefaultDisappearingMode(iqId, durationSeconds);
        _ = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
    }
}
