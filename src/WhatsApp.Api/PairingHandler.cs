using System.Threading.Channels;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Api;

/// <summary>
/// Options for phone-number 8-character pair-code linking (<c>link_code_companion_reg</c>).
/// </summary>
public sealed class PairCodeOptions
{
    public required string PhoneNumber { get; init; }
    public bool ShowPushNotification { get; init; } = true;
    public string? CustomCode { get; init; }
    public CompanionWebClientType? PlatformId { get; init; }
    public string? DisplayOs { get; init; }
}

/// <summary>
/// Configuration for <see cref="PairingHandler"/> QR rotation cadence and client type suffix.
/// </summary>
public sealed class PairingOptions
{
    public static readonly TimeSpan DefaultInitialQrTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultSubsequentQrTimeout = TimeSpan.FromSeconds(20);

    public TimeSpan InitialQrTimeout { get; init; } = DefaultInitialQrTimeout;
    public TimeSpan SubsequentQrTimeout { get; init; } = DefaultSubsequentQrTimeout;
    public CompanionWebClientType ClientType { get; init; } = CompanionWebClientType.Chrome;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// Generation-scoped coordinator for inbound <c>pair-device</c> QR rotation and
/// <c>pair-success</c> ADV cryptographic verification + pre-wire credential persistence.
/// Mirrors <c>whatsapp-rust/src/pair.rs</c>.
/// </summary>
public sealed class PairingHandler : IAsyncDisposable
{
    private readonly Func<Node, CancellationToken, ValueTask> _sendStanzaAsync;
    private readonly Func<Node, CancellationToken, ValueTask<Node>>? _sendIqAsync;
    private readonly Func<string>? _nextIqId;
    private readonly IDeviceStore _deviceStore;
    private readonly DeviceCredentials _credentials;
    private readonly Func<WhatsAppEvent, ValueTask> _emitEventAsync;
    private readonly Func<CancellationToken, ValueTask>? _onQrCodesExhaustedAsync;
    private readonly PairingOptions _options;

    private readonly SemaphoreSlim _pairLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _qrRotationCts;
    private Task? _qrRotationTask;
    private PairCodeFlowState? _pairCodeState;
    private long _fallbackIqSeq;
    private int _disposed;

    private sealed class PairCodeFlowState
    {
        public required string PhoneDigits { get; init; }
        public required string Code { get; init; }
        public required KeyPair EphemeralKeyPair { get; init; }
        public required byte[] PairingRef { get; init; }
        public required DateTimeOffset ExpiresAtUtc { get; init; }
        public int PrimaryHelloCount { get; set; }
        public bool Completed { get; set; }
    }

    public PairingHandler(
        Func<Node, CancellationToken, ValueTask> sendStanzaAsync,
        IDeviceStore deviceStore,
        DeviceCredentials credentials,
        Func<WhatsAppEvent, ValueTask> emitEventAsync,
        Func<CancellationToken, ValueTask>? onQrCodesExhaustedAsync = null,
        PairingOptions? options = null,
        Func<Node, CancellationToken, ValueTask<Node>>? sendIqAsync = null,
        Func<string>? nextIqId = null)
    {
        _sendStanzaAsync = sendStanzaAsync ?? throw new ArgumentNullException(nameof(sendStanzaAsync));
        _sendIqAsync = sendIqAsync;
        _nextIqId = nextIqId;
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _emitEventAsync = emitEventAsync ?? throw new ArgumentNullException(nameof(emitEventAsync));
        _onQrCodesExhaustedAsync = onQrCodesExhaustedAsync;
        _options = options ?? new PairingOptions();
    }

    public PairingHandler(
        NoiseSessionSocket socket,
        IDeviceStore deviceStore,
        DeviceCredentials credentials,
        ChannelWriter<WhatsAppEvent> eventWriter,
        Func<CancellationToken, ValueTask>? onQrCodesExhaustedAsync = null,
        PairingOptions? options = null,
        IqClient? iqClient = null)
        : this(
            (node, ct) => new ValueTask(socket.SendNodeAsync(node, ct)),
            deviceStore,
            credentials,
            (evt) => eventWriter.WriteAsync(evt),
            onQrCodesExhaustedAsync,
            options,
            iqClient is not null ? (iq, ct) => new ValueTask<Node>(iqClient.SendIqAsync(iq, PairCodeCrypto.CompanionFinishIqTimeout, ct)) : null,
            iqClient is not null ? iqClient.NextRequestId : null)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(eventWriter);
    }

    public async Task<string> RequestPairCodeAsync(PairCodeOptions options, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        if (_sendIqAsync is null)
        {
            throw new InvalidOperationException("Pair-code linking requires an IQ sender (IqClient).");
        }

        string phoneDigits = new(options.PhoneNumber.Where(char.IsAsciiDigit).ToArray());
        if (phoneDigits.Length < 5)
        {
            throw new ArgumentException("Phone number must include country code digits.", nameof(options));
        }

        string code;
        if (!string.IsNullOrWhiteSpace(options.CustomCode))
        {
            code = options.CustomCode.Trim().ToUpperInvariant();
            if (!PairCodeCrypto.ValidateCode(code))
            {
                throw new ArgumentException("Custom pair code must be 8 valid Crockford Base32 characters.", nameof(options));
            }
        }
        else
        {
            code = PairCodeCrypto.GenerateCode();
        }

        await _pairLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _options.TimeProvider.GetUtcNow();
            if (_pairCodeState is { Completed: false } existing && now <= existing.ExpiresAtUtc)
            {
                throw new InvalidOperationException("A phone-number pair code flow is already active.");
            }

            KeyPair ephemeral = Curve25519.GenerateKeyPair();
            byte[] wrappedEph = PairCodeCrypto.EncryptEphemeralPublicKey(ephemeral.PublicKey, code);
            var (platformId, display) = PairCodeCrypto.ResolveCompanionPlatform(
                null,
                options.PlatformId,
                options.DisplayOs);

            string iqId = NextRequestId();
            Node helloIq = PairCodeCrypto.BuildCompanionHelloIq(
                iqId,
                phoneDigits,
                _credentials.NoiseKey.PublicKey,
                wrappedEph,
                platformId,
                display,
                options.ShowPushNotification);

            Node resp = await _sendIqAsync(helloIq, ct).ConfigureAwait(false);
            byte[] pairingRef = PairCodeCrypto.ParseCompanionHelloResponse(resp)
                ?? throw new PairingException(500, "internal-server-error", "companion_hello response missing link_code_pairing_ref.");

            _pairCodeState = new PairCodeFlowState
            {
                PhoneDigits = phoneDigits,
                Code = code,
                EphemeralKeyPair = ephemeral,
                PairingRef = pairingRef,
                ExpiresAtUtc = now + PairCodeCrypto.DefaultCodeValidity
            };
        }
        finally
        {
            _pairLock.Release();
        }

        await _emitEventAsync(new PairCodeEvent(code, phoneDigits, PairCodeCrypto.DefaultCodeValidity)).ConfigureAwait(false);
        return code;
    }

    public async ValueTask<bool> HandleNotificationAsync(Node notificationNode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notificationNode);
        if (notificationNode.Tag != "notification" || notificationNode.GetChild("link_code_companion_reg") is null)
        {
            return false;
        }

        string? notifId = notificationNode.GetAttr("id");
        if (!string.IsNullOrEmpty(notifId)
            && notificationNode.GetAttr("from") is { Length: > 0 } fromStr
            && Jid.TryParse(fromStr, out var fromJid))
        {
            await _sendStanzaAsync(AckStanza.Build(notifId, "notification", fromJid, notificationNode.GetAttr("type")), ct)
                .ConfigureAwait(false);
        }

        if (PairCodeCrypto.IsRefreshCodeNotification(notificationNode))
        {
            await _emitEventAsync(new PairCodeRefreshRequestedEvent()).ConfigureAwait(false);
            return true;
        }

        if (!PairCodeCrypto.TryParsePrimaryHelloNotification(notificationNode, out var primaryHello) || primaryHello is null)
        {
            return false;
        }

        await HandlePrimaryHelloAsync(primaryHello, ct).ConfigureAwait(false);
        return true;
    }

    private async Task HandlePrimaryHelloAsync(ParsedPrimaryHello hello, CancellationToken ct)
    {
        if (_sendIqAsync is null)
        {
            return;
        }

        byte[] wrappedBundle;
        string phoneDigits;
        byte[] pairingRef;

        await _pairLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PairCodeFlowState? state = _pairCodeState;
            if (state is null || state.Completed)
            {
                return;
            }

            if (_options.TimeProvider.GetUtcNow() > state.ExpiresAtUtc
                || state.PrimaryHelloCount >= PairCodeCrypto.MaxPrimaryHelloAttempts
                || !state.PairingRef.AsSpan().SequenceEqual(hello.PairingRef))
            {
                return;
            }

            state.PrimaryHelloCount++;
            byte[] primaryEphPub = PairCodeCrypto.DecryptPrimaryEphemeralPublicKey(hello.WrappedPrimaryEphemeralPub, state.Code);
            WrappedKeyBundleResult bundle = PairCodeCrypto.PrepareKeyBundle(
                state.EphemeralKeyPair,
                primaryEphPub,
                hello.PrimaryIdentityPub,
                _credentials.IdentityKey);

            // Pre-wire durability: persist derived AdvSecretKey before sending companion_finish.
            _credentials.AdvSecretKey = bundle.AdvSecretKey;
            await _deviceStore.SaveAsync(_credentials, ct).ConfigureAwait(false);

            wrappedBundle = bundle.WrappedBundle;
            phoneDigits = state.PhoneDigits;
            pairingRef = state.PairingRef;
        }
        finally
        {
            _pairLock.Release();
        }

        Node finishIq = PairCodeCrypto.BuildCompanionFinishIq(
            NextRequestId(),
            phoneDigits,
            wrappedBundle,
            _credentials.IdentityKey.PublicKey,
            pairingRef);

        await _sendIqAsync(finishIq, ct).ConfigureAwait(false);
    }

    private string NextRequestId() =>
        _nextIqId?.Invoke() ?? $"paircode-{Interlocked.Increment(ref _fallbackIqSeq)}";

    /// <summary>
    /// Processes an inbound <c>&lt;iq&gt;</c> node if it carries <c>&lt;pair-device&gt;</c>
    /// or <c>&lt;pair-success&gt;</c>. Returns <c>true</c> if handled.
    /// </summary>
    public async ValueTask<bool> HandleIqAsync(Node iqNode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(iqNode);

        if (iqNode.Tag != "iq" || !string.Equals(iqNode.GetAttr("type"), "set", StringComparison.Ordinal))
        {
            return false;
        }

        if (iqNode.GetChild("pair-device") is not null)
        {
            await HandlePairDeviceAsync(iqNode, ct).ConfigureAwait(false);
            return true;
        }

        if (iqNode.GetChild("pair-success") is not null)
        {
            await HandlePairSuccessAsync(iqNode, ct).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task HandlePairDeviceAsync(Node iqNode, CancellationToken ct)
    {
        if (!PairingStanzas.TryParsePairDeviceRefs(iqNode, out string reqId, out IReadOnlyList<string> refs))
        {
            return;
        }

        CancellationTokenSource? previousCts;
        Task? previousTask;

        await _pairLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposeCts.IsCancellationRequested)
            {
                return;
            }

            (previousCts, previousTask) = DetachQrRotationLocked();

            // Acknowledge receipt of the pairing refs immediately before starting rotation.
            await _sendStanzaAsync(PairingStanzas.BuildResultAck(reqId), ct).ConfigureAwait(false);

            if (refs.Count > 0)
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                _qrRotationCts = cts;
                _qrRotationTask = RunQrRotationLoopAsync(refs, cts);
            }
        }
        finally
        {
            _pairLock.Release();
        }

        await CancelAndAwaitRotationAsync(previousCts, previousTask).ConfigureAwait(false);
    }

    private async Task HandlePairSuccessAsync(Node iqNode, CancellationToken ct)
    {
        CancellationTokenSource? previousCts = null;
        Task? previousTask = null;

        await _pairLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposeCts.IsCancellationRequested)
            {
                return;
            }

            if (!PairingStanzas.TryParsePairSuccess(iqNode, out var parsed, out _))
            {
                string? reqId = iqNode.GetAttr("id");
                if (!string.IsNullOrEmpty(reqId))
                {
                    await _sendStanzaAsync(PairingStanzas.BuildPairError(reqId, 500, "internal-error"), ct)
                        .ConfigureAwait(false);
                }
                await _emitEventAsync(new PairErrorEvent(null, null, 500, "internal-error"))
                    .ConfigureAwait(false);
                return;
            }

            Jid primaryJid = parsed!.DevicePnJid ?? parsed.DeviceLidJid!.Value;

            byte[] selfSignedBytes;
            uint keyIndex;
            try
            {
                (selfSignedBytes, keyIndex) = PairingCrypto.VerifyAndSignDeviceIdentity(
                    _credentials,
                    parsed.DeviceIdentityBytes);
            }
            catch (PairingException pex)
            {
                // Do not stop active QR rotation if a bogus/invalid pair-success fails crypto verification.
                await _sendStanzaAsync(
                        PairingStanzas.BuildPairError(parsed.RequestId, pex.Code, pex.ErrorText),
                        ct)
                    .ConfigureAwait(false);
                await _emitEventAsync(new PairErrorEvent(
                        parsed.DevicePnJid?.ToString(),
                        parsed.DeviceLidJid?.ToString(),
                        pex.Code,
                        pex.ErrorText))
                    .ConfigureAwait(false);
                return;
            }

            // Crypto verified: retire active QR rotation / pair-code flow now.
            (previousCts, previousTask) = DetachQrRotationLocked();
            if (_pairCodeState is not null)
            {
                _pairCodeState.Completed = true;
            }

            Jid? prevPn = _credentials.Pn;
            Jid? prevLid = _credentials.Lid;
            bool prevLidMigrated = _credentials.LidMigrated;
            string prevPushName = _credentials.PushName;
            byte[]? prevAccountIdentity = _credentials.AccountIdentityBytes;

            bool accountChanged = prevPn is not { } existingPn
                || !string.Equals(existingPn.User, primaryJid.User, StringComparison.Ordinal);

            bool reportedMigrated = parsed.ClientProps?.HasIsChatDbLidMigrated == true
                && parsed.ClientProps.IsChatDbLidMigrated;
            bool? lidMigratedUpdate = PairingCrypto.ComputeLidMigratedUpdate(reportedMigrated, accountChanged);

            _credentials.Pn = primaryJid;
            _credentials.Lid = accountChanged
                ? parsed.DeviceLidJid
                : (parsed.DeviceLidJid ?? prevLid);
            _credentials.AccountIdentityBytes = selfSignedBytes;
            if (lidMigratedUpdate.HasValue)
            {
                _credentials.LidMigrated = lidMigratedUpdate.Value;
            }
            if (!string.IsNullOrEmpty(parsed.PushName))
            {
                _credentials.PushName = parsed.PushName;
            }

            try
            {
                // Pre-wire durability: persist updated identity/JIDs BEFORE acknowledging on the wire.
                await _deviceStore.SaveAsync(_credentials, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _credentials.Pn = prevPn;
                _credentials.Lid = prevLid;
                _credentials.LidMigrated = prevLidMigrated;
                _credentials.PushName = prevPushName;
                _credentials.AccountIdentityBytes = prevAccountIdentity;

                await _sendStanzaAsync(PairingStanzas.BuildPairError(parsed.RequestId, 500, "internal-error"), ct)
                    .ConfigureAwait(false);
                await _emitEventAsync(new PairErrorEvent(
                        parsed.DevicePnJid?.ToString(),
                        parsed.DeviceLidJid?.ToString(),
                        500,
                        "internal-error"))
                    .ConfigureAwait(false);
                return;
            }

            Node response = PairingStanzas.BuildPairSuccessResponse(parsed.RequestId, selfSignedBytes, keyIndex);
            await _sendStanzaAsync(response, ct).ConfigureAwait(false);
            await _emitEventAsync(new PairSuccessEvent(
                    primaryJid.ToString(),
                    _credentials.Lid?.ToString()))
                .ConfigureAwait(false);
        }
        finally
        {
            _pairLock.Release();
            await CancelAndAwaitRotationAsync(previousCts, previousTask).ConfigureAwait(false);
        }
    }

    private async Task RunQrRotationLoopAsync(IReadOnlyList<string> refs, CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        try
        {
            for (int i = 0; i < refs.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                TimeSpan timeout = i == 0 ? _options.InitialQrTimeout : _options.SubsequentQrTimeout;
                string qrData = PairingCrypto.MakeQrData(_credentials, refs[i], _options.ClientType);
                await _emitEventAsync(new QrCodeEvent(qrData, timeout)).ConfigureAwait(false);

                await Task.Delay(timeout, _options.TimeProvider, token).ConfigureAwait(false);
            }

            if (!token.IsCancellationRequested)
            {
                await _emitEventAsync(new PairQrExhaustedEvent()).ConfigureAwait(false);
                if (_onQrCodesExhaustedAsync is not null)
                {
                    await _onQrCodesExhaustedAsync(token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when pair-success arrives, new pair-device supersedes refs, or handler disposes.
        }
    }

    private (CancellationTokenSource? Cts, Task? RotationTask) DetachQrRotationLocked()
    {
        var cts = _qrRotationCts;
        var task = _qrRotationTask;
        _qrRotationCts = null;
        _qrRotationTask = null;
        return (cts, task);
    }

    private static async Task CancelAndAwaitRotationAsync(CancellationTokenSource? cts, Task? rotationTask)
    {
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (rotationTask is not null)
        {
            try
            {
                await rotationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancellationTokenSource? activeCts;
        Task? activeTask;

        _disposeCts.Cancel();
        await _pairLock.WaitAsync().ConfigureAwait(false);
        try
        {
            (activeCts, activeTask) = DetachQrRotationLocked();
        }
        finally
        {
            _pairLock.Release();
        }

        await CancelAndAwaitRotationAsync(activeCts, activeTask).ConfigureAwait(false);
        _disposeCts.Dispose();
        _pairLock.Dispose();
    }
}
