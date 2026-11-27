using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Api;

/// <summary>
/// Runtime client for Phase 9C Meta AI / Bots: <c>xmlns="bot"</c> directory discovery,
/// MEX bot profiles / dynamic AI modes / CRL / business auto-reply control,
/// 1:1 &amp; group <c>BotInvokeMessage</c> prompts (persisting 32B <c>messageSecret</c> for
/// <c>msmsg</c> responses), welcome requests, and structured <c>BotFeedbackMessage</c>.
/// </summary>
public sealed class BotClient
{
    private readonly IqClient _iqClient;
    private readonly MexClient _mexClient;
    private readonly MessageSender _messageSender;
    private readonly DeviceCredentials _credentials;
    private readonly Func<WhatsAppEvent, ValueTask> _emitEvent;

    public BotClient(
        IqClient iqClient,
        MexClient mexClient,
        MessageSender messageSender,
        DeviceCredentials credentials,
        Func<WhatsAppEvent, ValueTask> emitEvent)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _mexClient = mexClient ?? throw new ArgumentNullException(nameof(mexClient));
        _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _emitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
    }

    public async Task<BotList> GetBotListAsync(string? bhash = null, CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = BotStanzas.BuildGetBotList(iqId, bhash);
        Node resp = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        BotList list = BotStanzas.ParseGetBotListResponse(resp);
        await _emitEvent(new BotDirectoryLoadedEvent(list)).ConfigureAwait(false);
        return list;
    }

    public async Task<UsyncBotProfileInfo?> GetUsyncBotProfileAsync(
        Jid botJid,
        string? personaId = null,
        CancellationToken ct = default)
    {
        if (!botJid.IsBot())
        {
            throw new ArgumentException($"Expected a bot JID, got '{botJid}'.", nameof(botJid));
        }

        string iqId = _iqClient.NextRequestId();
        string sid = $"bot.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        Node iq = UsyncProtocol.BuildQuery(
            iqId,
            sid,
            UsyncMode.Query,
            UsyncContext.Interactive,
            UsyncProtocolSet.Bot,
            [new UsyncUserSpec { Jid = botJid.ToNonAd(), PersonaId = personaId }]);
        Node resp = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        UsyncResponse parsed = UsyncProtocol.ParseResponse(resp);
        return parsed.Users.FirstOrDefault()?.BotProfile;
    }

    public async Task<IReadOnlyList<BotGqlProfile>> GetBotProfilesAsync(
        IReadOnlyList<string> personaIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(personaIds);
        if (personaIds.Count == 0)
        {
            return Array.Empty<BotGqlProfile>();
        }

        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchBotProfilesGql,
            BotStanzas.BuildFetchBotProfilesVariables(personaIds),
            ct: ct).ConfigureAwait(false);

        return resp.Data is { } data
            ? BotStanzas.ParseBotProfilesElement(data)
            : Array.Empty<BotGqlProfile>();
    }

    public async Task<IReadOnlyList<DynamicAiMode>> GetDynamicAiModesAsync(CancellationToken ct = default)
    {
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchDynamicAiModes,
            new { },
            ct: ct).ConfigureAwait(false);

        return resp.Data is { } data
            ? BotStanzas.ParseDynamicAiModesElement(data)
            : Array.Empty<DynamicAiMode>();
    }

    public async Task<BotCertificateRevocationList?> GetBotCertificateRevocationListAsync(
        string crlName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(crlName);
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.FetchBotCertificateRevocationList,
            BotStanzas.BuildFetchBotCrlVariables(crlName),
            ct: ct).ConfigureAwait(false);

        return resp.Data is { } data
            ? BotStanzas.ParseBotCrlElement(data)
            : null;
    }

    public Task<MexExecutionResponse> SetAiAgentAutoReplyAsync(
        Jid targetJid,
        bool enabled,
        CancellationToken ct = default)
    {
        string? phoneUser = targetJid.IsPn() ? targetJid.User : null;
        string? lidUser = targetJid.IsLid() ? targetJid.User : null;
        if (phoneUser is null && lidUser is null)
        {
            lidUser = targetJid.User;
        }

        return _mexClient.ExecuteAsync(
            MexStanzas.AiAgentAutoReplyControl,
            BotStanzas.BuildAiAgentAutoReplyVariables(phoneUser, lidUser, enabled),
            ct: ct);
    }

    /// <summary>
    /// Invokes a bot in either a 1:1 bot DM or a group chat via <c>@bot</c> mention.
    /// Wraps the prompt in <see cref="RichMessageBuilders.BuildBotInvokeMessage"/> so a 32-byte
    /// <c>MessageSecret</c> is persisted before sending and later <c>&lt;enc type="msmsg"&gt;</c>
    /// replies decrypt automatically.
    /// </summary>
    public Task<SendMessageResult> SendBotPromptAsync(
        Jid targetChatOrBotJid,
        string promptText,
        string? personaId = null,
        Jid? mentionedBotJid = null,
        IReadOnlyList<Jid>? groupParticipants = null,
        byte[]? messageSecret32 = null,
        string? messageId = null,
        bool awaitServerAck = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptText);
        if (!targetChatOrBotJid.IsGroup() && !targetChatOrBotJid.IsBot())
        {
            throw new ArgumentException(
                $"Target must be a bot JID or group JID, got '{targetChatOrBotJid}'.",
                nameof(targetChatOrBotJid));
        }

        string? invokerJid = targetChatOrBotJid.Server == Server.Bot || targetChatOrBotJid.IsGroup()
            ? (_credentials.Lid ?? _credentials.Pn)?.ToNonAdString()
            : (_credentials.Pn ?? _credentials.Lid)?.ToNonAdString();

        if (targetChatOrBotJid.IsGroup())
        {
            if (mentionedBotJid is not { } botJid)
            {
                throw new ArgumentException("Group bot invocation requires mentionedBotJid.", nameof(mentionedBotJid));
            }
            if (groupParticipants is null)
            {
                throw new ArgumentException("Group participant JIDs are required when sending to a @g.us chat.", nameof(groupParticipants));
            }

            Jid bareBot = botJid.ToNonAd();
            bool botIsGroupMember = groupParticipants.Any(p => p.ToNonAd() == bareBot);
            Message msg = botIsGroupMember
                ? RichMessageBuilders.BuildGroupBotPromptMessage(promptText, bareBot, invokerJid, personaId, messageSecret32)
                : RichMessageBuilders.BuildBotInvokeMessage(
                    new Message { Conversation = promptText },
                    invokerJid,
                    personaId,
                    messageSecret32,
                    [bareBot.ToString()]);

            return _messageSender.SendGroupMessageAsync(
                targetChatOrBotJid,
                msg,
                groupParticipants,
                messageId,
                awaitServerAck: awaitServerAck,
                botFanoutTargets: botIsGroupMember ? null : [bareBot],
                botEngagementType: botIsGroupMember ? "member" : "invoked",
                ct: ct);
        }

        Message directMsg = RichMessageBuilders.BuildDirectBotPromptMessage(
            promptText,
            invokerJid,
            personaId,
            messageSecret32);

        return _messageSender.SendRichMessageAsync(targetChatOrBotJid, directMsg, groupParticipants, messageId, awaitServerAck: awaitServerAck, ct: ct);
    }

    public Task<SendMessageResult> SendAiPsiMetadataAsync(
        Jid chatJid,
        byte[] aiPsiMetadata,
        string? invokerJid = null,
        IReadOnlyList<Jid>? groupParticipants = null,
        string? messageId = null,
        CancellationToken ct = default)
    {
        Message msg = RichMessageBuilders.BuildBotAiPsiMetadataMessage(aiPsiMetadata, invokerJid);
        return _messageSender.SendRichMessageAsync(chatJid, msg, groupParticipants, messageId, ct: ct);
    }

    public Task<SendMessageResult> SendAiQueryFanoutAsync(
        Jid chatJid,
        AIQueryFanout fanout,
        string? invokerJid = null,
        IReadOnlyList<Jid>? groupParticipants = null,
        string? messageId = null,
        CancellationToken ct = default)
    {
        Message msg = RichMessageBuilders.BuildBotAiQueryFanoutMessage(fanout, invokerJid);
        return _messageSender.SendRichMessageAsync(chatJid, msg, groupParticipants, messageId, ct: ct);
    }

    public Task<SendMessageResult> RequestWelcomeAsync(
        Jid botJid,
        Message.Types.RequestWelcomeMessageMetadata.Types.WelcomeTrigger? trigger = null,
        Message.Types.RequestWelcomeMessageMetadata.Types.LocalChatState? chatState = null,
        string? messageId = null,
        CancellationToken ct = default)
    {
        if (!botJid.IsBot())
        {
            throw new ArgumentException($"Expected a bot JID, got '{botJid}'.", nameof(botJid));
        }
        Message req = RichMessageBuilders.BuildBotRequestWelcomeMessage(trigger, chatState);
        return _messageSender.SendRichMessageAsync(botJid, req, messageId: messageId, ct: ct);
    }

    public Task<SendMessageResult> SendFeedbackAsync(
        Jid chatJid,
        MessageKey botResponseKey,
        BotFeedbackMessage.Types.BotFeedbackKind kind,
        string? text = null,
        ulong? kindNegativeMask = null,
        ulong? kindPositiveMask = null,
        BotFeedbackMessage.Types.ReportKind reportKind = BotFeedbackMessage.Types.ReportKind.None,
        BotFeedbackMessage.Types.SideBySideSurveyMetadata? surveyMetadata = null,
        IReadOnlyList<Jid>? groupParticipants = null,
        string? messageId = null,
        CancellationToken ct = default)
    {
        Message fb = RichMessageBuilders.BuildBotFeedbackMessage(
            botResponseKey,
            kind,
            text,
            kindNegativeMask,
            kindPositiveMask,
            reportKind,
            surveyMetadata);
        return _messageSender.SendRichMessageAsync(chatJid, fb, groupParticipants, messageId, ct: ct);
    }
}
