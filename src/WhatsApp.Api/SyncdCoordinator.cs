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

public sealed record SyncdCoexOptions(
    bool EnableCoexV2 = false,
    bool PersistKeysOnlyAfterServerAck = false,
    bool BatchedOrphanApply = false);

/// <summary>
/// Orchestrates AppState (<c>syncd</c>) inbound collection sync (external snapshots, external patch
/// mutations, pagination, anti-tampering validation, <c>PushNameSetting</c> propagation) and
/// serialized outbound mutation sends with <c>409</c> conflict absorption and rebuild.
/// Supports Syncd Coex V2 (<c>PersistKeysOnlyAfterServerAck</c>, <c>BatchedOrphanApply</c>).
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
    private readonly SyncdCoexOptions _coexOptions;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _collectionLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AppStateCollectionState> _collectionStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExpandedAppStateKeys> _expandedKeyCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _pendingKeysAwaitingAck = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<(string Collection, SyncdPatch Patch)>> _orphanPatches = new(StringComparer.Ordinal);

    private long _fallbackIqSeq;
    private int _disposed;

    public SyncdCoordinator(
        IWhatsAppBackend backend,
        IHttpClient httpClient,
        IqClient iqClient,
        Func<WhatsAppEvent, ValueTask>? emitEventAsync = null,
        string? mediaHost = null,
        SyncdCoexOptions? coexOptions = null)
        : this(
            backend,
            httpClient,
            (node, token) => new ValueTask<Node>((iqClient ?? throw new ArgumentNullException(nameof(iqClient))).SendIqAsync(node, IqClient.DefaultTimeout, token)),
            () => iqClient.NextRequestId(),
            emitEventAsync,
            mediaHost,
            coexOptions)
    {
    }

    public SyncdCoordinator(
        IWhatsAppBackend backend,
        IHttpClient httpClient,
        Func<Node, CancellationToken, ValueTask<Node>> sendIqAsync,
        Func<string>? nextIqId = null,
        Func<WhatsAppEvent, ValueTask>? emitEventAsync = null,
        string? mediaHost = null,
        SyncdCoexOptions? coexOptions = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _sendIqAsync = sendIqAsync ?? throw new ArgumentNullException(nameof(sendIqAsync));
        _nextIqId = nextIqId ?? (() => $"syncd-{Interlocked.Increment(ref _fallbackIqSeq)}");
        _emitEventAsync = emitEventAsync ?? (_ => ValueTask.CompletedTask);
        _mediaHost = string.IsNullOrWhiteSpace(mediaHost) ? DefaultMediaHost : mediaHost.Trim();
        _coexOptions = coexOptions ?? new SyncdCoexOptions();
    }

    public int PendingOrphanCount => _orphanPatches.Values.Sum(static l => { lock (l) { return l.Count; } });
    public int PendingKeyAckCount => _pendingKeysAwaitingAck.Count;

    public async Task StageSyncKeyAsync(byte[] keyId, byte[] keyData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        ArgumentNullException.ThrowIfNull(keyData);
        if (_coexOptions.PersistKeysOnlyAfterServerAck)
        {
            _pendingKeysAwaitingAck[Convert.ToHexStringLower(keyId)] = (byte[])keyData.Clone();
            return;
        }
        await _backend.AppSync.SaveSyncKeyAsync(keyId, keyData, ct).ConfigureAwait(false);
    }

    public async Task CommitPendingSyncKeyAckAsync(byte[] keyId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        if (_pendingKeysAwaitingAck.TryRemove(Convert.ToHexStringLower(keyId), out byte[]? keyData))
        {
            await _backend.AppSync.SaveSyncKeyAsync(keyId, keyData, ct).ConfigureAwait(false);
            await FlushOrphanPatchesAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<int> FlushOrphanPatchesAsync(CancellationToken ct = default)
    {
        int applied = 0;
        foreach (string keyHex in _orphanPatches.Keys.ToArray())
        {
            byte[] keyId = Convert.FromHexString(keyHex);
            byte[]? keyBytes = await _backend.AppSync.GetSyncKeyAsync(keyId, ct).ConfigureAwait(false);
            if (keyBytes is null)
            {
                continue;
            }

            if (!_orphanPatches.TryRemove(keyHex, out List<(string Collection, SyncdPatch Patch)>? batch))
            {
                continue;
            }

            foreach (IGrouping<string, SyncdPatch> grp in batch.GroupBy(static x => x.Collection, static x => x.Patch, StringComparer.Ordinal))
            {
                var list = new AppStateCollectionPatchList(grp.Key, false, grp.ToArray(), null, null);
                var (decoded, _) = await ApplyCollectionPatchListAsync(list, validateMacs: true, ct).ConfigureAwait(false);
                applied += decoded.Count;
            }
        }
        return applied;
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
            ProcessedPatchResult patchResult;
            try
            {
                var keyLookup = await PrefetchPatchKeysAsync(patch, ct).ConfigureAwait(false);
                var prevMacLookup = await PrefetchPreviousValueMacsAsync(collection, patch, ct).ConfigureAwait(false);

                patchResult = AppStateProcessor.ProcessPatch(
                    patch,
                    state,
                    id => ResolveCachedKey(keyLookup, id),
                    idx => prevMacLookup.GetValueOrDefault(Convert.ToHexStringLower(idx)),
                    collection,
                    validateMacs);
            }
            catch (AppStateException ex) when (_coexOptions.BatchedOrphanApply && ex.Kind == AppStateErrorKind.KeyNotFound && patch.KeyId is { HasId: true })
            {
                string keyHex = Convert.ToHexStringLower(patch.KeyId.Id.Span);
                List<(string Collection, SyncdPatch Patch)> bucket = _orphanPatches.GetOrAdd(keyHex, static _ => []);
                lock (bucket)
                {
                    bucket.Add((collection, patch));
                }
                continue;
            }

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
        DeviceCredentials? cachedCreds = null;
        bool credsLoaded = false;
        async ValueTask<DeviceCredentials?> GetCredsAsync()
        {
            if (!credsLoaded)
            {
                cachedCreds = await _backend.Device.LoadAsync(ct).ConfigureAwait(false);
                credsLoaded = true;
            }
            return cachedCreds;
        }
        foreach (DecodedAppStateMutation mutation in mutations)
        {
            if (mutation.Operation == SyncdMutation.Types.SyncdOperation.Remove)
            {
                long removeTs = mutation.ActionValue?.HasTimestamp == true ? mutation.ActionValue.Timestamp : 0;
                if (mutation.Index is ["contact", { Length: > 0 } removedJid, ..])
                {
                    await _emitEventAsync(new ContactRemovedEvent(removedJid, removeTs)).ConfigureAwait(false);
                }
                else if (mutation.Index is ["label_sublist", { Length: > 0 } pidStr, { Length: > 0 } sublistChatJid, ..]
                    && int.TryParse(pidStr, out int pid))
                {
                    await _emitEventAsync(new LabelSublistUpdatedEvent(pid, sublistChatJid, Removed: true, SubListId: null, removeTs))
                        .ConfigureAwait(false);
                }
                continue;
            }

            if (mutation.Operation != SyncdMutation.Types.SyncdOperation.Set || mutation.ActionValue is null)
            {
                continue;
            }

            SyncActionValue av = mutation.ActionValue;
            long ts = av.HasTimestamp ? av.Timestamp : 0;

            if (av.PushNameSetting is { HasName: true } pushSetting
                && !string.IsNullOrEmpty(pushSetting.Name))
            {
                latestPushName = pushSetting.Name;
            }

            if (av.StatusPrivacy is { } sp)
            {
                await _emitEventAsync(new StatusPrivacyUpdatedEvent(
                    sp.HasMode ? (int)sp.Mode : null,
                    sp.UserJid.ToArray(),
                    sp.HasShareToFb && sp.ShareToFb,
                    sp.HasShareToIg && sp.ShareToIg)).ConfigureAwait(false);
            }

            if (av.UserStatusMuteAction is { } usm && mutation.Index.Count >= 2)
            {
                await _emitEventAsync(new UserStatusMuteUpdatedEvent(
                    mutation.Index[1],
                    usm.HasMuted && usm.Muted)).ConfigureAwait(false);
            }

            if (av.PrivacySettingDisableLinkPreviewsAction is { HasIsPreviewsDisabled: true } lp)
            {
                await _emitEventAsync(new LinkPreviewsDisabledUpdatedEvent(lp.IsPreviewsDisabled, ts)).ConfigureAwait(false);
            }

            if (av.UnarchiveChatsSetting is { HasUnarchiveChats: true } ua)
            {
                await _emitEventAsync(new UnarchiveChatsSettingUpdatedEvent(ua.UnarchiveChats, ts)).ConfigureAwait(false);
            }

            if (av.PrivacySettingChannelsPersonalisedRecommendationAction is { HasIsUserOptedOut: true } chRec)
            {
                await _emitEventAsync(new ChannelsPersonalisedRecUpdatedEvent(chRec.IsUserOptedOut, ts)).ConfigureAwait(false);
            }

            if (av.PinAction is { HasPinned: true } pin && mutation.Index.Count >= 2)
            {
                await _emitEventAsync(new PinUpdatedEvent(mutation.Index[1], pin.Pinned, ts)).ConfigureAwait(false);
            }

            if (av.MuteAction is { HasMuted: true } mute && mutation.Index.Count >= 2)
            {
                long? endTs = mute.HasMuteEndTimestamp ? mute.MuteEndTimestamp : null;
                await _emitEventAsync(new MuteUpdatedEvent(mutation.Index[1], mute.Muted, endTs, ts)).ConfigureAwait(false);
            }

            if (av.ArchiveChatAction is { HasArchived: true } arc && mutation.Index.Count >= 2)
            {
                await _emitEventAsync(new ArchiveUpdatedEvent(mutation.Index[1], arc.Archived, ts)).ConfigureAwait(false);
            }

            if (av.MarkChatAsReadAction is { HasRead: true } mcr && mutation.Index.Count >= 2)
            {
                await _emitEventAsync(new MarkChatAsReadUpdatedEvent(mutation.Index[1], mcr.Read, ts)).ConfigureAwait(false);
            }

            if (av.LockChatAction is { HasLocked: true } lck && mutation.Index.Count >= 2)
            {
                await _emitEventAsync(new LockChatUpdatedEvent(mutation.Index[1], lck.Locked, ts)).ConfigureAwait(false);
            }

            if (av.StarAction is { HasStarred: true } star && mutation.Index.Count >= 4)
            {
                bool fromMe = string.Equals(mutation.Index[3], "1", StringComparison.Ordinal);
                await _emitEventAsync(new StarUpdatedEvent(mutation.Index[1], mutation.Index[2], fromMe, star.Starred, ts)).ConfigureAwait(false);
            }

            if (av.DeleteMessageForMeAction is { } dmfm && mutation.Index.Count >= 4)
            {
                bool fromMe = string.Equals(mutation.Index[3], "1", StringComparison.Ordinal);
                await _emitEventAsync(new DeleteMessageForMeUpdatedEvent(
                    mutation.Index[1],
                    mutation.Index[2],
                    fromMe,
                    dmfm.HasDeleteMedia && dmfm.DeleteMedia,
                    ts)).ConfigureAwait(false);
            }

            if (av.ClearChatAction is not null && mutation.Index.Count >= 4 && mutation.Index[0] == "clearChat")
            {
                bool delStarred = string.Equals(mutation.Index[2], "1", StringComparison.Ordinal);
                bool delMedia = string.Equals(mutation.Index[3], "1", StringComparison.Ordinal);
                await _emitEventAsync(new ClearChatUpdatedEvent(mutation.Index[1], delStarred, delMedia, ts)).ConfigureAwait(false);
            }

            if (av.DeleteChatAction is not null && mutation.Index.Count >= 3 && mutation.Index[0] == "deleteChat")
            {
                bool delMedia = string.Equals(mutation.Index[2], "1", StringComparison.Ordinal);
                await _emitEventAsync(new DeleteChatUpdatedEvent(mutation.Index[1], delMedia, ts)).ConfigureAwait(false);
            }

            if (av.FavoritesAction is { } favs)
            {
                string[] ids = favs.Favorites
                    .Where(f => f.HasId && !string.IsNullOrEmpty(f.Id))
                    .Select(f => f.Id)
                    .ToArray();
                await _emitEventAsync(new FavoritesUpdatedEvent(ids, ts)).ConfigureAwait(false);
            }

            if (av.ContactAction is { } contact && mutation.Index.Count >= 2)
            {
                await _emitEventAsync(new ContactUpdatedEvent(
                    mutation.Index[1],
                    IsLid: false,
                    contact.HasFullName ? contact.FullName : null,
                    contact.HasFirstName ? contact.FirstName : null,
                    contact.HasUsername ? contact.Username : null,
                    ts)).ConfigureAwait(false);
            }

            if (av.LidContactAction is { } lidContact && mutation.Index.Count >= 2)
            {
                await _emitEventAsync(new ContactUpdatedEvent(
                    mutation.Index[1],
                    IsLid: true,
                    lidContact.HasFullName ? lidContact.FullName : null,
                    lidContact.HasFirstName ? lidContact.FirstName : null,
                    lidContact.HasUsername ? lidContact.Username : null,
                    ts)).ConfigureAwait(false);
            }

            if (av.StickerAction is { HasIsFavorite: true } stk && mutation.Index.Count >= 2)
            {
                await _emitEventAsync(new FavoriteStickerUpdatedEvent(
                    mutation.Index[1],
                    stk.IsFavorite,
                    stk.HasDirectPath ? stk.DirectPath : null,
                    stk.HasMimetype ? stk.Mimetype : null,
                    ts)).ConfigureAwait(false);
            }

            if (mutation.Index.Count >= 2 && mutation.Index[0] == "removeRecentSticker")
            {
                long? lastSent = av.RemoveRecentStickerAction?.HasLastStickerSentTs == true
                    ? av.RemoveRecentStickerAction.LastStickerSentTs
                    : null;
                await _emitEventAsync(new RemoveRecentStickerUpdatedEvent(mutation.Index[1], lastSent, ts)).ConfigureAwait(false);
            }

            if (av.LabelEditAction is { } lea && mutation.Index is ["label_edit", { Length: > 0 } editLabelId, ..])
            {
                await _emitEventAsync(new LabelEditUpdatedEvent(
                    editLabelId,
                    lea.HasName ? lea.Name : null,
                    lea.HasColor ? lea.Color : null,
                    lea.HasDeleted && lea.Deleted,
                    lea.HasPredefinedId ? lea.PredefinedId : null,
                    lea.HasOrderIndex ? lea.OrderIndex : null,
                    lea.HasIsActive ? lea.IsActive : null,
                    lea.HasType ? (int)lea.Type : null,
                    ts)).ConfigureAwait(false);
            }

            if (av.LabelAssociationAction is { } laa)
            {
                bool labeled = laa.HasLabeled && laa.Labeled;
                if (mutation.Index is ["label_jid", { Length: > 0 } lJidId, { Length: > 0 } lChatJid, ..])
                {
                    await _emitEventAsync(new LabelAssociationUpdatedEvent(lJidId, lChatJid, labeled, ts)).ConfigureAwait(false);
                }
                else if (mutation.Index is ["label_message", { Length: > 0 } lMsgLabelId, { Length: > 0 } lMsgChat, { Length: > 0 } lMsgId, ..])
                {
                    bool fromMe = mutation.Index.Count >= 5 && string.Equals(mutation.Index[4], "1", StringComparison.Ordinal);
                    await _emitEventAsync(new MessageLabelAssociationUpdatedEvent(lMsgLabelId, lMsgChat, lMsgId, fromMe, labeled, ts))
                        .ConfigureAwait(false);
                }
            }

            if (av.LabelReorderingAction is { } lra && mutation.Index is ["label_reordering", ..])
            {
                await _emitEventAsync(new LabelReorderingUpdatedEvent(lra.SortedLabelIds.ToArray(), ts)).ConfigureAwait(false);
            }

            if (av.LabelSublistAction is { HasSubListId: true } lsa
                && mutation.Index is ["label_sublist", { Length: > 0 } setPidStr, { Length: > 0 } setSubChat, ..]
                && int.TryParse(setPidStr, out int setPid))
            {
                await _emitEventAsync(new LabelSublistUpdatedEvent(setPid, setSubChat, Removed: false, lsa.SubListId, ts))
                    .ConfigureAwait(false);
            }

            if (av.QuickReplyAction is { } qra && mutation.Index is ["quick_reply", { Length: > 0 } qrId, ..])
            {
                await _emitEventAsync(new QuickReplyUpdatedEvent(
                    qrId,
                    qra.HasShortcut ? qra.Shortcut : string.Empty,
                    qra.HasMessage ? qra.Message : string.Empty,
                    qra.Keywords.ToArray(),
                    qra.HasCount ? qra.Count : 0,
                    qra.HasDeleted && qra.Deleted,
                    ts)).ConfigureAwait(false);
            }

            if (av.PrivacySettingRelayAllCalls is { HasIsEnabled: true } rac)
            {
                await _emitEventAsync(new RelayAllCallsUpdatedEvent(rac.IsEnabled, ts)).ConfigureAwait(false);
            }

            if (av.CallLogAction?.CallLogRecord is { } clr
                && mutation.Index is ["call_log", { Length: > 0 } callCreatorStr, { Length: > 0 } callId, ..])
            {
                DeviceCredentials? ownCreds = await GetCredsAsync().ConfigureAwait(false);
                bool fromMe = IsOwnCreatorJid(ownCreds, callCreatorStr);
                await _emitEventAsync(new CallLogSyncedEvent(
                    callCreatorStr,
                    callId,
                    fromMe,
                    ts,
                    clr.Clone(),
                    FromFullSync: false)).ConfigureAwait(false);
            }
        }

        if (latestPushName is null)
        {
            return;
        }

        DeviceCredentials? creds = await GetCredsAsync().ConfigureAwait(false);
        if (creds is not null && !string.Equals(creds.PushName, latestPushName, StringComparison.Ordinal))
        {
            creds.PushName = latestPushName;
            await _backend.Device.SaveAsync(creds, ct).ConfigureAwait(false);
        }
    }

    private static bool IsOwnCreatorJid(DeviceCredentials? creds, string rawCreator)
    {
        if (creds is null || !Jid.TryParse(rawCreator, out Jid creator))
        {
            return false;
        }
        return (creds.Pn is Jid pn && string.Equals(pn.User, creator.User, StringComparison.Ordinal))
            || (creds.Lid is Jid lid && string.Equals(lid.User, creator.User, StringComparison.Ordinal));
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
