using Whatsapp;
using WhatsApp.Protocol.AppState;

namespace WhatsApp.Api;

/// <summary>
/// Business Quick Replies (<c>quick_reply</c> in <c>regular</c> collection) matching
/// <c>src/features/quick_replies.rs</c>. Deletion sends <c>SyncdOperation.SET</c> with <c>deleted=true</c>.
/// </summary>
public sealed class QuickRepliesClient
{
    private readonly SyncdCoordinator _syncd;
    private readonly TimeProvider _timeProvider;

    public QuickRepliesClient(SyncdCoordinator syncd, TimeProvider? timeProvider = null)
    {
        _syncd = syncd ?? throw new ArgumentNullException(nameof(syncd));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task SetQuickReplyAsync(
        string id,
        string shortcut,
        string message,
        IReadOnlyList<string>? keywords = null,
        int count = 0,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(shortcut);
        ArgumentException.ThrowIfNullOrEmpty(message);
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Quick reply count cannot be negative.");
        }

        var qr = new SyncActionValue.Types.QuickReplyAction
        {
            Shortcut = shortcut,
            Message = message,
            Count = count,
            Deleted = false
        };
        if (keywords is { Count: > 0 })
        {
            qr.Keywords.AddRange(keywords);
        }

        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            QuickReplyAction = qr
        };
        return _syncd.SendMutationAsync(
            AppStateSchemas.QuickReply.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.QuickReply.BuildIndex(id),
            value,
            AppStateSchemas.QuickReply.Version,
            ct);
    }

    public Task DeleteQuickReplyAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var qr = new SyncActionValue.Types.QuickReplyAction
        {
            Shortcut = string.Empty,
            Message = string.Empty,
            Count = 0,
            Deleted = true
        };
        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            QuickReplyAction = qr
        };
        return _syncd.SendMutationAsync(
            AppStateSchemas.QuickReply.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.QuickReply.BuildIndex(id),
            value,
            AppStateSchemas.QuickReply.Version,
            ct);
    }
}
