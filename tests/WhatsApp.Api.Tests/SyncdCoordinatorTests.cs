using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Storage.Sqlite;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class SyncdCoordinatorTests
{
    private static readonly byte[] KeyId = [0xAA, 0xBB, 0xCC, 0xDD, 0x01, 0x02];
    private static readonly byte[] MasterKey = Enumerable.Range(10, 32).Select(static b => (byte)b).ToArray();
    private static readonly byte[] MediaKey = Enumerable.Range(50, 32).Select(static b => (byte)b).ToArray();

    private sealed class MockCdnHttpClient : IHttpClient
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
        public List<Uri> RequestedUris { get; } = [];

        public void RegisterEncryptedBlob(string directPath, byte[] plaintext)
        {
            byte[] enc = MediaCipher.EncryptBlob(plaintext, MediaKey, WaMediaType.AppState);
            _blobs[directPath] = enc;
        }

        public ExternalBlobReference BuildReference(string directPath, byte[] plaintext)
        {
            byte[] enc = MediaCipher.EncryptBlob(plaintext, MediaKey, WaMediaType.AppState);
            _blobs[directPath] = enc;
            return new ExternalBlobReference
            {
                DirectPath = directPath,
                MediaKey = ByteString.CopyFrom(MediaKey),
                FileSizeBytes = (ulong)enc.Length,
                FileSha256 = ByteString.CopyFrom(SHA256.HashData(plaintext)),
                FileEncSha256 = ByteString.CopyFrom(SHA256.HashData(enc))
            };
        }

        public Task<byte[]> GetBytesAsync(Uri url, long maxBytes = 64 * 1024 * 1024, CancellationToken ct = default)
        {
            RequestedUris.Add(url);
            if (_blobs.TryGetValue(url.PathAndQuery, out byte[]? data))
            {
                return Task.FromResult(data);
            }
            throw new HttpRequestException($"404 Not Found: {url}");
        }

        public Task<byte[]> PostBytesAsync(Uri url, ReadOnlyMemory<byte> body, string contentType = "application/octet-stream", CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [TestMethod]
    public async Task SyncCollectionAsync_BootstrapsSnapshotPaginatesExternalPatchesAndUpdatesPushName()
    {
        await using var backend = new SqliteBackend(":memory:");
        await backend.AppSync.SaveSyncKeyAsync(KeyId, MasterKey);
        var creds = await backend.Device.LoadOrCreateAsync();
        Assert.AreEqual("WhatsApp Desktop", creds.PushName);

        var http = new MockCdnHttpClient();
        var keys = AppStateKeys.Expand(MasterKey);
        var events = new List<WhatsAppEvent>();

        // Snapshot v1 with a Mute mutation
        byte[] idx1 = Encoding.UTF8.GetBytes("[\"mute\",\"555@s.whatsapp.net\"]");
        var (mut1, valMac1) = AppStateMutationCodec.EncodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            idx1,
            new SyncActionValue { Timestamp = 100, MuteAction = new SyncActionValue.Types.MuteAction { Muted = true } },
            keys,
            KeyId,
            Enumerable.Repeat((byte)0x01, 16).ToArray());

        byte[] hash1 = LtHash.SubtractThenAdd(LtHash.CreateEmpty(), subtractItems: null, addItems: [valMac1]);
        var snapshot = new SyncdSnapshot
        {
            Version = new SyncdVersion { Version = 1 },
            KeyId = new KeyId { Id = ByteString.CopyFrom(KeyId) },
            Mac = ByteString.CopyFrom(AppStateHasher.GenerateSnapshotMac(hash1, 1, AppStateCollections.Regular, keys.SnapshotMacKey))
        };
        snapshot.Records.Add(mut1.Record);
        ExternalBlobReference snapRef = http.BuildReference("/v/t62/snap-v1?auth=1", snapshot.ToByteArray());

        // External Patch v2 with PushNameSetting("Bhavya")
        byte[] idx2 = Encoding.UTF8.GetBytes("[\"setting_pushName\"]");
        var (mut2, valMac2) = AppStateMutationCodec.EncodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            idx2,
            new SyncActionValue { Timestamp = 200, PushNameSetting = new SyncActionValue.Types.PushNameSetting { Name = "Bhavya" } },
            keys,
            KeyId,
            Enumerable.Repeat((byte)0x02, 16).ToArray());

        byte[] hash2 = LtHash.SubtractThenAdd(hash1, subtractItems: null, addItems: [valMac2]);
        var extMutationsProto = new SyncdMutations();
        extMutationsProto.Mutations.Add(mut2);
        ExternalBlobReference extPatchRef = http.BuildReference("/v/t62/patch-v2?auth=2", extMutationsProto.ToByteArray());

        var patch2Header = new SyncdPatch
        {
            Version = new SyncdVersion { Version = 2 },
            SnapshotMac = ByteString.CopyFrom(AppStateHasher.GenerateSnapshotMac(hash2, 2, AppStateCollections.Regular, keys.SnapshotMacKey)),
            KeyId = new KeyId { Id = ByteString.CopyFrom(KeyId) },
            ExternalMutations = extPatchRef
        };
        var fullPatch2ForMac = patch2Header.Clone();
        fullPatch2ForMac.Mutations.Add(mut2);
        patch2Header.PatchMac = ByteString.CopyFrom(AppStateHasher.GeneratePatchMac(fullPatch2ForMac, AppStateCollections.Regular, keys.PatchMacKey, 2));

        int iqTurn = 0;
        await using var coordinator = new SyncdCoordinator(
            backend,
            http,
            (iq, _) =>
            {
                iqTurn++;
                Node reqCol = iq.GetChild("sync")!.GetChild("collection")!;
                if (iqTurn == 1)
                {
                    Assert.AreEqual("true", reqCol.GetAttr("return_snapshot"));
                    return new ValueTask<Node>(BuildSyncResponse(AppStateCollections.Regular, hasMore: true, snapRef: snapRef));
                }

                Assert.AreEqual("1", reqCol.GetAttr("version"));
                Assert.IsNull(reqCol.GetAttr("return_snapshot"));
                return new ValueTask<Node>(BuildSyncResponse(AppStateCollections.Regular, hasMore: false, patches: [patch2Header]));
            },
            emitEventAsync: ev =>
            {
                events.Add(ev);
                return ValueTask.CompletedTask;
            });

        IReadOnlyList<DecodedAppStateMutation> decoded = await coordinator.SyncCollectionAsync(AppStateCollections.Regular);
        Assert.AreEqual(2, decoded.Count);
        Assert.AreEqual(2, http.RequestedUris.Count);
        Assert.AreEqual("https://mmg.whatsapp.net/v/t62/snap-v1?auth=1", http.RequestedUris[0].ToString());

        var (storedVersion, storedHash) = await backend.AppSync.GetCollectionStateAsync(AppStateCollections.Regular);
        Assert.AreEqual(2UL, storedVersion);
        CollectionAssert.AreEqual(hash2, storedHash);

        var updatedCreds = await backend.Device.LoadAsync();
        Assert.AreEqual("Bhavya", updatedCreds?.PushName);

        Assert.AreEqual(2, events.Count);
        Assert.AreEqual(new AppStateUpdatedEvent(AppStateCollections.Regular, 1, 1), events[0]);
        Assert.AreEqual(new AppStateUpdatedEvent(AppStateCollections.Regular, 2, 1), events[1]);
    }

    [TestMethod]
    public async Task SyncCollectionAsync_RecoversViaSnapshotWhenPatchSnapshotMacDiverges()
    {
        await using var backend = new SqliteBackend(":memory:");
        await backend.AppSync.SaveSyncKeyAsync(KeyId, MasterKey);
        var keys = AppStateKeys.Expand(MasterKey);
        var http = new MockCdnHttpClient();

        // Seed existing collection state v1 with stale MAC
        byte[] staleIdx = SHA256.HashData("stale"u8);
        byte[] staleVal = Enumerable.Repeat((byte)0x99, 32).ToArray();
        await backend.AppSync.CommitSnapshotAsync(AppStateCollections.CriticalBlock, 1, LtHash.CreateEmpty(), [(staleIdx, staleVal)]);

        // Server first sends v2 patch with intentionally diverged SnapshotMac (but valid PatchMac)
        byte[] idx2 = Encoding.UTF8.GetBytes("[\"pin_v1\",\"1@s.whatsapp.net\"]");
        var (mut2, _) = AppStateMutationCodec.EncodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            idx2,
            new SyncActionValue { Timestamp = 10, PinAction = new SyncActionValue.Types.PinAction { Pinned = true } },
            keys,
            KeyId,
            Enumerable.Repeat((byte)0x05, 16).ToArray());

        var badPatch = new SyncdPatch
        {
            Version = new SyncdVersion { Version = 2 },
            SnapshotMac = ByteString.CopyFrom(new byte[32]),
            KeyId = new KeyId { Id = ByteString.CopyFrom(KeyId) }
        };
        badPatch.Mutations.Add(mut2);
        badPatch.PatchMac = ByteString.CopyFrom(AppStateHasher.GeneratePatchMac(badPatch, AppStateCollections.CriticalBlock, keys.PatchMacKey, 2));

        // Clean recovery snapshot v2
        byte[] freshHash = LtHash.SubtractThenAdd(LtHash.CreateEmpty(), subtractItems: null, addItems: [mut2.Record.Value.Blob.Span[^32..].ToArray()]);
        var recSnap = new SyncdSnapshot
        {
            Version = new SyncdVersion { Version = 2 },
            KeyId = new KeyId { Id = ByteString.CopyFrom(KeyId) },
            Mac = ByteString.CopyFrom(AppStateHasher.GenerateSnapshotMac(freshHash, 2, AppStateCollections.CriticalBlock, keys.SnapshotMacKey))
        };
        recSnap.Records.Add(mut2.Record);
        ExternalBlobReference snapRef = http.BuildReference("/v/t62/recovery-snap", recSnap.ToByteArray());

        int turn = 0;
        await using var coordinator = new SyncdCoordinator(backend, http, (iq, _) =>
        {
            turn++;
            Node reqCol = iq.GetChild("sync")!.GetChild("collection")!;
            if (turn == 1)
            {
                Assert.AreEqual("1", reqCol.GetAttr("version"));
                return new ValueTask<Node>(BuildSyncResponse(AppStateCollections.CriticalBlock, hasMore: false, patches: [badPatch]));
            }

            Assert.AreEqual("true", reqCol.GetAttr("return_snapshot"));
            return new ValueTask<Node>(BuildSyncResponse(AppStateCollections.CriticalBlock, hasMore: false, snapRef: snapRef));
        });

        await coordinator.SyncCollectionAsync(AppStateCollections.CriticalBlock);

        var (finalVer, finalHash) = await backend.AppSync.GetCollectionStateAsync(AppStateCollections.CriticalBlock);
        Assert.AreEqual(2UL, finalVer);
        CollectionAssert.AreEqual(freshHash, finalHash);
        // Stale MAC from pre-recovery v1 must have been wiped atomically by CommitSnapshotAsync
        Assert.IsNull(await backend.AppSync.GetMutationMacAsync(AppStateCollections.CriticalBlock, staleIdx));
    }

    [TestMethod]
    public async Task SendMutationAsync_Absorbs409ConflictRebuildsPatchAndCommitsWinningVersion()
    {
        await using var backend = new SqliteBackend(":memory:");
        await backend.AppSync.SaveSyncKeyAsync(KeyId, MasterKey);
        var keys = AppStateKeys.Expand(MasterKey);
        var http = new MockCdnHttpClient();

        // Seed baseline v1
        byte[] seedIdx = Encoding.UTF8.GetBytes("[\"mute\",\"seed@s.whatsapp.net\"]");
        var (seedMut, seedValMac) = AppStateMutationCodec.EncodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            seedIdx,
            new SyncActionValue { Timestamp = 1, MuteAction = new SyncActionValue.Types.MuteAction { Muted = false } },
            keys,
            KeyId,
            Enumerable.Repeat((byte)0x10, 16).ToArray());
        byte[] hash1 = LtHash.SubtractThenAdd(LtHash.CreateEmpty(), subtractItems: null, addItems: [seedValMac]);
        byte[] seedIdxMac = seedMut.Record.Index.Blob.ToByteArray();
        await backend.AppSync.CommitSnapshotAsync(AppStateCollections.RegularLow, 1, hash1, [(seedIdxMac, seedValMac)]);

        // Competing remote patch v2
        byte[] competingIdx = Encoding.UTF8.GetBytes("[\"pin_v1\",\"peer@s.whatsapp.net\"]");
        var (compMut, compValMac) = AppStateMutationCodec.EncodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            competingIdx,
            new SyncActionValue { Timestamp = 2, PinAction = new SyncActionValue.Types.PinAction { Pinned = true } },
            keys,
            KeyId,
            Enumerable.Repeat((byte)0x20, 16).ToArray());
        byte[] hash2 = LtHash.SubtractThenAdd(hash1, subtractItems: null, addItems: [compValMac]);
        var conflictPatchV2 = new SyncdPatch
        {
            Version = new SyncdVersion { Version = 2 },
            SnapshotMac = ByteString.CopyFrom(AppStateHasher.GenerateSnapshotMac(hash2, 2, AppStateCollections.RegularLow, keys.SnapshotMacKey)),
            KeyId = new KeyId { Id = ByteString.CopyFrom(KeyId) }
        };
        conflictPatchV2.Mutations.Add(compMut);
        conflictPatchV2.PatchMac = ByteString.CopyFrom(AppStateHasher.GeneratePatchMac(conflictPatchV2, AppStateCollections.RegularLow, keys.PatchMacKey, 2));

        SyncdPatch? acceptedServerPatchV3 = null;
        int call = 0;

        await using var coordinator = new SyncdCoordinator(backend, http, (iq, _) =>
        {
            call++;
            Node reqCol = iq.GetChild("sync")!.GetChild("collection")!;
            if (call == 1)
            {
                // First send attempt built on base version 1 -> server responds 409 carrying v2
                Assert.AreEqual("1", reqCol.GetAttr("version"));
                Node patchChild = reqCol.GetChild("patch")!;
                Assert.IsNotNull(patchChild);
                var conflictNode = new Node(
                    "collection",
                    new Dictionary<string, NodeValue>
                    {
                        ["name"] = AppStateCollections.RegularLow,
                        ["type"] = "error",
                        ["has_more_patches"] = "false"
                    },
                    new Node[]
                    {
                        new("error", new Dictionary<string, NodeValue> { ["code"] = "409", ["text"] = "conflict" }),
                        new("patches", new Node[] { new("patch", conflictPatchV2.ToByteArray()) })
                    });
                return new ValueTask<Node>(new Node("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[]
                {
                    new("sync", new Node[] { conflictNode })
                }));
            }

            if (call == 2)
            {
                // Rebuilt send on base version 2 -> validate client computed matching v3 SnapshotMac + PatchMac
                Assert.AreEqual("2", reqCol.GetAttr("version"));
                byte[] submittedBytes = ((NodeContent.BytesContent)reqCol.GetChild("patch")!.Content!).Bytes;
                acceptedServerPatchV3 = SyncdPatch.Parser.ParseFrom(submittedBytes);
                acceptedServerPatchV3.Version = new SyncdVersion { Version = 3 };
                return new ValueTask<Node>(new Node("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[]
                {
                    new("sync")
                }));
            }

            // Follow-up sync confirms server version 3
            Assert.AreEqual("2", reqCol.GetAttr("version"));
            return new ValueTask<Node>(BuildSyncResponse(AppStateCollections.RegularLow, hasMore: false, patches: [acceptedServerPatchV3!]));
        });

        await coordinator.SendMutationAsync(
            AppStateCollections.RegularLow,
            SyncdMutation.Types.SyncdOperation.Set,
            Encoding.UTF8.GetBytes("[\"archive\",\"chat@s.whatsapp.net\"]"),
            new SyncActionValue { Timestamp = 3, ArchiveChatAction = new SyncActionValue.Types.ArchiveChatAction { Archived = true } });

        var (finalVer, _) = await backend.AppSync.GetCollectionStateAsync(AppStateCollections.RegularLow);
        Assert.AreEqual(3UL, finalVer);

        byte[] compIdxMac = compMut.Record.Index.Blob.ToByteArray();
        CollectionAssert.AreEqual(compValMac, await backend.AppSync.GetMutationMacAsync(AppStateCollections.RegularLow, compIdxMac));
    }

    private static Node BuildSyncResponse(
        string collection,
        bool hasMore,
        ExternalBlobReference? snapRef = null,
        IReadOnlyList<SyncdPatch>? patches = null)
    {
        var children = new List<Node>();
        if (snapRef is not null)
        {
            children.Add(new Node("snapshot", snapRef.ToByteArray()));
        }
        if (patches is { Count: > 0 })
        {
            children.Add(new Node("patches", patches.Select(static p => new Node("patch", p.ToByteArray())).ToArray()));
        }

        var colNode = new Node(
            "collection",
            new Dictionary<string, NodeValue>
            {
                ["name"] = collection,
                ["has_more_patches"] = hasMore ? "true" : "false"
            },
            new NodeContent.NodesContent(children));

        return new Node("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[]
        {
            new("sync", new Node[] { colNode })
        });
    }
}
