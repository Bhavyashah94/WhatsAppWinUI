using Whatsapp;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;

namespace WhatsApp.Api;

/// <summary>
/// Business Labels (<c>label_edit</c>, <c>label_jid</c>, <c>label_message</c>, <c>label_reordering</c>)
/// via AppState (<c>regular</c> collection) matching <c>src/features/labels.rs</c>.
/// </summary>
public sealed class LabelsClient
{
    private readonly SyncdCoordinator _syncd;
    private readonly TimeProvider _timeProvider;

    public LabelsClient(SyncdCoordinator syncd, TimeProvider? timeProvider = null)
    {
        _syncd = syncd ?? throw new ArgumentNullException(nameof(syncd));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task CreateOrUpdateLabelAsync(string labelId, string name, int color, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(labelId);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            LabelEditAction = new SyncActionValue.Types.LabelEditAction
            {
                Name = name,
                Color = color,
                Deleted = false
            }
        };
        return _syncd.SendMutationAsync(
            AppStateSchemas.LabelEdit.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.LabelEdit.BuildIndex(labelId),
            value,
            AppStateSchemas.LabelEdit.Version,
            ct);
    }

    public Task DeleteLabelAsync(string labelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(labelId);
        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            LabelEditAction = new SyncActionValue.Types.LabelEditAction
            {
                Deleted = true
            }
        };
        return _syncd.SendMutationAsync(
            AppStateSchemas.LabelEdit.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.LabelEdit.BuildIndex(labelId),
            value,
            AppStateSchemas.LabelEdit.Version,
            ct);
    }

    public Task AddChatLabelAsync(string labelId, Jid chatJid, CancellationToken ct = default) =>
        SetChatLabelAsync(labelId, chatJid, labeled: true, ct);

    public Task RemoveChatLabelAsync(string labelId, Jid chatJid, CancellationToken ct = default) =>
        SetChatLabelAsync(labelId, chatJid, labeled: false, ct);

    public Task AddMessageLabelAsync(string labelId, Jid chatJid, string messageId, CancellationToken ct = default) =>
        SetMessageLabelAsync(labelId, chatJid, messageId, labeled: true, ct);

    public Task RemoveMessageLabelAsync(string labelId, Jid chatJid, string messageId, CancellationToken ct = default) =>
        SetMessageLabelAsync(labelId, chatJid, messageId, labeled: false, ct);

    public Task ReorderLabelsAsync(IReadOnlyList<int> sortedLabelIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sortedLabelIds);
        var act = new SyncActionValue.Types.LabelReorderingAction();
        act.SortedLabelIds.AddRange(sortedLabelIds);
        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            LabelReorderingAction = act
        };
        return _syncd.SendMutationAsync(
            AppStateSchemas.LabelReordering.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.LabelReordering.BuildIndex(),
            value,
            AppStateSchemas.LabelReordering.Version,
            ct);
    }

    private Task SetChatLabelAsync(string labelId, Jid chatJid, bool labeled, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(labelId);
        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            LabelAssociationAction = new SyncActionValue.Types.LabelAssociationAction { Labeled = labeled }
        };
        return _syncd.SendMutationAsync(
            AppStateSchemas.LabelJid.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.LabelJid.BuildIndex(labelId, chatJid.ToNonAdString()),
            value,
            AppStateSchemas.LabelJid.Version,
            ct);
    }

    private Task SetMessageLabelAsync(string labelId, Jid chatJid, string messageId, bool labeled, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(labelId);
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            LabelAssociationAction = new SyncActionValue.Types.LabelAssociationAction { Labeled = labeled }
        };
        return _syncd.SendMutationAsync(
            AppStateSchemas.LabelMessage.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.LabelMessage.BuildIndex(labelId, chatJid.ToNonAdString(), messageId, "0", "0"),
            value,
            AppStateSchemas.LabelMessage.Version,
            ct);
    }
}
