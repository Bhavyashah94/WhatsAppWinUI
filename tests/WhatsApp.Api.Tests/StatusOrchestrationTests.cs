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
public sealed class StatusOrchestrationTests
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

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static async Task<DeviceCredentials> SetupNodeCredentialsAsync(
        SqliteBackend backend,
        string pnUser,
        string lidUser,
        ushort deviceId = 0)
    {
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice(pnUser, deviceId);
        creds.Lid = Jid.LidDevice(lidUser, deviceId);
        creds.PushName = pnUser;

        var primaryAccount = Curve25519.GenerateKeyPair();
        byte[] advDetails = new ADVDeviceIdentity { RawId = creds.RegistrationId, Timestamp = 1700000000, KeyIndex = deviceId }.ToByteArray();
        byte[] accSig = XEdDSA.Sign(primaryAccount.PrivateKey, [6, 0, .. advDetails, .. creds.IdentityKey.PublicKey]);
        byte[] devSig = XEdDSA.Sign(creds.IdentityKey.PrivateKey, [6, 1, .. advDetails, .. creds.IdentityKey.PublicKey, .. primaryAccount.PublicKey]);
        creds.AccountIdentityBytes = new ADVSignedDeviceIdentity
        {
            Details = ByteString.CopyFrom(advDetails),
            AccountSignatureKey = ByteString.CopyFrom(primaryAccount.PublicKey),
            AccountSignature = ByteString.CopyFrom(accSig),
            DeviceSignature = ByteString.CopyFrom(devSig)
        }.ToByteArray();
        await backend.Device.SaveAsync(creds);

        byte[] spkRecord = PreKeyManager.EncodeSignedPreKeyRecord(
            creds.SignedPreKeyId,
            creds.SignedPreKey,
            creds.SignedPreKeySignature,
            1700000000);
        await backend.Signal.SaveSignedPreKeyAsync(new SignedPreKeyEntry(
            creds.SignedPreKeyId,
            spkRecord,
            creds.SignedPreKeySignature,
            1700000000));

        var otpks = PreKeyManager.GeneratePreKeys(1, 5);
        await backend.Signal.SavePreKeysAsync(otpks.Select(k => (k.Id, k.RecordBytes)).ToArray());
        creds.NextPreKeyId = 6;
        await backend.Device.SaveAsync(creds);

        return creds;
    }

    private static Node BuildPreKeyUserNode(Jid jid, DeviceCredentials creds, uint preKeyId, byte[] preKeyPub)
    {
        var children = new List<Node>
        {
            new("registration", StanzaBuilder.EncodeUInt32BigEndian(creds.RegistrationId)),
            new("type", new[] { PreKeyIq.DjbKeyType }),
            new("identity", creds.IdentityKey.PublicKey),
            new("key", new Node[] { new("id", StanzaBuilder.EncodeKeyId24(preKeyId)), new("value", preKeyPub) }),
            new("skey", new Node[]
            {
                new("id", StanzaBuilder.EncodeKeyId24(creds.SignedPreKeyId)),
                new("value", creds.SignedPreKey.PublicKey),
                new("signature", creds.SignedPreKeySignature)
            })
        };
        if (jid.Device != 0 && creds.AccountIdentityBytes is { Length: > 0 } adv)
        {
            children.Add(new Node("device-identity", adv));
        }
        return new Node("user", new Dictionary<string, NodeValue> { ["jid"] = jid }, children.ToArray());
    }

    [TestMethod]
    public async Task StatusClient_SendText_Revoke_And_Reaction_FollowWireAndRetryRules()
    {
        await using var aliceDb = new SqliteBackend(":memory:");
        await using var bobDb = new SqliteBackend(":memory:");
        DeviceCredentials aliceCreds = await SetupNodeCredentialsAsync(aliceDb, "15550000001", "100000000000001", 0);
        DeviceCredentials bobCreds = await SetupNodeCredentialsAsync(bobDb, "15550000002", "100000000000002", 0);

        Jid aliceLid = aliceCreds.Lid!.Value.ToNonAd();
        Jid bobLid = bobCreds.Lid!.Value.ToNonAd();
        Jid bobPn = bobCreds.Pn!.Value.ToNonAd();

        await aliceDb.Protocol.SaveDeviceListAsync(aliceLid.ToString(), [0u]);
        await aliceDb.Protocol.SaveDeviceListAsync(bobLid.ToString(), [0u]);
        await aliceDb.Protocol.SaveDeviceListAsync(bobPn.ToString(), [0u]);

        var aliceCache = new LidPnCache();
        aliceCache.Learn(bobPn.ToString(), bobLid.ToString());

        var events = Channel.CreateUnbounded<WhatsAppEvent>();
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());

        var usync = new UsyncClient(wire.Iq, aliceDb, aliceCache);
        var preKeyFetcher = new PreKeyFetchClient(wire.Iq, aliceDb, aliceCache, aliceCreds);
        var retry = new RetryCoordinator(wire.Socket, aliceDb, preKeyFetcher, aliceCache, aliceCreds, e => events.Writer.WriteAsync(e));
        var sender = new MessageSender(wire.Socket, usync, preKeyFetcher, aliceDb, aliceCache, aliceCreds, retry);
        var statusClient = new StatusClient(
            wire.Socket,
            usync,
            preKeyFetcher,
            sender,
            aliceDb,
            aliceCache,
            aliceCreds,
            retry,
            new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1700001000)));

        byte[] bobOtpkPub = PreKeyManager.DecodePreKeyRecord((await bobDb.Signal.GetPreKeyAsync(1))!).KeyPair.PublicKey;

        // 1) SendTextStatusAsync: distributes SKDM to Bob + bare audience <to> + <meta status_setting="allowlist"/> + retry cache
        Task<SendMessageResult> sendTask = statusClient.SendTextStatusAsync(
            "Story #1",
            [bobPn],
            options: new StatusSendOptions(StatusPrivacySetting.AllowList, MessageIdOverride: "ST-TEXT-1"));

        Node preKeyReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("encrypt", preKeyReq.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = preKeyReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("list", new[] { BuildPreKeyUserNode(bobLid, bobCreds, 1, bobOtpkPub) })
        }));

        Node postStanza = await wire.ReadClientNodeAsync();
        SendMessageResult sendResult = await sendTask;

        Assert.AreEqual("ST-TEXT-1", sendResult.MessageId);
        Assert.AreEqual("status@broadcast", sendResult.ChatJid);
        Assert.AreEqual("status@broadcast", postStanza.GetAttr("to"));
        Assert.AreEqual("allowlist", postStanza.GetChild("meta")?.GetAttr("status_setting"));
        Assert.IsTrue(retry.TryGetCachedSentMessage("ST-TEXT-1", out _));

        Node[] participantTos = postStanza.GetChild("participants")!.GetChildren("to").ToArray();
        Assert.AreEqual(2, participantTos.Length);
        Assert.AreEqual(bobLid.ToString(), participantTos[0].GetAttr("jid"));
        Assert.IsNotNull(participantTos[0].GetChild("enc"));
        Assert.AreEqual(aliceLid.ToString(), participantTos[1].GetAttr("jid"));
        Assert.IsNull(participantTos[1].GetChild("enc"));

        // Bob decrypts both SKDM + skmsg from wire stanza
        Node skdmEnc = participantTos[0].GetChild("enc")!;
        KeyPair bobOtpkPair = PreKeyManager.DecodePreKeyRecord((await bobDb.Signal.GetPreKeyAsync(1))!).KeyPair;
        byte[] skdmRawBytes = ((NodeContent.BytesContent)skdmEnc.Content!).Bytes;
        (byte[] decryptedSkdmWrap, _) = SignalCipher.DecryptPreKey(
            WhatsApp.Protocol.Signal.PreKeySignalMessage.Parse(skdmRawBytes),
            bobCreds.IdentityKey,
            bobCreds.SignedPreKey,
            bobOtpkPair,
            null);
        Message skdmMsg = Message.Parser.ParseFrom(MessageFraming.UnpadMessage(decryptedSkdmWrap));
        SenderKeyRecord bobReceiverSk = new();
        SenderKeyCipher.ProcessDistribution(bobReceiverSk, skdmMsg.SenderKeyDistributionMessage.AxolotlSenderKeyDistributionMessage.ToByteArray());
        byte[] skmsgBytes = ((NodeContent.BytesContent)postStanza.GetChild("enc")!.Content!).Bytes;
        byte[] decryptedPost = SenderKeyCipher.Decrypt(bobReceiverSk, skmsgBytes);
        Message parsedPost = Message.Parser.ParseFrom(MessageFraming.UnpadMessage(decryptedPost));
        Assert.AreEqual("Story #1", parsedPost.ExtendedTextMessage.Text);

        // 2) Second post reuses distributed SKDM (no encrypt prekey fetch, no <enc> inside <participants>)
        Task<SendMessageResult> musicTask = statusClient.SendMusicStatusAsync(
            "Midnight City",
            "M83",
            [bobLid],
            options: new StatusSendOptions(StatusPrivacySetting.Contacts, MessageIdOverride: "ST-MUSIC-1"));
        Node musicStanza = await wire.ReadClientNodeAsync();
        await musicTask;
        Assert.AreEqual("contacts", musicStanza.GetChild("meta")?.GetAttr("status_setting"));
        Assert.IsTrue(musicStanza.GetChild("participants")!.GetChildren("to").All(n => n.GetChild("enc") is null));

        // 3) RevokeStatusAsync: enforces StanzaIdOverride != targetMessageId and omits <meta status_setting>
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            statusClient.RevokeStatusAsync("ST-TEXT-1", [bobLid], new StatusSendOptions(StanzaIdOverride: "ST-TEXT-1")));

        Task<SendMessageResult> revokeTask = statusClient.RevokeStatusAsync(
            "ST-TEXT-1",
            [bobLid],
            new StatusSendOptions(StatusPrivacySetting.AllowList, StanzaIdOverride: "ST-REVOKE-1"));
        Node revokeStanza = await wire.ReadClientNodeAsync();
        await revokeTask;
        Assert.AreEqual("ST-REVOKE-1", revokeStanza.GetAttr("id"));
        Assert.IsNull(revokeStanza.GetChild("meta"));

        // 4) SendStatusReactionAsync: goes pairwise DM to author without <meta status_setting>
        Task<SendMessageResult> rxTask = statusClient.SendStatusReactionAsync(
            new MessageKey { RemoteJid = "status@broadcast", Id = "ST-BOB-1", Participant = bobLid.ToString() },
            "🔥");
        Node rxStanza = await wire.ReadClientNodeAsync();
        SendMessageResult rxResult = await rxTask;
        Assert.AreEqual(bobLid.ToString(), rxResult.ChatJid);
        Assert.AreEqual(bobLid.ToString(), rxStanza.GetAttr("to"));
        Assert.IsNull(rxStanza.GetChild("meta"));
    }

    [TestMethod]
    public async Task GroupStatusV2_StatusAddOns_And_AppStateStatusPrivacyMute_WorkEndToEnd()
    {
        await using var aliceDb = new SqliteBackend(":memory:");
        await using var bobDb = new SqliteBackend(":memory:");
        DeviceCredentials aliceCreds = await SetupNodeCredentialsAsync(aliceDb, "15551110000", "100000000000011", 0);
        DeviceCredentials bobCreds = await SetupNodeCredentialsAsync(bobDb, "15552220000", "100000000000022", 0);

        Jid aliceLid = aliceCreds.Lid!.Value.ToNonAd();
        Jid bobLid = bobCreds.Lid!.Value.ToNonAd();
        Jid groupJid = Jid.Parse("120363000000003333@g.us");

        await bobDb.Protocol.SaveDeviceListAsync(aliceLid.ToString(), [0u]);
        await bobDb.Protocol.SaveDeviceListAsync(bobLid.ToString(), [0u]);
        await aliceDb.Protocol.SaveDeviceListAsync(aliceLid.ToString(), [0u]);

        // Seed bilateral DM session so Bob can send GroupStatusV2 SKDM + skmsg and status add-ons to Alice
        byte[] aliceOtpkPub = PreKeyManager.DecodePreKeyRecord((await aliceDb.Signal.GetPreKeyAsync(1))!).KeyPair.PublicKey;
        var aliceBundle = new PreKeyBundle(
            aliceCreds.RegistrationId, 0, aliceCreds.SignedPreKeyId, aliceCreds.SignedPreKey.PublicKey,
            aliceCreds.SignedPreKeySignature, aliceCreds.IdentityKey.PublicKey, 1, aliceOtpkPub);
        SignalSession bobToAlice = SignalCipher.InitiateSession(aliceBundle, bobCreds.IdentityKey, bobCreds.RegistrationId);
        await bobDb.Signal.SaveIdentityAsync(aliceLid.ToProtocolAddress(), aliceCreds.IdentityKey.PublicKey);
        await bobDb.Signal.SaveSessionAsync(aliceLid.ToProtocolAddress(), bobToAlice);

        var aliceCache = new LidPnCache();
        var bobCache = new LidPnCache();
        var aliceEvents = Channel.CreateUnbounded<WhatsAppEvent>();
        var bobEvents = Channel.CreateUnbounded<WhatsAppEvent>();

        await using var aliceWire = new LocalWirePair();
        await using var bobWire = new LocalWirePair();

        var alicePreKeys = new PreKeyFetchClient(aliceWire.Iq, aliceDb, aliceCache, aliceCreds);
        var bobPreKeys = new PreKeyFetchClient(bobWire.Iq, bobDb, bobCache, bobCreds);
        var aliceRetry = new RetryCoordinator(aliceWire.Socket, aliceDb, alicePreKeys, aliceCache, aliceCreds, e => aliceEvents.Writer.WriteAsync(e));
        var bobRetry = new RetryCoordinator(bobWire.Socket, bobDb, bobPreKeys, bobCache, bobCreds, e => bobEvents.Writer.WriteAsync(e));
        var aliceReceiver = new MessageReceiver(aliceWire.Socket, aliceDb, aliceCache, aliceCreds, aliceRetry, e => aliceEvents.Writer.WriteAsync(e));
        var aliceUsync = new UsyncClient(aliceWire.Iq, aliceDb, aliceCache);
        var bobUsync = new UsyncClient(bobWire.Iq, bobDb, bobCache);
        var bobSender = new MessageSender(bobWire.Socket, bobUsync, bobPreKeys, bobDb, bobCache, bobCreds, bobRetry);
        var bobStatus = new StatusClient(bobWire.Socket, bobUsync, bobPreKeys, bobSender, bobDb, bobCache, bobCreds, bobRetry);

        await using var aliceRouter = new StanzaRouter(aliceWire.Socket, aliceWire.Iq, new StanzaHandlers
        {
            OnMessage = (n, t) => new ValueTask(aliceReceiver.HandleMessageStanzaAsync(n, t))
        });
        await using var bobRouter = new StanzaRouter(bobWire.Socket, bobWire.Iq, new StanzaHandlers());

        // 1) Bob sends GroupStatusMessageV2 -> Alice's MessageReceiver emits GroupStatusReceivedEvent
        Task<SendMessageResult> gsTask = bobStatus.SendGroupStatusV2Async(
            groupJid,
            StatusStanzas.BuildTextStatus("Team milestone!"),
            [aliceLid, bobLid],
            messageId: "GS2-1");
        Node gsOut = await bobWire.ReadClientNodeAsync();
        await gsTask;

        Node aliceSkdmEnc = gsOut.GetChild("participants")!.GetChildren("to").Single(n => n.GetAttr("jid") == aliceLid.ToString()).GetChild("enc")!;
        Node gsInbound = new("message", new Dictionary<string, NodeValue>
        {
            ["id"] = gsOut.GetAttr("id")!,
            ["from"] = groupJid,
            ["participant"] = bobLid,
            ["type"] = "text",
            ["t"] = "1700001100"
        }, new[] { aliceSkdmEnc, gsOut.GetChild("enc")! });
        aliceWire.SendServerNode(gsInbound);

        GroupStatusReceivedEvent? gsEvent = null;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (gsEvent is null)
            {
                if (await aliceEvents.Reader.ReadAsync(cts.Token) is GroupStatusReceivedEvent e)
                {
                    gsEvent = e;
                }
            }
        }
        Assert.AreEqual(groupJid.ToString(), gsEvent.GroupJid);
        Assert.AreEqual(bobLid.ToString(), gsEvent.SenderJid);
        Assert.AreEqual("GS2-1", gsEvent.MessageId);
        Assert.AreEqual("Team milestone!", gsEvent.Record.Text);

        // 2) Bob sends StatusQuestionAnswerMessage and StatusStickerInteractionMessage DM -> Alice emits MessageAddOnEvent
        var statusKey = new MessageKey { RemoteJid = "status@broadcast", Id = "ST-ALICE-1", Participant = aliceLid.ToString() };
        Task<SendMessageResult> qaTask = bobSender.SendDmAsync(aliceLid, StatusStanzas.BuildStatusQuestionAnswer(statusKey, "Count me in"), messageId: "SQA-1");
        Node qaOut = await bobWire.ReadClientNodeAsync();
        await qaTask;
        Node qaEnc = qaOut.GetChild("enc")
            ?? qaOut.GetChild("participants")!.GetChildren("to").Single(n => n.GetAttr("jid") == aliceLid.ToString()).GetChild("enc")!;
        aliceWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "SQA-1",
            ["from"] = bobLid,
            ["type"] = "text",
            ["t"] = "1700001101"
        }, new[] { qaEnc }));

        Task<SendMessageResult> stkTask = bobSender.SendDmAsync(
            aliceLid,
            StatusStanzas.BuildStatusStickerInteraction(statusKey, "stk-100", Message.Types.StatusStickerInteractionMessage.Types.StatusStickerType.Reaction),
            messageId: "SSI-1");
        Node stkOut = await bobWire.ReadClientNodeAsync();
        await stkTask;
        Node stkEnc = stkOut.GetChild("enc")
            ?? stkOut.GetChild("participants")!.GetChildren("to").Single(n => n.GetAttr("jid") == aliceLid.ToString()).GetChild("enc")!;
        aliceWire.SendServerNode(new Node("message", new Dictionary<string, NodeValue>
        {
            ["id"] = "SSI-1",
            ["from"] = bobLid,
            ["type"] = "text",
            ["t"] = "1700001102"
        }, new[] { stkEnc }));

        var addOns = new List<MessageAddOnEvent>();
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (addOns.Count < 2)
            {
                if (await aliceEvents.Reader.ReadAsync(cts.Token) is MessageAddOnEvent addOn)
                {
                    addOns.Add(addOn);
                }
            }
        }
        Assert.IsTrue(addOns.Any(a => a.Kind == "status_question_answer" && a.TargetMessageId == "ST-ALICE-1" && a.PayloadText == "Count me in"));
        Assert.IsTrue(addOns.Any(a => a.Kind == "status_sticker_interaction" && a.TargetMessageId == "ST-ALICE-1" && a.PayloadText == "stk-100"));

        // 3) AppState StatusPrivacyAction & UserStatusMuteAction emit events & update StatusClient state
        byte[] keyId = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
        byte[] masterKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        byte[] cdnMediaKey = Enumerable.Range(40, 32).Select(i => (byte)i).ToArray();
        await aliceDb.AppSync.SaveSyncKeyAsync(keyId, masterKey);
        ExpandedAppStateKeys appKeys = AppStateKeys.Expand(masterKey);
        var syncdHttp = new MockSyncdHttpClient();

        byte[] idxPriv = Encoding.UTF8.GetBytes("[\"setting_statusPrivacy\"]");
        var (mutPriv, valMacPriv) = AppStateMutationCodec.EncodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            idxPriv,
            new SyncActionValue
            {
                Timestamp = 1700001200,
                StatusPrivacy = new SyncActionValue.Types.StatusPrivacyAction
                {
                    Mode = SyncActionValue.Types.StatusPrivacyAction.Types.StatusDistributionMode.AllowList,
                    ShareToFb = true,
                    UserJid = { bobLid.ToString() }
                }
            },
            appKeys,
            keyId,
            Enumerable.Repeat((byte)0x0A, 16).ToArray());

        byte[] idxMute = AppStateSchemas.UserStatusMute.BuildIndex(bobLid.ToString());
        var (mutMute, valMacMute) = AppStateMutationCodec.EncodeRecord(
            SyncdMutation.Types.SyncdOperation.Set,
            idxMute,
            new SyncActionValue
            {
                Timestamp = 1700001201,
                UserStatusMuteAction = new SyncActionValue.Types.UserStatusMuteAction { Muted = true }
            },
            appKeys,
            keyId,
            Enumerable.Repeat((byte)0x0B, 16).ToArray());

        byte[] snapHash = LtHash.SubtractThenAdd(LtHash.CreateEmpty(), subtractItems: null, addItems: [valMacPriv, valMacMute]);
        var snap = new SyncdSnapshot
        {
            Version = new SyncdVersion { Version = 1 },
            KeyId = new KeyId { Id = ByteString.CopyFrom(keyId) },
            Mac = ByteString.CopyFrom(AppStateHasher.GenerateSnapshotMac(snapHash, 1, AppStateCollections.Regular, appKeys.SnapshotMacKey))
        };
        snap.Records.Add(mutPriv.Record);
        snap.Records.Add(mutMute.Record);
        ExternalBlobReference snapRef = syncdHttp.BuildReference("/v/t62/status-snap", snap.ToByteArray(), cdnMediaKey);

        var aliceStatus = new StatusClient(aliceWire.Socket, aliceUsync, alicePreKeys,
            new MessageSender(aliceWire.Socket, aliceUsync, alicePreKeys, aliceDb, aliceCache, aliceCreds, aliceRetry),
            aliceDb, aliceCache, aliceCreds, aliceRetry);

        await using var syncd = new SyncdCoordinator(aliceDb, syncdHttp, (iq, _) =>
        {
            Node? reqCol = iq.GetChild("sync")?.GetChild("collection");
            string colName = reqCol?.GetAttr("name") ?? AppStateCollections.Regular;
            bool isBootstrap = reqCol?.GetAttr("return_snapshot") == "true";
            Node colResp = colName == AppStateCollections.Regular && isBootstrap
                ? new("collection", new Dictionary<string, NodeValue> { ["name"] = colName, ["version"] = "1" }, new[]
                {
                    new Node("snapshot", snapRef.ToByteArray())
                })
                : new("collection", new Dictionary<string, NodeValue> { ["name"] = colName, ["version"] = "2", ["type"] = "result" });
            return ValueTask.FromResult(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = iq.GetAttr("id") ?? "1", ["type"] = "result" }, new[]
            {
                new Node("sync", new[] { colResp })
            }));
        }, emitEventAsync: e =>
        {
            if (e is StatusPrivacyUpdatedEvent spe)
            {
                aliceStatus.ApplySyncedPrivacy(new SyncActionValue.Types.StatusPrivacyAction
                {
                    Mode = spe.Mode.HasValue
                        ? (SyncActionValue.Types.StatusPrivacyAction.Types.StatusDistributionMode)spe.Mode.Value
                        : SyncActionValue.Types.StatusPrivacyAction.Types.StatusDistributionMode.Contacts,
                    ShareToFb = spe.ShareToFb,
                    ShareToIg = spe.ShareToIg,
                    UserJid = { spe.UserJids }
                });
            }
            else if (e is UserStatusMuteUpdatedEvent ume)
            {
                aliceStatus.ApplySyncedUserMute(ume.UserJid, ume.Muted);
            }
            return aliceEvents.Writer.WriteAsync(e);
        });

        await syncd.SyncCollectionAsync(AppStateCollections.Regular);

        StatusPrivacyUpdatedEvent? privEv = null;
        UserStatusMuteUpdatedEvent? muteEv = null;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (privEv is null || muteEv is null)
            {
                WhatsAppEvent ev = await aliceEvents.Reader.ReadAsync(cts.Token);
                if (ev is StatusPrivacyUpdatedEvent sp) privEv = sp;
                else if (ev is UserStatusMuteUpdatedEvent um) muteEv = um;
            }
        }

        Assert.AreEqual((int)SyncActionValue.Types.StatusPrivacyAction.Types.StatusDistributionMode.AllowList, privEv.Mode);
        Assert.IsTrue(privEv.ShareToFb);
        CollectionAssert.AreEqual(new[] { bobLid.ToString() }, privEv.UserJids.ToArray());
        Assert.AreEqual(bobLid.ToString(), muteEv.UserJid);
        Assert.IsTrue(muteEv.Muted);
        Assert.IsTrue(aliceStatus.IsUserMuted(bobLid));
        Assert.IsTrue(aliceStatus.AudiencePrivacy?.ShareToFb);

        // Local SetUserStatusMutedAsync pushes mutation and updates StatusClient dictionary
        await aliceStatus.SetUserStatusMutedAsync(syncd, aliceLid, true);
        Assert.IsTrue(aliceStatus.IsUserMuted(aliceLid));
    }
}
