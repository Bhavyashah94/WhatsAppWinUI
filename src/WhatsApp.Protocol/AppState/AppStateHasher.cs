using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Whatsapp;

namespace WhatsApp.Protocol.AppState;

public sealed class AppStateException : Exception
{
    public AppStateErrorKind Kind { get; }

    public AppStateException(AppStateErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }
}

public enum AppStateErrorKind
{
    MissingKeyId,
    KeyNotFound,
    MissingValueBlob,
    ValueBlobTooShort,
    MissingIndexMac,
    MismatchingContentMac,
    MismatchingIndexMac,
    SnapshotMacMissing,
    SnapshotMacMismatch,
    PatchMacMismatch,
    PatchVersionMismatch,
    UnsupportedOperation,
    DuplicateIndexInPatch,
    DecryptionFailed,
    DecodeFailed,
    ExternalBlobDownloadFailed
}

public readonly record struct AppStateFoldReport(int Folded, int Unkeyed, int Valueless);

/// <summary>
/// Cryptographic MAC generators, <c>ltHash</c> accumulators, and WA Web–compatible
/// wire helpers for AppState (<c>syncd</c>) snapshots and patches.
/// Mirrors <c>wacore/appstate/src/hash.rs</c> and <c>WAWebSyncdAntiTampering</c>.
/// </summary>
public static class AppStateHasher
{
    /// <summary>
    /// Extracts the trailing MAC slice matching JS <c>slice(len - 32)</c>
    /// (including double-negative wrap semantics on malformed short blobs).
    /// </summary>
    public static int GetValueMacStartOffset(int blobLength) =>
        blobLength >= 32 ? blobLength - 32 : Math.Max(0, (2 * blobLength) - 32);

    public static byte[] GetValueMacTail(ReadOnlySpan<byte> blob) =>
        blob[GetValueMacStartOffset(blob.Length)..].ToArray();

    /// <summary>
    /// Encodes a collection version matching WA Web <c>to64BitNetworkOrder</c>:
    /// an 8-byte buffer with the low 32 bits big-endian at bytes <c>[4..8]</c>.
    /// </summary>
    public static byte[] EncodeVersionNetworkOrder(ulong version)
    {
        byte[] buf = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), unchecked((uint)version));
        return buf;
    }

    /// <summary>
    /// Computes the 32-byte truncated HMAC-SHA512 content MAC over
    /// <c>[op + 1] || keyId || data || [0..0, (keyId.Length + 1) &amp; 0xff]</c>.
    /// </summary>
    public static byte[] GenerateContentMac(
        SyncdMutation.Types.SyncdOperation operation,
        ReadOnlySpan<byte> ivAndCiphertext,
        ReadOnlySpan<byte> keyId,
        ReadOnlySpan<byte> valueMacKey)
    {
        byte[] opByte = [(byte)((int)operation + 1)];
        byte[] lenTrailer = new byte[8];
        lenTrailer[7] = (byte)((keyId.Length + 1) & 0xff);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA512, valueMacKey);
        hmac.AppendData(opByte);
        hmac.AppendData(keyId);
        hmac.AppendData(ivAndCiphertext);
        hmac.AppendData(lenTrailer);

        Span<byte> full = stackalloc byte[64];
        hmac.GetHashAndReset(full);
        return full[..32].ToArray();
    }

    public static byte[] GenerateIndexMac(ReadOnlySpan<byte> indexJsonUtf8, ReadOnlySpan<byte> indexKey) =>
        HMACSHA256.HashData(indexKey, indexJsonUtf8);

    public static byte[] GenerateSnapshotMac(
        ReadOnlySpan<byte> ltHash128,
        ulong version,
        string collectionName,
        ReadOnlySpan<byte> snapshotMacKey)
    {
        byte[] versionBe = EncodeVersionNetworkOrder(version);
        byte[] nameBytes = Encoding.UTF8.GetBytes(collectionName);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, snapshotMacKey);
        hmac.AppendData(ltHash128);
        hmac.AppendData(versionBe);
        hmac.AppendData(nameBytes);
        return hmac.GetHashAndReset();
    }

    public static byte[] GeneratePatchMac(
        SyncdPatch patch,
        string collectionName,
        ReadOnlySpan<byte> patchMacKey,
        ulong version)
    {
        ArgumentNullException.ThrowIfNull(patch);
        byte[] versionBe = EncodeVersionNetworkOrder(version);
        byte[] nameBytes = Encoding.UTF8.GetBytes(collectionName);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, patchMacKey);
        if (patch.HasSnapshotMac && patch.SnapshotMac.Length > 0)
        {
            hmac.AppendData(patch.SnapshotMac.Span);
        }

        foreach (SyncdMutation mutation in patch.Mutations)
        {
            if (mutation.Record?.Value is { HasBlob: true } val)
            {
                ReadOnlySpan<byte> blob = val.Blob.Span;
                hmac.AppendData(blob[GetValueMacStartOffset(blob.Length)..]);
            }
        }

        hmac.AppendData(versionBe);
        hmac.AppendData(nameBytes);
        return hmac.GetHashAndReset();
    }

    /// <summary>
    /// Folds snapshot records keyed by index MAC (keeping only the last record per index)
    /// into <paramref name="ltHash128"/>.
    /// </summary>
    public static (AppStateFoldReport Report, IReadOnlyList<SyncdRecord> Winners) UpdateHashFromRecords(
        Span<byte> ltHash128,
        IReadOnlyList<SyncdRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var indexed = new List<(string KeyHex, SyncdRecord Record, byte[] ValueMac)>(records.Count);
        int unkeyed = 0;
        int valueless = 0;

        for (int i = 0; i < records.Count; i++)
        {
            SyncdRecord rec = records[i];
            if (rec.Value is not { HasBlob: true })
            {
                valueless++;
                continue;
            }

            byte[] indexBytes = rec.Index?.HasBlob == true ? rec.Index.Blob.ToByteArray() : [];
            if (indexBytes.Length == 0)
            {
                unkeyed++;
            }

            byte[] valueMac = GetValueMacTail(rec.Value.Blob.Span);
            string keyHex = Convert.ToHexStringLower(indexBytes);
            indexed.Add((keyHex, rec, valueMac));
        }

        var winnersMap = new Dictionary<string, (SyncdRecord Record, byte[] ValueMac)>(StringComparer.Ordinal);
        var winnerOrder = new List<string>();
        foreach (var item in indexed)
        {
            if (!winnersMap.ContainsKey(item.KeyHex))
            {
                winnerOrder.Add(item.KeyHex);
            }
            // Overwrite while preserving key appearance; WA Web Map iterates in insertion order with replaced value.
            winnersMap[item.KeyHex] = (item.Record, item.ValueMac);
        }

        var addItems = new List<byte[]>(winnerOrder.Count);
        var winnerRecords = new List<SyncdRecord>(winnerOrder.Count);
        foreach (string key in winnerOrder)
        {
            var entry = winnersMap[key];
            addItems.Add(entry.ValueMac);
            winnerRecords.Add(entry.Record);
        }

        LtHash.SubtractThenAddInPlace(ltHash128, subtractItems: null, addItems: addItems);
        return (new AppStateFoldReport(addItems.Count, unkeyed, valueless), winnerRecords);
    }

    /// <summary>
    /// Updates <paramref name="ltHash128"/> incrementally across a patch's mutations,
    /// mirroring WA Web's <c>indexMode</c> SET+REMOVE double-subtraction guard.
    /// </summary>
    public static bool UpdateHash(
        Span<byte> ltHash128,
        IReadOnlyList<SyncdMutation> mutations,
        Func<byte[], int, byte[]?> getPrevSetValueMac)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(getPrevSetValueMac);

        bool indexMode = mutations.All(m => m.Record?.Index?.HasBlob == true);
        HashSet<string>? removedInPatch = null;
        if (indexMode)
        {
            removedInPatch = new HashSet<string>(StringComparer.Ordinal);
            foreach (SyncdMutation m in mutations)
            {
                if (GetKnownOperation(m) == SyncdMutation.Types.SyncdOperation.Remove
                    && m.Record?.Index?.HasBlob == true)
                {
                    removedInPatch.Add(Convert.ToHexStringLower(m.Record.Index.Blob.Span));
                }
            }
        }

        var added = new List<byte[]>(mutations.Count);
        var removed = new List<byte[]>(mutations.Count);
        bool hasMissingRemove = false;

        for (int i = 0; i < mutations.Count; i++)
        {
            SyncdMutation m = mutations[i];
            var op = GetKnownOperation(m);
            bool isSet = op == SyncdMutation.Types.SyncdOperation.Set;

            if (isSet && m.Record?.Value is { HasBlob: true } val)
            {
                added.Add(GetValueMacTail(val.Blob.Span));
            }

            if (m.Record?.Index is { HasBlob: true } idx)
            {
                byte[] idxBytes = idx.Blob.ToByteArray();
                if (isSet && removedInPatch is not null && removedInPatch.Contains(Convert.ToHexStringLower(idxBytes)))
                {
                    continue;
                }

                byte[]? prev = getPrevSetValueMac(idxBytes, i);
                if (prev is not null)
                {
                    removed.Add(prev);
                }
                else if (op == SyncdMutation.Types.SyncdOperation.Remove)
                {
                    hasMissingRemove = true;
                }
            }
        }

        LtHash.SubtractThenAddInPlace(ltHash128, removed, added);
        return hasMissingRemove;
    }

    public static SyncdMutation.Types.SyncdOperation GetKnownOperation(SyncdMutation mutation)
    {
        if (!mutation.HasOperation)
        {
            return SyncdMutation.Types.SyncdOperation.Set;
        }

        return mutation.Operation switch
        {
            SyncdMutation.Types.SyncdOperation.Set => SyncdMutation.Types.SyncdOperation.Set,
            SyncdMutation.Types.SyncdOperation.Remove => SyncdMutation.Types.SyncdOperation.Remove,
            _ => throw new AppStateException(
                AppStateErrorKind.UnsupportedOperation,
                $"Unsupported SyncdOperation: {(int)mutation.Operation}")
        };
    }
}
