using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Api;

public enum GroupHistoryRetryStage
{
    Upload = 0,
    Bundle = 1,
    Notice = 2
}

public sealed record GroupHistoryRetryToken(
    Jid GroupJid,
    IReadOnlyList<Jid> Recipients,
    Message BundleMessage,
    Message NoticeMessage,
    string BundleMessageId,
    string NoticeMessageId,
    int MessageCount,
    byte[]? CompressedPayload,
    GroupHistoryRetryStage Stage);

public sealed record PreparedGroupHistoryShare(
    IReadOnlyList<ParticipantChangeResult> Participants,
    Jid GroupJid,
    IReadOnlyList<Jid> Recipients,
    UploadedMediaResult Upload,
    long UploadTimestampSeconds,
    Message NoticeMessage,
    string BundleMessageId,
    string NoticeMessageId,
    int MessageCount,
    long OldestTimestamp);

public enum GroupHistoryShareStatus
{
    NotRequested = 0,
    Skipped = 1,
    PreparationFailed = 2,
    UploadFailed = 3,
    BundleNotSent = 4,
    BundleRejected = 5,
    BundleRetryableRejection = 6,
    BundleIndeterminate = 7,
    BundlePartialFanout = 8,
    NoticeNotSent = 9,
    NoticeRejected = 10,
    NoticeRetryableRejection = 11,
    NoticeIndeterminate = 12,
    Shared = 13
}

public sealed record GroupHistoryShareOutcome(
    GroupHistoryShareStatus Status,
    GroupHistorySkipReason? SkipReason = null,
    string? BundleMessageId = null,
    string? NoticeMessageId = null,
    int MessageCount = 0,
    int? EncryptedDevices = null,
    int? AddressedDevices = null,
    string? ErrorCode = null,
    string? ErrorText = null,
    GroupHistoryRetryToken? RetryToken = null);

public sealed record GroupHistoryAddResult(
    IReadOnlyList<ParticipantChangeResult> Participants,
    GroupHistoryShareOutcome HistoryShare);

/// <summary>
/// Orchestrates Group History Sharing when adding participants: validates group &amp; account
/// A/B props, selects eligible plaintext non-ephemeral messages, zlib-compresses and uploads
/// the encrypted bundle via <see cref="MediaTransferClient"/> (<c>/mms/group-history</c>),
/// sends the pairwise <see cref="Message.Types.MessageHistoryBundle"/> to opted-in new
/// members only, waits for correlated server ACK before broadcasting the group-wide
/// <see cref="Message.Types.MessageHistoryNotice"/>, and provides resumable retry tokens.
/// </summary>
public sealed class GroupHistoryCoordinator
{
    private readonly IqClient _iqClient;
    private readonly MessageSender _messageSender;
    private readonly MediaTransferClient? _mediaTransfer;
    private readonly DeviceCredentials _credentials;
    private readonly TimeProvider _timeProvider;
    private IReadOnlyDictionary<uint, string>? _accountProps;

    public GroupHistoryCoordinator(
        IqClient iqClient,
        MessageSender messageSender,
        MediaTransferClient? mediaTransfer,
        DeviceCredentials credentials,
        TimeProvider? timeProvider = null)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        _mediaTransfer = mediaTransfer;
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void UpdateAccountProps(IReadOnlyDictionary<uint, string> accountProps)
    {
        ArgumentNullException.ThrowIfNull(accountProps);
        _accountProps = accountProps;
    }

    public async Task<IReadOnlyDictionary<uint, string>> RefreshAccountPropsAsync(CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = AbPropsIq.BuildGetProps(iqId);
        Node resp = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);

        AbPropsResponse parsed = AbPropsIq.ParseResponse(resp);
        _accountProps = parsed.ExperimentProps;
        return parsed.ExperimentProps;
    }

    public async Task<GroupHistoryAddResult> AddParticipantsWithHistoryAsync(
        GroupClient groups,
        Jid groupJid,
        IReadOnlyList<Jid> participants,
        IReadOnlyList<Jid> optedInReceivers,
        IReadOnlyList<WebMessageInfo> historyMessages,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        IReadOnlyList<ParticipantChangeResult> addResults = await groups.AddParticipantsAsync(groupJid, participants, ct).ConfigureAwait(false);
        if (optedInReceivers.Count == 0)
        {
            return new GroupHistoryAddResult(addResults, new GroupHistoryShareOutcome(GroupHistoryShareStatus.NotRequested));
        }

        (GroupMetadata Metadata, GroupHistoryLimits Limits, GroupHistorySkipReason? Skip) ctx =
            await CheckGroupHistoryContextAsync(groups, groupJid, ct).ConfigureAwait(false);
        if (ctx.Skip is { } skipReason)
        {
            return new GroupHistoryAddResult(addResults, new GroupHistoryShareOutcome(GroupHistoryShareStatus.Skipped, skipReason));
        }

        List<Jid> historyReceivers = SelectOptedInSuccessfulRecipients(ctx.Metadata, addResults, optedInReceivers);
        if (historyReceivers.Count == 0)
        {
            return new GroupHistoryAddResult(
                addResults,
                new GroupHistoryShareOutcome(GroupHistoryShareStatus.Skipped, GroupHistorySkipReason.NoOptedInSuccessfulRecipients));
        }

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var (selected, oldestTs) = GroupHistoryCodec.SelectEligibleMessages(groupJid, historyMessages, now, ctx.Limits);
        if (selected.Count == 0)
        {
            return new GroupHistoryAddResult(
                addResults,
                new GroupHistoryShareOutcome(GroupHistoryShareStatus.Skipped, GroupHistorySkipReason.NoEligibleMessages));
        }

        var nonReceivers = addResults
            .Where(r => r.IsOk && !historyReceivers.Any(hr => string.Equals(hr.ToNonAdString(), r.Jid.ToNonAdString(), StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Jid.ToNonAdString())
            .ToArray();

        var historyMeta = new Message.Types.MessageHistoryMetadata
        {
            OldestMessageTimestampInWindow = Math.Max(0, now - ctx.Limits.TimeWindowSeconds),
            MessageCount = selected.Count,
            OldestMessageTimestampInBundle = oldestTs
        };
        foreach (Jid hr in historyReceivers)
        {
            historyMeta.HistoryReceivers.Add(hr.ToNonAdString());
        }
        foreach (string nr in nonReceivers)
        {
            historyMeta.NonHistoryReceivers.Add(nr);
        }

        byte[] compressed;
        try
        {
            compressed = GroupHistoryCodec.CompressGroupHistory(selected);
        }
        catch (Exception ex)
        {
            return new GroupHistoryAddResult(
                addResults,
                new GroupHistoryShareOutcome(GroupHistoryShareStatus.PreparationFailed, ErrorText: ex.Message));
        }

        Message noticeMsg = GroupHistoryCodec.BuildNoticeMessage(historyMeta);
        string bundleMsgId = _messageSender.NextMessageId();
        string noticeMsgId = _messageSender.NextMessageId();

        if (_mediaTransfer is null)
        {
            var retry = new GroupHistoryRetryToken(
                groupJid.ToNonAd(),
                historyReceivers,
                new Message(),
                noticeMsg,
                bundleMsgId,
                noticeMsgId,
                selected.Count,
                compressed,
                GroupHistoryRetryStage.Upload);
            return new GroupHistoryAddResult(
                addResults,
                new GroupHistoryShareOutcome(GroupHistoryShareStatus.UploadFailed, ErrorText: "MediaTransferClient is not configured.", RetryToken: retry));
        }

        UploadedMediaResult upload;
        long uploadTs = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        try
        {
            upload = await _mediaTransfer.UploadAsync(compressed, WaMediaType.GroupHistory, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var retry = new GroupHistoryRetryToken(
                groupJid.ToNonAd(),
                historyReceivers,
                new Message(),
                noticeMsg,
                bundleMsgId,
                noticeMsgId,
                selected.Count,
                compressed,
                GroupHistoryRetryStage.Upload);
            return new GroupHistoryAddResult(
                addResults,
                new GroupHistoryShareOutcome(GroupHistoryShareStatus.UploadFailed, ErrorText: ex.Message, RetryToken: retry));
        }

        var prepared = new PreparedGroupHistoryShare(
            addResults,
            groupJid.ToNonAd(),
            historyReceivers,
            upload,
            uploadTs,
            noticeMsg,
            bundleMsgId,
            noticeMsgId,
            selected.Count,
            oldestTs);

        GroupHistoryShareOutcome deliveryOutcome = await DeliverPreparedShareAsync(groups, prepared, ct).ConfigureAwait(false);
        return new GroupHistoryAddResult(addResults, deliveryOutcome);
    }

    public async Task<GroupHistoryShareOutcome> DeliverPreparedShareAsync(
        GroupClient groups,
        PreparedGroupHistoryShare prepared,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(prepared);

        var ctx = await CheckGroupHistoryContextAsync(groups, prepared.GroupJid, ct).ConfigureAwait(false);
        if (ctx.Skip is { } skipReason)
        {
            return new GroupHistoryShareOutcome(GroupHistoryShareStatus.Skipped, skipReason);
        }

        foreach (Jid r in prepared.Recipients)
        {
            GroupParticipantInfo? member = ctx.Metadata.Participants.FirstOrDefault(p =>
                string.Equals(p.Jid.ToNonAdString(), r.ToNonAdString(), StringComparison.OrdinalIgnoreCase)
                || (p.PhoneNumber is Jid pn && string.Equals(pn.ToNonAdString(), r.ToNonAdString(), StringComparison.OrdinalIgnoreCase))
                || (p.Lid is Jid l && string.Equals(l.ToNonAdString(), r.ToNonAdString(), StringComparison.OrdinalIgnoreCase)));

            if (member is null || member.Details?.GroupHistorySent == true)
            {
                return new GroupHistoryShareOutcome(GroupHistoryShareStatus.Skipped, GroupHistorySkipReason.NoOptedInSuccessfulRecipients);
            }
        }

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (prepared.MessageCount > ctx.Limits.MaxMessages
            || prepared.OldestTimestamp < Math.Max(0, now - ctx.Limits.TimeWindowSeconds))
        {
            return new GroupHistoryShareOutcome(GroupHistoryShareStatus.Skipped, GroupHistorySkipReason.NoEligibleMessages);
        }

        Message.Types.MessageHistoryMetadata meta = prepared.NoticeMessage.MessageHistoryNotice.MessageHistoryMetadata;
        Message bundleMsg = GroupHistoryCodec.BuildBundleMessage(
            prepared.Upload.DirectPath,
            prepared.Upload.FileSha256,
            prepared.Upload.FileEncSha256,
            prepared.Upload.MediaKey32,
            prepared.UploadTimestampSeconds,
            meta);

        var retry = new GroupHistoryRetryToken(
            prepared.GroupJid,
            prepared.Recipients,
            bundleMsg,
            prepared.NoticeMessage,
            prepared.BundleMessageId,
            prepared.NoticeMessageId,
            prepared.MessageCount,
            CompressedPayload: null,
            GroupHistoryRetryStage.Bundle);

        return await DriveDeliveryAsync(groups, retry, ctx.Limits, startAtNotice: false, ctx.Metadata, ct).ConfigureAwait(false);
    }

    public async Task<GroupHistoryShareOutcome> RetryGroupHistoryAsync(
        GroupClient groups,
        GroupHistoryRetryToken retry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(retry);

        var ctx = await CheckGroupHistoryContextAsync(groups, retry.GroupJid, ct).ConfigureAwait(false);
        if (ctx.Skip is { } skipReason)
        {
            return new GroupHistoryShareOutcome(GroupHistoryShareStatus.Skipped, skipReason);
        }

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        GroupHistoryRetryToken activeToken = retry;
        if (retry.Stage == GroupHistoryRetryStage.Upload)
        {
            if (retry.CompressedPayload is null
                || !GroupHistoryCodec.FitsLimits(retry.NoticeMessage.MessageHistoryNotice?.MessageHistoryMetadata, ctx.Limits, now))
            {
                return new GroupHistoryShareOutcome(GroupHistoryShareStatus.Skipped, GroupHistorySkipReason.NoEligibleMessages);
            }
            if (_mediaTransfer is null)
            {
                return new GroupHistoryShareOutcome(GroupHistoryShareStatus.UploadFailed, ErrorText: "MediaTransferClient is not configured.", RetryToken: retry);
            }
            try
            {
                UploadedMediaResult upload = await _mediaTransfer.UploadAsync(retry.CompressedPayload, WaMediaType.GroupHistory, ct: ct).ConfigureAwait(false);
                Message bundleMsg = GroupHistoryCodec.BuildBundleMessage(
                    upload.DirectPath,
                    upload.FileSha256,
                    upload.FileEncSha256,
                    upload.MediaKey32,
                    now,
                    retry.NoticeMessage.MessageHistoryNotice!.MessageHistoryMetadata!);
                activeToken = retry with { BundleMessage = bundleMsg, CompressedPayload = null, Stage = GroupHistoryRetryStage.Bundle };
            }
            catch (Exception ex)
            {
                return new GroupHistoryShareOutcome(GroupHistoryShareStatus.UploadFailed, ErrorText: ex.Message, RetryToken: retry);
            }
        }

        return await DriveDeliveryAsync(groups, activeToken, ctx.Limits, startAtNotice: activeToken.Stage == GroupHistoryRetryStage.Notice, ctx.Metadata, ct).ConfigureAwait(false);
    }

    private async Task<GroupHistoryShareOutcome> DriveDeliveryAsync(
        GroupClient groups,
        GroupHistoryRetryToken token,
        GroupHistoryLimits limits,
        bool startAtNotice,
        GroupMetadata? knownMetadata,
        CancellationToken ct)
    {
        bool noticeStage = startAtNotice;
        while (true)
        {
            long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (!noticeStage && !GroupHistoryCodec.FitsLimits(token.BundleMessage.MessageHistoryBundle?.MessageHistoryMetadata, limits, now))
            {
                return new GroupHistoryShareOutcome(GroupHistoryShareStatus.Skipped, GroupHistorySkipReason.NoEligibleMessages);
            }

            if (!noticeStage)
            {
                SendDirectGroupResult bundleSend;
                try
                {
                    bundleSend = await _messageSender.SendGroupDirectAsync(
                        token.GroupJid,
                        token.Recipients,
                        token.BundleMessage,
                        messageId: token.BundleMessageId,
                        awaitServerAck: true,
                        ct: ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new GroupHistoryShareOutcome(
                        GroupHistoryShareStatus.BundleNotSent,
                        BundleMessageId: token.BundleMessageId,
                        ErrorText: ex.Message,
                        RetryToken: token);
                }

                if (!bundleSend.AckReceived)
                {
                    return new GroupHistoryShareOutcome(
                        GroupHistoryShareStatus.BundleIndeterminate,
                        BundleMessageId: token.BundleMessageId,
                        RetryToken: token);
                }

                if (!string.IsNullOrEmpty(bundleSend.AckErrorCode) || !string.IsNullOrEmpty(bundleSend.AckErrorText))
                {
                    bool retryable = IsRetryableCode(bundleSend.AckErrorCode);
                    return new GroupHistoryShareOutcome(
                        retryable ? GroupHistoryShareStatus.BundleRetryableRejection : GroupHistoryShareStatus.BundleRejected,
                        BundleMessageId: token.BundleMessageId,
                        ErrorCode: bundleSend.AckErrorCode,
                        ErrorText: bundleSend.AckErrorText,
                        RetryToken: retryable ? token : null);
                }

                if (bundleSend.EncryptedDeviceCount < bundleSend.AddressedDeviceCount)
                {
                    return new GroupHistoryShareOutcome(
                        GroupHistoryShareStatus.BundlePartialFanout,
                        BundleMessageId: token.BundleMessageId,
                        EncryptedDevices: bundleSend.EncryptedDeviceCount,
                        AddressedDevices: bundleSend.AddressedDeviceCount,
                        RetryToken: token);
                }

                noticeStage = true;
            }

            GroupHistoryRetryToken noticeToken = token with { Stage = GroupHistoryRetryStage.Notice };
            GroupMetadata? meta = knownMetadata
                ?? (groups.TryGetCached(token.GroupJid, out GroupMetadata? cachedMeta) ? cachedMeta : null)
                ?? await groups.QueryMetadataAsync(token.GroupJid, bypassCache: false, ct).ConfigureAwait(false);
            IReadOnlyList<Jid> allMembers = meta?.Participants.Select(p => p.Jid).ToArray() ?? token.Recipients;

            SendDirectGroupResult noticeSend;
            try
            {
                noticeSend = await _messageSender.SendGroupWithAckAsync(
                    token.GroupJid,
                    token.NoticeMessage,
                    allMembers,
                    messageId: token.NoticeMessageId,
                    addressingMode: meta?.AddressingMode,
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new GroupHistoryShareOutcome(
                    GroupHistoryShareStatus.NoticeNotSent,
                    BundleMessageId: token.BundleMessageId,
                    NoticeMessageId: token.NoticeMessageId,
                    ErrorText: ex.Message,
                    RetryToken: noticeToken);
            }

            if (!noticeSend.AckReceived)
            {
                return new GroupHistoryShareOutcome(
                    GroupHistoryShareStatus.NoticeIndeterminate,
                    BundleMessageId: token.BundleMessageId,
                    NoticeMessageId: token.NoticeMessageId,
                    RetryToken: noticeToken);
            }

            if (!string.IsNullOrEmpty(noticeSend.AckErrorCode) || !string.IsNullOrEmpty(noticeSend.AckErrorText))
            {
                bool retryable = IsRetryableCode(noticeSend.AckErrorCode);
                return new GroupHistoryShareOutcome(
                    retryable ? GroupHistoryShareStatus.NoticeRetryableRejection : GroupHistoryShareStatus.NoticeRejected,
                    BundleMessageId: token.BundleMessageId,
                    NoticeMessageId: token.NoticeMessageId,
                    ErrorCode: noticeSend.AckErrorCode,
                    ErrorText: noticeSend.AckErrorText,
                    RetryToken: retryable ? noticeToken : null);
            }

            return new GroupHistoryShareOutcome(
                GroupHistoryShareStatus.Shared,
                BundleMessageId: token.BundleMessageId,
                NoticeMessageId: token.NoticeMessageId,
                MessageCount: token.MessageCount);
        }
    }

    private async Task<(GroupMetadata Metadata, GroupHistoryLimits Limits, GroupHistorySkipReason? Skip)> CheckGroupHistoryContextAsync(
        GroupClient groups,
        Jid groupJid,
        CancellationToken ct)
    {
        GroupMetadata? metadata;
        try
        {
            metadata = await groups.QueryMetadataAsync(groupJid, bypassCache: true, ct).ConfigureAwait(false);
        }
        catch
        {
            return (null!, null!, GroupHistorySkipReason.GroupMetadataUnavailable);
        }

        if (metadata is null)
        {
            return (null!, null!, GroupHistorySkipReason.GroupMetadataUnavailable);
        }
        if (metadata.HasCapi || metadata.IsParentGroup)
        {
            return (metadata, null!, GroupHistorySkipReason.UnsupportedGroup);
        }
        if (!metadata.HasGroupHistory)
        {
            return (metadata, null!, GroupHistorySkipReason.SharingDisabled);
        }

        GroupParticipantInfo? ownParticipant = metadata.Participants.FirstOrDefault(p =>
            (_credentials.Pn is Jid pn && MatchParticipant(p, pn))
            || (_credentials.Lid is Jid lid && MatchParticipant(p, lid)));

        if (ownParticipant is null || !GroupHistoryCodec.CanUserShareHistory(ownParticipant.IsAdmin, metadata.ShareHistoryMode))
        {
            return (metadata, null!, GroupHistorySkipReason.SenderNotAuthorized);
        }

        if (_accountProps is null || _accountProps.Count == 0)
        {
            return (metadata, null!, GroupHistorySkipReason.AccountPropsUnavailable);
        }

        GroupPropsResponse groupProps;
        try
        {
            string iqId = _iqClient.NextRequestId();
            Node gpResp = await _iqClient.SendIqAsync(AbPropsIq.BuildGetGroupProps(iqId, groupJid), IqClient.DefaultTimeout, ct).ConfigureAwait(false);
            groupProps = AbPropsIq.ParseGroupPropsResponse(gpResp);
        }
        catch
        {
            return (metadata, null!, GroupHistorySkipReason.GroupPropsUnavailable);
        }

        try
        {
            GroupHistoryLimits limits = GroupHistoryCodec.ResolveLimits(_accountProps, groupProps);
            return (metadata, limits, null);
        }
        catch (InvalidOperationException ex) when (Enum.TryParse<GroupHistorySkipReason>(ex.Message, out GroupHistorySkipReason reason))
        {
            return (metadata, null!, reason);
        }
    }

    private static List<Jid> SelectOptedInSuccessfulRecipients(
        GroupMetadata metadata,
        IReadOnlyList<ParticipantChangeResult> addResults,
        IReadOnlyList<Jid> optedIn)
    {
        var okJids = addResults.Where(r => r.IsOk).Select(r => r.Jid.ToNonAdString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var optedInSet = optedIn.Select(j => j.ToNonAdString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<Jid>();
        foreach (GroupParticipantInfo p in metadata.Participants)
        {
            string bare = p.Jid.ToNonAdString();
            bool wasAdded = okJids.Contains(bare)
                || (p.PhoneNumber is Jid pn && okJids.Contains(pn.ToNonAdString()))
                || (p.Lid is Jid l && okJids.Contains(l.ToNonAdString()));
            if (!wasAdded || p.Details?.GroupHistorySent == true)
            {
                continue;
            }
            bool isOptedIn = optedInSet.Contains(bare)
                || (p.PhoneNumber is Jid pn2 && optedInSet.Contains(pn2.ToNonAdString()))
                || (p.Lid is Jid l2 && optedInSet.Contains(l2.ToNonAdString()));
            if (isOptedIn)
            {
                list.Add(p.Jid.ToNonAd());
            }
        }
        return list;
    }

    private static bool MatchParticipant(GroupParticipantInfo p, Jid user)
    {
        string bare = user.ToNonAdString();
        return string.Equals(p.Jid.ToNonAdString(), bare, StringComparison.OrdinalIgnoreCase)
            || (p.PhoneNumber is Jid pn && string.Equals(pn.ToNonAdString(), bare, StringComparison.OrdinalIgnoreCase))
            || (p.Lid is Jid l && string.Equals(l.ToNonAdString(), bare, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRetryableCode(string? code) =>
        code == "429" || (int.TryParse(code, out int c) && c >= 500 && c <= 599);
}
