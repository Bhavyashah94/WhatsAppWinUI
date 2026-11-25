using System.Security.Cryptography;
using Whatsapp;

namespace WhatsApp.Protocol.AppState;

public sealed class AppStateCollectionState
{
    public ulong Version { get; set; }
    public byte[] Hash { get; set; } = LtHash.CreateEmpty();
    public bool MacMismatchFatal { get; set; }
    public bool Bootstrapped { get; set; }

    public bool HasBaseline => Bootstrapped || Version > 0;
    public bool IsEmptyHash => Hash.Length == LtHash.HashSizeBytes && Hash.AsSpan().IndexOfAnyExcept((byte)0) < 0;

    public AppStateCollectionState Clone() => new()
    {
        Version = Version,
        Hash = (byte[])Hash.Clone(),
        MacMismatchFatal = MacMismatchFatal,
        Bootstrapped = Bootstrapped
    };
}

public sealed record ProcessedSnapshotResult(
    AppStateCollectionState State,
    IReadOnlyList<DecodedAppStateMutation> Mutations,
    IReadOnlyList<AppStateMutationMacs> AddedMacs);

public sealed record ProcessedPatchResult(
    AppStateCollectionState State,
    IReadOnlyList<DecodedAppStateMutation> Mutations,
    IReadOnlyList<AppStateMutationMacs> AddedMacs,
    IReadOnlyList<byte[]> RemovedIndexMacs,
    bool SnapshotMacDiverged);

/// <summary>
/// Pure sans-I/O AppState snapshot &amp; patch verifier and decoder.
/// Validates anti-tampering <c>patchMac</c>, <c>snapshotMac</c>, duplicate-index rules,
/// strict monotonic versions, content MACs, and index MACs matching <c>wacore/appstate/src/processor.rs</c>.
/// </summary>
public static class AppStateProcessor
{
    public static ProcessedSnapshotResult ProcessSnapshot(
        SyncdSnapshot snapshot,
        AppStateCollectionState initialState,
        Func<byte[], ExpandedAppStateKeys> getKeys,
        string collectionName,
        bool validateMacs = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(getKeys);
        ArgumentException.ThrowIfNullOrEmpty(collectionName);

        AppStateCollectionState state = initialState.Clone();
        // A snapshot always rebuilds ltHash from zero regardless of any prior local state.
        state.Hash = LtHash.CreateEmpty();

        ulong version = snapshot.Version?.HasVersion == true ? snapshot.Version.Version : 0UL;
        state.Version = version;

        var (foldReport, winners) = AppStateHasher.UpdateHashFromRecords(state.Hash, snapshot.Records);
        if (foldReport.Valueless > 0)
        {
            throw new AppStateException(
                AppStateErrorKind.MissingValueBlob,
                $"Snapshot '{collectionName}' v{version} has {foldReport.Valueless} record(s) without value blob.");
        }

        if (validateMacs)
        {
            if (!snapshot.HasMac || snapshot.KeyId is not { HasId: true } || snapshot.KeyId.Id.Length == 0)
            {
                throw new AppStateException(
                    AppStateErrorKind.SnapshotMacMissing,
                    $"Snapshot '{collectionName}' v{version} is missing MAC or KeyId.");
            }

            ExpandedAppStateKeys snapKeys = getKeys(snapshot.KeyId.Id.ToByteArray());
            byte[] computed = AppStateHasher.GenerateSnapshotMac(
                state.Hash,
                version,
                collectionName,
                snapKeys.SnapshotMacKey);

            if (!CryptographicOperations.FixedTimeEquals(computed, snapshot.Mac.Span))
            {
                throw new AppStateException(
                    AppStateErrorKind.SnapshotMacMismatch,
                    $"Snapshot '{collectionName}' v{version} MAC mismatch.");
            }
        }

        var mutations = new List<DecodedAppStateMutation>(winners.Count);
        var addedMacs = new List<AppStateMutationMacs>(winners.Count);

        foreach (SyncdRecord rec in winners)
        {
            if (rec.KeyId is not { HasId: true } || rec.KeyId.Id.Length == 0)
            {
                throw new AppStateException(AppStateErrorKind.MissingKeyId, "Snapshot record missing KeyId.");
            }

            byte[] keyId = rec.KeyId.Id.ToByteArray();
            ExpandedAppStateKeys keys = getKeys(keyId);

            var (decoded, macs) = AppStateMutationCodec.DecodeRecord(
                SyncdMutation.Types.SyncdOperation.Set,
                rec,
                keys,
                keyId,
                validateMacs);

            mutations.Add(decoded);
            addedMacs.Add(macs);
        }

        // Snapshot rebuilds ltHash from scratch; clear any prior divergence flag and mark bootstrapped.
        state.MacMismatchFatal = false;
        state.Bootstrapped = true;
        return new ProcessedSnapshotResult(state, mutations, addedMacs);
    }

    public static ProcessedPatchResult ProcessPatch(
        SyncdPatch patch,
        AppStateCollectionState initialState,
        Func<byte[], ExpandedAppStateKeys> getKeys,
        Func<byte[], byte[]?> getPrevValueMac,
        string collectionName,
        bool validateMacs = true)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(getKeys);
        ArgumentNullException.ThrowIfNull(getPrevValueMac);
        ArgumentException.ThrowIfNullOrEmpty(collectionName);

        AppStateCollectionState state = initialState.Clone();
        if (state.Hash.Length != LtHash.HashSizeBytes)
        {
            state.Hash = LtHash.CreateEmpty();
        }

        ulong originalVersion = state.Version;
        bool hadNoPriorState = originalVersion == 0 && state.IsEmptyHash;
        ulong patchVersion = patch.Version?.HasVersion == true ? patch.Version.Version : 0UL;
        ulong expectedVersion = originalVersion + 1UL;

        if (!hadNoPriorState && patchVersion != expectedVersion)
        {
            throw new AppStateException(
                AppStateErrorKind.PatchVersionMismatch,
                $"Expected '{collectionName}' patch version {expectedVersion}, got {patchVersion}.");
        }

        foreach (SyncdMutation m in patch.Mutations)
        {
            _ = AppStateHasher.GetKnownOperation(m);
        }

        if (validateMacs)
        {
            if (patch.KeyId is not { HasId: true } || patch.KeyId.Id.Length == 0)
            {
                throw new AppStateException(AppStateErrorKind.MissingKeyId, "Patch missing KeyId.");
            }
            if (!patch.HasPatchMac || patch.PatchMac.Length == 0)
            {
                throw new AppStateException(AppStateErrorKind.PatchMacMismatch, "Patch missing PatchMac.");
            }
            if (!patch.HasSnapshotMac || patch.SnapshotMac.Length == 0)
            {
                throw new AppStateException(AppStateErrorKind.SnapshotMacMissing, "Patch missing SnapshotMac.");
            }
        }

        state.Version = patchVersion;

        // Track SET-within-patch overwrites without feeding REMOVE (REMOVE resolves against store).
        var inPatchSet = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        bool hasMissingRemove = AppStateHasher.UpdateHash(state.Hash, patch.Mutations, (indexMac, idx) =>
        {
            SyncdMutation current = patch.Mutations[idx];
            bool isRemove = AppStateHasher.GetKnownOperation(current) == SyncdMutation.Types.SyncdOperation.Remove;
            string indexHex = Convert.ToHexStringLower(indexMac);

            byte[]? prev = !isRemove && inPatchSet.TryGetValue(indexHex, out byte[]? inPatchVal)
                ? inPatchVal
                : getPrevValueMac(indexMac);

            if (!isRemove && current.Record?.Value is { HasBlob: true } v)
            {
                inPatchSet[indexHex] = AppStateHasher.GetValueMacTail(v.Blob.Span);
            }

            return prev;
        });

        bool snapshotMacDiverged = false;
        if (validateMacs && patch.KeyId is { HasId: true })
        {
            ExpandedAppStateKeys patchKeys = getKeys(patch.KeyId.Id.ToByteArray());
            byte[] computedPatchMac = AppStateHasher.GeneratePatchMac(patch, collectionName, patchKeys.PatchMacKey, patchVersion);
            if (!CryptographicOperations.FixedTimeEquals(computedPatchMac, patch.PatchMac.Span))
            {
                throw new AppStateException(
                    AppStateErrorKind.PatchMacMismatch,
                    $"Patch '{collectionName}' v{patchVersion} patchMAC mismatch.");
            }

            if (!state.MacMismatchFatal && !hasMissingRemove)
            {
                byte[] computedSnapshotMac = AppStateHasher.GenerateSnapshotMac(
                    state.Hash,
                    patchVersion,
                    collectionName,
                    patchKeys.SnapshotMacKey);
                if (!CryptographicOperations.FixedTimeEquals(computedSnapshotMac, patch.SnapshotMac.Span))
                {
                    snapshotMacDiverged = true;
                    state.MacMismatchFatal = true;
                }
            }

            DetectDuplicateIndexInPatch(patch.Mutations);
        }

        var mutations = new List<DecodedAppStateMutation>(patch.Mutations.Count);
        var addedMacs = new List<AppStateMutationMacs>();
        var removedIndexMacs = new List<byte[]>();

        foreach (SyncdMutation m in patch.Mutations)
        {
            if (m.Record is null)
            {
                continue;
            }

            var op = AppStateHasher.GetKnownOperation(m);
            if (m.Record.KeyId is not { HasId: true } || m.Record.KeyId.Id.Length == 0)
            {
                throw new AppStateException(AppStateErrorKind.MissingKeyId, "Patch mutation record missing KeyId.");
            }

            byte[] keyId = m.Record.KeyId.Id.ToByteArray();
            ExpandedAppStateKeys keys = getKeys(keyId);
            var (decoded, macs) = AppStateMutationCodec.DecodeRecord(op, m.Record, keys, keyId, validateMacs);

            if (op == SyncdMutation.Types.SyncdOperation.Set)
            {
                addedMacs.Add(macs);
            }
            else
            {
                removedIndexMacs.Add(macs.IndexMac);
            }

            mutations.Add(decoded);
        }

        return new ProcessedPatchResult(state, mutations, addedMacs, removedIndexMacs, snapshotMacDiverged);
    }

    private static void DetectDuplicateIndexInPatch(IReadOnlyList<SyncdMutation> mutations)
    {
        var seenSet = new HashSet<string>(StringComparer.Ordinal);
        var seenRemove = new HashSet<string>(StringComparer.Ordinal);

        foreach (SyncdMutation m in mutations)
        {
            if (m.Record?.Index is not { HasBlob: true })
            {
                continue;
            }

            string hex = Convert.ToHexStringLower(m.Record.Index.Blob.Span);
            var op = AppStateHasher.GetKnownOperation(m);
            var target = op == SyncdMutation.Types.SyncdOperation.Set ? seenSet : seenRemove;

            if (!target.Add(hex))
            {
                throw new AppStateException(
                    AppStateErrorKind.DuplicateIndexInPatch,
                    $"Duplicate index MAC in patch for operation {op}.");
            }
        }
    }
}
