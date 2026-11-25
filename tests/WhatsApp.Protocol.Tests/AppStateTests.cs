using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class AppStateTests
{
    private static readonly byte[] SampleMasterKey = Enumerable.Range(1, 32).Select(static b => (byte)b).ToArray();
    private static readonly byte[] SampleKeyId = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];

    [TestMethod]
    public void LtHash_SubtractThenAdd_Wraps16BitLanesAndCancelsCleanly()
    {
        byte[] itemA = Encoding.UTF8.GetBytes("value-mac-alpha");
        byte[] itemB = Encoding.UTF8.GetBytes("value-mac-beta");

        byte[] baseHash = LtHash.CreateEmpty();
        byte[] afterAddA = LtHash.SubtractThenAdd(baseHash, subtractItems: null, addItems: [itemA]);
        Assert.AreEqual(LtHash.HashSizeBytes, afterAddA.Length);
        CollectionAssert.AreNotEqual(baseHash, afterAddA);

        byte[] afterRemoveA = LtHash.SubtractThenAdd(afterAddA, subtractItems: [itemA], addItems: null);
        CollectionAssert.AreEqual(baseHash, afterRemoveA);

        byte[] addAB = LtHash.SubtractThenAdd(baseHash, subtractItems: null, addItems: [itemA, itemB]);
        byte[] addThenUpdate = LtHash.SubtractThenAdd(afterAddA, subtractItems: null, addItems: [itemB]);
        CollectionAssert.AreEqual(addAB, addThenUpdate);

        // Verify explicit 16-bit LE arithmetic wraparound without carrying into bit 16
        byte[] nearMax = new byte[LtHash.HashSizeBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(nearMax.AsSpan(0, 2), 0xFFFF);
        byte[] laneDelta = new byte[LtHash.HashSizeBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(laneDelta.AsSpan(0, 2), 3);

        LtHash.ApplyPointwiseUint16(nearMax, laneDelta, subtract: false);
        Assert.AreEqual((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(nearMax.AsSpan(0, 2)));
        Assert.AreEqual((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(nearMax.AsSpan(2, 2)));

        LtHash.ApplyPointwiseUint16(nearMax, laneDelta, subtract: true);
        Assert.AreEqual((ushort)0xFFFF, BinaryPrimitives.ReadUInt16LittleEndian(nearMax.AsSpan(0, 2)));
    }

    [TestMethod]
    public void AppStateKeys_Expand_DerivesFive32ByteSubKeysAndValidatesInputLength()
    {
        ExpandedAppStateKeys keys = AppStateKeys.Expand(SampleMasterKey);
        Assert.AreEqual(32, keys.IndexKey.Length);
        Assert.AreEqual(32, keys.ValueEncryptionKey.Length);
        Assert.AreEqual(32, keys.ValueMacKey.Length);
        Assert.AreEqual(32, keys.SnapshotMacKey.Length);
        Assert.AreEqual(32, keys.PatchMacKey.Length);

        byte[] expected160 = new byte[160];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, SampleMasterKey, expected160, default, "WhatsApp Mutation Keys"u8);
        CollectionAssert.AreEqual(expected160[..32], keys.IndexKey);
        CollectionAssert.AreEqual(expected160[32..64], keys.ValueEncryptionKey);
        CollectionAssert.AreEqual(expected160[64..96], keys.ValueMacKey);
        CollectionAssert.AreEqual(expected160[96..128], keys.SnapshotMacKey);
        CollectionAssert.AreEqual(expected160[128..160], keys.PatchMacKey);
    }

    [TestMethod]
    public void AppStateHasher_EncodesVersion32BitWrapAndShortMacOffsets()
    {
        // High 32 bits must be dropped (matching JS bitwise shift `(e >>> 0)` / to64BitNetworkOrder)
        byte[] wrapped = AppStateHasher.EncodeVersionNetworkOrder(0x0000_0001_0000_0005UL);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 0, 0, 0, 5 }, wrapped);

        Assert.AreEqual(10, AppStateHasher.GetValueMacStartOffset(42));
        Assert.AreEqual(0, AppStateHasher.GetValueMacStartOffset(16));
        Assert.AreEqual(8, AppStateHasher.GetValueMacStartOffset(20));
    }

    [TestMethod]
    public void AppStateMutationCodec_EncodeDecode_RoundTripsAndDetectsTampering()
    {
        ExpandedAppStateKeys keys = AppStateKeys.Expand(SampleMasterKey);
        byte[] indexJson = Encoding.UTF8.GetBytes("[\"setting_pushName\"]");
        byte[] iv = Enumerable.Range(10, 16).Select(static i => (byte)i).ToArray();
        var value = new SyncActionValue
        {
            Timestamp = 1_700_000_000_000L,
            PushNameSetting = new SyncActionValue.Types.PushNameSetting { Name = "Alice" }
        };

        var (mutation, valueMac) = AppStateMutationCodec.EncodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            indexJson,
            value,
            keys,
            SampleKeyId,
            iv,
            actionVersion: 1);

        Assert.AreEqual(32, valueMac.Length);
        var (decoded, macs) = AppStateMutationCodec.DecodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            mutation.Record,
            keys,
            SampleKeyId,
            validateMacs: true);

        Assert.AreEqual(SyncdMutation.Types.SyncdOperation.Set, decoded.Operation);
        CollectionAssert.AreEqual(new[] { "setting_pushName" }, decoded.Index.ToArray());
        Assert.AreEqual("Alice", decoded.ActionValue?.PushNameSetting?.Name);
        CollectionAssert.AreEqual(valueMac, macs.ValueMac);

        // Corrupt content MAC -> MismatchingContentMac
        byte[] badVal = mutation.Record.Value.Blob.ToByteArray();
        badVal[^1] ^= 0xFF;
        SyncdRecord tamperedMac = mutation.Record.Clone();
        tamperedMac.Value.Blob = ByteString.CopyFrom(badVal);
        var exMac = Assert.ThrowsException<AppStateException>(() =>
            AppStateMutationCodec.DecodeRecord(SyncdMutation.Types.SyncdOperation.Set, tamperedMac, keys, SampleKeyId));
        Assert.AreEqual(AppStateErrorKind.MismatchingContentMac, exMac.Kind);

        // Corrupt index MAC (with matching content MAC? Test mismatched index blob directly)
        SyncdRecord tamperedIndex = mutation.Record.Clone();
        byte[] badIdx = mutation.Record.Index.Blob.ToByteArray();
        badIdx[0] ^= 0x01;
        tamperedIndex.Index.Blob = ByteString.CopyFrom(badIdx);
        var exIdx = Assert.ThrowsException<AppStateException>(() =>
            AppStateMutationCodec.DecodeRecord(SyncdMutation.Types.SyncdOperation.Set, tamperedIndex, keys, SampleKeyId));
        Assert.AreEqual(AppStateErrorKind.MismatchingIndexMac, exIdx.Kind);
    }

    [TestMethod]
    public void AppStateProcessor_ProcessSnapshot_VerifiesMacRejectsValuelessAndMarksBootstrapped()
    {
        ExpandedAppStateKeys keys = AppStateKeys.Expand(SampleMasterKey);
        byte[] indexJson = Encoding.UTF8.GetBytes("[\"mute\",\"123@s.whatsapp.net\"]");
        byte[] iv = Enumerable.Range(20, 16).Select(static i => (byte)i).ToArray();
        var (mut, _) = AppStateMutationCodec.EncodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            indexJson,
            new SyncActionValue { Timestamp = 42, MuteAction = new SyncActionValue.Types.MuteAction { Muted = true } },
            keys,
            SampleKeyId,
            iv);

        byte[] expectedHash = LtHash.SubtractThenAdd(LtHash.CreateEmpty(), subtractItems: null, addItems: [mut.Record.Value.Blob.Span[^32..].ToArray()]);
        byte[] snapMac = AppStateHasher.GenerateSnapshotMac(expectedHash, 7, AppStateCollections.RegularHigh, keys.SnapshotMacKey);

        var snapshot = new SyncdSnapshot
        {
            Version = new SyncdVersion { Version = 7 },
            KeyId = new KeyId { Id = ByteString.CopyFrom(SampleKeyId) },
            Mac = ByteString.CopyFrom(snapMac)
        };
        snapshot.Records.Add(mut.Record);

        var initial = new AppStateCollectionState { MacMismatchFatal = true };
        ProcessedSnapshotResult result = AppStateProcessor.ProcessSnapshot(
            snapshot,
            initial,
            _ => keys,
            AppStateCollections.RegularHigh);

        Assert.IsTrue(result.State.Bootstrapped);
        Assert.IsFalse(result.State.MacMismatchFatal);
        Assert.AreEqual(7UL, result.State.Version);
        CollectionAssert.AreEqual(expectedHash, result.State.Hash);
        Assert.AreEqual(1, result.Mutations.Count);

        // Missing value blob must fail explicitly (anti-tampering WAWebSyncdAntiTampering)
        SyncdSnapshot valueless = snapshot.Clone();
        valueless.Records[0].Value = null;
        var exValueless = Assert.ThrowsException<AppStateException>(() =>
            AppStateProcessor.ProcessSnapshot(valueless, new AppStateCollectionState(), _ => keys, AppStateCollections.RegularHigh));
        Assert.AreEqual(AppStateErrorKind.MissingValueBlob, exValueless.Kind);

        // Tampered snapshot MAC must throw SnapshotMacMismatch
        SyncdSnapshot badMac = snapshot.Clone();
        badMac.Mac = ByteString.CopyFrom(new byte[32]);
        var exMac = Assert.ThrowsException<AppStateException>(() =>
            AppStateProcessor.ProcessSnapshot(badMac, new AppStateCollectionState(), _ => keys, AppStateCollections.RegularHigh));
        Assert.AreEqual(AppStateErrorKind.SnapshotMacMismatch, exMac.Kind);
    }

    [TestMethod]
    public void AppStateProcessor_ProcessPatch_ValidatesOverwriteRemoveOrderDuplicatesAndDivergence()
    {
        ExpandedAppStateKeys keys = AppStateKeys.Expand(SampleMasterKey);
        var storeMacs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        Func<byte[], byte[]?> lookupMac = idx => storeMacs.GetValueOrDefault(Convert.ToHexStringLower(idx));

        byte[] idxA = Encoding.UTF8.GetBytes("[\"pin_v1\",\"100@s.whatsapp.net\"]");
        byte[] iv1 = Enumerable.Repeat((byte)0x11, 16).ToArray();
        var (m1, valMac1) = AppStateMutationCodec.EncodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            idxA,
            new SyncActionValue { Timestamp = 1, PinAction = new SyncActionValue.Types.PinAction { Pinned = true } },
            keys,
            SampleKeyId,
            iv1);

        var p1 = BuildSignedPatch(keys, SampleKeyId, AppStateCollections.RegularLow, 1, LtHash.CreateEmpty(), lookupMac, m1);
        ProcessedPatchResult res1 = AppStateProcessor.ProcessPatch(
            p1,
            new AppStateCollectionState(),
            _ => keys,
            lookupMac,
            AppStateCollections.RegularLow);
        Assert.IsFalse(res1.SnapshotMacDiverged);
        storeMacs[Convert.ToHexStringLower(res1.AddedMacs[0].IndexMac)] = res1.AddedMacs[0].ValueMac;

        // Wrong version (v3 expected v2)
        var gapPatch = p1.Clone();
        gapPatch.Version = new SyncdVersion { Version = 3 };
        var exGap = Assert.ThrowsException<AppStateException>(() =>
            AppStateProcessor.ProcessPatch(gapPatch, res1.State, _ => keys, lookupMac, AppStateCollections.RegularLow));
        Assert.AreEqual(AppStateErrorKind.PatchVersionMismatch, exGap.Kind);

        // Overwrite same index -> must subtract valMac1 exactly once and add new value MAC
        byte[] iv2 = Enumerable.Repeat((byte)0x22, 16).ToArray();
        var (m2Set, _) = AppStateMutationCodec.EncodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            idxA,
            new SyncActionValue { Timestamp = 2, PinAction = new SyncActionValue.Types.PinAction { Pinned = false } },
            keys,
            SampleKeyId,
            iv2);
        var p2 = BuildSignedPatch(keys, SampleKeyId, AppStateCollections.RegularLow, 2, res1.State.Hash, lookupMac, m2Set);
        ProcessedPatchResult res2 = AppStateProcessor.ProcessPatch(
            p2,
            res1.State,
            _ => keys,
            lookupMac,
            AppStateCollections.RegularLow);
        Assert.IsFalse(res2.SnapshotMacDiverged);
        CollectionAssert.AreEqual(
            LtHash.SubtractThenAdd(LtHash.CreateEmpty(), subtractItems: null, addItems: [res2.AddedMacs[0].ValueMac]),
            res2.State.Hash);

        // Tamper SnapshotMac while keeping valid PatchMac -> SnapshotMacDiverged = true & latches MacMismatchFatal
        var badSnapMacPatch = p2.Clone();
        badSnapMacPatch.SnapshotMac = ByteString.CopyFrom(new byte[32]);
        badSnapMacPatch.PatchMac = ByteString.CopyFrom(AppStateHasher.GeneratePatchMac(badSnapMacPatch, AppStateCollections.RegularLow, keys.PatchMacKey, 2));
        ProcessedPatchResult resDiv = AppStateProcessor.ProcessPatch(
            badSnapMacPatch,
            res1.State,
            _ => keys,
            lookupMac,
            AppStateCollections.RegularLow);
        Assert.IsTrue(resDiv.SnapshotMacDiverged);
        Assert.IsTrue(resDiv.State.MacMismatchFatal);

        // Duplicate SET of same index inside one patch
        var dupPatch = BuildSignedPatch(keys, SampleKeyId, AppStateCollections.RegularLow, 1, LtHash.CreateEmpty(), _ => null, m1, m1);
        var exDup = Assert.ThrowsException<AppStateException>(() =>
            AppStateProcessor.ProcessPatch(dupPatch, new AppStateCollectionState(), _ => keys, _ => null, AppStateCollections.RegularLow));
        Assert.AreEqual(AppStateErrorKind.DuplicateIndexInPatch, exDup.Kind);
    }

    [TestMethod]
    public void AppStatePatchParser_ParsesSnapshotsPatchesHasMoreAndErrors()
    {
        var extSnap = new ExternalBlobReference { DirectPath = "/v/t62/snap", MediaKey = ByteString.CopyFrom(new byte[32]) };
        var inlinePatch = new SyncdPatch { Version = new SyncdVersion { Version = 2 } };

        var colOk = new Node(
            "collection",
            new Dictionary<string, NodeValue>
            {
                ["name"] = AppStateCollections.Regular,
                ["has_more_patches"] = "true"
            },
            new Node[]
            {
                new("snapshot", extSnap.ToByteArray()),
                new("patches", new Node[] { new("patch", inlinePatch.ToByteArray()) })
            });

        var colConflict = new Node(
            "collection",
            new Dictionary<string, NodeValue>
            {
                ["name"] = AppStateCollections.CriticalBlock,
                ["type"] = "error"
            },
            new Node[]
            {
                new("error", new Dictionary<string, NodeValue> { ["code"] = "409", ["text"] = "conflict" })
            });

        var colFatal = new Node(
            "collection",
            new Dictionary<string, NodeValue>
            {
                ["name"] = AppStateCollections.RegularLow,
                ["type"] = "error"
            },
            new Node[]
            {
                new("error", new Dictionary<string, NodeValue> { ["code"] = "400", ["text"] = "bad-request" })
            });

        var syncIq = new Node("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[]
        {
            new("sync", new Node[] { colOk, colConflict, colFatal })
        });

        var parsed = AppStatePatchParser.ParseCollections(syncIq);
        Assert.AreEqual(3, parsed.Count);

        Assert.AreEqual(AppStateCollections.Regular, parsed[0].CollectionName);
        Assert.IsTrue(parsed[0].HasMorePatches);
        Assert.AreEqual("/v/t62/snap", parsed[0].SnapshotReference?.DirectPath);
        Assert.AreEqual(1, parsed[0].Patches.Count);
        Assert.IsNull(parsed[0].Error);

        Assert.AreEqual(CollectionSyncErrorKind.Conflict, parsed[1].Error?.Kind);
        Assert.AreEqual(409, parsed[1].Error?.Code);

        Assert.AreEqual(CollectionSyncErrorKind.Fatal, parsed[2].Error?.Kind);
        Assert.AreEqual(400, parsed[2].Error?.Code);
    }

    private static SyncdPatch BuildSignedPatch(
        ExpandedAppStateKeys keys,
        byte[] keyId,
        string collection,
        ulong version,
        byte[] priorHash,
        Func<byte[], byte[]?> prevLookup,
        params SyncdMutation[] mutations)
    {
        byte[] nextHash = (byte[])priorHash.Clone();
        AppStateHasher.UpdateHash(nextHash, mutations, (idx, _) => prevLookup(idx));
        byte[] snapMac = AppStateHasher.GenerateSnapshotMac(nextHash, version, collection, keys.SnapshotMacKey);

        var patch = new SyncdPatch
        {
            Version = new SyncdVersion { Version = version },
            SnapshotMac = ByteString.CopyFrom(snapMac),
            KeyId = new KeyId { Id = ByteString.CopyFrom(keyId) }
        };
        patch.Mutations.AddRange(mutations);
        patch.PatchMac = ByteString.CopyFrom(AppStateHasher.GeneratePatchMac(patch, collection, keys.PatchMacKey, version));
        return patch;
    }
}
