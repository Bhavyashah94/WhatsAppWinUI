using Whatsapp;
using WhatsApp.Protocol.AppState;

namespace WhatsApp.Api;

/// <summary>
/// Runtime client for AppState-backed account preferences (<c>setting_disableLinkPreviews</c>,
/// <c>setting_unarchiveChats</c>, and <c>setting_channels_personalised_recommendation</c>).
/// </summary>
public sealed class AppStateSettingsClient
{
    private readonly SyncdCoordinator _syncd;
    private readonly TimeProvider _timeProvider;

    public AppStateSettingsClient(SyncdCoordinator syncd, TimeProvider? timeProvider = null)
    {
        _syncd = syncd ?? throw new ArgumentNullException(nameof(syncd));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task SetLinkPreviewsDisabledAsync(bool disabled, CancellationToken ct = default)
    {
        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            PrivacySettingDisableLinkPreviewsAction = new SyncActionValue.Types.PrivacySettingDisableLinkPreviewsAction
            {
                IsPreviewsDisabled = disabled
            }
        };
        return PushAsync(AppStateSchemas.DisableLinkPreviews, value, ct);
    }

    public Task SetUnarchiveChatsAsync(bool unarchiveChats, CancellationToken ct = default)
    {
        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            UnarchiveChatsSetting = new SyncActionValue.Types.UnarchiveChatsSetting
            {
                UnarchiveChats = unarchiveChats
            }
        };
        return PushAsync(AppStateSchemas.UnarchiveChatsSetting, value, ct);
    }

    public Task SetChannelsPersonalisedRecommendationAsync(bool optOut, CancellationToken ct = default)
    {
        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            PrivacySettingChannelsPersonalisedRecommendationAction =
                new SyncActionValue.Types.PrivacySettingChannelsPersonalisedRecommendationAction
                {
                    IsUserOptedOut = optOut
                }
        };
        return PushAsync(AppStateSchemas.ChannelsPersonalisedRecommendation, value, ct);
    }

    public Task SetRelayAllCallsAsync(bool enabled, CancellationToken ct = default)
    {
        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            PrivacySettingRelayAllCalls = new SyncActionValue.Types.PrivacySettingRelayAllCalls
            {
                IsEnabled = enabled
            }
        };
        return PushAsync(AppStateSchemas.RelayAllCalls, value, ct);
    }

    private Task PushAsync(AppStateActionSpec spec, SyncActionValue value, CancellationToken ct) =>
        _syncd.SendMutationAsync(
            spec.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            spec.BuildIndex(),
            value,
            actionVersion: spec.Version,
            ct: ct);
}
