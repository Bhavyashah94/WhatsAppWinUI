using System.Threading.Channels;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Api;

public sealed class ConnectionOrchestratorOptions
{
    public static readonly Uri DefaultChatWebSocketUri = new("wss://web.whatsapp.com/ws/chat");

    public IReadOnlyList<Uri> Endpoints { get; init; } = [DefaultChatWebSocketUri];
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DirtyBitOfflineWaitTimeout { get; init; } = TimeSpan.FromSeconds(65);
    public NoiseHandshakeOptions? HandshakeOptions { get; init; }
    public PairingOptions? PairingOptions { get; init; }
    public KeepaliveOptions? KeepaliveOptions { get; init; }
    public OfflineBatchOptions? OfflineBatchOptions { get; init; }
    public IHttpClient? HttpClient { get; init; }
    public bool AutoSendActiveIq { get; init; } = true;
    public bool AutoUploadPreKeys { get; init; } = true;
    public bool AutoSyncAppState { get; init; } = true;
    public bool ForcePreKeyUpload { get; init; }
    public int WantedPreKeyCount { get; init; } = PreKeyUploader.DefaultWantedCount;
    public StanzaHandlers? ExternalHandlers { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// Full-lifecycle connection coordinator orchestrating Noise handshakes (XX / IK / IK-&gt;XXfallback),
/// QR pairing, keepalive watchdog, client-paced offline backlog drain, passive/active cold-boot,
/// pre-key replenishment, dirty-bit cleanup, and generation-fenced reconnects.
/// </summary>
public sealed class ConnectionOrchestrator : IAsyncDisposable
{
    private readonly IWhatsAppBackend _backend;
    private readonly ITransportFactory _transportFactory;
    private readonly ConnectionOrchestratorOptions _options;
    private readonly Channel<WhatsAppEvent> _events = Channel.CreateUnbounded<WhatsAppEvent>();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly LidPnCache _lidPnCache;

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private NoiseSessionSocket? _activeSocket;
    private IqClient? _activeIqClient;
    private PairingHandler? _activePairing;
    private SyncdCoordinator? _activeSyncd;
    private UsyncClient? _activeUsync;
    private ProfilePictureClient? _activeProfilePictures;
    private NotificationRouter? _activeNotifications;
    private PreKeyFetchClient? _activePreKeyFetcher;
    private RetryCoordinator? _activeRetryCoordinator;
    private MessageReceiver? _activeMessageReceiver;
    private MessageSender? _activeMessageSender;
    private PresenceClient? _activePresence;
    private MediaTransferClient? _activeMediaTransfer;
    private HistorySyncCoordinator? _activeHistorySync;
    private PdoHistoryClient? _activePdoHistory;
    private MexClient? _activeMexClient;
    private GroupClient? _activeGroups;
    private GroupHistoryCoordinator? _activeGroupHistory;
    private CommunityClient? _activeCommunities;
    private StatusClient? _activeStatus;
    private PrivacyClient? _activePrivacy;
    private ProfileClient? _activeProfile;
    private AppStateSettingsClient? _activeAppStateSettings;
    private ChatActionsClient? _activeChatActions;
    private ContactClient? _activeContacts;
    private AccountSafetyClient? _activeAccountSafety;
    private MediaReuploadCoordinator? _activeMediaReupload;
    private CommentClient? _activeComments;
    private BroadcastClient? _activeBroadcasts;
    private NewsletterClient? _activeNewsletters;
    private BlockingClient? _activeBlocking;
    private LabelsClient? _activeLabels;
    private QuickRepliesClient? _activeQuickReplies;
    private BusinessClient? _activeBusiness;
    private FlowClient? _activeFlows;
    private BotClient? _activeBots;
    private CallSignalingClient? _activeCallSignaling;
    private CallClient? _activeCalls;
    private long _generation;
    private int _disposed;

    public ConnectionOrchestrator(
        IWhatsAppBackend backend,
        ITransportFactory transportFactory,
        ConnectionOrchestratorOptions? options = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _options = options ?? new ConnectionOrchestratorOptions();
        _lidPnCache = new LidPnCache(_options.TimeProvider);
        if (_options.Endpoints.Count == 0)
        {
            throw new ArgumentException("At least one transport endpoint is required.", nameof(options));
        }
    }

    public ChannelReader<WhatsAppEvent> Events => _events.Reader;
    public long CurrentGeneration => Volatile.Read(ref _generation);
    public NoiseSessionSocket? ActiveSocket => Volatile.Read(ref _activeSocket);
    public IqClient? ActiveIqClient => Volatile.Read(ref _activeIqClient);
    public PairingHandler? ActivePairing => Volatile.Read(ref _activePairing);
    public SyncdCoordinator? ActiveSyncd => Volatile.Read(ref _activeSyncd);
    public ILidPnCache LidPnCache => _lidPnCache;
    public UsyncClient? ActiveUsync => Volatile.Read(ref _activeUsync);
    public ProfilePictureClient? ActiveProfilePictures => Volatile.Read(ref _activeProfilePictures);
    public NotificationRouter? ActiveNotifications => Volatile.Read(ref _activeNotifications);
    public PreKeyFetchClient? ActivePreKeyFetcher => Volatile.Read(ref _activePreKeyFetcher);
    public RetryCoordinator? ActiveRetryCoordinator => Volatile.Read(ref _activeRetryCoordinator);
    public MessageReceiver? ActiveMessageReceiver => Volatile.Read(ref _activeMessageReceiver);
    public MessageSender? ActiveMessageSender => Volatile.Read(ref _activeMessageSender);
    public PresenceClient? ActivePresence => Volatile.Read(ref _activePresence);
    public MediaTransferClient? ActiveMediaTransfer => Volatile.Read(ref _activeMediaTransfer);
    public HistorySyncCoordinator? ActiveHistorySync => Volatile.Read(ref _activeHistorySync);
    public PdoHistoryClient? ActivePdoHistory => Volatile.Read(ref _activePdoHistory);
    public MexClient? ActiveMexClient => Volatile.Read(ref _activeMexClient);
    public GroupClient? ActiveGroups => Volatile.Read(ref _activeGroups);
    public GroupHistoryCoordinator? ActiveGroupHistory => Volatile.Read(ref _activeGroupHistory);
    public CommunityClient? ActiveCommunities => Volatile.Read(ref _activeCommunities);
    public StatusClient? ActiveStatus => Volatile.Read(ref _activeStatus);
    public PrivacyClient? ActivePrivacy => Volatile.Read(ref _activePrivacy);
    public ProfileClient? ActiveProfile => Volatile.Read(ref _activeProfile);
    public AppStateSettingsClient? ActiveAppStateSettings => Volatile.Read(ref _activeAppStateSettings);
    public ChatActionsClient? ActiveChatActions => Volatile.Read(ref _activeChatActions);
    public ContactClient? ActiveContacts => Volatile.Read(ref _activeContacts);
    public AccountSafetyClient? ActiveAccountSafety => Volatile.Read(ref _activeAccountSafety);
    public MediaReuploadCoordinator? ActiveMediaReupload => Volatile.Read(ref _activeMediaReupload);
    public CommentClient? ActiveComments => Volatile.Read(ref _activeComments);
    public BroadcastClient? ActiveBroadcasts => Volatile.Read(ref _activeBroadcasts);
    public NewsletterClient? ActiveNewsletters => Volatile.Read(ref _activeNewsletters);
    public BlockingClient? ActiveBlocking => Volatile.Read(ref _activeBlocking);
    public LabelsClient? ActiveLabels => Volatile.Read(ref _activeLabels);
    public QuickRepliesClient? ActiveQuickReplies => Volatile.Read(ref _activeQuickReplies);
    public BusinessClient? ActiveBusiness => Volatile.Read(ref _activeBusiness);
    public FlowClient? ActiveFlows => Volatile.Read(ref _activeFlows);
    public BotClient? ActiveBots => Volatile.Read(ref _activeBots);
    public CallSignalingClient? ActiveCallSignaling => Volatile.Read(ref _activeCallSignaling);
    public CallClient? ActiveCalls => Volatile.Read(ref _activeCalls);

    /// <summary>
    /// Starts the managed background connect-and-reconnect loop until stopped, logged out/replaced,
    /// or disposed.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_runTask is { IsCompleted: false })
            {
                return;
            }

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            CancellationToken loopToken = _runCts.Token;
            _runTask = Task.Run(() => RunReconnectLoopAsync(loopToken), loopToken);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// Stops the reconnect loop and cleanly closes any active session.
    /// </summary>
    public async Task StopAsync()
    {
        Task? toAwait = null;
        await _runGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _runCts?.Cancel();
            toAwait = _runTask;
        }
        finally
        {
            _runGate.Release();
        }

        if (toAwait is not null)
        {
            try
            {
                await toAwait.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunReconnectLoopAsync(CancellationToken ct)
    {
        TimeSpan delay = _options.InitialReconnectDelay;
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;
            SessionOutcome outcome = await RunSingleSessionAsync(ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested || outcome.StopReconnect)
            {
                break;
            }

            if (outcome.ImmediateReconnect)
            {
                delay = _options.InitialReconnectDelay;
                continue;
            }

            if (outcome.ResetBackoff)
            {
                delay = _options.InitialReconnectDelay;
            }

            TimeSpan sleepDuration = outcome.PenaltyDelay ?? delay;
            await EmitEventAsync(new DisconnectedEvent(outcome.Reason, WillReconnect: true)).ConfigureAwait(false);

            try
            {
                await Task.Delay(sleepDuration, _options.TimeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (outcome.PenaltyDelay is null)
            {
                long nextMs = Math.Min((long)_options.MaxReconnectDelay.TotalMilliseconds, (long)delay.TotalMilliseconds * 2);
                delay = TimeSpan.FromMilliseconds(Math.Max(1, nextMs));
            }
        }
    }

    internal sealed record SessionOutcome(
        string Reason,
        bool StopReconnect,
        bool ImmediateReconnect = false,
        bool ResetBackoff = false,
        TimeSpan? PenaltyDelay = null);

    /// <summary>
    /// Executes a single transport + Noise handshake + stanza routing session until it ends cleanly
    /// or fails, returning instructions for the outer reconnect supervisor.
    /// </summary>
    internal async Task<SessionOutcome> RunSingleSessionAsync(CancellationToken ct)
    {
        long gen = Interlocked.Increment(ref _generation);
        ITransport? transport = null;
        NoiseSessionSocket? socket = null;
        StanzaRouter? router = null;
        PairingHandler? pairing = null;
        SyncdCoordinator? syncd = null;
        NotificationRouter? notifications = null;
        KeepaliveWatchdog? watchdog = null;
        OfflineBatchCoordinator? offlineBatch = null;
        CallClient? callClient = null;

        var sessionStopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var routerClosedTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        int loggedSuccess = 0;
        int outcomeSet = 0;
        SessionOutcome? explicitOutcome = null;

        void SignalDisconnect(SessionOutcome outcome)
        {
            if (Interlocked.CompareExchange(ref outcomeSet, 1, 0) == 0)
            {
                explicitOutcome = outcome;
                sessionStopCts.Cancel();
            }
        }

        try
        {
            IReadOnlyList<LidPnMappingRecord> persistedMappings = await _backend.Protocol.LoadLidPnMappingsAsync(ct).ConfigureAwait(false);
            foreach (LidPnMappingRecord mapping in persistedMappings)
            {
                _lidPnCache.Learn(mapping.PnJid, mapping.LidJid, LidPnLearningSource.HistorySync);
            }

            DeviceCredentials credentials = await _backend.Device.LoadOrCreateAsync(ct).ConfigureAwait(false);
            transport = await _transportFactory.ConnectAsync(_options.Endpoints, ct).ConfigureAwait(false);

            NoiseHandshakeResult hs = await NoiseHandshakeRunner.RunAsync(
                transport,
                credentials,
                _options.HandshakeOptions,
                ct).ConfigureAwait(false);

            if (hs.ServerStaticPublic is { Length: 32 } serverPub
                && (credentials.ServerStaticPublic is null
                    || !credentials.ServerStaticPublic.SequenceEqual(serverPub)))
            {
                credentials.ServerStaticPublic = serverPub;
                await _backend.Device.SaveAsync(credentials, ct).ConfigureAwait(false);
            }

            socket = new NoiseSessionSocket(gen, transport, hs.Keys, hs.RemainingWireBytes);
            var iqClient = new IqClient(socket);
            Volatile.Write(ref _activeSocket, socket);
            Volatile.Write(ref _activeIqClient, iqClient);

            offlineBatch = new OfflineBatchCoordinator(
                gen,
                (node, token) => new ValueTask(socket.SendNodeAsync(node, token)),
                EmitEventAsync,
                _options.OfflineBatchOptions);

            pairing = new PairingHandler(
                socket,
                _backend.Device,
                credentials,
                _events.Writer,
                onQrCodesExhaustedAsync: _ =>
                {
                    SignalDisconnect(new SessionOutcome("QR codes exhausted", StopReconnect: false));
                    return ValueTask.CompletedTask;
                },
                options: _options.PairingOptions,
                iqClient: iqClient);
            Volatile.Write(ref _activePairing, pairing);

            if (_options.HttpClient is { } http)
            {
                syncd = new SyncdCoordinator(_backend, http, iqClient, EmitEventAsync);
                Volatile.Write(ref _activeSyncd, syncd);
            }

            var mediaTransfer = _options.HttpClient is { } mediaHttp
                ? new MediaTransferClient(iqClient, mediaHttp, _options.TimeProvider)
                : null;
            var historySync = new HistorySyncCoordinator(_backend, _lidPnCache, mediaTransfer, EmitEventAsync);
            var usync = new UsyncClient(iqClient, _backend, _lidPnCache, _options.TimeProvider);
            var preKeyFetcher = new PreKeyFetchClient(iqClient, _backend, _lidPnCache, credentials);
            var retryCoordinator = new RetryCoordinator(socket, _backend, preKeyFetcher, _lidPnCache, credentials, EmitEventAsync);
            Func<CancellationToken, Task>? onAppStateKeysSaved = syncd is not null && _options.AutoSyncAppState
                ? _token =>
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await syncd.SyncCollectionsAsync(
                                [
                                    AppStateCollections.CriticalBlock,
                                    AppStateCollections.CriticalUnblockLow,
                                    AppStateCollections.RegularHigh,
                                    AppStateCollections.Regular,
                                    AppStateCollections.RegularLow
                                ],
                                ct: sessionStopCts.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Syncd retries on next account_sync dirty bit or reconnect.
                        }
                    });
                    return Task.CompletedTask;
                }
                : null;
            var messageReceiver = new MessageReceiver(
                socket,
                _backend,
                _lidPnCache,
                credentials,
                retryCoordinator,
                EmitEventAsync,
                _options.TimeProvider,
                historySync,
                mediaTransfer,
                onAppStateKeysSaved);
            var messageSender = new MessageSender(socket, usync, preKeyFetcher, _backend, _lidPnCache, credentials, retryCoordinator, _options.TimeProvider, _options.HttpClient);
            var presenceClient = new PresenceClient(socket, _backend, credentials, EmitEventAsync);
            var pdoHistory = new PdoHistoryClient(_backend, messageSender, credentials);
            var mexClient = new MexClient(iqClient);
            var groupClient = new GroupClient(iqClient, mexClient, messageSender, _backend, _lidPnCache, emitEvent: EmitEventAsync, timeProvider: _options.TimeProvider);
            var groupHistory = new GroupHistoryCoordinator(iqClient, messageSender, mediaTransfer, credentials, _options.TimeProvider);
            var communities = new CommunityClient(iqClient, mexClient, groupClient, _backend, _lidPnCache);
            var statusClient = new StatusClient(socket, usync, preKeyFetcher, messageSender, _backend, _lidPnCache, credentials, retryCoordinator, _options.TimeProvider);
            var privacyClient = new PrivacyClient(iqClient);
            var profileClient = new ProfileClient(socket, mexClient, _backend, credentials, syncd, _options.TimeProvider);
            var accountSafetyClient = new AccountSafetyClient(iqClient, _backend, credentials, EmitEventAsync, _options.TimeProvider);
            var mediaReupload = new MediaReuploadCoordinator(socket, _backend, _lidPnCache, credentials, EmitEventAsync);
            var commentClient = new CommentClient(messageSender, _backend, credentials, _options.TimeProvider);
            var broadcastClient = new BroadcastClient(iqClient, socket, usync, preKeyFetcher, messageSender, _backend, _lidPnCache, credentials, retryCoordinator);
            var newsletterClient = new NewsletterClient(iqClient, mexClient, socket, messageSender, _lidPnCache, mediaTransfer, EmitEventAsync, _options.TimeProvider);
            var blockingClient = new BlockingClient(iqClient, _lidPnCache, EmitEventAsync, _options.TimeProvider);
            var businessClient = new BusinessClient(iqClient, mexClient, usync, credentials, _options.HttpClient);
            var flowClient = new FlowClient(mexClient, _lidPnCache, _options.HttpClient);
            var botClient = new BotClient(iqClient, mexClient, messageSender, credentials, EmitEventAsync);
            var callSignalingClient = new CallSignalingClient(socket, credentials, EmitEventAsync, syncd, _options.TimeProvider);
            callClient = new CallClient(callSignalingClient, usync, preKeyFetcher, _backend, _lidPnCache, credentials, EmitEventAsync, timeProvider: _options.TimeProvider);
            LabelsClient? labelsClient = syncd is not null ? new LabelsClient(syncd, _options.TimeProvider) : null;
            QuickRepliesClient? quickRepliesClient = syncd is not null ? new QuickRepliesClient(syncd, _options.TimeProvider) : null;
            AppStateSettingsClient? appStateSettingsClient = syncd is not null ? new AppStateSettingsClient(syncd, _options.TimeProvider) : null;
            ChatActionsClient? chatActionsClient = syncd is not null ? new ChatActionsClient(syncd, messageSender, _options.TimeProvider) : null;
            ContactClient? contactClient = syncd is not null ? new ContactClient(syncd, _lidPnCache, _options.TimeProvider) : null;

            Volatile.Write(ref _activeUsync, usync);
            Volatile.Write(ref _activeProfilePictures, new ProfilePictureClient(iqClient, _backend, _options.HttpClient));
            Volatile.Write(ref _activePreKeyFetcher, preKeyFetcher);
            Volatile.Write(ref _activeRetryCoordinator, retryCoordinator);
            Volatile.Write(ref _activeMessageReceiver, messageReceiver);
            Volatile.Write(ref _activeMessageSender, messageSender);
            Volatile.Write(ref _activePresence, presenceClient);
            Volatile.Write(ref _activeMediaTransfer, mediaTransfer);
            Volatile.Write(ref _activeHistorySync, historySync);
            Volatile.Write(ref _activePdoHistory, pdoHistory);
            Volatile.Write(ref _activeMexClient, mexClient);
            Volatile.Write(ref _activeGroups, groupClient);
            Volatile.Write(ref _activeGroupHistory, groupHistory);
            Volatile.Write(ref _activeCommunities, communities);
            Volatile.Write(ref _activeStatus, statusClient);
            Volatile.Write(ref _activePrivacy, privacyClient);
            Volatile.Write(ref _activeProfile, profileClient);
            Volatile.Write(ref _activeAppStateSettings, appStateSettingsClient);
            Volatile.Write(ref _activeChatActions, chatActionsClient);
            Volatile.Write(ref _activeContacts, contactClient);
            Volatile.Write(ref _activeAccountSafety, accountSafetyClient);
            Volatile.Write(ref _activeMediaReupload, mediaReupload);
            Volatile.Write(ref _activeComments, commentClient);
            Volatile.Write(ref _activeBroadcasts, broadcastClient);
            Volatile.Write(ref _activeNewsletters, newsletterClient);
            Volatile.Write(ref _activeBlocking, blockingClient);
            Volatile.Write(ref _activeBusiness, businessClient);
            Volatile.Write(ref _activeFlows, flowClient);
            Volatile.Write(ref _activeBots, botClient);
            Volatile.Write(ref _activeCallSignaling, callSignalingClient);
            Volatile.Write(ref _activeCalls, callClient);
            Volatile.Write(ref _activeLabels, labelsClient);
            Volatile.Write(ref _activeQuickReplies, quickRepliesClient);
            notifications = new NotificationRouter(
                _backend,
                _lidPnCache,
                credentials,
                socket,
                iqClient,
                syncd,
                EmitEventAsync,
                _options.TimeProvider,
                messageReceiver,
                groupClient,
                mediaReupload,
                businessClient);
            Volatile.Write(ref _activeNotifications, notifications);

            KeepaliveOptions keepaliveOpts = _options.KeepaliveOptions ?? new KeepaliveOptions
            {
                TimeProvider = _options.TimeProvider
            };
            watchdog = new KeepaliveWatchdog(
                gen,
                iqClient,
                reconnectGen =>
                {
                    if (reconnectGen == gen)
                    {
                        SignalDisconnect(new SessionOutcome("Keepalive dead socket / timeout", StopReconnect: false));
                    }
                    return Task.CompletedTask;
                },
                () => socket.IsConnected && !sessionStopCts.IsCancellationRequested,
                keepaliveOpts);

            var handlers = new StanzaHandlers
            {
                OnIqRequest = async (iq, token) =>
                {
                    watchdog.RecordDataReceived();
                    if (PingIq.IsPingRequest(iq))
                    {
                        watchdog.RecordDataSent();
                        await socket.SendNodeAsync(PingIq.BuildPong(iq), token).ConfigureAwait(false);
                        return;
                    }
                    if (await pairing.HandleIqAsync(iq, token).ConfigureAwait(false))
                    {
                        return;
                    }
                    if (_options.ExternalHandlers?.OnIqRequest is { } ext)
                    {
                        await ext(iq, token).ConfigureAwait(false);
                    }
                },

                OnIb = async (ib, token) =>
                {
                    watchdog.RecordDataReceived();
                    InfoBulletinPayload bulletin = InfoBulletinStanza.Parse(ib);

                    if (bulletin.EdgeRoutingInfo is { Length: > 0 } routing)
                    {
                        credentials.EdgeRoutingInfo = routing;
                        await _backend.Device.SaveAsync(credentials, token).ConfigureAwait(false);
                    }

                    if (bulletin.OfflinePreview is { } preview)
                    {
                        await offlineBatch.OnOfflinePreviewAsync(preview, token).ConfigureAwait(false);
                    }

                    if (bulletin.OfflineCompletedCount is int offlineCount)
                    {
                        await offlineBatch.OnOfflineCompletedAsync(offlineCount).ConfigureAwait(false);
                    }

                    foreach (DirtyBit bit in bulletin.DirtyBits)
                    {
                        await EmitEventAsync(new DirtyStateEvent(bit.Type, bit.Timestamp)).ConfigureAwait(false);
                        _ = Task.Run(() => CleanDirtyBitAsync(
                            bit,
                            iqClient,
                            offlineBatch,
                            syncd,
                            gen,
                            sessionStopCts.Token));
                    }

                    if (_options.ExternalHandlers?.OnIb is { } ext)
                    {
                        await ext(ib, token).ConfigureAwait(false);
                    }
                },

                OnSuccess = async (successNode, token) =>
                {
                    watchdog.RecordDataReceived();
                    if (Interlocked.Exchange(ref loggedSuccess, 1) != 0)
                    {
                        return;
                    }

                    if (successNode.GetAttr("lid") is { Length: > 0 } lidAttr
                        && Jid.TryParse(lidAttr, out var parsedLid)
                        && credentials.Lid != parsedLid)
                    {
                        credentials.Lid = parsedLid;
                        await _backend.Device.SaveAsync(credentials, token).ConfigureAwait(false);
                    }

                    await EmitEventAsync(new ConnectedEvent(
                        credentials.Pn?.ToString() ?? string.Empty,
                        credentials.Lid?.ToString())).ConfigureAwait(false);

                    if (_options.AutoSendActiveIq || _options.AutoUploadPreKeys)
                    {
                        _ = Task.Run(() => RunPostLoginInitializationAsync(
                            iqClient,
                            credentials,
                            gen,
                            sessionStopCts.Token));
                    }

                    if (_options.ExternalHandlers?.OnSuccess is { } ext)
                    {
                        await ext(successNode, token).ConfigureAwait(false);
                    }
                },

                OnFailure = async (failureNode, token) =>
                {
                    watchdog.RecordDataReceived();
                    ConnectFailureInfo failure = ConnectFailureStanza.Parse(failureNode);

                    if (failure.IsLoggedOut)
                    {
                        await _backend.Device.ClearPairingAsync(CancellationToken.None).ConfigureAwait(false);
                        await EmitEventAsync(new LoggedOutEvent(
                            OnConnect: true,
                            failure.RawReasonCode,
                            failure.LogoutMessageHeader,
                            failure.LogoutMessageSubtext)).ConfigureAwait(false);
                        await EmitEventAsync(new DisconnectedEvent($"Logged out ({failure.Reason})", WillReconnect: false)).ConfigureAwait(false);
                        SignalDisconnect(new SessionOutcome($"ConnectFailure:{failure.Reason}", StopReconnect: true));
                    }
                    else if (failure.Reason == ConnectFailureReason.TempBanned)
                    {
                        TimeSpan? expire = failure.ExpireSeconds.HasValue
                            ? TimeSpan.FromSeconds(failure.ExpireSeconds.Value)
                            : null;
                        await EmitEventAsync(new TemporaryBanEvent(failure.Code, expire, failure.Message, failure.Url)).ConfigureAwait(false);
                        await EmitEventAsync(new DisconnectedEvent("TempBanned", WillReconnect: false)).ConfigureAwait(false);
                        SignalDisconnect(new SessionOutcome("TempBanned", StopReconnect: true));
                    }
                    else if (failure.Reason == ConnectFailureReason.ClientOutdated)
                    {
                        await EmitEventAsync(new ClientOutdatedEvent(failure.Message)).ConfigureAwait(false);
                        await EmitEventAsync(new DisconnectedEvent("ClientOutdated", WillReconnect: false)).ConfigureAwait(false);
                        SignalDisconnect(new SessionOutcome("ClientOutdated", StopReconnect: true));
                    }
                    else if (failure.ShouldReconnect)
                    {
                        await EmitEventAsync(new ConnectFailureEvent(failure.RawReasonCode, failure.Message)).ConfigureAwait(false);
                        SignalDisconnect(new SessionOutcome($"TransientConnectFailure:{failure.Reason}", StopReconnect: false));
                    }
                    else
                    {
                        await EmitEventAsync(new ConnectFailureEvent(failure.RawReasonCode, failure.Message)).ConfigureAwait(false);
                        await EmitEventAsync(new DisconnectedEvent($"ConnectFailure:{failure.Reason}", WillReconnect: false)).ConfigureAwait(false);
                        SignalDisconnect(new SessionOutcome($"ConnectFailure:{failure.Reason}", StopReconnect: true));
                    }

                    if (_options.ExternalHandlers?.OnFailure is { } ext)
                    {
                        await ext(failureNode, token).ConfigureAwait(false);
                    }
                },

                OnStreamError = async (errorNode, token) =>
                {
                    watchdog.RecordDataReceived();
                    StreamErrorInfo info = StreamErrorStanza.Parse(errorNode);

                    switch (info.Kind)
                    {
                        case StreamErrorKind.ConflictReplaced:
                        case StreamErrorKind.Code when info.Code == "409":
                            await EmitEventAsync(new StreamReplacedEvent()).ConfigureAwait(false);
                            await EmitEventAsync(new DisconnectedEvent("StreamReplaced", WillReconnect: false)).ConfigureAwait(false);
                            SignalDisconnect(new SessionOutcome("StreamReplaced", StopReconnect: true));
                            break;

                        case StreamErrorKind.ConflictDeviceRemoved:
                        case StreamErrorKind.Code when info.Code is "516" or "401":
                            await _backend.Device.ClearPairingAsync(CancellationToken.None).ConfigureAwait(false);
                            int reasonCode = int.TryParse(info.Code, out int c) ? c : 401;
                            await EmitEventAsync(new LoggedOutEvent(OnConnect: false, reasonCode)).ConfigureAwait(false);
                            await EmitEventAsync(new DisconnectedEvent($"LoggedOut:{info.Code ?? info.ConflictType}", WillReconnect: false)).ConfigureAwait(false);
                            SignalDisconnect(new SessionOutcome($"LoggedOut:{info.Code ?? info.ConflictType}", StopReconnect: true));
                            break;

                        case StreamErrorKind.Code when info.Code == "515":
                            await EmitEventAsync(new StreamErrorEvent("515", "stream-restart")).ConfigureAwait(false);
                            SignalDisconnect(new SessionOutcome("StreamRestart515", StopReconnect: false, ImmediateReconnect: true));
                            break;

                        case StreamErrorKind.Code when info.Code == "429":
                            await EmitEventAsync(new StreamErrorEvent("429", "rate-limited")).ConfigureAwait(false);
                            SignalDisconnect(new SessionOutcome("RateLimited429", StopReconnect: false, PenaltyDelay: TimeSpan.FromSeconds(15)));
                            break;

                        case StreamErrorKind.Code when info.Code == "503":
                            await EmitEventAsync(new StreamErrorEvent("503", "service-unavailable")).ConfigureAwait(false);
                            SignalDisconnect(new SessionOutcome("ServiceUnavailable503", StopReconnect: false));
                            break;

                        default:
                            await EmitEventAsync(new StreamErrorEvent(info.Code, info.Kind.ToString())).ConfigureAwait(false);
                            break;
                    }

                    if (_options.ExternalHandlers?.OnStreamError is { } ext)
                    {
                        await ext(errorNode, token).ConfigureAwait(false);
                    }
                },

                OnAck = (node, token) => DispatchTrafficNodeAsync(
                    node,
                    token,
                    watchdog,
                    offlineBatch,
                    async (n, t) =>
                    {
                        messageSender.TryResolveMessageAck(n);
                        mediaReupload.NotifyReceiptAck(n);
                        callSignalingClient.TryResolveCallAck(n);
                        if (_options.ExternalHandlers?.OnAck is { } extAck)
                        {
                            await extAck(n, t).ConfigureAwait(false);
                        }
                    }),
                OnReceipt = (node, token) => DispatchTrafficNodeAsync(
                    node,
                    token,
                    watchdog,
                    offlineBatch,
                    async (n, t) =>
                    {
                        await messageReceiver.HandleReceiptStanzaAsync(n, t).ConfigureAwait(false);
                        if (_options.ExternalHandlers?.OnReceipt is { } extReceipt)
                        {
                            await extReceipt(n, t).ConfigureAwait(false);
                        }
                    }),
                OnNotification = async (node, token) =>
                {
                    watchdog.RecordDataReceived();
                    using var lease = offlineBatch.TrackOfflineStanza(node);

                    if (await pairing.HandleNotificationAsync(node, token).ConfigureAwait(false))
                    {
                        return;
                    }

                    if (notifications is not null)
                    {
                        await notifications.HandleAsync(
                            node,
                            _options.AutoSyncAppState,
                            sendAck: true,
                            sessionStopCts.Token,
                            token).ConfigureAwait(false);
                    }

                    if (_options.ExternalHandlers?.OnNotification is { } extNotif)
                    {
                        await extNotif(node, token).ConfigureAwait(false);
                    }
                },
                OnMessage = (node, token) => DispatchTrafficNodeAsync(
                    node,
                    token,
                    watchdog,
                    offlineBatch,
                    async (n, t) =>
                    {
                        await messageReceiver.HandleMessageStanzaAsync(n, t).ConfigureAwait(false);
                        if (_options.ExternalHandlers?.OnMessage is { } extMsg)
                        {
                            await extMsg(n, t).ConfigureAwait(false);
                        }
                    }),
                OnPresence = (node, token) => DispatchTrafficNodeAsync(
                    node,
                    token,
                    watchdog,
                    offlineBatch,
                    async (n, t) =>
                    {
                        await presenceClient.HandlePresenceStanzaAsync(n, t).ConfigureAwait(false);
                        if (_options.ExternalHandlers?.OnPresence is { } extPres)
                        {
                            await extPres(n, t).ConfigureAwait(false);
                        }
                    }),
                OnChatState = (node, token) => DispatchTrafficNodeAsync(
                    node,
                    token,
                    watchdog,
                    offlineBatch,
                    async (n, t) =>
                    {
                        await presenceClient.HandleChatStateStanzaAsync(n, t).ConfigureAwait(false);
                        if (_options.ExternalHandlers?.OnChatState is { } extChat)
                        {
                            await extChat(n, t).ConfigureAwait(false);
                        }
                    }),
                OnCall = (node, token) => DispatchTrafficNodeAsync(
                    node,
                    token,
                    watchdog,
                    offlineBatch,
                    async (n, t) =>
                    {
                        IncomingCallStanza? parsed = await callSignalingClient.HandleCallStanzaAsync(n, t).ConfigureAwait(false);
                        if (parsed is not null)
                        {
                            await callClient.ProcessIncomingCallStanzaAsync(parsed, n, t).ConfigureAwait(false);
                        }
                        if (_options.ExternalHandlers?.OnCall is { } extCall)
                        {
                            await extCall(n, t).ConfigureAwait(false);
                        }
                    }),
                OnUnhandled = (node, token) => DispatchTrafficNodeAsync(node, token, watchdog, offlineBatch, _options.ExternalHandlers?.OnUnhandled),

                OnClosed = (_, fault) => routerClosedTcs.TrySetResult(fault)
            };

            router = new StanzaRouter(socket, iqClient, handlers);

            // Wait until either the session is signaled to stop (515/401/keepalive/stop) or the socket closes.
            Task stopWaiter = WaitUntilCanceledAsync(sessionStopCts.Token);
            await Task.WhenAny(routerClosedTcs.Task, stopWaiter).ConfigureAwait(false);

            return explicitOutcome ?? new SessionOutcome(
                routerClosedTcs.Task.IsCompleted && routerClosedTcs.Task.Result is { } ex
                    ? $"Transport closed: {ex.Message}"
                    : "Transport closed",
                StopReconnect: ct.IsCancellationRequested,
                ResetBackoff: Volatile.Read(ref loggedSuccess) == 1);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new SessionOutcome("Cancelled", StopReconnect: true);
        }
        catch (Exception ex)
        {
            return new SessionOutcome($"Session exception: {ex.Message}", StopReconnect: false);
        }
        finally
        {
            Volatile.Write(ref _activeSocket, null);
            Volatile.Write(ref _activeIqClient, null);
            Volatile.Write(ref _activePairing, null);
            Volatile.Write(ref _activeSyncd, null);
            Volatile.Write(ref _activeUsync, null);
            Volatile.Write(ref _activeProfilePictures, null);
            Volatile.Write(ref _activeNotifications, null);
            Volatile.Write(ref _activePreKeyFetcher, null);
            Volatile.Write(ref _activeRetryCoordinator, null);
            Volatile.Write(ref _activeMessageReceiver, null);
            Volatile.Write(ref _activeMessageSender, null);
            Volatile.Write(ref _activePresence, null);
            Volatile.Write(ref _activeMediaTransfer, null);
            Volatile.Write(ref _activeHistorySync, null);
            Volatile.Write(ref _activePdoHistory, null);
            Volatile.Write(ref _activeMexClient, null);
            Volatile.Write(ref _activeGroups, null);
            Volatile.Write(ref _activeGroupHistory, null);
            Volatile.Write(ref _activeCommunities, null);
            Volatile.Write(ref _activeStatus, null);
            Volatile.Write(ref _activePrivacy, null);
            Volatile.Write(ref _activeProfile, null);
            Volatile.Write(ref _activeAppStateSettings, null);
            Volatile.Write(ref _activeChatActions, null);
            Volatile.Write(ref _activeContacts, null);
            Volatile.Write(ref _activeAccountSafety, null);
            Volatile.Write(ref _activeMediaReupload, null);
            Volatile.Write(ref _activeComments, null);
            Volatile.Write(ref _activeBroadcasts, null);
            Volatile.Write(ref _activeNewsletters, null);
            Volatile.Write(ref _activeBlocking, null);
            Volatile.Write(ref _activeBusiness, null);
            Volatile.Write(ref _activeFlows, null);
            Volatile.Write(ref _activeBots, null);
            Volatile.Write(ref _activeCallSignaling, null);
            Volatile.Write(ref _activeCalls, null);
            Volatile.Write(ref _activeLabels, null);
            Volatile.Write(ref _activeQuickReplies, null);
            sessionStopCts.Cancel();

            if (watchdog is not null) await watchdog.DisposeAsync().ConfigureAwait(false);
            if (callClient is not null) await callClient.DisposeAsync().ConfigureAwait(false);
            if (router is not null) await router.DisposeAsync().ConfigureAwait(false);
            if (pairing is not null) await pairing.DisposeAsync().ConfigureAwait(false);
            if (syncd is not null) await syncd.DisposeAsync().ConfigureAwait(false);
            if (offlineBatch is not null) await offlineBatch.DisposeAsync().ConfigureAwait(false);
            if (socket is not null) await socket.DisposeAsync().ConfigureAwait(false);
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);

            sessionStopCts.Dispose();
        }
    }

    private async Task RunPostLoginInitializationAsync(
        IqClient iqClient,
        DeviceCredentials credentials,
        long expectedGeneration,
        CancellationToken sessionToken)
    {
        if (sessionToken.IsCancellationRequested || Volatile.Read(ref _generation) != expectedGeneration)
        {
            return;
        }

        if (_options.AutoSendActiveIq)
        {
            try
            {
                string activeId = iqClient.NextRequestId();
                await iqClient.SendIqAsync(
                    PassiveIq.BuildActive(activeId),
                    IqClient.DefaultTimeout,
                    sessionToken).ConfigureAwait(false);
            }
            catch
            {
                // Active IQ failure surfaces via keepalive/disconnect.
            }
        }

        if (_options.AutoUploadPreKeys && !sessionToken.IsCancellationRequested && Volatile.Read(ref _generation) == expectedGeneration)
        {
            try
            {
                await PreKeyUploader.EnsureUploadedAsync(
                    iqClient,
                    _backend,
                    credentials,
                    force: _options.ForcePreKeyUpload,
                    wantedCount: _options.WantedPreKeyCount,
                    ct: sessionToken).ConfigureAwait(false);
            }
            catch
            {
                // Pre-key upload retries on next login/session.
            }
        }
    }

    private async Task CleanDirtyBitAsync(
        DirtyBit bit,
        IqClient iqClient,
        OfflineBatchCoordinator offlineBatch,
        SyncdCoordinator? syncd,
        long expectedGeneration,
        CancellationToken sessionToken)
    {
        try
        {
            bool needsOfflineWait = bit.Type is DirtyBitsIq.TypeGroups or DirtyBitsIq.TypeNewsletterMetadata;
            if (needsOfflineWait && offlineBatch.IsArmed && !offlineBatch.IsCompleted)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                waitCts.CancelAfter(_options.DirtyBitOfflineWaitTimeout);
                await offlineBatch.Completion.WaitAsync(waitCts.Token).ConfigureAwait(false);
            }

            if (sessionToken.IsCancellationRequested || Volatile.Read(ref _generation) != expectedGeneration)
            {
                return;
            }

            if (string.Equals(bit.Type, DirtyBitsIq.TypeAccountSync, StringComparison.Ordinal)
                && syncd is not null
                && _options.AutoSyncAppState)
            {
                await syncd.SyncCollectionsAsync(
                    [
                        AppStateCollections.CriticalBlock,
                        AppStateCollections.CriticalUnblockLow,
                        AppStateCollections.RegularHigh,
                        AppStateCollections.Regular,
                        AppStateCollections.RegularLow
                    ],
                    ct: sessionToken).ConfigureAwait(false);
            }

            string id = iqClient.NextRequestId();
            await iqClient.SendIqAsync(
                DirtyBitsIq.BuildClean(id, bit),
                IqClient.DefaultTimeout,
                sessionToken).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal: server will re-announce dirty state on next login if not cleared.
        }
    }

    private static async ValueTask DispatchTrafficNodeAsync(
        Node node,
        CancellationToken ct,
        KeepaliveWatchdog watchdog,
        OfflineBatchCoordinator offlineBatch,
        Func<Node, CancellationToken, ValueTask>? externalHandler)
    {
        watchdog.RecordDataReceived();
        using var lease = offlineBatch.TrackOfflineStanza(node);
        if (externalHandler is not null)
        {
            await externalHandler(node, ct).ConfigureAwait(false);
        }
    }

    private ValueTask EmitEventAsync(WhatsAppEvent evt)
    {
        if (evt is RelayAllCallsUpdatedEvent rac)
        {
            Volatile.Read(ref _activeCallSignaling)?.ApplyRelayAllCallsSetting(rac.Enabled);
        }
        return _events.Writer.WriteAsync(evt);
    }

    private static async Task WaitUntilCanceledAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = token.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), tcs);
        await tcs.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _runCts?.Dispose();
        _runGate.Dispose();
        _events.Writer.TryComplete();
    }
}
