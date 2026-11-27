using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WhatsApp.Api;
using WhatsApp.Protocol;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;
using WhatsApp.Storage.Sqlite;
using Whatsapp;

namespace WhatsApp.Cli.Tests;

[TestClass]
public sealed class CliMessagingMediaAndHistoryTests
{
    private sealed class LoopbackHttpClient : IHttpClient
    {
        public byte[]? NextDownloadBytes { get; set; }
        public List<(Uri Url, byte[] Body)> Uploads { get; } = [];
        public string UploadResponseJson { get; set; } =
            """{"url":"https://mock-cdn.local/v/t62/up1","direct_path":"/v/t62/up1","handle":"hnd1"}""";

        public Task<byte[]> GetBytesAsync(Uri url, long maxBytes = 64 * 1024 * 1024, CancellationToken ct = default) =>
            Task.FromResult(NextDownloadBytes ?? throw new HttpRequestException($"No payload queued for {url}."));

        public Task<byte[]> PostBytesAsync(Uri url, ReadOnlyMemory<byte> body, string contentType = "application/octet-stream", CancellationToken ct = default)
        {
            Uploads.Add((url, body.ToArray()));
            return Task.FromResult(Encoding.UTF8.GetBytes(UploadResponseJson));
        }
    }

    private sealed class DuplexLoopbackTransport : ITransport
    {
        private readonly System.Threading.Channels.Channel<byte[]> _inbound = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly System.Threading.Channels.Channel<byte[]> _outbound = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly Pipe _pipe = new();
        private readonly Task _pumpTask;
        private int _connected = 1;

        public DuplexLoopbackTransport()
        {
            _pumpTask = Task.Run(async () =>
            {
                await foreach (byte[] pkt in _inbound.Reader.ReadAllAsync())
                {
                    await _pipe.Writer.WriteAsync(pkt);
                    await _pipe.Writer.FlushAsync();
                }
                await _pipe.Writer.CompleteAsync();
            });
        }

        public bool IsConnected => Volatile.Read(ref _connected) != 0;

        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
            await _outbound.Writer.WriteAsync(data.ToArray(), ct);

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (true)
            {
                ReadResult res = await _pipe.Reader.ReadAsync(ct);
                ReadOnlySequence<byte> seq = res.Buffer;
                if (seq.Length > 0)
                {
                    int copy = (int)Math.Min(buffer.Length, seq.Length);
                    seq.Slice(0, copy).CopyTo(buffer.Span);
                    _pipe.Reader.AdvanceTo(seq.GetPosition(copy));
                    return copy;
                }
                _pipe.Reader.AdvanceTo(seq.End);
                if (res.IsCompleted) return 0;
            }
        }

        public void PushToClient(byte[] wireFrame) => _inbound.Writer.TryWrite(wireFrame);
        public async Task<byte[]> ReadClientChunkAsync(CancellationToken ct = default) => await _outbound.Reader.ReadAsync(ct);

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            Volatile.Write(ref _connected, 0);
            _inbound.Writer.TryComplete();
            _outbound.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            try { await _pumpTask; } catch { }
        }
    }

    private sealed class LoopbackFactory(Func<DuplexLoopbackTransport, Task> script) : ITransportFactory
    {
        public Task<ITransport> ConnectAsync(IReadOnlyList<Uri> endpoints, CancellationToken ct = default)
        {
            var tr = new DuplexLoopbackTransport();
            _ = Task.Run(async () =>
            {
                try { await script(tr); }
                catch { }
            }, ct);
            return Task.FromResult<ITransport>(tr);
        }
    }

    [TestMethod]
    public async Task SqliteHistoryStore_PersistsContactsChatStateGroupsAndResolvesChatNames()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"wa-cli-11b-sql-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "history.db");

        try
        {
            await using var db = new SqliteBackend(dbPath, 1);
            await db.InitializeAsync();
            await db.Device.LoadOrCreateAsync();

            await db.Protocol.UpsertLidPnMappingsAsync([
                new LidPnMappingRecord("15551112222@s.whatsapp.net", "200000000000111@lid")
            ]);

            await db.History.UpsertContactAsync(new ContactRecord(
                "15551112222@s.whatsapp.net",
                Name: "Alice Priya",
                Pushname: "AliceP",
                PhoneNumber: "15551112222@s.whatsapp.net",
                IsAddressBookContact: true));

            await db.History.UpsertGroupAsync(new GroupMetadataRecord(
                "120363000999@g.us",
                "Architecture Gang",
                "15551112222@s.whatsapp.net",
                1710000000,
                "Weekly architecture chat"));

            await db.History.SaveMessageAsync(new MessageRecord(
                "MSG-LID-1",
                "200000000000111@lid",
                "200000000000111@lid",
                FromMe: false,
                Timestamp: 1710000100,
                MessageType.Text,
                "Hello FTS5 world from Alice"));

            await db.History.SaveMessageAsync(new MessageRecord(
                "MSG-GRP-1",
                "120363000999@g.us",
                "15551112222@s.whatsapp.net",
                FromMe: false,
                Timestamp: 1710000200,
                MessageType.Text,
                "Group sprint update"));

            await db.History.UpdateChatStateAsync("200000000000111@lid", pinned: true, muteEndTimestamp: -1);
            await db.History.UpdateChatStateAsync("120363000999@g.us", archived: true);

            IReadOnlyList<ChatSummary> chats = await db.History.GetChatsAsync(limit: 10);
            Assert.AreEqual(2, chats.Count);
            // Pinned LID chat comes first and resolves name via lid_mappings -> contacts
            Assert.AreEqual("200000000000111@lid", chats[0].ChatJid);
            Assert.AreEqual("Alice Priya", chats[0].Name);
            Assert.IsTrue(chats[0].Pinned);
            Assert.AreEqual(-1L, chats[0].MuteEndTimestamp);
            // Group chat resolves subject from groups table
            Assert.AreEqual("120363000999@g.us", chats[1].ChatJid);
            Assert.AreEqual("Architecture Gang", chats[1].Name);
            Assert.IsTrue(chats[1].Archived);

            MessageRecord? byId = await db.History.GetMessageByIdAsync("MSG-LID-1");
            Assert.IsNotNull(byId);
            Assert.AreEqual("Hello FTS5 world from Alice", byId.Text);

            GroupMetadataRecord? grp = await db.History.GetGroupAsync("120363000999@g.us");
            Assert.IsNotNull(grp);
            Assert.AreEqual("Architecture Gang", grp.Subject);

            // CLI read-only commands work against the persisted SQLite DB without connecting
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            await using var rt = await CliRuntime.CreateAndInitializeAsync(
                new CliOptions { DatabasePath = dbPath, QuietEvents = true },
                backend: db,
                output: stdout);
            var disp = new CommandDispatcher(rt, stdout, stderr);

            Assert.AreEqual(0, (await disp.ExecuteAsync("chats list 10")).ExitCode, stderr.ToString());
            Assert.AreEqual(0, (await disp.ExecuteAsync("history 200000000000111@lid 5")).ExitCode, stderr.ToString());
            Assert.AreEqual(0, (await disp.ExecuteAsync("search sprint")).ExitCode, stderr.ToString());
            Assert.AreEqual(0, (await disp.ExecuteAsync("contacts search Alice")).ExitCode, stderr.ToString());
            Assert.AreEqual(0, (await disp.ExecuteAsync("help")).ExitCode, stderr.ToString());

            string text = stdout.ToString();
            Assert.IsTrue(text.Contains("Alice Priya (200000000000111@lid) [PIN][MUTE]"), text);
            Assert.IsTrue(text.Contains("Architecture Gang (120363000999@g.us) [ARC]"), text);
            Assert.IsTrue(text.Contains("Group sprint update"), text);

            // Removing contact clears address book name cleanly
            await db.History.RemoveContactAsync("15551112222@s.whatsapp.net");
            IReadOnlyList<ContactRecord> afterRemove = await db.History.SearchContactsAsync("15551112222");
            Assert.IsFalse(afterRemove[0].IsAddressBookContact);
            Assert.IsNull(afterRemove[0].Name);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [TestMethod]
    public async Task CliMessagingMediaAndGroupCommands_LoopbackEndToEndWork()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"wa-cli-11b-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "profile.db");
        string outStickerPath = Path.Combine(tempDir, "downloaded.webp");
        string uploadImgPath = Path.Combine(tempDir, "upload.jpg");

        try
        {
            byte[] stickerBytes = [0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50];
            MediaUploadEncryptionResult encSticker = MediaCipher.EncryptForUpload(stickerBytes, WaMediaType.Sticker);
            byte[] imageBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
            await File.WriteAllBytesAsync(uploadImgPath, imageBytes);

            var http = new LoopbackHttpClient { NextDownloadBytes = encSticker.DataToUpload };
            KeyPair serverStatic = Curve25519.GenerateKeyPair();
            var sentStanzas = new ConcurrentQueue<Node>();

            var factory = new LoopbackFactory(async tr =>
            {
                byte[] pkt1 = await tr.ReadClientChunkAsync();
                int off = Constants.WaConnHeader.Length;
                int frameLen = (pkt1[off] << 16) | (pkt1[off + 1] << 8) | pkt1[off + 2];
                var hello = global::Whatsapp.HandshakeMessage.Parser.ParseFrom(pkt1.AsSpan(off + 3, frameLen)).ClientHello;

                byte[] clientEph = hello.Ephemeral.ToByteArray();
                KeyPair serverEphemeral = Curve25519.GenerateKeyPair();

                var serverNoise = new NoiseState("Noise_IK_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);
                serverNoise.Authenticate(serverStatic.PublicKey);
                serverNoise.Authenticate(clientEph);
                serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, clientEph));
                byte[] clientStatic = serverNoise.Decrypt(hello.Static.ToByteArray());
                serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverStatic.PrivateKey, clientStatic));
                _ = ClientPayload.Parser.ParseFrom(serverNoise.Decrypt(hello.Payload.ToByteArray()));

                serverNoise.Authenticate(serverEphemeral.PublicKey);
                serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientEph));
                serverNoise.MixKey(Curve25519.CalculateSharedSecret(serverEphemeral.PrivateKey, clientStatic));
                byte[] encCert = serverNoise.Encrypt("ok"u8);

                NoiseKeys sKeys = serverNoise.Split();
                using var codec = new NoiseSessionCodec(new NoiseKeys(sKeys.ReadCipher, sKeys.WriteCipher));
                byte[] EncodeNode(Node n)
                {
                    var enc = new BinaryEncoder();
                    enc.WriteNode(n);
                    return codec.EncryptFrame(BinaryPacker.Pack(enc.ToArray()));
                }

                byte[] helloFrame = Framing.EncodeFrame(
                    WhatsApp.Protocol.Noise.HandshakeMessage.EncodeServerHello(serverEphemeral.PublicKey, null, encCert));
                byte[] successFrame = EncodeNode(new Node("success", new Dictionary<string, NodeValue> { ["lid"] = "111111111111111:0@lid" }));
                byte[] ibFrame = EncodeNode(new Node("ib", new Node[] { new Node("offline", new Dictionary<string, NodeValue> { ["count"] = "0" }) }));
                tr.PushToClient([.. helloFrame, .. successFrame, .. ibFrame]);

                while (tr.IsConnected)
                {
                    byte[] chunk;
                    try { chunk = await tr.ReadClientChunkAsync(); }
                    catch { break; }

                    codec.Feed(chunk);
                    while (codec.TryDecryptNextFrame(out byte[] packed))
                    {
                        Node req = new BinaryDecoder(BinaryPacker.Unpack(packed)).ReadNode();
                        sentStanzas.Enqueue(req);

                        if (req.Tag == "message" && req.GetAttr("id") is { Length: > 0 } msgAckId)
                        {
                            tr.PushToClient(EncodeNode(new Node("ack", new Dictionary<string, NodeValue>
                            {
                                ["id"] = msgAckId,
                                ["class"] = "message"
                            })));
                        }
                        else if (req.Tag == "iq")
                        {
                            string id = req.GetAttr("id") ?? "0";
                            string? xmlns = req.GetAttr("xmlns");
                            if (xmlns == "w:m")
                            {
                                tr.PushToClient(EncodeNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = id, ["type"] = "result" }, new[]
                                {
                                    new Node("media_conn", new Dictionary<string, NodeValue> { ["auth"] = "MOCK_AUTH", ["ttl"] = "3600" }, new[]
                                    {
                                        new Node("host", new Dictionary<string, NodeValue> { ["hostname"] = "mock-cdn.local", ["type"] = "primary" })
                                    })
                                })));
                            }
                            else if (xmlns == "w:g2" && req.GetChild("participating") is not null)
                            {
                                tr.PushToClient(EncodeNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = id, ["type"] = "result" }, new[]
                                {
                                    new Node("groups", new[]
                                    {
                                        new Node("group", new Dictionary<string, NodeValue>
                                        {
                                            ["id"] = "1203630001",
                                            ["subject"] = "Dev Group",
                                            ["creation"] = "1710000000",
                                            ["creator"] = "15550000099@s.whatsapp.net"
                                        }, new[]
                                        {
                                            new Node("participant", new Dictionary<string, NodeValue> { ["jid"] = "15550000099@s.whatsapp.net", ["type"] = "superadmin" }),
                                            new Node("participant", new Dictionary<string, NodeValue> { ["jid"] = "15550000100@s.whatsapp.net" })
                                        })
                                    })
                                })));
                            }
                            else if (xmlns == "w:g2" && req.GetChild("create") is Node createReq)
                            {
                                tr.PushToClient(EncodeNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = id, ["type"] = "result" }, new[]
                                {
                                    new Node("group", new Dictionary<string, NodeValue>
                                    {
                                        ["id"] = "1203639999",
                                        ["subject"] = createReq.GetAttr("subject") ?? "Created",
                                        ["creation"] = "1710000010",
                                        ["creator"] = "15550000099@s.whatsapp.net"
                                    }, new[]
                                    {
                                        new Node("participant", new Dictionary<string, NodeValue> { ["jid"] = "15550000099@s.whatsapp.net", ["type"] = "superadmin" }),
                                        new Node("participant", new Dictionary<string, NodeValue> { ["jid"] = "15550000100@s.whatsapp.net" })
                                    })
                                })));
                            }
                            else if (xmlns == "w:profile:picture")
                            {
                                tr.PushToClient(EncodeNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = id, ["type"] = "result" }, new[]
                                {
                                    new Node("picture", new Dictionary<string, NodeValue> { ["id"] = "pic-999" })
                                })));
                            }
                            else if (xmlns == "w:g2" && req.GetAttr("type") == "set")
                            {
                                tr.PushToClient(EncodeNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = id, ["type"] = "result" })));
                            }
                            else if (xmlns == "usync")
                            {
                                tr.PushToClient(EncodeNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = id, ["type"] = "result" }, new[]
                                {
                                    new Node("usync", new[]
                                    {
                                        new Node("result"),
                                        new Node("list", new[]
                                        {
                                            new Node("user", new Dictionary<string, NodeValue> { ["jid"] = "15550000100@s.whatsapp.net" }, new[]
                                            {
                                                new Node("contact", new Dictionary<string, NodeValue> { ["type"] = "in" })
                                            })
                                        })
                                    })
                                })));
                            }
                            else if (xmlns == "w:mex")
                            {
                                string qid = req.GetChild("query")?.GetAttr("query_id") ?? string.Empty;
                                string mexJson = qid switch
                                {
                                    "25399611239711790" => """{"data":{"xwa2_newsletter_subscribed":[{"id":"1203638888@newsletter","thread_metadata":{"name":{"text":"News Demo"},"subscribers_count":"10"}}]}}""",
                                    "29516483177936279" => """{"data":{"xwa_product_catalog_get_product_catalog":{"product_catalog":{"products":[{"id":"SKU-CLI","name":"Tea"}],"paging":{}}}}}""",
                                    _ => """{"data":{"xwa_extensions_get_flow_data":{"extensions_flow_data":[{"flow_id":"flow-999","extensions":{"metadata":{"flow_name":"cli_flow","state":"PUBLISHED"}}}],"endpoint_public_key":{"key":"PEM","signature":"SIG"}}}}"""
                                };
                                tr.PushToClient(EncodeNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = id, ["type"] = "result" }, new[]
                                {
                                    new Node("result", mexJson)
                                })));
                            }
                            else if (xmlns == "privacy")
                            {
                                tr.PushToClient(EncodeNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = id, ["type"] = "result" }, new[]
                                {
                                    new Node("privacy", new[]
                                    {
                                        new Node("category", new Dictionary<string, NodeValue> { ["name"] = "last", ["value"] = "contacts" })
                                    })
                                })));
                            }
                            else if (xmlns == "blocklist")
                            {
                                tr.PushToClient(EncodeNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = id, ["type"] = "result" }, new[]
                                {
                                    new Node("list", Array.Empty<Node>())
                                })));
                            }
                            else if (xmlns == "w:b")
                            {
                                tr.PushToClient(EncodeNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = id, ["type"] = "result" }, new[]
                                {
                                    new Node("lists", Array.Empty<Node>())
                                })));
                            }
                        }
                        else if (req.Tag == "call")
                        {
                            tr.PushToClient(EncodeNode(new Node("ack", new Dictionary<string, NodeValue>
                            {
                                ["id"] = req.GetAttr("id") ?? "0",
                                ["class"] = "call",
                                ["type"] = (req.Content as NodeContent.NodesContent)?.Nodes.FirstOrDefault()?.Tag ?? "offer"
                            })));
                        }
                    }
                }
            });

            // Seed paired credentials, pre-established Signal sessions, device lists, and synced sticker record
            await using (var seedDb = new SqliteBackend(dbPath, 1))
            {
                await seedDb.InitializeAsync();
                DeviceCredentials c = await seedDb.Device.LoadOrCreateAsync();
                c.Pn = Jid.PnDevice("15550000099", 1);
                c.Lid = Jid.LidDevice("111111111111111", 1);
                c.ServerStaticPublic = serverStatic.PublicKey;
                await seedDb.Device.SaveAsync(c);

                var peerDev = Jid.PnDevice("15550000100", 0);
                var ownPrimary = Jid.PnDevice("15550000099", 0);
                await using var peerMem = new SqliteBackend(":memory:");
                DeviceCredentials peerCreds = await peerMem.Device.LoadOrCreateAsync();
                var peerBundle = new PreKeyBundle(peerCreds.RegistrationId, 0, peerCreds.SignedPreKeyId, peerCreds.SignedPreKey.PublicKey, peerCreds.SignedPreKeySignature, peerCreds.IdentityKey.PublicKey, null, null);
                await seedDb.Signal.SaveIdentityAsync(peerDev.ToProtocolAddress(), peerCreds.IdentityKey.PublicKey);
                await seedDb.Signal.SaveSessionAsync(peerDev.ToProtocolAddress(), SignalCipher.InitiateSession(peerBundle, c.IdentityKey, c.RegistrationId));
                await seedDb.Signal.SaveIdentityAsync(ownPrimary.ToProtocolAddress(), c.IdentityKey.PublicKey);
                await seedDb.Signal.SaveSessionAsync(ownPrimary.ToProtocolAddress(), SignalCipher.InitiateSession(
                    new PreKeyBundle(c.RegistrationId, 0, c.SignedPreKeyId, c.SignedPreKey.PublicKey, c.SignedPreKeySignature, c.IdentityKey.PublicKey, null, null),
                    c.IdentityKey,
                    c.RegistrationId));

                await seedDb.Protocol.SaveDeviceListAsync("15550000100@s.whatsapp.net", [0u]);
                await seedDb.Protocol.SaveDeviceListAsync("15550000099@s.whatsapp.net", [0u, 1u]);

                await seedDb.History.SaveMessageAsync(new MessageRecord(
                    "STK-001",
                    "15550000100@s.whatsapp.net",
                    "15550000100@s.whatsapp.net",
                    FromMe: false,
                    Timestamp: 1710000300,
                    MessageType.Sticker,
                    Text: null,
                    MimeType: "image/webp",
                    DirectPath: "/v/t62.15575-24/stk1.enc",
                    MediaKey: encSticker.MediaKey32,
                    FileHash: encSticker.FileSha256,
                    FileEncHash: encSticker.FileEncSha256,
                    FileLength: encSticker.FileLength));
            }

            var output = new StringWriter();
            var err = new StringWriter();
            var cliOpts = new CliOptions { DatabasePath = dbPath, QuietEvents = true };
            var orchOpts = new ConnectionOrchestratorOptions
            {
                HttpClient = http,
                AutoSendActiveIq = false,
                AutoUploadPreKeys = false,
                AutoSyncAppState = false,
            };

            await using (CliRuntime rt = await CliRuntime.CreateAndInitializeAsync(
                cliOpts,
                transportFactory: factory,
                httpClient: http,
                orchestratorOverrides: orchOpts,
                output: output))
            {
                var disp = new CommandDispatcher(rt, output, err);

                Assert.AreEqual(0, (await disp.ExecuteAsync("connect --timeout-sec 3 --wait-sync-sec 2")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync($"media download STK-001 \"{outStickerPath}\"")).ExitCode, err.ToString());
                CollectionAssert.AreEqual(stickerBytes, await File.ReadAllBytesAsync(outStickerPath));

                Assert.AreEqual(0, (await disp.ExecuteAsync("group list")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("group create \"Clone Room\" 15550000100 --desc \"Hi there\" --add-mode all --link-mode all")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync($"group pic 1203639999@g.us \"{uploadImgPath}\"")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("group settings 1203639999@g.us --locked true --announce true --approval on --ephemeral 86400")).ExitCode, err.ToString());
                GroupMetadataRecord? storedGroup = await rt.Backend.History.GetGroupAsync("1203630001@g.us");
                Assert.IsNotNull(storedGroup);
                Assert.AreEqual("Dev Group", storedGroup.Subject);

                Assert.AreEqual(0, (await disp.ExecuteAsync("usync 15550000100")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("presence available")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("typing 15550000100 composing")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("read 15550000100 STK-001 --self")).ExitCode, err.ToString());

                Assert.AreEqual(0, (await disp.ExecuteAsync("send text 15550000100 Hello live CLI")).ExitCode, err.ToString());
                var helloMsg = (await rt.Backend.History.GetMessagesAsync("15550000100@s.whatsapp.net", null, 5))
                    .First(m => m.Text == "Hello live CLI");
                Assert.AreEqual(0, (await disp.ExecuteAsync($"edit 15550000100 {helloMsg.Id} Edited live CLI")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync($"revoke 15550000100 {helloMsg.Id}")).ExitCode, err.ToString());

                Assert.AreEqual(0, (await disp.ExecuteAsync("send text 15550000100 Tag check --mention 15550000100")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("send text 15550000100 Native all tag check --all")).ExitCode, err.ToString());
                Message allMsg = RichMessageBuilders.BuildMentionText("@all everyone", mentionAll: true);
                Assert.AreEqual(1u, allMsg.ExtendedTextMessage.ContextInfo.NonJidMentions);
                Assert.AreEqual(0, (await disp.ExecuteAsync("reply 15550000100 STK-001 Replying")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("react 15550000100 STK-001 👍")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("pin 15550000100 STK-001 true")).ExitCode, err.ToString());
                Message pinMsg = RichMessageBuilders.BuildPinInChat(new MessageKey { Id = "STK-001", RemoteJid = "15550000100@s.whatsapp.net" }, true, 1700000000000);
                Assert.AreEqual(604_800u, pinMsg.MessageContextInfo.MessageAddOnDurationInSecs);
                Assert.AreEqual(0, (await disp.ExecuteAsync("keep 15550000100 STK-001 true")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("poll create 15550000100 Lunch? --opt Pizza --opt Tacos")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("event create 15550000100 Standup --start 1760000000 --loc HQ")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("send contact 15550000100 15550000100 Alice Example")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync($"send media image 15550000100 \"{uploadImgPath}\" My photo")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync($"send media ptt 15550000100 \"{uploadImgPath}\"")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("pdo 15550000100 10")).ExitCode, err.ToString());

                await rt.Backend.History.SaveMessageAsync(new MessageRecord(
                    "FLOW-001",
                    "15550000100@s.whatsapp.net",
                    "15550000100@s.whatsapp.net",
                    FromMe: false,
                    Timestamp: 1710000350,
                    MessageType.Text,
                    Text: "[Flow: Book Now]",
                    InteractiveJson: WhatsApp.Protocol.Binary.Stanzas.InteractiveMessageParser.Serialize(
                        new WhatsApp.Protocol.Binary.Stanzas.InteractiveMessageContent(
                            Kind: "interactive",
                            Body: "Complete booking",
                            NativeFlowName: "galaxy_message",
                            Flow: new WhatsApp.Protocol.Binary.Stanzas.InteractiveFlowInfo(FlowId: "flow-999", FlowToken: "tok-cli", FlowCta: "Book Now", FlowAction: "navigate")))));

                Assert.AreEqual(0, (await disp.ExecuteAsync("interact list 15550000100 FLOW-001")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("interact location 15550000100 FLOW-001 19.076 72.8777 --name Mumbai")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("interact phone 15550000100 FLOW-001 15550000100 Alice")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("interact address 15550000100 FLOW-001 --field city=Mumbai --field pin=400001")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("interact callperm 15550000100 FLOW-001 accept --exp 1800000000")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("interact flow 15550000100 FLOW-001 --fetch --field slot=morning")).ExitCode, err.ToString());

                // Stage 1 + Phase 11C + Stage 4 Calling CLI checks
                Assert.AreEqual(0, (await disp.ExecuteAsync("send text 15550000100 Look spoiler --spoiler")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("send text 15550000100 Check https://example.com --preview")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("send live-location 15550000100 19.076 72.877 Mumbai")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("send scheduled-call 15550000100 1800000000000 Catchup")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("privacy get")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("block list")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("broadcast list")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("newsletter list")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("business catalog 15550000100")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("safety fingerprint 15550000100")).ExitCode, err.ToString());
                Assert.AreEqual(0, (await disp.ExecuteAsync("call dial 15550000100")).ExitCode, err.ToString());

                var recentMsgs = await rt.Backend.History.GetMessagesAsync("15550000100@s.whatsapp.net", null, 30);
                Assert.IsTrue(recentMsgs.Any(m => m.Text == "@15550000100 Tag check"));
                Assert.IsTrue(recentMsgs.Single(m => m.Id == helloMsg.Id).IsRevoked);
                string? stkRxJson = recentMsgs.Single(m => m.Id == "STK-001").ReactionsJson;
                Assert.IsFalse(string.IsNullOrEmpty(stkRxJson));
                var stkRxMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stkRxJson);
                Assert.AreEqual("👍", stkRxMap?.Values.FirstOrDefault(), $"Actual json={stkRxJson}");
                Assert.IsFalse(recentMsgs.Any(m => m.Type is MessageType.Unknown or MessageType.Protocol));
            }

            string outText = output.ToString();
            Assert.IsTrue(outText.Contains("Dev Group (1203630001@g.us)"), outText);
            Assert.IsTrue(outText.Contains("Clone Room (1203639999@g.us)"), outText);
            Assert.IsTrue(outText.Contains("Updated group picture for 1203639999@g.us: id=pic-999"), outText);
            Assert.IsTrue(outText.Contains("Updated group settings for 1203639999@g.us: locked=True, announce=True, approval=On, ephemeral=86400"), outText);
            Assert.IsTrue(outText.Contains("Flow flow-999: name=cli_flow, state=PUBLISHED"), outText);
            Assert.IsTrue(outText.Contains("Sent galaxy_message flow response"), outText);
            Assert.IsTrue(outText.Contains("Sent address_message response"), outText);
            Assert.IsTrue(outText.Contains("Sent call_permission_request response (accept)"), outText);
            Assert.IsTrue(outText.Contains("Privacy settings (1):"), outText);
            Assert.IsTrue(outText.Contains("Catalog (1 products):"), outText);
            Assert.IsTrue(outText.Contains("News Demo (1203638888@newsletter)"), outText);
            Assert.IsTrue(outText.Contains("Placed audio call to 15550000100@s.whatsapp.net"), outText);
            Assert.IsTrue(outText.Contains("reg=True"), outText);
            Assert.IsTrue(http.Uploads.Count == 2, $"Expected 2 media uploads, got {http.Uploads.Count}");
            Assert.IsTrue(sentStanzas.Any(n => n.Tag == "presence"));
            Assert.IsTrue(sentStanzas.Any(n => n.Tag == "chatstate"));
            Assert.IsTrue(sentStanzas.Any(n => n.Tag == "receipt" && n.GetAttr("type") == "read-self"));
            Assert.IsTrue(sentStanzas.Count(n => n.Tag == "message") >= 8);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
