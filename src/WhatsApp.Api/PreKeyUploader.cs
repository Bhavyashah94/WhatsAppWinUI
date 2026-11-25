using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Api;

public sealed record PreKeyUploadResult(
    bool Uploaded,
    int ServerCount,
    int UploadedCount,
    uint NextPreKeyId);

/// <summary>
/// Checks server-side pre-key inventory and replenishes batches cleanly matching
/// <c>WAWebUploadPreKeysJob</c> (<c>UPLOAD_KEYS_COUNT = 812</c>) and <c>whatsapp-rust/src/prekeys.rs</c>.
/// </summary>
public static class PreKeyUploader
{
    public const int DefaultWantedCount = PreKeyManager.DefaultUploadCountTarget;
    public const int MinPreKeyCount = PreKeyManager.DefaultLowPreKeyThreshold;
    public const uint MaxPreKeyId = 16_777_215; // 2^24 - 1

    public static async Task<PreKeyUploadResult> EnsureUploadedAsync(
        IqClient iqClient,
        IWhatsAppBackend backend,
        DeviceCredentials credentials,
        bool force = false,
        int wantedCount = DefaultWantedCount,
        TimeSpan? iqTimeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(iqClient);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(credentials);

        int clampedWanted = Math.Clamp(wantedCount, MinPreKeyCount, DefaultWantedCount);
        int serverCount = 0;

        if (!force)
        {
            string countIqId = iqClient.NextRequestId();
            Node countResp = await iqClient.SendIqAsync(
                PreKeyIq.BuildPreKeyCount(countIqId),
                iqTimeout ?? IqClient.DefaultTimeout,
                ct).ConfigureAwait(false);

            serverCount = PreKeyIq.ParsePreKeyCount(countResp);
            if (!PreKeyManager.NeedsReplenishment(serverCount, MinPreKeyCount))
            {
                return new PreKeyUploadResult(
                    Uploaded: false,
                    ServerCount: serverCount,
                    UploadedCount: 0,
                    NextPreKeyId: credentials.NextPreKeyId);
            }
        }

        uint startId = credentials.NextPreKeyId < 1 || credentials.NextPreKeyId > MaxPreKeyId
            ? 1U
            : credentials.NextPreKeyId;

        var generated = new List<GeneratedPreKey>(clampedWanted);
        uint firstSpan = Math.Min((uint)clampedWanted, MaxPreKeyId - startId + 1U);
        generated.AddRange(PreKeyManager.GeneratePreKeys(startId, (int)firstSpan));

        int remaining = clampedWanted - (int)firstSpan;
        uint cursor = startId + firstSpan > MaxPreKeyId ? 1U : startId + firstSpan;
        if (remaining > 0)
        {
            generated.AddRange(PreKeyManager.GeneratePreKeys(1U, remaining));
            cursor = 1U + (uint)remaining;
        }

        // Persist pre-keys and monotonic NextPreKeyId BEFORE transmitting on the wire,
        // so peers immediately receive decryptable keys even on abrupt disconnect.
        var toStore = generated
            .Select(pk => (pk.Id, pk.RecordBytes))
            .ToArray();
        await backend.Signal.SavePreKeysAsync(toStore, ct).ConfigureAwait(false);

        credentials.NextPreKeyId = cursor;
        await backend.Device.SaveAsync(credentials, ct).ConfigureAwait(false);

        string uploadIqId = iqClient.NextRequestId();
        Node uploadIq = PreKeyIq.BuildUploadPreKeys(
            uploadIqId,
            credentials.RegistrationId,
            credentials.IdentityKey.PublicKey,
            credentials.SignedPreKeyId,
            credentials.SignedPreKey.PublicKey,
            credentials.SignedPreKeySignature,
            PreKeyManager.ToUploadItems(generated));

        await iqClient.SendIqAsync(
            uploadIq,
            iqTimeout ?? IqClient.DefaultTimeout,
            ct).ConfigureAwait(false);

        return new PreKeyUploadResult(
            Uploaded: true,
            ServerCount: serverCount,
            UploadedCount: generated.Count,
            NextPreKeyId: cursor);
    }
}
