using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Api;

/// <summary>
/// Runtime client for global <c>&lt;presence&gt;</c> availability &amp; subscriptions (with stored
/// <c>tctoken</c> attachment) and per-chat <c>&lt;chatstate&gt;</c> typing/recording indicators.
/// </summary>
public sealed class PresenceClient
{
    private readonly NoiseSessionSocket _socket;
    private readonly IWhatsAppBackend _backend;
    private readonly DeviceCredentials _credentials;
    private readonly Func<WhatsAppEvent, ValueTask> _emitEvent;

    public PresenceClient(
        NoiseSessionSocket socket,
        IWhatsAppBackend backend,
        DeviceCredentials credentials,
        Func<WhatsAppEvent, ValueTask> emitEvent)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _emitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
    }

    public Task SetAvailabilityAsync(PresenceStatus status, string? pushName = null, CancellationToken ct = default)
    {
        string? name = pushName ?? (status == PresenceStatus.Available ? _credentials.PushName : null);
        Node stanza = PresenceStanzas.BuildAvailability(status, name);
        return _socket.SendNodeAsync(stanza, ct);
    }

    public async Task SubscribeAsync(
        Jid targetUserJid,
        string? name = null,
        Jid? groupContext = null,
        CancellationToken ct = default)
    {
        Jid bare = targetUserJid.ToNonAd();
        byte[]? tcToken = await _backend.Protocol.GetTcTokenAsync(bare.ToString(), ct).ConfigureAwait(false);
        Node stanza = PresenceStanzas.BuildSubscribe(bare, name, groupContext, tcToken);
        await _socket.SendNodeAsync(stanza, ct).ConfigureAwait(false);
    }

    public Task SendChatStateAsync(
        Jid toChatJid,
        ChatActivity activity,
        Jid? participant = null,
        CancellationToken ct = default)
    {
        Node stanza = PresenceStanzas.BuildChatState(toChatJid, activity, participant);
        return _socket.SendNodeAsync(stanza, ct);
    }

    public async ValueTask<bool> HandlePresenceStanzaAsync(Node stanza, CancellationToken ct = default)
    {
        ParsedPresenceStanza? parsed = PresenceStanzas.ParseIncomingPresence(stanza);
        if (parsed is null)
        {
            return false;
        }

        await _emitEvent(new PresenceUpdateEvent(
            parsed.From.ToNonAdString(),
            parsed.Status == PresenceStatus.Available,
            parsed.LastSeenSeconds)).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> HandleChatStateStanzaAsync(Node stanza, CancellationToken ct = default)
    {
        ParsedChatStateStanza? parsed = PresenceStanzas.ParseIncomingChatState(stanza);
        if (parsed is null)
        {
            return false;
        }

        string act = parsed.Activity switch
        {
            ChatActivity.Typing => "typing",
            ChatActivity.RecordingAudio => "recording_audio",
            _ => "idle"
        };

        await _emitEvent(new ChatStateEvent(
            parsed.ChatJid.ToNonAdString(),
            parsed.Participant?.ToNonAdString(),
            act)).ConfigureAwait(false);
        return true;
    }
}
