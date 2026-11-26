using System.Text;
using System.Text.Json;

namespace WhatsApp.Protocol.AppState;

/// <summary>
/// Declarative schema descriptor for an AppState (<c>syncd</c>) mutation action,
/// aligned with live <c>web.whatsapp.com</c> (<c>WAWeb*Sync</c>) and <c>wacore/appstate/src/schemas.rs</c>.
/// </summary>
public sealed record AppStateActionSpec(
    string ActionName,
    string Collection,
    int Version,
    int ArgCount)
{
    public byte[] BuildIndex(params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != ArgCount)
        {
            throw new ArgumentException(
                $"AppState action '{ActionName}' expects {ArgCount} index argument(s), got {args.Length}.",
                nameof(args));
        }

        if (ArgCount == 0)
        {
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { ActionName }));
        }

        var parts = new string[1 + args.Length];
        parts[0] = ActionName;
        for (int i = 0; i < args.Length; i++)
        {
            parts[i + 1] = args[i] ?? string.Empty;
        }
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts));
    }
}

/// <summary>
/// Canonical AppState schema catalog mapping each action name, target collection, and version.
/// </summary>
public static class AppStateSchemas
{
    public static readonly AppStateActionSpec Archive =
        new("archive", AppStateCollections.RegularLow, 3, 1);

    public static readonly AppStateActionSpec Pin =
        new("pin_v1", AppStateCollections.RegularLow, 5, 1);

    public static readonly AppStateActionSpec Mute =
        new("mute", AppStateCollections.RegularHigh, 2, 1);

    public static readonly AppStateActionSpec MarkChatAsRead =
        new("markChatAsRead", AppStateCollections.RegularLow, 3, 1);

    public static readonly AppStateActionSpec LockChat =
        new("lock", AppStateCollections.RegularLow, 7, 1);

    public static readonly AppStateActionSpec Star =
        new("star", AppStateCollections.RegularHigh, 2, 4);

    public static readonly AppStateActionSpec DeleteMessageForMe =
        new("deleteMessageForMe", AppStateCollections.RegularHigh, 3, 4);

    public static readonly AppStateActionSpec ClearChat =
        new("clearChat", AppStateCollections.RegularHigh, 6, 3);

    public static readonly AppStateActionSpec DeleteChat =
        new("deleteChat", AppStateCollections.RegularHigh, 6, 2);

    public static readonly AppStateActionSpec Favorites =
        new("favorites", AppStateCollections.RegularHigh, 1, 0);

    public static readonly AppStateActionSpec Contact =
        new("contact", AppStateCollections.CriticalUnblockLow, 2, 1);

    public static readonly AppStateActionSpec LidContact =
        new("lid_contact", AppStateCollections.CriticalUnblockLow, 1, 1);

    public static readonly AppStateActionSpec SettingPushName =
        new("setting_pushName", AppStateCollections.CriticalBlock, 1, 0);

    public static readonly AppStateActionSpec DisableLinkPreviews =
        new("setting_disableLinkPreviews", AppStateCollections.Regular, 8, 0);

    public static readonly AppStateActionSpec UnarchiveChatsSetting =
        new("setting_unarchiveChats", AppStateCollections.RegularLow, 4, 0);

    public static readonly AppStateActionSpec ChannelsPersonalisedRecommendation =
        new("setting_channels_personalised_recommendation", AppStateCollections.Regular, 1, 0);

    public static readonly AppStateActionSpec FavoriteSticker =
        new("favoriteSticker", AppStateCollections.RegularLow, 7, 1);

    public static readonly AppStateActionSpec RemoveRecentSticker =
        new("removeRecentSticker", AppStateCollections.RegularLow, 7, 1);

    public static readonly AppStateActionSpec UserStatusMute =
        new("userStatusMute", AppStateCollections.RegularHigh, 7, 1);

    public static readonly AppStateActionSpec StatusPrivacy =
        new("status_privacy", AppStateCollections.RegularHigh, 7, 0);

    public static readonly AppStateActionSpec RelayAllCalls =
        new("setting_relayAllCalls", AppStateCollections.Regular, 1, 0);
}
