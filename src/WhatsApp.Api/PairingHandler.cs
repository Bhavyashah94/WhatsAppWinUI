using System.Threading.Channels;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Api;

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
    private readonly IDeviceStore _deviceStore;
    private readonly DeviceCredentials _credentials;
    private readonly Func<WhatsAppEvent, ValueTask> _emitEventAsync;
    private readonly Func<CancellationToken, ValueTask>? _onQrCodesExhaustedAsync;
    private readonly PairingOptions _options;

    private readonly SemaphoreSlim _pairLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _qrRotationCts;
    private Task? _qrRotationTask;
    private int _disposed;

    public PairingHandler(
        Func<Node, CancellationToken, ValueTask> sendStanzaAsync,
        IDeviceStore deviceStore,
        DeviceCredentials credentials,
        Func<WhatsAppEvent, ValueTask> emitEventAsync,
        Func<CancellationToken, ValueTask>? onQrCodesExhaustedAsync = null,
        PairingOptions? options = null)
    {
        _sendStanzaAsync = sendStanzaAsync ?? throw new ArgumentNullException(nameof(sendStanzaAsync));
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
        PairingOptions? options = null)
        : this(
            (node, ct) => new ValueTask(socket.SendNodeAsync(node, ct)),
            deviceStore,
            credentials,
            (evt) => eventWriter.WriteAsync(evt),
            onQrCodesExhaustedAsync,
            options)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(eventWriter);
    }

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

            // Crypto verified: retire active QR rotation now.
            (previousCts, previousTask) = DetachQrRotationLocked();

            Jid? prevPn = _credentials.Pn;
            Jid? prevLid = _credentials.Lid;
            bool prevLidMigrated = _credentials.LidMigrated;
            string prevPushName = _credentials.PushName;

            bool accountChanged = prevPn is not { } existingPn
                || !string.Equals(existingPn.User, primaryJid.User, StringComparison.Ordinal);

            bool reportedMigrated = parsed.ClientProps?.HasIsChatDbLidMigrated == true
                && parsed.ClientProps.IsChatDbLidMigrated;
            bool? lidMigratedUpdate = PairingCrypto.ComputeLidMigratedUpdate(reportedMigrated, accountChanged);

            _credentials.Pn = primaryJid;
            _credentials.Lid = accountChanged
                ? parsed.DeviceLidJid
                : (parsed.DeviceLidJid ?? prevLid);
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
