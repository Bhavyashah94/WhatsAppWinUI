using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Api;

public sealed record ClearChatOptions(bool DeleteStarred = false, bool DeleteMedia = false);

public sealed record ChatMessageRef(
    string MessageId,
    bool FromMe,
    Jid? Participant = null,
    long TimestampSeconds = 0);

/// <summary>
/// Runtime client for AppState-backed chat actions (<c>pin_v1</c>, <c>mute</c>,
/// <c>archive</c>, <c>markChatAsRead</c>, <c>lock</c>, <c>star</c>, <c>deleteMessageForMe</c>,
/// <c>clearChat</c>, <c>deleteChat</c>, <c>favorites</c>, <c>favoriteSticker</c>,
/// <c>removeRecentSticker</c>) and per-chat disappearing timers.
/// </summary>
public sealed class ChatActionsClient
{
    private const long MuteIndefinite = -1;

    private readonly SyncdCoordinator _syncd;
    private readonly MessageSender? _messageSender;
    private readonly TimeProvider _timeProvider;

    public ChatActionsClient(
        SyncdCoordinator syncd,
        MessageSender? messageSender = null,
        TimeProvider? timeProvider = null)
    {
        _syncd = syncd ?? throw new ArgumentNullException(nameof(syncd));
        _messageSender = messageSender;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task PinChatAsync(Jid chatJid, CancellationToken ct = default) =>
        PushChatMutationAsync(AppStateSchemas.Pin, chatJid, new SyncActionValue
        {
            Timestamp = NowMs(),
            PinAction = new SyncActionValue.Types.PinAction { Pinned = true }
        }, ct);

    public Task UnpinChatAsync(Jid chatJid, CancellationToken ct = default) =>
        PushChatMutationAsync(AppStateSchemas.Pin, chatJid, new SyncActionValue
        {
            Timestamp = NowMs(),
            PinAction = new SyncActionValue.Types.PinAction { Pinned = false }
        }, ct);

    public Task ArchiveChatAsync(Jid chatJid, SyncActionValue.Types.SyncActionMessageRange? messageRange = null, CancellationToken ct = default) =>
        SendArchiveAsync(chatJid, true, messageRange, ct);

    public Task UnarchiveChatAsync(Jid chatJid, SyncActionValue.Types.SyncActionMessageRange? messageRange = null, CancellationToken ct = default) =>
        SendArchiveAsync(chatJid, false, messageRange, ct);

    private Task SendArchiveAsync(Jid chatJid, bool archived, SyncActionValue.Types.SyncActionMessageRange? range, CancellationToken ct)
    {
        var act = new SyncActionValue.Types.ArchiveChatAction { Archived = archived };
        if (range is not null) act.MessageRange = range.Clone();
        return PushChatMutationAsync(AppStateSchemas.Archive, chatJid, new SyncActionValue
        {
            Timestamp = NowMs(),
            ArchiveChatAction = act
        }, ct);
    }

    public Task MuteChatAsync(Jid chatJid, CancellationToken ct = default) =>
        SendMuteAsync(chatJid, true, MuteIndefinite, ct);

    public Task MuteChatUntilAsync(Jid chatJid, long muteEndTimestampMs, CancellationToken ct = default)
    {
        long nowMs = NowMs();
        if (muteEndTimestampMs <= nowMs)
        {
            throw new ArgumentOutOfRangeException(nameof(muteEndTimestampMs), "Mute end timestamp must be in the future.");
        }
        return SendMuteAsync(chatJid, true, muteEndTimestampMs, ct);
    }

    public Task UnmuteChatAsync(Jid chatJid, CancellationToken ct = default) =>
        SendMuteAsync(chatJid, false, endTs: null, ct);

    private Task SendMuteAsync(Jid chatJid, bool muted, long? endTs, CancellationToken ct)
    {
        var act = new SyncActionValue.Types.MuteAction { Muted = muted };
        if (endTs.HasValue) act.MuteEndTimestamp = endTs.Value;
        return PushChatMutationAsync(AppStateSchemas.Mute, chatJid, new SyncActionValue
        {
            Timestamp = NowMs(),
            MuteAction = act
        }, ct);
    }

    public Task MarkChatAsReadAsync(
        Jid chatJid,
        bool read,
        SyncActionValue.Types.SyncActionMessageRange? messageRange = null,
        CancellationToken ct = default)
    {
        var act = new SyncActionValue.Types.MarkChatAsReadAction { Read = read };
        if (messageRange is not null) act.MessageRange = messageRange.Clone();
        return PushChatMutationAsync(AppStateSchemas.MarkChatAsRead, chatJid, new SyncActionValue
        {
            Timestamp = NowMs(),
            MarkChatAsReadAction = act
        }, ct);
    }

    public Task LockChatAsync(Jid chatJid, bool locked, CancellationToken ct = default) =>
        PushChatMutationAsync(AppStateSchemas.LockChat, chatJid, new SyncActionValue
        {
            Timestamp = NowMs(),
            LockChatAction = new SyncActionValue.Types.LockChatAction { Locked = locked }
        }, ct);

    public Task StarMessageAsync(Jid chatJid, ChatMessageRef target, bool starred, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(target.MessageId);
        byte[] index = AppStateSchemas.Star.BuildIndex(
            chatJid.ToNonAdString(),
            target.MessageId,
            BoolStr(target.FromMe),
            ParticipantKey(target));
        var value = new SyncActionValue
        {
            Timestamp = NowMs(),
            StarAction = new SyncActionValue.Types.StarAction { Starred = starred }
        };
        return _syncd.SendMutationAsync(AppStateSchemas.Star.Collection, SyncdMutation.Types.SyncdOperation.Set, index, value, ct: ct);
    }

    public Task DeleteMessageForMeAsync(Jid chatJid, ChatMessageRef target, bool deleteMedia = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(target.MessageId);
        byte[] index = AppStateSchemas.DeleteMessageForMe.BuildIndex(
            chatJid.ToNonAdString(),
            target.MessageId,
            BoolStr(target.FromMe),
            ParticipantKey(target));
        var act = new SyncActionValue.Types.DeleteMessageForMeAction { DeleteMedia = deleteMedia };
        if (target.TimestampSeconds > 0) act.MessageTimestamp = target.TimestampSeconds;
        var value = new SyncActionValue
        {
            Timestamp = NowMs(),
            DeleteMessageForMeAction = act
        };
        return _syncd.SendMutationAsync(AppStateSchemas.DeleteMessageForMe.Collection, SyncdMutation.Types.SyncdOperation.Set, index, value, ct: ct);
    }

    public Task ClearChatAsync(
        Jid chatJid,
        ClearChatOptions? options = null,
        SyncActionValue.Types.SyncActionMessageRange? messageRange = null,
        CancellationToken ct = default)
    {
        ClearChatOptions opts = options ?? new ClearChatOptions();
        byte[] index = AppStateSchemas.ClearChat.BuildIndex(
            chatJid.ToNonAdString(),
            BoolStr(opts.DeleteStarred),
            BoolStr(opts.DeleteMedia));
        var act = new SyncActionValue.Types.ClearChatAction();
        if (messageRange is not null) act.MessageRange = messageRange.Clone();
        var value = new SyncActionValue { Timestamp = NowMs(), ClearChatAction = act };
        return _syncd.SendMutationAsync(AppStateSchemas.ClearChat.Collection, SyncdMutation.Types.SyncdOperation.Set, index, value, ct: ct);
    }

    public Task DeleteChatAsync(
        Jid chatJid,
        bool deleteMedia = false,
        SyncActionValue.Types.SyncActionMessageRange? messageRange = null,
        CancellationToken ct = default)
    {
        byte[] index = AppStateSchemas.DeleteChat.BuildIndex(chatJid.ToNonAdString(), BoolStr(deleteMedia));
        var act = new SyncActionValue.Types.DeleteChatAction();
        if (messageRange is not null) act.MessageRange = messageRange.Clone();
        var value = new SyncActionValue { Timestamp = NowMs(), DeleteChatAction = act };
        return _syncd.SendMutationAsync(AppStateSchemas.DeleteChat.Collection, SyncdMutation.Types.SyncdOperation.Set, index, value, ct: ct);
    }

    public Task SetFavoritesAsync(IEnumerable<Jid> favoriteChatJids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(favoriteChatJids);
        var act = new SyncActionValue.Types.FavoritesAction();
        foreach (Jid jid in favoriteChatJids)
        {
            act.Favorites.Add(new SyncActionValue.Types.FavoritesAction.Types.Favorite { Id = jid.ToNonAdString() });
        }
        var value = new SyncActionValue { Timestamp = NowMs(), FavoritesAction = act };
        return _syncd.SendMutationAsync(
            AppStateSchemas.Favorites.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.Favorites.BuildIndex(),
            value,
            ct: ct);
    }

    public Task SetFavoriteStickerAsync(
        string fileHash,
        bool isFavorite,
        string? directPath = null,
        string? mimetype = null,
        byte[]? mediaKey = null,
        byte[]? fileEncSha256 = null,
        uint? height = null,
        uint? width = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileHash);
        var act = new SyncActionValue.Types.StickerAction { IsFavorite = isFavorite };
        if (!string.IsNullOrEmpty(directPath)) act.DirectPath = directPath;
        if (!string.IsNullOrEmpty(mimetype)) act.Mimetype = mimetype;
        if (mediaKey is { Length: > 0 }) act.MediaKey = ByteString.CopyFrom(mediaKey);
        if (fileEncSha256 is { Length: > 0 }) act.FileEncSha256 = ByteString.CopyFrom(fileEncSha256);
        if (height.HasValue) act.Height = height.Value;
        if (width.HasValue) act.Width = width.Value;

        var value = new SyncActionValue { Timestamp = NowMs(), StickerAction = act };
        return _syncd.SendMutationAsync(
            AppStateSchemas.FavoriteSticker.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.FavoriteSticker.BuildIndex(fileHash),
            value,
            ct: ct);
    }

    public Task RemoveRecentStickerAsync(string fileHash, long? lastStickerSentTsSeconds = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileHash);
        var act = new SyncActionValue.Types.RemoveRecentStickerAction();
        if (lastStickerSentTsSeconds.HasValue) act.LastStickerSentTs = lastStickerSentTsSeconds.Value;

        var value = new SyncActionValue { Timestamp = NowMs(), RemoveRecentStickerAction = act };
        return _syncd.SendMutationAsync(
            AppStateSchemas.RemoveRecentSticker.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.RemoveRecentSticker.BuildIndex(fileHash),
            value,
            ct: ct);
    }

    public Task<SendMessageResult> SetDirectChatDisappearingAsync(
        Jid peerJid,
        uint expirationSeconds,
        CancellationToken ct = default)
    {
        if (_messageSender is null)
        {
            throw new InvalidOperationException("MessageSender is required to update per-chat disappearing settings.");
        }
        if (!(peerJid.IsPn() || peerJid.IsLid()))
        {
            throw new ArgumentException($"Direct disappearing timer requires PN or LID JID, got '{peerJid}'. Use GroupClient.SetEphemeralAsync for groups.", nameof(peerJid));
        }

        long nowSec = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        Message msg = RichMessageBuilders.BuildEphemeralSetting(expirationSeconds, nowSec);
        return _messageSender.SendDmAsync(peerJid.ToNonAd(), msg, ct: ct);
    }

    private Task PushChatMutationAsync(AppStateActionSpec spec, Jid chatJid, SyncActionValue value, CancellationToken ct) =>
        _syncd.SendMutationAsync(
            spec.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            spec.BuildIndex(chatJid.ToNonAdString()),
            value,
            ct: ct);

    private long NowMs() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    private static string BoolStr(bool flag) => flag ? "1" : "0";
    private static string ParticipantKey(ChatMessageRef target) =>
        target.FromMe || target.Participant is not Jid p ? "0" : p.ToNonAdString();
}
