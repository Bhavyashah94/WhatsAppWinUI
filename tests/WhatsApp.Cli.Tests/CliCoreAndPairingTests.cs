using System.Buffers;
using System.IO.Pipelines;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WhatsApp.Api;
using WhatsApp.Protocol;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;
using WhatsApp.Storage.Sqlite;
using Whatsapp;

namespace WhatsApp.Cli.Tests;

[TestClass]
public sealed class CliCoreAndPairingTests
{
    private static readonly List<string> TestContextLogger = [];
    private sealed class LoopbackTransport : ITransport
    {
        private readonly System.Threading.Channels.Channel<byte[]> _inbound = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly System.Threading.Channels.Channel<byte[]> _outbound = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly Pipe _pipe = new();
        private readonly Task _pumpTask;
        private int _connected = 1;

        public LoopbackTransport()
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
                if (res.IsCompleted)
                {
                    return 0;
                }
            }
        }

        public void PushToClient(byte[] wireFrame) => _inbound.Writer.TryWrite(wireFrame);
        public async Task<byte[]> ReadClientFrameAsync(CancellationToken ct = default) => await _outbound.Reader.ReadAsync(ct);

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

    private sealed class ScriptedServerFactory : ITransportFactory
    {
        private readonly Func<LoopbackTransport, Task> _serverScript;
        private readonly List<Task> _tasks = [];
        public int ConnectionCount { get; private set; }

        public ScriptedServerFactory(Func<LoopbackTransport, Task> serverScript) =>
            _serverScript = serverScript;

        public Task<ITransport> ConnectAsync(IReadOnlyList<Uri> endpoints, CancellationToken ct = default)
        {
            ConnectionCount++;
            var tr = new LoopbackTransport();
            _tasks.Add(Task.Run(async () =>
            {
                try { await _serverScript(tr); }
                catch (Exception ex) { TestContextLogger.Add(ex.ToString()); }
            }));
            return Task.FromResult<ITransport>(tr);
        }
    }

    [TestMethod]
    public void CliOptions_And_CommandTokenizer_ParseFlagsAndQuotesCorrectly()
    {
        CliOptions opts = CliOptions.Parse([
            "--profile", "work_1",
            "--device-id", "2",
            "--push-name", "My PC",
            "--json-events",
            "--cmd", "pair phone +15550001234"
        ]);
        Assert.AreEqual("work_1", opts.Profile);
        Assert.AreEqual(2, opts.DeviceId);
        Assert.AreEqual("My PC", opts.PushName);
        Assert.IsTrue(opts.JsonEvents);
        Assert.AreEqual("pair phone +15550001234", opts.SingleCommand);
        Assert.IsTrue(opts.ResolveDatabasePath().EndsWith(Path.Combine("profiles", "work_1", "wa.db"), StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<string> tokens = CommandTokenizer.Tokenize("send text \"1555@s.whatsapp.net\" 'hello \\'world\\''");
        CollectionAssert.AreEqual(new[] { "send", "text", "1555@s.whatsapp.net", "hello 'world'" }, tokens.ToArray());

        Assert.ThrowsException<FormatException>(() => CommandTokenizer.Tokenize("send \"unterminated"));
    }

    [TestMethod]
    public void QrConsoleRenderer_RendersWhatsAppPairingMatrixAndRedactsSecrets()
    {
        string waPayload = "2@LongPairingRefPayloadABCDEF0123456789+/=,NoisePubBase640123456789ABCDEF=,IdentityPubBase64=,AdvSecretBase64=";
        string ascii = QrConsoleRenderer.RenderUnicodeHalfBlocks(waPayload);
        Assert.IsTrue(ascii.Contains('█') && ascii.Contains('▀') && ascii.Contains('▄'));

        string redacted = QrConsoleRenderer.RedactSecret(new byte[32]);
        Assert.AreEqual("<redacted:32B>", redacted);
        Assert.AreEqual("***7890", QrConsoleRenderer.RedactString("secret12347890", 4));
    }

    [TestMethod]
    public void CliEventFormatter_FormatsHumanAndJsonWithoutLeakingKeys()
    {
        var qr = new QrCodeEvent("2@VERY_SECRET_QR_TOKEN_VALUE_ABCD", TimeSpan.FromSeconds(60));
        string human = CliEventFormatter.FormatHuman(qr);
        Assert.IsFalse(human.Contains("VERY_SECRET_QR_TOKEN", StringComparison.Ordinal));
        Assert.IsTrue(human.Contains("***ABCD"));

        string json = CliEventFormatter.FormatJson(new ConnectedEvent("1555@s.whatsapp.net", "111@lid"), DateTimeOffset.UnixEpoch);
        Assert.IsTrue(json.Contains("\"type\":\"ConnectedEvent\""));
        Assert.IsTrue(json.Contains("1555@s.whatsapp.net"));
    }

    [TestMethod]
    public async Task CliRuntime_And_CommandDispatcher_StatusConnectReconnectAndResetWork()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"wa-cli-11a-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "profile.db");

        try
        {
            KeyPair serverStatic = Curve25519.GenerateKeyPair();
            var serverFactory = new ScriptedServerFactory(async tr =>
            {
                byte[] pkt1 = await tr.ReadClientFrameAsync();
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
                byte[] EncryptNode(Node n)
                {
                    var enc = new BinaryEncoder();
                    enc.WriteNode(n);
                    return codec.EncryptFrame(BinaryPacker.Pack(enc.ToArray()));
                }

                byte[] helloFrame = Framing.EncodeFrame(
                    WhatsApp.Protocol.Noise.HandshakeMessage.EncodeServerHello(serverEphemeral.PublicKey, null, encCert));
                byte[] successFrame = EncryptNode(new Node("success", new Dictionary<string, NodeValue> { ["lid"] = "111111111111111:0@lid" }));
                byte[] ibFrame = EncryptNode(new Node("ib", new Node[]
                {
                    new Node("offline", new Dictionary<string, NodeValue> { ["count"] = "0" })
                }));
                tr.PushToClient([.. helloFrame, .. successFrame, .. ibFrame]);

                while (tr.IsConnected)
                {
                    await Task.Delay(50);
                }
            });

            // Seed paired credentials so connect uses IK & cold-boot reload preserves ServerStaticPublic
            await using (var seedDb = new SqliteBackend(dbPath, 1))
            {
                await seedDb.InitializeAsync();
                DeviceCredentials c = await seedDb.Device.LoadOrCreateAsync();
                c.Pn = Jid.PnDevice("15550000099", 1);
                c.Lid = Jid.LidDevice("111111111111111", 1);
                c.ServerStaticPublic = serverStatic.PublicKey;
                await seedDb.Device.SaveAsync(c);
            }

            var output = new StringWriter();
            var err = new StringWriter();
            var cliOpts = new CliOptions { DatabasePath = dbPath, QuietEvents = true };
            var orchOpts = new ConnectionOrchestratorOptions
            {
                AutoSendActiveIq = false,
                AutoUploadPreKeys = false,
                AutoSyncAppState = false,
            };

            await using (CliRuntime rt = await CliRuntime.CreateAndInitializeAsync(
                cliOpts,
                transportFactory: serverFactory,
                orchestratorOverrides: orchOpts,
                output: output))
            {
                var disp = new CommandDispatcher(rt, output, err);

                string Evts() => string.Join(" | ", rt.GetRecentEvents(20).Select(e => CliEventFormatter.FormatHuman(e.Event)));
                Assert.AreEqual(0, (await disp.ExecuteAsync("connect --timeout-sec 2 --wait-sync-sec 2")).ExitCode, $"err={err} evts={Evts()} logs={string.Join("; ", TestContextLogger)}");
                Assert.AreEqual(0, (await disp.ExecuteAsync("status")).ExitCode, $"err={err} evts={Evts()} logs={string.Join("; ", TestContextLogger)}");
                Assert.AreEqual(0, (await disp.ExecuteAsync("reconnect --timeout-sec 2 --wait-sync-sec 2")).ExitCode, $"err={err} evts={Evts()} logs={string.Join("; ", TestContextLogger)}");
                Assert.AreEqual(2, serverFactory.ConnectionCount);

                Assert.AreEqual(1, (await disp.ExecuteAsync("reset-pairing")).ExitCode);
                Assert.AreEqual(0, (await disp.ExecuteAsync("reset-pairing --confirm")).ExitCode);
            }

            string text = output.ToString();
            Assert.IsTrue(text.Contains("Offline drain complete: processed=0"), text);
            Assert.IsTrue(text.Contains("IK ready"), text);
            Assert.IsTrue(text.Contains("Cleared pairing credentials"), text);
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
