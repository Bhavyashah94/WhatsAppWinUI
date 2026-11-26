using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Api;

public sealed record PushNameOutcome(bool Synced, Exception? SyncError = null);

/// <summary>
/// Runtime client for own-profile updates: display push name (<c>&lt;presence name/&gt;</c>
/// + <c>DeviceCredentials</c> + <c>setting_pushName</c> syncd) and MEX expiring/emoji About text
/// (<c>WAWebMexUpdateTextStatusJobMutation</c>).
/// </summary>
public sealed class ProfileClient
{
    private readonly NoiseSessionSocket _socket;
    private readonly MexClient _mexClient;
    private readonly SyncdCoordinator? _syncd;
    private readonly IWhatsAppBackend _backend;
    private readonly DeviceCredentials _credentials;
    private readonly TimeProvider _timeProvider;

    public ProfileClient(
        NoiseSessionSocket socket,
        MexClient mexClient,
        IWhatsAppBackend backend,
        DeviceCredentials credentials,
        SyncdCoordinator? syncd = null,
        TimeProvider? timeProvider = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _mexClient = mexClient ?? throw new ArgumentNullException(nameof(mexClient));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _syncd = syncd;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PushNameOutcome> SetPushNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Push name cannot be empty.", nameof(name));
        }

        var presenceNode = new Node("presence", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["name"] = name
        });
        await _socket.SendNodeAsync(presenceNode, ct).ConfigureAwait(false);

        _credentials.PushName = name;
        await _backend.Device.SaveAsync(_credentials, ct).ConfigureAwait(false);

        if (_syncd is null)
        {
            return new PushNameOutcome(Synced: false);
        }

        try
        {
            await SyncPushNameAsync(name, ct).ConfigureAwait(false);
            return new PushNameOutcome(Synced: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PushNameOutcome(Synced: false, SyncError: ex);
        }
    }

    public Task SyncPushNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Push name cannot be empty.", nameof(name));
        }
        if (_syncd is null)
        {
            throw new InvalidOperationException("SyncdCoordinator is required to sync push name.");
        }

        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            PushNameSetting = new SyncActionValue.Types.PushNameSetting { Name = name }
        };
        return _syncd.SendMutationAsync(
            AppStateSchemas.SettingPushName.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.SettingPushName.BuildIndex(),
            value,
            ct: ct);
    }

    public async Task SetTextStatusAsync(
        string? text,
        string? emoji = null,
        TimeSpan? expiry = null,
        CancellationToken ct = default)
    {
        string? trimmedText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        string? trimmedEmoji = string.IsNullOrWhiteSpace(emoji) ? null : emoji.Trim();
        if (trimmedText is null && trimmedEmoji is null)
        {
            throw new ArgumentException("A text status requires non-empty text or emoji; use ClearTextStatusAsync to remove.");
        }

        long durationSeconds;
        if (expiry is null)
        {
            durationSeconds = -1;
        }
        else
        {
            long secs = (long)expiry.Value.TotalSeconds;
            if (secs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(expiry), "Text status expiry duration must be at least 1 second.");
            }
            durationSeconds = secs;
        }

        object input = trimmedEmoji is not null
            ? new
            {
                text = trimmedText,
                ephemeral_duration_sec = durationSeconds,
                emoji = new { content = trimmedEmoji }
            }
            : new
            {
                text = trimmedText,
                ephemeral_duration_sec = durationSeconds
            };

        await ExecuteTextStatusAsync(input, ct).ConfigureAwait(false);
    }

    public Task ClearTextStatusAsync(CancellationToken ct = default) =>
        ExecuteTextStatusAsync(new { text = (string?)null, ephemeral_duration_sec = 0L }, ct);

    private async Task ExecuteTextStatusAsync(object input, CancellationToken ct)
    {
        MexExecutionResponse resp = await _mexClient.ExecuteAsync(
            MexStanzas.UpdateTextStatus,
            new { input },
            ct: ct).ConfigureAwait(false);

        string? result = MexClient.TryGetString(resp.Data, "xwa2_update_text_status", "result");
        if (!string.Equals(result, "SUCCESS", StringComparison.Ordinal))
        {
            throw new MexException(MexStanzas.UpdateTextStatus.Name, $"MEX text status update returned '{result ?? "<null>"}'.");
        }
    }
}
