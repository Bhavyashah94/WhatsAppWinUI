using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Protocol.Abstractions;

/// <summary>
/// Device credentials and metadata persistence contract.
/// </summary>
public interface IDeviceStore
{
    ValueTask<DeviceCredentials> LoadOrCreateAsync(CancellationToken ct = default);
    ValueTask<DeviceCredentials?> LoadAsync(CancellationToken ct = default);
    ValueTask SaveAsync(DeviceCredentials credentials, CancellationToken ct = default);
    ValueTask ClearPairingAsync(CancellationToken ct = default);
}

public sealed record SignedPreKeyEntry(uint Id, byte[] KeyPairBytes, byte[] Signature, long CreatedAtUnixSeconds);

/// <summary>
/// Signal Protocol storage contract (identities, sessions, pre-keys, signed pre-keys, sender keys).
/// </summary>
public interface ISignalStore
{
    ValueTask<SignalSession?> GetSessionAsync(string address, CancellationToken ct = default);
    ValueTask SaveSessionAsync(string address, SignalSession session, CancellationToken ct = default);
    ValueTask DeleteSessionAsync(string address, CancellationToken ct = default);
    ValueTask DeleteSessionsByUserAsync(string user, CancellationToken ct = default);

    ValueTask<byte[]?> GetIdentityAsync(string address, CancellationToken ct = default);
    ValueTask SaveIdentityAsync(string address, byte[] identityKey, CancellationToken ct = default);
    ValueTask DeleteIdentityAsync(string address, CancellationToken ct = default);

    ValueTask<byte[]?> GetPreKeyAsync(uint preKeyId, CancellationToken ct = default);
    ValueTask<(uint Id, byte[] KeyPairBytes)?> GetFirstPreKeyAsync(CancellationToken ct = default);
    ValueTask SavePreKeysAsync(IReadOnlyList<(uint Id, byte[] KeyPairBytes)> preKeys, CancellationToken ct = default);
    ValueTask DeletePreKeyAsync(uint preKeyId, CancellationToken ct = default);

    ValueTask<SignedPreKeyEntry?> GetSignedPreKeyAsync(uint signedPreKeyId, CancellationToken ct = default);
    ValueTask SaveSignedPreKeyAsync(SignedPreKeyEntry entry, CancellationToken ct = default);
    ValueTask DeleteSignedPreKeyAsync(uint signedPreKeyId, CancellationToken ct = default);

    ValueTask<byte[]?> GetSenderKeyAsync(string groupJid, string senderAddress, CancellationToken ct = default);
    ValueTask SaveSenderKeyAsync(string groupJid, string senderAddress, byte[] recordBytes, CancellationToken ct = default);
    ValueTask DeleteSenderKeyAsync(string groupJid, string senderAddress, CancellationToken ct = default);

    ValueTask<IReadOnlySet<string>> GetSenderKeyDistributedDevicesAsync(string groupJid, CancellationToken ct = default);
    ValueTask MarkSenderKeyDistributedAsync(string groupJid, IReadOnlyList<string> deviceJids, CancellationToken ct = default);
    ValueTask ClearSenderKeyDistributedAsync(string groupJid, string? userPrefix = null, CancellationToken ct = default);
}

/// <summary>
/// AppState (Syncd) cryptographic state contract (sync keys, collection versions/LTHash, mutation MACs).
/// </summary>
public interface IAppSyncStore
{
    ValueTask<byte[]?> GetSyncKeyAsync(byte[] keyId, CancellationToken ct = default);
    ValueTask<byte[]?> GetLatestSyncKeyIdAsync(CancellationToken ct = default);
    ValueTask SaveSyncKeyAsync(byte[] keyId, byte[] keyData, CancellationToken ct = default);

    ValueTask<(ulong Version, byte[] LtHash)> GetCollectionStateAsync(string collection, CancellationToken ct = default);
    ValueTask CommitSnapshotAsync(
        string collection,
        ulong newVersion,
        byte[] newLtHash,
        IReadOnlyList<(byte[] IndexMac, byte[] ValueMac)> macs,
        CancellationToken ct = default);
    ValueTask CommitPatchAsync(
        string collection,
        ulong newVersion,
        byte[] newLtHash,
        IReadOnlyList<(byte[] IndexMac, byte[] ValueMac)> addedMacs,
        IReadOnlyList<byte[]> removedIndexMacs,
        CancellationToken ct = default);
    ValueTask<byte[]?> GetMutationMacAsync(string collection, byte[] indexMac, CancellationToken ct = default);
}

/// <summary>
/// WhatsApp multi-device protocol metadata (LID/PN mapping, SKDM tracking, device lists, privacy tokens).
/// </summary>
public interface IProtocolStore
{
    ValueTask UpsertLidPnMappingsAsync(IReadOnlyList<LidPnMappingRecord> mappings, CancellationToken ct = default);
    ValueTask<IReadOnlyList<LidPnMappingRecord>> LoadLidPnMappingsAsync(CancellationToken ct = default);

    ValueTask<IReadOnlyList<uint>> GetDeviceListAsync(string userJid, CancellationToken ct = default);
    ValueTask SaveDeviceListAsync(string userJid, IReadOnlyList<uint> deviceIds, CancellationToken ct = default);
    ValueTask DeleteDeviceListAsync(string userJid, CancellationToken ct = default);

    ValueTask StoreTcTokenAsync(string jid, byte[] token, long timestamp, CancellationToken ct = default);
    ValueTask<byte[]?> GetTcTokenAsync(string jid, CancellationToken ct = default);
}

/// <summary>
/// Persisted message secrets contract for poll/edit/event validation.
/// </summary>
public interface IMsgSecretStore
{
    ValueTask PutMsgSecretsAsync(IReadOnlyList<(string MessageId, byte[] Secret, long? Timestamp)> entries, CancellationToken ct = default);
    ValueTask<(byte[] Secret, long? Timestamp)?> GetMsgSecretAsync(string messageId, CancellationToken ct = default);
    ValueTask DeleteExpiredMsgSecretsAsync(long cutoffTimestamp, CancellationToken ct = default);
}

/// <summary>
/// Contiguous message range boundary record (min..max timestamps synced locally per chat).
/// </summary>
public sealed record ActiveMessageRange(string ChatJid, ulong MinTimestamp, ulong MaxTimestamp);

/// <summary>
/// Normalized conversation/contact/message history persistence and contiguity tracking.
/// </summary>
public interface IMessageHistoryStore
{
    ValueTask UpsertBatchAsync(HistorySyncBatch batch, CancellationToken ct = default);
    ValueTask SaveMessageAsync(MessageRecord message, CancellationToken ct = default);
    ValueTask<IReadOnlyList<MessageRecord>> GetMessagesAsync(string chatJid, ulong? beforeTimestamp, int limit, CancellationToken ct = default);
    ValueTask<int> GetMessageCountAsync(string? chatJid = null, CancellationToken ct = default);
    ValueTask<ActiveMessageRange?> GetMessageRangeAsync(string chatJid, CancellationToken ct = default);
    ValueTask UpdateMessageRangeAsync(ActiveMessageRange range, CancellationToken ct = default);

    ValueTask<IReadOnlyList<ChatSummary>> GetChatsAsync(int limit = 50, int offset = 0, bool? archived = null, CancellationToken ct = default);
    ValueTask<IReadOnlyList<MessageRecord>> SearchMessagesAsync(string query, string? chatJid = null, int limit = 50, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ContactRecord>> SearchContactsAsync(string query, int limit = 50, CancellationToken ct = default);
    ValueTask SaveDraftAsync(string chatJid, string? draftText, long timestampMs, CancellationToken ct = default);
    ValueTask ApplyMessageEditAsync(string chatJid, string messageId, string? newText, long editedTimestampSeconds, CancellationToken ct = default);
    ValueTask MarkMessageRevokedAsync(string chatJid, string messageId, CancellationToken ct = default);
    ValueTask UpsertMessageReactionAsync(string chatJid, string messageId, string senderJid, string emoji, CancellationToken ct = default);
}

/// <summary>
/// Unified backend storage contract composing all 6 domain-scoped stores.
/// </summary>
public interface IWhatsAppBackend : IAsyncDisposable
{
    IDeviceStore Device { get; }
    ISignalStore Signal { get; }
    IAppSyncStore AppSync { get; }
    IProtocolStore Protocol { get; }
    IMsgSecretStore MsgSecrets { get; }
    IMessageHistoryStore History { get; }
}
