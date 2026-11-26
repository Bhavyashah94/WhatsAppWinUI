using System.Collections.Concurrent;
using System.Security.Cryptography;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Api;

public enum MediaReuploadErrorKind
{
    NotLoggedIn,
    Conflict,
    Rejected,
    Timeout,
    Cancelled,
    DecryptionFailed
}

public sealed class MediaReuploadException : Exception
{
    public MediaReuploadErrorKind Kind { get; }
    public int? StatusCode { get; }

    public MediaReuploadException(MediaReuploadErrorKind kind, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }
}

public sealed record MediaReuploadRequest(
    string MessageId,
    Jid ChatJid,
    bool FromMe,
    byte[] MediaKey32,
    Jid? Participant = null);

/// <summary>
/// Correlated Media Reupload Recovery Coordinator matching <c>media_reupload.rs</c>
/// and <c>WAWebRequestMediaReuploadManager</c>: pre-registers ACK + <c>mediaretry</c>
/// notification waiters keyed by stanza ID, deduplicates identical concurrent callers,
/// rejects conflicting keys for the same ID, and decrypts the resulting direct path.
/// </summary>
public sealed class MediaReuploadCoordinator
{
    public static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromSeconds(30);

    private sealed class InFlightEntry
    {
        public required string MessageId { get; init; }
        public required Jid ChatJid { get; init; }
        public required bool FromMe { get; init; }
        public required Jid? Participant { get; init; }
        public required byte[] MediaKey32 { get; init; }
        public TaskCompletionSource<Node> AckTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Node> NotificationTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<MediaRetryDecryptedResult>? SharedOperation { get; set; }

        public bool Matches(MediaReuploadRequest req) =>
            string.Equals(ChatJid.ToNonAdString(), req.ChatJid.ToNonAdString(), StringComparison.OrdinalIgnoreCase)
            && FromMe == req.FromMe
            && string.Equals(Participant?.ToNonAdString() ?? string.Empty, req.Participant?.ToNonAdString() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && MediaKey32.AsSpan().SequenceEqual(req.MediaKey32);
    }

    private readonly NoiseSessionSocket _socket;
    private readonly IWhatsAppBackend _backend;
    private readonly ILidPnCache _lidPnCache;
    private readonly DeviceCredentials _credentials;
    private readonly Func<WhatsAppEvent, ValueTask>? _emitEvent;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, InFlightEntry> _inFlight = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public MediaReuploadCoordinator(
        NoiseSessionSocket socket,
        IWhatsAppBackend backend,
        ILidPnCache lidPnCache,
        DeviceCredentials credentials,
        Func<WhatsAppEvent, ValueTask>? emitEvent = null,
        TimeSpan? stepTimeout = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _emitEvent = emitEvent;
        _timeout = stepTimeout ?? DefaultStepTimeout;
    }

    public Task<MediaRetryDecryptedResult> RequestAsync(MediaReuploadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.MessageId);
        if (request.MediaKey32 is not { Length: 32 })
        {
            throw new ArgumentException("MediaKey must be 32 bytes.", nameof(request));
        }

        InFlightEntry entry;
        lock (_lock)
        {
            if (_inFlight.TryGetValue(request.MessageId, out InFlightEntry? existing))
            {
                if (!existing.Matches(request))
                {
                    throw new MediaReuploadException(
                        MediaReuploadErrorKind.Conflict,
                        $"Conflicting in-flight media reupload request for message '{request.MessageId}'.");
                }
                return existing.SharedOperation!.WaitAsync(ct);
            }

            entry = new InFlightEntry
            {
                MessageId = request.MessageId,
                ChatJid = request.ChatJid.ToNonAd(),
                FromMe = request.FromMe,
                Participant = request.Participant?.ToNonAd(),
                MediaKey32 = (byte[])request.MediaKey32.Clone()
            };
            entry.SharedOperation = ExecuteRequestAsync(entry);
            _inFlight[request.MessageId] = entry;
        }

        return entry.SharedOperation.WaitAsync(ct);
    }

    public bool NotifyReceiptAck(Node ackNode)
    {
        ArgumentNullException.ThrowIfNull(ackNode);
        if (!string.Equals(ackNode.Tag, "ack", StringComparison.Ordinal)
            || !string.Equals(ackNode.GetAttr("class"), "receipt", StringComparison.Ordinal)
            || !string.Equals(ackNode.GetAttr("type"), "server-error", StringComparison.Ordinal))
        {
            return false;
        }

        string? id = ackNode.GetAttr("id");
        if (string.IsNullOrEmpty(id) || !_inFlight.TryGetValue(id, out InFlightEntry? entry))
        {
            return false;
        }

        return entry.AckTcs.TrySetResult(ackNode);
    }

    public async Task<bool> NotifyMediaRetryAsync(Node notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ParsedMediaRetryNotification? parsed = RetryAndMediaRetryStanzas.ParseMediaRetryNotification(notification);
        if (parsed is null)
        {
            return false;
        }

        if (_inFlight.TryGetValue(parsed.StanzaId, out InFlightEntry? entry))
        {
            entry.NotificationTcs.TrySetResult(notification);
            return true;
        }

        return false;
    }

    private async Task<MediaRetryDecryptedResult> ExecuteRequestAsync(InFlightEntry entry)
    {
        try
        {
            if (_credentials.Lid is not Jid ownLid)
            {
                throw new MediaReuploadException(MediaReuploadErrorKind.NotLoggedIn, "Local LID is unavailable for media reupload.");
            }

            Jid chat = entry.ChatJid;
            if (chat.IsPn() && _lidPnCache.TryGetLid(chat.ToString(), out string? lidStr) && !string.IsNullOrEmpty(lidStr) && Jid.TryParse(lidStr, out Jid resolvedLid))
            {
                chat = resolvedLid.ToNonAd();
            }

            (byte[] encPayload, byte[] iv12) = MediaRetryCrypto.EncryptServerErrorReceipt(entry.MediaKey32, entry.MessageId);
            Node receipt = RetryAndMediaRetryStanzas.BuildMediaServerErrorReceipt(
                entry.MessageId,
                chat,
                entry.FromMe,
                encPayload,
                iv12,
                entry.Participant,
                ownFromJid: ownLid.ToNonAd());

            await _socket.SendNodeAsync(receipt, CancellationToken.None).ConfigureAwait(false);

            Node ackNode;
            try
            {
                ackNode = await entry.AckTcs.Task.WaitAsync(_timeout).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new MediaReuploadException(MediaReuploadErrorKind.Timeout, $"Timed out waiting for server-error ACK on '{entry.MessageId}'.", inner: ex);
            }

            string? errorAttr = ackNode.GetAttr("error") ?? ackNode.GetChild("error")?.GetAttr("code");
            if (!string.IsNullOrEmpty(errorAttr))
            {
                int? code = int.TryParse(errorAttr, out int c) ? c : null;
                throw new MediaReuploadException(MediaReuploadErrorKind.Rejected, $"Media retry receipt rejected ({errorAttr}).", code);
            }

            Node notifNode;
            try
            {
                notifNode = await entry.NotificationTcs.Task.WaitAsync(_timeout).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new MediaReuploadException(MediaReuploadErrorKind.Timeout, $"Timed out waiting for mediaretry notification on '{entry.MessageId}'.", inner: ex);
            }

            ParsedMediaRetryNotification parsed = RetryAndMediaRetryStanzas.ParseMediaRetryNotification(notifNode)
                ?? throw new MediaReuploadException(MediaReuploadErrorKind.DecryptionFailed, "Malformed mediaretry notification.");

            if (parsed.ErrorCode is int errCode)
            {
                throw new MediaReuploadException(MediaReuploadErrorKind.Rejected, $"Media retry notification returned server error {errCode}.", errCode);
            }

            MediaRetryDecryptedResult result = MediaRetryCrypto.DecryptMediaRetryNotification(
                entry.MediaKey32,
                parsed.StanzaId,
                parsed.EncIv12,
                parsed.EncPayload);

            if (_emitEvent is not null)
            {
                string statusText = result.Status switch
                {
                    MediaRetryStatus.Success => "ok",
                    MediaRetryStatus.NotFound => "not-found",
                    MediaRetryStatus.DecryptionError => "decryption-error",
                    _ => "unknown"
                };
                await _emitEvent(new MediaRetryNotificationEvent(parsed.StanzaId, statusText, result.DirectPath)).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _inFlight.TryRemove(entry.MessageId, out _);
        }
    }
}
