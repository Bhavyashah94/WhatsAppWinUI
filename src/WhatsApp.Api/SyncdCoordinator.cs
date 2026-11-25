using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Api;

/// <summary>
/// Orchestrates AppState (<c>syncd</c>) inbound collection sync (external snapshots, external patch
/// mutations, pagination, anti-tampering validation, <c>PushNameSetting</c> propagation) and
/// serialized outbound mutation sends with <c>409</c> conflict absorption and rebuild.
/// Mirrors <c>whatsapp-rust/src/client/app_state.rs</c> and <c>wacore/src/appstate_sync.rs</c>.
/// </summary>
public sealed class SyncdCoordinator : IAsyncDisposable
{
    public const string DefaultMediaHost = "mmg.whatsapp.net";
    public const int MaxSyncPages = 50;
    public const int MaxPatchSendAttempts = 5;

    private readonly IWhatsAppBackend _backend;
    private readonly IHttpClient _httpClient;
    private readonly Func<Node, CancellationToken, ValueTask<Node>> _sendIqAsync;
    private readonly Func<string> _nextIqId;
    private readonly Func<WhatsAppEvent, ValueTask> _emitEventAsync;
    private readonly string _mediaHost;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _collectionLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AppStateCollectionState> _collectionStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExpandedAppStateKeys> _expandedKeyCache = new(StringComparer.Ordinal);

    private long _fallbackIqSeq;
    private int _disposed;

    public SyncdCoordinator(
        IWhatsAppBackend backend,
        IHttpClient httpClient,
        IqClient iqClient,
        Func<WhatsAppEvent, ValueTask>? emitEventAsync = null,
        string? mediaHost = null)
        : this(
            backend,
            httpClient,
            (node, token) => new ValueTask<Node>((iqClient ?? throw new ArgumentNullException(nameof(iqClient))).SendIqAsync(node, IqClient.DefaultTimeout, token)),
            () => iqClient.NextRequestId(),
            emitEventAsync,
            mediaHost)
    {
    }

    public SyncdCoordinator(
        IWhatsAppBackend backend,
        IHttpClient httpClient,
        Func<Node, CancellationToken, ValueTask<Node>> sendIqAsync,
        Func<string>? nextIqId = null,
        Func<WhatsAppEvent, ValueTask>? emitEventAsync = null,
        string? mediaHost = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _sendIqAsync = sendIqAsync ?? throw new ArgumentNullException(nameof(sendIqAsync));
        _nextIqId = nextIqId ?? (() => $"syncd-{Interlocked.Increment(ref _fallbackIqSeq)}");
        _emitEventAsync = emitEventAsync ?? (_ => ValueTask.CompletedTask);
        _mediaHost = string.IsNullOrWhiteSpace(mediaHost) ? DefaultMediaHost : mediaHost.Trim();
    }

    public async Task<IReadOnlyList<DecodedAppStateMutation>> SyncCollectionAsync(
        string collection,
        bool validateMacs = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(collection);
        IReadOnlyDictionary<string, IReadOnlyList<DecodedAppStateMutation>> result =
            await SyncCollectionsAsync([collection], validateMacs, ct).ConfigureAwait(false);
        return result.TryGetValue(collection, out var list) ? list : Array.Empty<DecodedAppStateMutation>();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DecodedAppStateMutation>>> SyncCollectionsAsync(
        IReadOnlyList<string> collections,
        bool validateMacs = true,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(collections);
        if (collections.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<DecodedAppStateMutation>>(StringComparer.Ordinal);
        }

        // Acquire per-collection locks in deterministic sorted order to prevent deadlocks across overlapping batches.
        List<string> ordered = collections.Distinct(StringComparer.Ordinal).OrderBy(static c => c, StringComparer.Ordinal).ToList();
        var heldLocks = new List<SemaphoreSlim>(ordered.Count);
        foreach (string col in ordered)
        {
            SemaphoreSlim gate = GetCollectionLock(col);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            heldLocks.Add(gate);
        }

        try
        {
            return await SyncCollectionsUnlockedAsync(ordered, validateMacs, ct).ConfigureAwait(false);
        }
        finally
        {
            for (int i = heldLocks.Count - 1; i >= 0; i--)
            {
                heldLocks[i].Release();
            }
        }
    }

    public async Task SendMutationAsync(
        string collection,
        SyncdMutation.Types.SyncdOperation operation,
        ReadOnlyMemory<byte> indexJsonUtf8,
        SyncActionValue value,
        int actionVersion = 1,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrEmpty(collection);
        ArgumentNullException.ThrowIfNull(value);

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SemaphoreSlim collectionGate = GetCollectionLock(collection);
            await collectionGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                AppStateCollectionState state = await GetOrLoadCollectionStateAsync(collection, ct).ConfigureAwait(false);
                if (!state.HasBaseline)
                {
                    await SyncCollectionsUnlockedAsync([collection], validateMacs: true, ct).ConfigureAwait(false);
                    state = await GetOrLoadCollectionStateAsync(collection, ct).ConfigureAwait(false);
                    if (!state.HasBaseline)
                    {
                        throw new AppStateException(
                            AppStateErrorKind.PatchVersionMismatch,
                            $"AppState patch for '{collection}' aborted: initial baseline bootstrap did not complete.");
                    }
                }

                for (int attempt = 1; attempt <= MaxPatchSendAttempts; attempt++)
                {
                    var (patchBytes, baseVersion) = await BuildOutboundPatchAsync(
                        collection,
                        operation,
                        indexJsonUtf8,
                        value,
                        actionVersion,
                        ct).ConfigureAwait(false);

                    Node colReq = AppStateSyncIq.BuildCollectionRequest(
                        collection,
                        version: baseVersion,
                        returnSnapshot: false,
                        encodedPatch: patchBytes);
                    Node iq = AppStateSyncIq.BuildSync(_nextIqId(), [colReq]);
                    Node resp = await _sendIqAsync(iq, ct).ConfigureAwait(false);

                    AppStateCollectionPatchList? verdict = ParseSendResponse(collection, resp);
                    if (verdict is null || verdict.Error is null)
                    {
                        await SyncCollectionsUnlockedAsync([collection], validateMacs: true, ct).ConfigureAwait(false);
                        return;
                    }

                    if (verdict.Error.Kind == CollectionSyncErrorKind.Conflict)
                    {
                        var absorbed = new AppStateCollectionPatchList(
                            verdict.CollectionName,
                            verdict.HasMorePatches,
                            verdict.Patches,
                            verdict.SnapshotReference,
                            Error: null);

                        bool needsFollowUpSync = verdict.HasMorePatches ||
                            (absorbed.Patches.Count == 0 && absorbed.SnapshotReference is null);

                        if (absorbed.Patches.Count > 0 || absorbed.SnapshotReference is not null)
                        {
                            var (_, diverged) = await ApplyCollectionPatchListAsync(absorbed, validateMacs: true, ct)
                                .ConfigureAwait(false);
                            if (diverged)
                            {
                                needsFollowUpSync = true;
                            }
                        }

                        if (needsFollowUpSync)
                        {
                            await SyncCollectionsUnlockedAsync([collection], validateMacs: true, ct).ConfigureAwait(false);
                        }
                        continue;
                    }

                    throw new AppStateException(
                        AppStateErrorKind.DecodeFailed,
                        $"Server rejected AppState patch for '{collection}' ({verdict.Error.Code}): {verdict.Error.Text}");
                }

                throw new AppStateException(
                    AppStateErrorKind.PatchVersionMismatch,
                    $"AppState patch for '{collection}' kept conflicting after {MaxPatchSendAttempts} attempts.");
            }
            finally
            {
                collectionGate.Release();
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<DecodedAppStateMutation>>> SyncCollectionsUnlockedAsync(
        IReadOnlyList<string> collections,
        bool validateMacs,
        CancellationToken ct)
    {
        var allMutations = new Dictionary<string, List<DecodedAppStateMutation>>(StringComparer.Ordinal);
        var pending = new List<string>(collections.Count);
        foreach (string col in collections)
        {
            allMutations[col] = [];
            pending.Add(col);
        }

        for (int page = 0; page < MaxSyncPages && pending.Count > 0; page++)
        {
            var reqs = new List<Node>(pending.Count);
            foreach (string col in pending)
            {
                AppStateCollectionState state = await GetOrLoadCollectionStateAsync(col, ct).ConfigureAwait(false);
                bool requestSnapshot = !state.HasBaseline || state.MacMismatchFatal;
                reqs.Add(AppStateSyncIq.BuildCollectionRequest(
                    col,
                    version: requestSnapshot ? null : state.Version,
                    returnSnapshot: requestSnapshot));
            }

            Node iq = AppStateSyncIq.BuildSync(_nextIqId(), reqs);
            Node resp = await _sendIqAsync(iq, ct).ConfigureAwait(false);
            IReadOnlyList<AppStateCollectionPatchList> parsed = AppStatePatchParser.ParseCollections(resp);

            var nextPending = new List<string>();
            var returnedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (AppStateCollectionPatchList colList in parsed)
            {
                returnedNames.Add(colList.CollectionName);
                if (!allMutations.TryGetValue(colList.CollectionName, out var bucket))
                {
                    bucket = [];
                    allMutations[colList.CollectionName] = bucket;
                }

                if (colList.Error is { Kind: CollectionSyncErrorKind.Fatal } fatal)
                {
                    throw new AppStateException(
                        AppStateErrorKind.DecodeFailed,
                        $"Server fatal error syncing AppState '{colList.CollectionName}' ({fatal.Code}): {fatal.Text}");
                }

                var (decoded, diverged) = await ApplyCollectionPatchListAsync(colList, validateMacs, ct).ConfigureAwait(false);
                bucket.AddRange(decoded);

                bool needsRetry = diverged || colList.HasMorePatches || colList.Error is not null;
                if (!needsRetry)
                {
                    await MarkCollectionBootstrappedAsync(colList.CollectionName, ct).ConfigureAwait(false);
                }
                else
                {
                    nextPending.Add(colList.CollectionName);
                }
            }

            foreach (string reqCol in pending)
            {
                if (!returnedNames.Contains(reqCol) && allMutations.TryGetValue(reqCol, out _))
                {
                    await MarkCollectionBootstrappedAsync(reqCol, ct).ConfigureAwait(false);
                }
            }

            pending = nextPending;
        }

        if (pending.Count > 0)
        {
            throw new AppStateException(
                AppStateErrorKind.DecodeFailed,
                $"AppState sync did not converge within {MaxSyncPages} pages for: {string.Join(", ", pending)}");
        }

        return allMutations.ToDictionary(
            static kv => kv.Key,
            static kv => (IReadOnlyList<DecodedAppStateMutation>)kv.Value,
            StringComparer.Ordinal);
    }

    private async Task<(List<DecodedAppStateMutation> Mutations, bool SnapshotMacDiverged)> ApplyCollectionPatchListAsync(
        AppStateCollectionPatchList patchList,
        bool validateMacs,
        CancellationToken ct)
    {
        string collection = patchList.CollectionName;
        var decodedAll = new List<DecodedAppStateMutation>();
        AppStateCollectionState state = await GetOrLoadCollectionStateAsync(collection, ct).ConfigureAwait(false);

        if (patchList.SnapshotReference is { } snapRef)
        {
            byte[] snapBytes = await DownloadAndDecryptExternalBlobAsync(snapRef, ct).ConfigureAwait(false);
            SyncdSnapshot snapshot;
            try
            {
                snapshot = SyncdSnapshot.Parser.ParseFrom(snapBytes);
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new AppStateException(AppStateErrorKind.DecodeFailed, $"Failed to parse SyncdSnapshot for '{collection}'.", ex);
            }

            var keyLookup = await PrefetchSnapshotKeysAsync(snapshot, ct).ConfigureAwait(false);
            ProcessedSnapshotResult snapResult = AppStateProcessor.ProcessSnapshot(
                snapshot,
                state,
                id => ResolveCachedKey(keyLookup, id),
                collection,
                validateMacs);

            var addedTuples = snapResult.AddedMacs.Select(static m => (m.IndexMac, m.ValueMac)).ToArray();
            await _backend.AppSync.CommitSnapshotAsync(
                collection,
                snapResult.State.Version,
                snapResult.State.Hash,
                addedTuples,
                ct).ConfigureAwait(false);

            state = snapResult.State;
            _collectionStates[collection] = state;

            decodedAll.AddRange(snapResult.Mutations);
            await ApplySideEffectsAsync(snapResult.Mutations, ct).ConfigureAwait(false);
            await _emitEventAsync(new AppStateUpdatedEvent(collection, state.Version, snapResult.Mutations.Count)).ConfigureAwait(false);
        }

        foreach (SyncdPatch rawPatch in patchList.Patches)
        {
            SyncdPatch patch = await HydratePatchAsync(rawPatch, ct).ConfigureAwait(false);
            var keyLookup = await PrefetchPatchKeysAsync(patch, ct).ConfigureAwait(false);
            var prevMacLookup = await PrefetchPreviousValueMacsAsync(collection, patch, ct).ConfigureAwait(false);

            ProcessedPatchResult patchResult = AppStateProcessor.ProcessPatch(
                patch,
                state,
                id => ResolveCachedKey(keyLookup, id),
                idx => prevMacLookup.GetValueOrDefault(Convert.ToHexStringLower(idx)),
                collection,
                validateMacs);

            if (patchResult.SnapshotMacDiverged)
            {
                state = patchResult.State;
                state.MacMismatchFatal = true;
                _collectionStates[collection] = state;
                return (decodedAll, true);
            }

            var addedTuples = patchResult.AddedMacs.Select(static m => (m.IndexMac, m.ValueMac)).ToArray();
            await _backend.AppSync.CommitPatchAsync(
                collection,
                patchResult.State.Version,
                patchResult.State.Hash,
                addedTuples,
                patchResult.RemovedIndexMacs,
                ct).ConfigureAwait(false);

            state = patchResult.State;
            state.Bootstrapped = true;
            _collectionStates[collection] = state;

            decodedAll.AddRange(patchResult.Mutations);
            await ApplySideEffectsAsync(patchResult.Mutations, ct).ConfigureAwait(false);
            await _emitEventAsync(new AppStateUpdatedEvent(collection, state.Version, patchResult.Mutations.Count)).ConfigureAwait(false);
        }

        return (decodedAll, false);
    }

    private async Task<(byte[] PatchBytes, ulong BaseVersion)> BuildOutboundPatchAsync(
        string collection,
        SyncdMutation.Types.SyncdOperation operation,
        ReadOnlyMemory<byte> indexJsonUtf8,
        SyncActionValue value,
        int actionVersion,
        CancellationToken ct)
    {
        byte[] keyId = await _backend.AppSync.GetLatestSyncKeyIdAsync(ct).ConfigureAwait(false)
            ?? throw new AppStateException(AppStateErrorKind.KeyNotFound, "No AppState sync key available for outbound patch.");

        ExpandedAppStateKeys keys = await GetOrLoadKeysAsync(keyId, ct).ConfigureAwait(false);
        AppStateCollectionState state = await GetOrLoadCollectionStateAsync(collection, ct).ConfigureAwait(false);
        ulong baseVersion = state.Version;

        byte[] iv = new byte[AppStateMutationCodec.IvSizeBytes];
        RandomNumberGenerator.Fill(iv);

        var (mutation, _) = AppStateMutationCodec.EncodeRecord(
            operation,
            indexJsonUtf8.Span,
            value,
            keys,
            keyId,
            iv,
            actionVersion);

        byte[] nextHash = (byte[])state.Hash.Clone();
        var prevMacs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (mutation.Record?.Index is { HasBlob: true } idxBlob)
        {
            byte[] idxBytes = idxBlob.Blob.ToByteArray();
            byte[]? existing = await _backend.AppSync.GetMutationMacAsync(collection, idxBytes, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                prevMacs[Convert.ToHexStringLower(idxBytes)] = existing;
            }
        }

        AppStateHasher.UpdateHash(
            nextHash,
            [mutation],
            (idx, _) => prevMacs.GetValueOrDefault(Convert.ToHexStringLower(idx)));

        ulong nextVersion = baseVersion + 1UL;
        byte[] snapshotMac = AppStateHasher.GenerateSnapshotMac(nextHash, nextVersion, collection, keys.SnapshotMacKey);

        var patch = new SyncdPatch
        {
            SnapshotMac = ByteString.CopyFrom(snapshotMac),
            KeyId = new KeyId { Id = ByteString.CopyFrom(keyId) }
        };
        patch.Mutations.Add(mutation);

        byte[] patchMac = AppStateHasher.GeneratePatchMac(patch, collection, keys.PatchMacKey, nextVersion);
        patch.PatchMac = ByteString.CopyFrom(patchMac);

        return (patch.ToByteArray(), baseVersion);
    }

    private async Task<SyncdPatch> HydratePatchAsync(SyncdPatch patch, CancellationToken ct)
    {
        if (patch.ExternalMutations is not { } extRef || patch.Mutations.Count > 0)
        {
            return patch;
        }

        byte[] plainBytes = await DownloadAndDecryptExternalBlobAsync(extRef, ct).ConfigureAwait(false);
        SyncdMutations externalMutations;
        try
        {
            externalMutations = SyncdMutations.Parser.ParseFrom(plainBytes);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new AppStateException(AppStateErrorKind.DecodeFailed, "Failed to parse external SyncdMutations blob.", ex);
        }

        SyncdPatch hydrated = patch.Clone();
        hydrated.Mutations.AddRange(externalMutations.Mutations);
        return hydrated;
    }

    private async Task<byte[]> DownloadAndDecryptExternalBlobAsync(ExternalBlobReference extRef, CancellationToken ct)
    {
        if (!extRef.HasDirectPath || string.IsNullOrWhiteSpace(extRef.DirectPath))
        {
            throw new AppStateException(AppStateErrorKind.ExternalBlobDownloadFailed, "ExternalBlobReference missing DirectPath.");
        }

        string path = extRef.DirectPath;
        if (!path.StartsWith('/'))
        {
            throw new AppStateException(
                AppStateErrorKind.ExternalBlobDownloadFailed,
                $"ExternalBlobReference DirectPath must be relative: '{path}'.");
        }

        if (!extRef.HasMediaKey || extRef.MediaKey.Length != MediaCipher.MediaKeyLength)
        {
            throw new AppStateException(
                AppStateErrorKind.ExternalBlobDownloadFailed,
                "ExternalBlobReference missing valid 32-byte MediaKey.");
        }

        Uri url = new($"https://{_mediaHost}{path}", UriKind.Absolute);
        byte[] encBlob;
        try
        {
            encBlob = await _httpClient.GetBytesAsync(url, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AppStateException(AppStateErrorKind.ExternalBlobDownloadFailed, $"Failed to download AppState external blob '{path}'.", ex);
        }

        try
        {
            return MediaCipher.DecryptBlob(
                encBlob,
                extRef.MediaKey.Span,
                WaMediaType.AppState,
                extRef.HasFileEncSha256 ? extRef.FileEncSha256.Span : default,
                extRef.HasFileSha256 ? extRef.FileSha256.Span : default);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new AppStateException(AppStateErrorKind.ExternalBlobDownloadFailed, $"Failed to decrypt AppState external blob '{path}'.", ex);
        }
    }

    private async Task ApplySideEffectsAsync(IReadOnlyList<DecodedAppStateMutation> mutations, CancellationToken ct)
    {
        string? latestPushName = null;
        foreach (DecodedAppStateMutation mutation in mutations)
        {
            if (mutation.Operation == SyncdMutation.Types.SyncdOperation.Set
                && mutation.ActionValue?.PushNameSetting is { HasName: true } pushSetting
                && !string.IsNullOrEmpty(pushSetting.Name))
            {
                latestPushName = pushSetting.Name;
            }
        }

        if (latestPushName is null)
        {
            return;
        }

        DeviceCredentials? creds = await _backend.Device.LoadAsync(ct).ConfigureAwait(false);
        if (creds is not null && !string.Equals(creds.PushName, latestPushName, StringComparison.Ordinal))
        {
            creds.PushName = latestPushName;
            await _backend.Device.SaveAsync(creds, ct).ConfigureAwait(false);
        }
    }

    private static AppStateCollectionPatchList? ParseSendResponse(string expectedCollection, Node response)
    {
        if (response.GetChild("sync")?.GetChild("collection") is null)
        {
            return null;
        }

        IReadOnlyList<AppStateCollectionPatchList> lists = AppStatePatchParser.ParseCollections(response);
        if (lists.Count == 0)
        {
            return null;
        }

        AppStateCollectionPatchList match = lists.FirstOrDefault(l => string.Equals(l.CollectionName, expectedCollection, StringComparison.Ordinal))
            ?? throw new AppStateException(
                AppStateErrorKind.DecodeFailed,
                $"AppState send response collection mismatch: expected '{expectedCollection}', got '{lists[0].CollectionName}'.");

        return match;
    }

    private async Task MarkCollectionBootstrappedAsync(string collection, CancellationToken ct)
    {
        AppStateCollectionState state = await GetOrLoadCollectionStateAsync(collection, ct).ConfigureAwait(false);
        state.Bootstrapped = true;
        _collectionStates[collection] = state;
    }

    private async Task<AppStateCollectionState> GetOrLoadCollectionStateAsync(string collection, CancellationToken ct)
    {
        if (_collectionStates.TryGetValue(collection, out AppStateCollectionState? cached))
        {
            return cached.Clone();
        }

        var (version, ltHash) = await _backend.AppSync.GetCollectionStateAsync(collection, ct).ConfigureAwait(false);
        var loaded = new AppStateCollectionState
        {
            Version = version,
            Hash = ltHash.Length == LtHash.HashSizeBytes ? ltHash : LtHash.CreateEmpty(),
            Bootstrapped = version > 0
        };
        _collectionStates[collection] = loaded;
        return loaded.Clone();
    }

    private async Task<Dictionary<string, ExpandedAppStateKeys>> PrefetchSnapshotKeysAsync(SyncdSnapshot snapshot, CancellationToken ct)
    {
        var map = new Dictionary<string, ExpandedAppStateKeys>(StringComparer.Ordinal);
        if (snapshot.KeyId is { HasId: true } && snapshot.KeyId.Id.Length > 0)
        {
            await CacheKeyAsync(map, snapshot.KeyId.Id.ToByteArray(), ct).ConfigureAwait(false);
        }

        foreach (SyncdRecord rec in snapshot.Records)
        {
            if (rec.KeyId is { HasId: true } && rec.KeyId.Id.Length > 0)
            {
                await CacheKeyAsync(map, rec.KeyId.Id.ToByteArray(), ct).ConfigureAwait(false);
            }
        }
        return map;
    }

    private async Task<Dictionary<string, ExpandedAppStateKeys>> PrefetchPatchKeysAsync(SyncdPatch patch, CancellationToken ct)
    {
        var map = new Dictionary<string, ExpandedAppStateKeys>(StringComparer.Ordinal);
        if (patch.KeyId is { HasId: true } && patch.KeyId.Id.Length > 0)
        {
            await CacheKeyAsync(map, patch.KeyId.Id.ToByteArray(), ct).ConfigureAwait(false);
        }

        foreach (SyncdMutation m in patch.Mutations)
        {
            if (m.Record?.KeyId is { HasId: true } && m.Record.KeyId.Id.Length > 0)
            {
                await CacheKeyAsync(map, m.Record.KeyId.Id.ToByteArray(), ct).ConfigureAwait(false);
            }
        }
        return map;
    }

    private async Task CacheKeyAsync(Dictionary<string, ExpandedAppStateKeys> map, byte[] keyId, CancellationToken ct)
    {
        string hex = Convert.ToHexStringLower(keyId);
        if (!map.ContainsKey(hex))
        {
            map[hex] = await GetOrLoadKeysAsync(keyId, ct).ConfigureAwait(false);
        }
    }

    private async Task<ExpandedAppStateKeys> GetOrLoadKeysAsync(byte[] keyId, CancellationToken ct)
    {
        string hex = Convert.ToHexStringLower(keyId);
        if (_expandedKeyCache.TryGetValue(hex, out ExpandedAppStateKeys? cached))
        {
            return cached;
        }

        byte[] raw = await _backend.AppSync.GetSyncKeyAsync(keyId, ct).ConfigureAwait(false)
            ?? throw new AppStateException(AppStateErrorKind.KeyNotFound, $"Missing AppState sync key '{hex}'.");

        ExpandedAppStateKeys expanded = AppStateKeys.Expand(raw);
        _expandedKeyCache[hex] = expanded;
        return expanded;
    }

    private static ExpandedAppStateKeys ResolveCachedKey(Dictionary<string, ExpandedAppStateKeys> map, byte[] keyId)
    {
        string hex = Convert.ToHexStringLower(keyId);
        return map.TryGetValue(hex, out ExpandedAppStateKeys? keys)
            ? keys
            : throw new AppStateException(AppStateErrorKind.KeyNotFound, $"Missing AppState sync key '{hex}'.");
    }

    private async Task<Dictionary<string, byte[]>> PrefetchPreviousValueMacsAsync(
        string collection,
        SyncdPatch patch,
        CancellationToken ct)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (SyncdMutation m in patch.Mutations)
        {
            if (m.Record?.Index is not { HasBlob: true } idx)
            {
                continue;
            }

            byte[] idxBytes = idx.Blob.ToByteArray();
            string hex = Convert.ToHexStringLower(idxBytes);
            if (result.ContainsKey(hex))
            {
                continue;
            }

            byte[]? valueMac = await _backend.AppSync.GetMutationMacAsync(collection, idxBytes, ct).ConfigureAwait(false);
            if (valueMac is not null)
            {
                result[hex] = valueMac;
            }
        }
        return result;
    }

    private SemaphoreSlim GetCollectionLock(string collection) =>
        _collectionLocks.GetOrAdd(collection, static _ => new SemaphoreSlim(1, 1));

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _sendLock.Dispose();
            foreach (SemaphoreSlim gate in _collectionLocks.Values)
            {
                gate.Dispose();
            }
        }
        return ValueTask.CompletedTask;
    }
}
