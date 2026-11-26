using System.Text.Json;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Api;

/// <summary>
/// Newsletter (Channel) feature client implementing MEX GraphQL lifecycle/directory/admin APIs,
/// <c>xmlns="newsletter"</c> history/my_addons/live_updates IQs, and plaintext channel publishing,
/// reactions, polls, edits, and revocations matching <c>src/features/newsletter.rs</c>.
/// </summary>
public sealed class NewsletterClient
{
    private readonly IqClient _iqClient;
    private readonly MexClient _mexClient;
    private readonly NoiseSessionSocket _socket;
    private readonly MessageSender _messageSender;
    private readonly ILidPnCache _lidPnCache;
    private readonly MediaTransferClient? _mediaTransfer;
    private readonly Func<WhatsAppEvent, ValueTask>? _emitEvent;
    private readonly TimeProvider _timeProvider;

    public NewsletterClient(
        IqClient iqClient,
        MexClient mexClient,
        NoiseSessionSocket socket,
        MessageSender messageSender,
        ILidPnCache lidPnCache,
        MediaTransferClient? mediaTransfer = null,
        Func<WhatsAppEvent, ValueTask>? emitEvent = null,
        TimeProvider? timeProvider = null)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _mexClient = mexClient ?? throw new ArgumentNullException(nameof(mexClient));
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _mediaTransfer = mediaTransfer;
        _emitEvent = emitEvent;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ─── MEX Metadata, Lifecycle & Subscriptions ─────────────────────────

    public async Task<IReadOnlyList<NewsletterMetadata>> ListSubscribedAsync(CancellationToken ct = default)
    {
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchSubscribedNewsletters,
            NewsletterStanzas.BuildListSubscribedVariables(),
            ct: ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseSubscribedNewsletters(resp.Data);
    }

    public async Task<NewsletterMetadata> GetMetadataAsync(Jid channelJid, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchNewsletter,
            NewsletterStanzas.BuildFetchNewsletterVariables(channelJid.ToNonAdString(), "JID"),
            ct: ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseSingleNewsletter(resp.Data, "xwa2_newsletter")
            ?? throw new KeyNotFoundException($"Newsletter '{channelJid}' not found.");
    }

    public async Task<NewsletterMetadata> GetMetadataByInviteAsync(string inviteCode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteCode);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchNewsletter,
            NewsletterStanzas.BuildFetchNewsletterVariables(inviteCode, "INVITE"),
            ct: ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseSingleNewsletter(resp.Data, "xwa2_newsletter")
            ?? throw new KeyNotFoundException($"Newsletter for invite '{inviteCode}' not found.");
    }

    public async Task<NewsletterMetadata> CreateAsync(
        string name,
        string? description = null,
        byte[]? pictureJpeg = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.CreateNewsletter,
            NewsletterStanzas.BuildCreateNewsletterVariables(name, description, pictureJpeg),
            ct: ct).ConfigureAwait(false);

        NewsletterMetadata created = NewsletterStanzas.ParseSingleNewsletter(resp.Data, "xwa2_newsletter_create")
            ?? throw new InvalidDataException("Newsletter creation returned null payload.");
        await EmitAsync(new NewsletterMetadataUpdatedEvent(created, NowUnixSeconds())).ConfigureAwait(false);
        return created;
    }

    public async Task<NewsletterMetadata> JoinAsync(Jid channelJid, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.JoinNewsletter,
            NewsletterStanzas.BuildNewsletterIdVariables(channelJid),
            ct: ct).ConfigureAwait(false);

        NewsletterMetadata joined = NewsletterStanzas.ParseSingleNewsletter(resp.Data, "xwa2_newsletter_join_v2")
            ?? throw new InvalidDataException($"Failed to join newsletter '{channelJid}'.");
        await EmitAsync(new NewsletterJoinedEvent(joined, NowUnixSeconds())).ConfigureAwait(false);
        return joined;
    }

    public async Task LeaveAsync(Jid channelJid, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.LeaveNewsletter,
            NewsletterStanzas.BuildNewsletterIdVariables(channelJid),
            ct: ct).ConfigureAwait(false);

        RequireNonNullField(resp.Data, "xwa2_newsletter_leave_v2", $"Failed to leave newsletter '{channelJid}'.");
        await EmitAsync(new NewsletterLeftEvent(channelJid.ToNonAdString(), NowUnixSeconds())).ConfigureAwait(false);
    }

    public async Task<NewsletterMetadata> UpdateAsync(
        Jid channelJid,
        string? name = null,
        string? description = null,
        CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.UpdateNewsletter,
            NewsletterStanzas.BuildUpdateNewsletterVariables(channelJid, name, description),
            ct: ct).ConfigureAwait(false);

        NewsletterMetadata updated = NewsletterStanzas.ParseSingleNewsletter(resp.Data, "xwa2_newsletter_update")
            ?? throw new InvalidDataException($"Newsletter update returned null for '{channelJid}'.");
        await EmitAsync(new NewsletterMetadataUpdatedEvent(updated, NowUnixSeconds())).ConfigureAwait(false);
        return updated;
    }

    public async Task<NewsletterMetadata> SetPictureAsync(Jid channelJid, byte[] jpegBytes, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        ArgumentNullException.ThrowIfNull(jpegBytes);
        if (jpegBytes.Length == 0)
        {
            throw new ArgumentException("Picture data cannot be empty; use RemovePictureAsync instead.", nameof(jpegBytes));
        }

        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.UpdateNewsletter,
            NewsletterStanzas.BuildUpdateNewsletterVariables(channelJid, pictureJpeg: jpegBytes),
            ct: ct).ConfigureAwait(false);

        NewsletterMetadata updated = NewsletterStanzas.ParseSingleNewsletter(resp.Data, "xwa2_newsletter_update")
            ?? throw new InvalidDataException($"Newsletter picture update returned null for '{channelJid}'.");
        await EmitAsync(new NewsletterMetadataUpdatedEvent(updated, NowUnixSeconds())).ConfigureAwait(false);
        return updated;
    }

    public async Task<NewsletterMetadata> RemovePictureAsync(Jid channelJid, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.UpdateNewsletter,
            NewsletterStanzas.BuildUpdateNewsletterVariables(channelJid, clearPicture: true),
            ct: ct).ConfigureAwait(false);

        NewsletterMetadata updated = NewsletterStanzas.ParseSingleNewsletter(resp.Data, "xwa2_newsletter_update")
            ?? throw new InvalidDataException($"Newsletter picture removal returned null for '{channelJid}'.");
        await EmitAsync(new NewsletterMetadataUpdatedEvent(updated, NowUnixSeconds())).ConfigureAwait(false);
        return updated;
    }

    public async Task DeleteAsync(Jid channelJid, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.DeleteNewsletter,
            NewsletterStanzas.BuildNewsletterIdVariables(channelJid),
            ct: ct).ConfigureAwait(false);

        RequireNonNullField(resp.Data, "xwa2_newsletter_delete_v2", $"Failed to delete newsletter '{channelJid}'.");
        await EmitAsync(new NewsletterDeletedEvent(channelJid.ToNonAdString(), NowUnixSeconds())).ConfigureAwait(false);
    }

    public async Task SetFollowerMuteAsync(Jid channelJid, bool muted, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        await SetUserSettingMuteAsync(channelJid, "MUTE_FOLLOWER_ACTIVITY", muted, ct).ConfigureAwait(false);
    }

    public async Task SetAdminMuteAsync(Jid channelJid, bool muted, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        await SetUserSettingMuteAsync(channelJid, "MUTE_ADMIN_ACTIVITY", muted, ct).ConfigureAwait(false);
    }

    private async Task SetUserSettingMuteAsync(Jid channelJid, string muteType, bool muted, CancellationToken ct)
    {
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.UpdateNewsletterUserSetting,
            NewsletterStanzas.BuildUpdateUserSettingVariables(channelJid, muteType, muted),
            ct: ct).ConfigureAwait(false);

        RequireNonNullField(
            resp.Data,
            "xwa2_newsletter_update_user_setting",
            $"Failed to update newsletter setting '{muteType}' for '{channelJid}'.");
    }

    // ─── Admin, Invites, Ownership, Followers & Pins ─────────────────────

    public async Task<NewsletterAdminInfo> GetAdminInfoAsync(Jid channelJid, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchNewsletterAdminInfo,
            NewsletterStanzas.BuildNewsletterIdVariables(channelJid),
            ct: ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseAdminInfo(resp.Data);
    }

    public async Task<IReadOnlyList<NewsletterFollower>> GetFollowersAsync(Jid channelJid, uint count = 100, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchNewsletterFollowers,
            NewsletterStanzas.BuildFetchFollowersVariables(channelJid, count),
            ct: ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseFollowers(resp.Data);
    }

    public async Task ChangeOwnerAsync(Jid channelJid, Jid userJid, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        Jid targetLid = ResolveAdminLidOrThrow(userJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.ChangeNewsletterOwner,
            NewsletterStanzas.BuildNewsletterUserActionVariables(channelJid, targetLid),
            ct: ct).ConfigureAwait(false);
        RequireNonNullField(resp.Data, "xwa2_newsletter_change_owner", $"Failed to change owner for '{channelJid}'.");
    }

    public async Task DemoteAdminAsync(Jid channelJid, Jid userJid, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        Jid targetLid = ResolveAdminLidOrThrow(userJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.DemoteNewsletterAdmin,
            NewsletterStanzas.BuildNewsletterUserActionVariables(channelJid, targetLid),
            ct: ct).ConfigureAwait(false);
        RequireNonNullField(resp.Data, "xwa2_newsletter_admin_demote", $"Failed to demote admin in '{channelJid}'.");
    }

    public async Task CreateAdminInviteAsync(Jid channelJid, Jid userJid, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        Jid targetLid = ResolveAdminLidOrThrow(userJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.CreateNewsletterAdminInvite,
            NewsletterStanzas.BuildNewsletterUserActionVariables(channelJid, targetLid),
            ct: ct).ConfigureAwait(false);
        RequireNonNullField(resp.Data, "xwa2_newsletter_admin_invite_create", $"Failed to create admin invite for '{channelJid}'.");
    }

    public async Task AcceptAdminInviteAsync(Jid channelJid, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.AcceptNewsletterAdminInvite,
            NewsletterStanzas.BuildNewsletterIdVariables(channelJid),
            ct: ct).ConfigureAwait(false);
        RequireNonNullField(resp.Data, "xwa2_newsletter_admin_invite_accept", $"Failed to accept admin invite for '{channelJid}'.");
    }

    public async Task RevokeAdminInviteAsync(Jid channelJid, Jid userJid, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        Jid targetLid = ResolveAdminLidOrThrow(userJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.RevokeNewsletterAdminInvite,
            NewsletterStanzas.BuildNewsletterUserActionVariables(channelJid, targetLid),
            ct: ct).ConfigureAwait(false);
        RequireNonNullField(resp.Data, "xwa2_newsletter_admin_invite_revoke", $"Failed to revoke admin invite for '{channelJid}'.");
    }

    public async Task PinMessagesAsync(Jid channelJid, IReadOnlyList<ulong> serverIds, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        ArgumentNullException.ThrowIfNull(serverIds);
        string[] ids = serverIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await _mexClient.ExecuteAsync(
            MexStanzas.NewsletterPinMessages,
            NewsletterStanzas.BuildPinMessagesVariables(channelJid, ids),
            ct: ct).ConfigureAwait(false);
    }

    public async Task UnpinMessagesAsync(Jid channelJid, IReadOnlyList<ulong> serverIds, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        ArgumentNullException.ThrowIfNull(serverIds);
        string[] ids = serverIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await _mexClient.ExecuteAsync(
            MexStanzas.NewsletterUnpinMessages,
            NewsletterStanzas.BuildPinMessagesVariables(channelJid, ids),
            ct: ct).ConfigureAwait(false);
    }

    // ─── Directory & Search ──────────────────────────────────────────────

    public async Task<IReadOnlyList<NewsletterDirectoryCategoryPreview>> GetDirectoryCategoriesPreviewAsync(
        IReadOnlyList<string>? categories = null,
        string? countryCode = null,
        int? perCategoryLimit = null,
        CancellationToken ct = default)
    {
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchNewsletterDirectoryCategoriesPreview,
            NewsletterStanzas.BuildDirectoryCategoriesPreviewVariables(categories, countryCode, perCategoryLimit),
            ct: ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseDirectoryCategoriesPreview(resp.Data);
    }

    public async Task<NewsletterDirectoryPage> GetDirectoryListAsync(
        string? view = "TRENDING",
        IReadOnlyList<string>? categories = null,
        string? countryCodes = null,
        int? limit = 20,
        string? startCursor = null,
        CancellationToken ct = default)
    {
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchNewsletterDirectoryList,
            NewsletterStanzas.BuildDirectoryListVariables(view, categories, countryCodes, limit, startCursor),
            ct: ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseDirectoryPage(resp.Data, "xwa2_newsletters_directory_list");
    }

    public async Task<NewsletterDirectoryPage> SearchDirectoryAsync(
        string searchText,
        IReadOnlyList<string>? categories = null,
        int? limit = 20,
        string? startCursor = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchText);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchNewsletterDirectorySearchResults,
            NewsletterStanzas.BuildDirectorySearchVariables(searchText, categories, limit, startCursor),
            ct: ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseDirectoryPage(resp.Data, "xwa2_newsletters_directory_search");
    }

    public async Task<NewsletterDirectoryPage> GetRecommendedAsync(
        string? countryCodes = null,
        int? limit = 20,
        CancellationToken ct = default)
    {
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchRecommendedNewsletters,
            NewsletterStanzas.BuildRecommendedNewslettersVariables(countryCodes, limit),
            ct: ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseDirectoryPage(resp.Data, "xwa2_newsletters_recommended");
    }

    public async Task<NewsletterDirectoryPage> GetSimilarAsync(
        Jid channelJid,
        string? countryCodes = null,
        int? limit = 20,
        CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchSimilarNewsletters,
            NewsletterStanzas.BuildSimilarNewslettersVariables(channelJid, countryCodes, limit),
            ct: ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseDirectoryPage(resp.Data, "xwa2_newsletters_similar");
    }

    // ─── Standard xmlns="newsletter" IQs ─────────────────────────────────

    public async Task<IReadOnlyList<NewsletterMessage>> GetMessagesAsync(
        Jid channelJid,
        uint count = 50,
        ulong? beforeServerId = null,
        CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        string iqId = _iqClient.NextRequestId();
        Node iq = NewsletterStanzas.BuildGetMessagesIq(iqId, channelJid, count, beforeServerId);
        Node resp = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseMessagesResponse(resp);
    }

    public async Task<IReadOnlyList<NewsletterMyAddOns>> GetMyAddOnsAsync(
        Jid channelJid,
        uint limit = 50,
        CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        string iqId = _iqClient.NextRequestId();
        Node iq = NewsletterStanzas.BuildGetMyAddOnsIq(iqId, channelJid, limit);
        Node resp = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseMyAddOnsResponse(resp, channelJid);
    }

    public async Task<ulong> SubscribeLiveUpdatesAsync(Jid channelJid, CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        string iqId = _iqClient.NextRequestId();
        Node iq = NewsletterStanzas.BuildSubscribeLiveUpdatesIq(iqId, channelJid);
        Node resp = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        return NewsletterStanzas.ParseLiveUpdatesDurationSeconds(resp);
    }

    // ─── Plaintext Channel Publishing, Edits, Revokes, Reactions & Polls ──

    public Task<SendMessageResult> PublishTextAsync(
        Jid channelJid,
        string text,
        string? messageId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return PublishMessageAsync(channelJid, new Message { Conversation = text }, messageId, ct);
    }

    public Task<SendMessageResult> PublishPollAsync(
        Jid channelJid,
        string question,
        IReadOnlyList<string> options,
        uint selectableOptionsCount = 1,
        string? quizCorrectOption = null,
        string? messageId = null,
        CancellationToken ct = default)
    {
        Message poll = RichMessageBuilders.BuildNewsletterPollCreation(
            question,
            options,
            selectableOptionsCount,
            quizCorrectOption);
        return PublishMessageAsync(channelJid, poll, messageId, ct);
    }

    public Task<SendMessageResult> PublishMessageAsync(
        Jid channelJid,
        Message message,
        string? messageId = null,
        CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        return _messageSender.SendNewsletterMessageAsync(channelJid, message, messageId, ct: ct);
    }

    public async Task<UploadedMediaResult> UploadPlaintextMediaAsync(
        ReadOnlyMemory<byte> plaintext,
        WaMediaType newsletterMediaType,
        CancellationToken ct = default)
    {
        if (newsletterMediaType.IsEncrypted())
        {
            throw new ArgumentException($"Expected an unencrypted newsletter WaMediaType, got '{newsletterMediaType}'.", nameof(newsletterMediaType));
        }
        MediaTransferClient client = _mediaTransfer
            ?? throw new InvalidOperationException("MediaTransferClient requires an IHttpClient configured in ConnectionOrchestratorOptions.");
        return await client.UploadAsync(plaintext, newsletterMediaType, checkResumeIfLarge: true, generateSidecar: false, ct: ct)
            .ConfigureAwait(false);
    }

    public async Task<string> SendReactionAsync(
        Jid channelJid,
        ulong serverId,
        string reactionCode,
        CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        string reqId = _messageSender.NextMessageId();
        Node stanza = MessageStanzas.BuildNewsletterServerReactionStanza(channelJid, reqId, serverId, reactionCode);
        await _socket.SendNodeAsync(stanza, ct).ConfigureAwait(false);
        return reqId;
    }

    public async Task<string> SendPollVoteByOptionNamesAsync(
        Jid channelJid,
        ulong serverId,
        IReadOnlyList<string> optionNames,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(optionNames);
        byte[][] hashes = optionNames.Select(AddonEncryption.HashPollOption).ToArray();
        return await SendPollVoteAsync(channelJid, serverId, hashes, ct).ConfigureAwait(false);
    }

    public async Task<string> SendPollVoteAsync(
        Jid channelJid,
        ulong serverId,
        IReadOnlyList<byte[]> optionHashes32,
        CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        ArgumentNullException.ThrowIfNull(optionHashes32);
        if (optionHashes32.Count > NewsletterStanzas.MaxPollVoteOptions)
        {
            throw new ArgumentException(
                $"A channel poll vote carries at most {NewsletterStanzas.MaxPollVoteOptions} options.",
                nameof(optionHashes32));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (byte[] hash in optionHashes32)
        {
            if (hash is not { Length: 32 })
            {
                throw new ArgumentException("Each poll vote option hash must be exactly 32 bytes.", nameof(optionHashes32));
            }
            if (!seen.Add(Convert.ToHexString(hash)))
            {
                throw new ArgumentException("A channel poll vote names each option at most once.", nameof(optionHashes32));
            }
        }

        string reqId = _messageSender.NextMessageId();
        Node stanza = MessageStanzas.BuildNewsletterPollVoteStanza(channelJid, reqId, serverId, optionHashes32);
        await _socket.SendNodeAsync(stanza, ct).ConfigureAwait(false);
        return reqId;
    }

    public async Task EditMessageAsync(
        Jid channelJid,
        string messageId,
        Message replacementContent,
        CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(replacementContent);

        Node stanza = MessageStanzas.BuildNewsletterEditStanza(channelJid, messageId, replacementContent);
        await _socket.SendNodeAsync(stanza, ct).ConfigureAwait(false);
    }

    public async Task RevokeMessageAsync(
        Jid channelJid,
        string messageId,
        CancellationToken ct = default)
    {
        EnsureNewsletterJid(channelJid);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        Node stanza = MessageStanzas.BuildNewsletterEditStanza(channelJid, messageId, replacementContent: null);
        await _socket.SendNodeAsync(stanza, ct).ConfigureAwait(false);
    }

    private Jid ResolveAdminLidOrThrow(Jid userJid)
    {
        Jid bare = userJid.ToNonAd();
        if (bare.IsLid())
        {
            return bare;
        }
        if (_lidPnCache.TryGetLid(bare.ToString(), out string? lidStr)
            && !string.IsNullOrEmpty(lidStr)
            && Jid.TryParse(lidStr, out Jid lid))
        {
            return lid.ToNonAd();
        }
        throw new InvalidOperationException($"No known LID for newsletter admin '{userJid}'.");
    }

    private static void EnsureNewsletterJid(Jid channelJid)
    {
        if (!channelJid.IsNewsletter())
        {
            throw new ArgumentException($"Expected a @newsletter JID, got '{channelJid}'.", nameof(channelJid));
        }
    }

    private static void RequireNonNullField(JsonElement? data, string fieldName, string errorMessage)
    {
        if (data is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty(fieldName, out JsonElement field)
            || field.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException(errorMessage);
        }
    }

    private long NowUnixSeconds() => _timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private ValueTask EmitAsync(WhatsAppEvent evt) =>
        _emitEvent is not null ? _emitEvent(evt) : ValueTask.CompletedTask;
}
