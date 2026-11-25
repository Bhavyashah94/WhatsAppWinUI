using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Api;

/// <summary>
/// Downloads (or unwraps inline bootstrap), decompresses, decodes, and persists
/// <see cref="Message.Types.HistorySyncNotification"/> payloads into <see cref="IMessageHistoryStore"/>,
/// <see cref="ILidPnCache"/>, and <see cref="IMsgSecretStore"/>, emitting <see cref="HistorySyncReceivedEvent"/>.
/// </summary>
public sealed class HistorySyncCoordinator
{
    private readonly IWhatsAppBackend _backend;
    private readonly ILidPnCache _lidPnCache;
    private readonly MediaTransferClient? _mediaClient;
    private readonly Func<WhatsAppEvent, ValueTask> _emitEvent;

    public HistorySyncCoordinator(
        IWhatsAppBackend backend,
        ILidPnCache lidPnCache,
        MediaTransferClient? mediaClient,
        Func<WhatsAppEvent, ValueTask> emitEvent)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _mediaClient = mediaClient;
        _emitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
    }

    public async Task<HistorySyncBatch> ProcessNotificationAsync(
        Message.Types.HistorySyncNotification notification,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        byte[] compressedZlib;
        if (notification.HasInitialHistBootstrapInlinePayload && notification.InitialHistBootstrapInlinePayload.Length > 0)
        {
            compressedZlib = notification.InitialHistBootstrapInlinePayload.ToByteArray();
        }
        else
        {
            if (_mediaClient is null)
            {
                throw new InvalidOperationException("HistorySync CDN chunk requires MediaTransferClient (IHttpClient).");
            }
            if (!notification.HasDirectPath || notification.MediaKey.Length != MediaCipher.MediaKeyLength)
            {
                throw new InvalidDataException("HistorySyncNotification missing DirectPath or valid 32-byte MediaKey.");
            }

            compressedZlib = await _mediaClient.DownloadAsync(
                notification.DirectPath,
                WaMediaType.History,
                notification.MediaKey.ToByteArray(),
                notification.HasFileSha256 ? notification.FileSha256.ToByteArray() : ReadOnlyMemory<byte>.Empty,
                notification.HasFileEncSha256 ? notification.FileEncSha256.ToByteArray() : ReadOnlyMemory<byte>.Empty,
                ct).ConfigureAwait(false);
        }

        byte[] protoBytes = HistorySyncInflater.Decompress(compressedZlib);
        uint? chunkOrder = notification.HasChunkOrder ? notification.ChunkOrder : null;
        uint? progress = notification.HasProgress ? notification.Progress : null;

        HistorySync proto = HistorySync.Parser.ParseFrom(protoBytes);
        HistorySyncBatch batch = HistorySyncDecoder.Decode(protoBytes, chunkOrder, progress);

        foreach (LidPnMappingRecord map in batch.LidMappings)
        {
            _lidPnCache.Learn(map.PnJid, map.LidJid, LidPnLearningSource.HistorySync);
        }
        foreach (InlineContactRecord contact in batch.InlineContacts)
        {
            if (!string.IsNullOrEmpty(contact.PnJid) && !string.IsNullOrEmpty(contact.LidJid))
            {
                _lidPnCache.Learn(contact.PnJid, contact.LidJid, LidPnLearningSource.HistorySync);
            }
        }

        await _backend.History.UpsertBatchAsync(batch, ct).ConfigureAwait(false);

        // Seed non-forwarded parent message secrets so later reactions/edits/polls can decrypt.
        var secretEntries = new List<(string MessageId, byte[] Secret, long? Timestamp)>();
        foreach (Conversation conv in proto.Conversations)
        {
            foreach (HistorySyncMsg hsMsg in conv.Messages)
            {
                WebMessageInfo? wmi = hsMsg.Message;
                if (wmi?.Key?.HasId != true || wmi.Message is null)
                {
                    continue;
                }

                byte[]? secret = wmi.Message.MessageContextInfo?.MessageSecret is { Length: 32 } ms
                    ? ms.ToByteArray()
                    : (wmi.Message.PollCreationMessage ?? wmi.Message.PollCreationMessageV2 ?? wmi.Message.PollCreationMessageV3 ?? wmi.Message.PollCreationMessageV5 ?? wmi.Message.PollCreationMessageV6)?.EncKey is { Length: 32 } ek
                        ? ek.ToByteArray()
                        : null;

                if (secret is not null)
                {
                    long? ts = wmi.HasMessageTimestamp ? unchecked((long)wmi.MessageTimestamp) : null;
                    secretEntries.Add((wmi.Key.Id, secret, ts));
                }
            }
        }

        if (secretEntries.Count > 0)
        {
            await _backend.MsgSecrets.PutMsgSecretsAsync(secretEntries, ct).ConfigureAwait(false);
        }

        string? peerSessionId = notification.HasPeerDataRequestSessionId ? notification.PeerDataRequestSessionId : null;
        await _emitEvent(new HistorySyncReceivedEvent(batch, peerSessionId)).ConfigureAwait(false);
        return batch;
    }
}
