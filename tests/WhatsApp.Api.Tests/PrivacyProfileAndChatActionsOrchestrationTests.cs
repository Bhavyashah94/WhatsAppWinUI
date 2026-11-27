using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;
using WhatsApp.Storage.Sqlite;
using LocalWirePair = WhatsApp.Api.Tests.UsyncAndNotificationOrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class PrivacyProfileAndChatActionsOrchestrationTests
{
    private sealed class MockSyncdHttpClient : IHttpClient
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public ExternalBlobReference BuildReference(string directPath, byte[] plaintext, byte[] mediaKey)
        {
            _blobs[directPath] = MediaCipher.EncryptBlob(plaintext, mediaKey, WaMediaType.AppState);
            return new ExternalBlobReference
            {
                DirectPath = directPath,
                MediaKey = ByteString.CopyFrom(mediaKey),
                FileSizeBytes = (ulong)_blobs[directPath].Length,
                FileSha256 = ByteString.CopyFrom(SHA256.HashData(plaintext)),
                FileEncSha256 = ByteString.CopyFrom(SHA256.HashData(_blobs[directPath]))
            };
        }

        public Task<byte[]> GetBytesAsync(Uri url, long maxBytes = 64 * 1024 * 1024, CancellationToken ct = default)
        {
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
    public async Task Privacy_Profile_AccountSafety_And_Notification_WorkEndToEnd()
    {
        await using var backend = new SqliteBackend(":memory:");
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15550000001", 1);
        creds.Lid = Jid.LidDevice("100000000000001", 1);
        uint initialSpkId = creds.SignedPreKeyId;
        await backend.Device.SaveAsync(creds);

        Jid peerLid = Jid.Parse("100000000000002@lid");
        KeyPair peerIdKey = Curve25519.GenerateKeyPair();
        await backend.Signal.SaveIdentityAsync(peerLid.ToProtocolAddress(), peerIdKey.PublicKey);
        await backend.Protocol.StoreTcTokenAsync(peerLid.ToString(), [0xAA, 0xBB], 1700000000);

        var events = Channel.CreateUnbounded<WhatsAppEvent>();
        var lidPn = new LidPnCache();
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());

        var mex = new MexClient(wire.Iq);
        var privacy = new PrivacyClient(wire.Iq);
        var profile = new ProfileClient(wire.Socket, mex, backend, creds);
        var safety = new AccountSafetyClient(wire.Iq, backend, creds, e => events.Writer.WriteAsync(e));
        var notifications = new NotificationRouter(backend, lidPn, creds, wire.Socket, wire.Iq, syncd: null, e => events.Writer.WriteAsync(e));

        // 1) Privacy GET + SET disallowed list + Default disappearing mode
        Task<PrivacySettingsSnapshot> getTask = privacy.GetPrivacySettingsAsync();
        Node getReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("privacy", getReq.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = getReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("privacy", new[]
            {
                new Node("category", new Dictionary<string, NodeValue> { ["name"] = "last", ["value"] = "contacts" }),
                new Node("category", new Dictionary<string, NodeValue> { ["name"] = "calladd", ["value"] = "screen" })
            })
        }));
        PrivacySettingsSnapshot snap = await getTask;
        Assert.AreEqual(PrivacyValue.Screen, snap.Get(PrivacyCategory.CallAdd)?.Value);

        Task<SetPrivacyResult> disallowTask = privacy.SetDisallowedListAsync(
            PrivacyCategory.Last,
            "old-dh",
            [new DisallowedListUserEntry(DisallowedListAction.Add, peerLid)]);
        Node disallowReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("lid", disallowReq.GetChild("privacy")?.GetAttr("addressing_mode"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = disallowReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("privacy", new[]
            {
                new Node("category", new Dictionary<string, NodeValue> { ["name"] = "last", ["value"] = "contact_blacklist", ["dhash"] = "new-dh" })
            })
        }));
        Assert.AreEqual("new-dh", (await disallowTask).Dhash);

        Task dmTask = privacy.SetDefaultDisappearingModeAsync(604800);
        Node dmReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("604800", dmReq.GetChild("disappearing_mode")?.GetAttr("duration"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = dmReq.GetAttr("id")!, ["type"] = "result" }));
        await dmTask;

        // 2) Profile SetPushName (no syncd -> Synced=false, presence sent + local persisted) + MEX TextStatus (live doc ID 9152604461510864)
        Task<PushNameOutcome> pushTask = profile.SetPushNameAsync("Bhavya");
        Node presNode = await wire.ReadClientNodeAsync();
        Assert.AreEqual("presence", presNode.Tag);
        Assert.AreEqual("Bhavya", presNode.GetAttr("name"));
        PushNameOutcome pushOutcome = await pushTask;
        Assert.IsFalse(pushOutcome.Synced);
        Assert.AreEqual("Bhavya", (await backend.Device.LoadAsync())?.PushName);

        Task textStatusTask = profile.SetTextStatusAsync("Deep in .NET", "🚀", TimeSpan.FromHours(8));
        Node textMexReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("9152604461510864", textMexReq.GetChild("query")?.GetAttr("query_id"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = textMexReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("result", "{\"data\":{\"xwa2_update_text_status\":{\"result\":\"SUCCESS\"}}}")
        }));
        await textStatusTask;

        // 3) AccountSafety: RotateSignedPreKey retains previous key in signed_prekeys + ReportSpam attaches TcToken + RemoveDevice + SafetyNumber
        Task<uint> rotTask = safety.RotateSignedPreKeyAsync();
        Node rotReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("encrypt", rotReq.GetAttr("xmlns"));
        Assert.IsNotNull(rotReq.GetChild("rotate")?.GetChild("skey"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = rotReq.GetAttr("id")!, ["type"] = "result" }));
        uint newSpkId = await rotTask;
        Assert.AreEqual(initialSpkId + 1, newSpkId);
        Assert.IsNotNull(await backend.Signal.GetSignedPreKeyAsync(initialSpkId));
        Assert.IsNotNull(await backend.Signal.GetSignedPreKeyAsync(newSpkId));

        Task<SpamReportResult> spamTask = safety.ReportMessageAsync(new SpamReportRequest("M-1", 1700000100, FromJid: peerLid));
        Node spamReq = await wire.ReadClientNodeAsync();
        Assert.IsNotNull(spamReq.GetChild("tctoken"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = spamReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("report_id", "RPT-99")
        }));
        Assert.AreEqual("RPT-99", (await spamTask).ReportId);

        Task remTask = safety.RemoveCompanionDeviceAsync(Jid.Parse("15550000001:3@s.whatsapp.net"));
        Node remReq = await wire.ReadClientNodeAsync();
        Assert.IsNotNull(remReq.GetChild("remove-companion-device"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = remReq.GetAttr("id")!, ["type"] = "result" }));
        await remTask;

        SafetyNumberFingerprint? fp = await safety.ComputeSafetyNumberAsync(peerLid);
        Assert.IsNotNull(fp);
        Assert.AreEqual(60, fp.DisplayableDigits.Length);

        // 4) NotificationRouter handles <notification type="privacy">
        await notifications.HandleAsync(new Node("notification", new Dictionary<string, NodeValue>
        {
            ["id"] = "np-1",
            ["from"] = StanzaBuilder.ServerPn,
            ["type"] = "privacy",
            ["t"] = "1700000200"
        }, new[]
        {
            new Node("privacy", new[]
            {
                new Node("category", new Dictionary<string, NodeValue> { ["name"] = "readreceipts", ["value"] = "none" })
            })
        }), sendAck: false);

        PrivacyUpdatedEvent? privEv = null;
        while (events.Reader.TryRead(out WhatsAppEvent? ev))
        {
            if (ev is PrivacyUpdatedEvent pe) privEv = pe;
        }
        Assert.IsNotNull(privEv);
        Assert.AreEqual(PrivacyValue.None, privEv.Categories[0].Value);
    }

    [TestMethod]
    public async Task AppStateSettings_ChatActions_And_Contacts_RoundTripThroughSyncd()
    {
        await using var backend = new SqliteBackend(":memory:");
        byte[] keyId = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
        byte[] masterKey = Enumerable.Range(10, 32).Select(i => (byte)i).ToArray();
        byte[] mediaKey = Enumerable.Range(60, 32).Select(i => (byte)i).ToArray();
        await backend.AppSync.SaveSyncKeyAsync(keyId, masterKey);
        ExpandedAppStateKeys keys = AppStateKeys.Expand(masterKey);

        var http = new MockSyncdHttpClient();
        var events = new List<WhatsAppEvent>();
        var lidPn = new LidPnCache();

        Jid peerPn = Jid.Parse("15557770001@s.whatsapp.net");
        Jid peerLid = Jid.Parse("100000000000077@lid");
        lidPn.Learn(peerPn.ToString(), peerLid.ToString());

        var sentMutations = new List<(string Collection, SyncdMutation.Types.SyncdOperation Op, string[] Index, SyncActionValue? Value, int Version)>();
        var colVersions = new Dictionary<string, ulong>(StringComparer.Ordinal);

        await using var syncd = new SyncdCoordinator(
            backend,
            http,
            (iq, _ct) =>
            {
                Node? col = iq.GetChild("sync")?.GetChild("collection");
                string name = col?.GetAttr("name") ?? AppStateCollections.Regular;
                if (col?.GetChild("patch") is { } patchNode && patchNode.Content is NodeContent.BytesContent bc)
                {
                    SyncdPatch p = SyncdPatch.Parser.ParseFrom(bc.Bytes);
                    foreach (SyncdMutation m in p.Mutations)
                    {
                        (DecodedAppStateMutation decoded, _) = AppStateMutationCodec.DecodeRecord(m.Operation, m.Record, keys, keyId, validateMacs: true);
                        sentMutations.Add((name, decoded.Operation, decoded.Index.ToArray(), decoded.ActionValue, decoded.Version));
                    }
                    ulong nextVer = colVersions.GetValueOrDefault(name) + 1;
                    colVersions[name] = nextVer;
                    return ValueTask.FromResult(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = iq.GetAttr("id") ?? "1", ["type"] = "result" }, new[]
                    {
                        new Node("sync", new[] { new Node("collection", new Dictionary<string, NodeValue> { ["name"] = name, ["version"] = nextVer.ToString(), ["type"] = "result" }) })
                    }));
                }

                // Snapshot for inbound Syncd event round-trip verification
                var snap = new SyncdSnapshot { Version = new SyncdVersion { Version = 1 }, KeyId = new KeyId { Id = ByteString.CopyFrom(keyId) } };
                var valMacs = new List<byte[]>();
                byte ivCounter = 1;
                void AddMut(SyncdMutation.Types.SyncdOperation op, byte[] idxBytes, SyncActionValue val)
                {
                    (SyncdMutation mut, byte[] mac) = AppStateMutationCodec.EncodeRecord(op, idxBytes, val, keys, keyId, Enumerable.Repeat(ivCounter++, 16).ToArray());
                    snap.Records.Add(mut.Record);
                    if (op == SyncdMutation.Types.SyncdOperation.Set) valMacs.Add(mac);
                }

                AddMut(SyncdMutation.Types.SyncdOperation.Set, AppStateSchemas.DisableLinkPreviews.BuildIndex(),
                    new SyncActionValue { Timestamp = 100, PrivacySettingDisableLinkPreviewsAction = new() { IsPreviewsDisabled = true } });
                AddMut(SyncdMutation.Types.SyncdOperation.Set, AppStateSchemas.UnarchiveChatsSetting.BuildIndex(),
                    new SyncActionValue { Timestamp = 101, UnarchiveChatsSetting = new() { UnarchiveChats = true } });
                AddMut(SyncdMutation.Types.SyncdOperation.Set, AppStateSchemas.Pin.BuildIndex(peerLid.ToString()),
                    new SyncActionValue { Timestamp = 102, PinAction = new() { Pinned = true } });
                AddMut(SyncdMutation.Types.SyncdOperation.Set, AppStateSchemas.Favorites.BuildIndex(),
                    new SyncActionValue { Timestamp = 103, FavoritesAction = new() { Favorites = { new SyncActionValue.Types.FavoritesAction.Types.Favorite { Id = peerLid.ToString() } } } });
                AddMut(SyncdMutation.Types.SyncdOperation.Set, AppStateSchemas.FavoriteSticker.BuildIndex("stkhash-1"),
                    new SyncActionValue { Timestamp = 104, StickerAction = new() { IsFavorite = true, Mimetype = "image/webp" } });

                byte[] hash = LtHash.SubtractThenAdd(LtHash.CreateEmpty(), null, valMacs);
                snap.Mac = ByteString.CopyFrom(AppStateHasher.GenerateSnapshotMac(hash, 1, AppStateCollections.Regular, keys.SnapshotMacKey));
                ExternalBlobReference snapRef = http.BuildReference("/v/t62/8d-snap", snap.ToByteArray(), mediaKey);

                return ValueTask.FromResult(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = iq.GetAttr("id") ?? "1", ["type"] = "result" }, new[]
                {
                    new Node("sync", new[]
                    {
                        new Node("collection", new Dictionary<string, NodeValue> { ["name"] = AppStateCollections.Regular, ["version"] = "1" }, new[]
                        {
                            new Node("snapshot", snapRef.ToByteArray())
                        })
                    })
                }));
            },
            emitEventAsync: ev =>
            {
                events.Add(ev);
                return ValueTask.CompletedTask;
            });

        // Bootstrap snapshot first and assert all Phase 8D events arrive
        await syncd.SyncCollectionAsync(AppStateCollections.Regular);
        Assert.IsTrue(events.OfType<LinkPreviewsDisabledUpdatedEvent>().Any(e => e.PreviewsDisabled));
        Assert.IsTrue(events.OfType<UnarchiveChatsSettingUpdatedEvent>().Any(e => e.UnarchiveChats));
        Assert.IsTrue(events.OfType<PinUpdatedEvent>().Any(e => e.ChatJid == peerLid.ToString() && e.Pinned));
        Assert.IsTrue(events.OfType<FavoritesUpdatedEvent>().Any(e => e.FavoriteIds.SequenceEqual([peerLid.ToString()])));
        Assert.IsTrue(events.OfType<FavoriteStickerUpdatedEvent>().Any(e => e.FileHash == "stkhash-1" && e.IsFavorite));

        // Now push outbound AppStateSettings, ChatActions, Stickers, and Contacts
        var settings = new AppStateSettingsClient(syncd);
        var chatActions = new ChatActionsClient(syncd);
        var contacts = new ContactClient(syncd, lidPn);

        await settings.SetLinkPreviewsDisabledAsync(true);
        await settings.SetUnarchiveChatsAsync(false);
        await settings.SetChannelsPersonalisedRecommendationAsync(true);
        await chatActions.PinChatAsync(peerLid);
        await chatActions.MuteChatAsync(peerLid);
        await chatActions.ArchiveChatAsync(peerLid);
        await chatActions.MarkChatAsReadAsync(peerLid, true);
        await chatActions.LockChatAsync(peerLid, true);
        await chatActions.StarMessageAsync(peerLid, new ChatMessageRef("M-9", FromMe: true), true);
        await chatActions.DeleteMessageForMeAsync(peerLid, new ChatMessageRef("M-9", FromMe: true, TimestampSeconds: 1700000000), deleteMedia: true);
        await chatActions.ClearChatAsync(peerLid, new ClearChatOptions(DeleteStarred: true, DeleteMedia: false));
        await chatActions.DeleteChatAsync(peerLid, deleteMedia: true);
        await chatActions.SetFavoritesAsync([peerLid]);
        await chatActions.SetFavoriteStickerAsync("stk-hash", true);
        await chatActions.RemoveRecentStickerAsync("stk-hash", 1700000000);
        await contacts.SaveContactAsync(peerPn, "Alice Smith", "Alice");
        await contacts.SaveLidContactAsync(peerLid, "Alice Smith", "Alice");
        await contacts.DeleteContactAsync(peerPn);

        Assert.AreEqual(18, sentMutations.Count);
        Assert.IsTrue(sentMutations.Any(m => m.Collection == AppStateCollections.Regular && m.Index is ["setting_disableLinkPreviews"]));
        Assert.IsTrue(sentMutations.Any(m => m.Collection == AppStateCollections.RegularLow && m.Index is ["pin_v1", _] && m.Version == AppStateSchemas.Pin.Version));
        Assert.IsTrue(sentMutations.Any(m => m.Collection == AppStateCollections.RegularHigh && m.Index is ["clearChat", _, "1", "0"] && m.Version == AppStateSchemas.ClearChat.Version));
        Assert.IsTrue(sentMutations.Any(m => m.Collection == AppStateCollections.RegularHigh && m.Index is ["deleteChat", _, "1"] && m.Version == AppStateSchemas.DeleteChat.Version));
        Assert.IsTrue(sentMutations.Any(m => m.Collection == AppStateCollections.CriticalUnblockLow && m.Op == SyncdMutation.Types.SyncdOperation.Remove && m.Index is ["contact", _]));
    }
}
