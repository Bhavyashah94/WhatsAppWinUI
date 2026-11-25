using System.Collections.Concurrent;
using System.Threading.Channels;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class IqClientAndStanzaRouterTests
{
    private sealed class MockSessionPair : IAsyncDisposable
    {
        private readonly MockTransport _transport;
        private readonly NoiseSessionCodec _serverCodec;

        public NoiseSessionSocket ClientSocket { get; }
        public IqClient Iq { get; }

        public MockSessionPair(long generation = 1, Action<byte[], NoiseSessionCodec>? onClientWire = null)
        {
            byte[] c2sKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
            byte[] s2cKey = Enumerable.Range(61, 32).Select(i => (byte)i).ToArray();

            _serverCodec = new NoiseSessionCodec(new NoiseKeys(new NoiseCipher(s2cKey), new NoiseCipher(c2sKey)));
            _transport = new MockTransport(_serverCodec, onClientWire);

            ClientSocket = new NoiseSessionSocket(
                generation,
                _transport,
                new NoiseKeys(new NoiseCipher(c2sKey), new NoiseCipher(s2cKey)));
            Iq = new IqClient(ClientSocket);
        }

        public Node ReadNextClientNode(CancellationToken ct = default)
        {
            byte[] wire = _transport.Outbound.Reader.ReadAsync(ct).AsTask().GetAwaiter().GetResult();
            _serverCodec.Feed(wire);
            Assert.IsTrue(_serverCodec.TryDecryptNextFrame(out byte[] packed));
            byte[] plain = BinaryPacker.Unpack(packed);
            return new BinaryDecoder(plain).ReadNode();
        }

        public async Task<Node> ReadNextClientNodeAsync(CancellationToken ct = default)
        {
            byte[] wire = await _transport.Outbound.Reader.ReadAsync(ct);
            _serverCodec.Feed(wire);
            Assert.IsTrue(_serverCodec.TryDecryptNextFrame(out byte[] packed));
            byte[] plain = BinaryPacker.Unpack(packed);
            return new BinaryDecoder(plain).ReadNode();
        }

        public void InjectServerNode(Node node) => _transport.EnqueueServerWire(EncryptServerNode(_serverCodec, node));

        public Task CloseServerAsync() => _transport.DisconnectAsync();

        public static byte[] EncryptServerNode(NoiseSessionCodec codec, Node node)
        {
            var encoder = new BinaryEncoder();
            encoder.WriteNode(node);
            byte[] packed = BinaryPacker.Pack(encoder.ToArray());
            return codec.EncryptFrame(packed);
        }

        public async ValueTask DisposeAsync()
        {
            await ClientSocket.DisposeAsync();
            await _transport.DisposeAsync();
            _serverCodec.Dispose();
        }

        private sealed class MockTransport : ITransport
        {
            private readonly NoiseSessionCodec _serverCodec;
            private readonly Action<byte[], NoiseSessionCodec>? _onClientWire;
            private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
            private Memory<byte> _pendingRead;
            private int _closed;

            public Channel<byte[]> Outbound { get; } = Channel.CreateUnbounded<byte[]>();
            public bool IsConnected => Volatile.Read(ref _closed) == 0;

            public MockTransport(NoiseSessionCodec serverCodec, Action<byte[], NoiseSessionCodec>? onClientWire)
            {
                _serverCodec = serverCodec;
                _onClientWire = onClientWire;
            }

            public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
            {
                byte[] copy = data.ToArray();
                Outbound.Writer.TryWrite(copy);
                _onClientWire?.Invoke(copy, _serverCodec);
                return Task.CompletedTask;
            }

            public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                while (_pendingRead.IsEmpty)
                {
                    if (!await _inbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        return 0;
                    }
                    if (_inbound.Reader.TryRead(out byte[]? chunk))
                    {
                        _pendingRead = chunk;
                    }
                }

                int copy = Math.Min(buffer.Length, _pendingRead.Length);
                _pendingRead.Span[..copy].CopyTo(buffer.Span);
                _pendingRead = _pendingRead[copy..];
                return copy;
            }

            public void EnqueueServerWire(byte[] bytes) => _inbound.Writer.TryWrite(bytes);

            public Task DisconnectAsync(CancellationToken ct = default)
            {
                if (Interlocked.Exchange(ref _closed, 1) == 0)
                {
                    _inbound.Writer.TryComplete();
                    Outbound.Writer.TryComplete();
                }
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => new(DisconnectAsync());
        }
    }

    [TestMethod]
    public async Task SendIqAsync_RejectsNonIqStanzaBeforeTransportWrite()
    {
        await using var pair = new MockSessionPair();
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            pair.Iq.SendIqAsync(new Node("message")));
        Assert.AreEqual(0, pair.Iq.PendingCount);
    }

    [TestMethod]
    public async Task SendIqAsync_RejectsDuplicateInFlightIdWithoutReplacingExistingWaiter()
    {
        await using var pair = new MockSessionPair();
        await using var router = new StanzaRouter(pair.ClientSocket, pair.Iq);

        var firstIq = new Node("iq", new Dictionary<string, NodeValue>
        {
            ["id"] = "dup-1",
            ["type"] = "get",
            ["xmlns"] = "w:p",
            ["to"] = "s.whatsapp.net"
        });
        var secondIq = new Node("iq", new Dictionary<string, NodeValue>
        {
            ["id"] = "dup-1",
            ["type"] = "get",
            ["xmlns"] = "w:p",
            ["to"] = "s.whatsapp.net"
        });

        Task<Node> firstTask = pair.Iq.SendIqAsync(firstIq);
        _ = await pair.ReadNextClientNodeAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            pair.Iq.SendIqAsync(secondIq));
        Assert.AreEqual(1, pair.Iq.PendingCount);

        pair.InjectServerNode(new Node("iq", new Dictionary<string, NodeValue>
        {
            ["id"] = "dup-1",
            ["type"] = "result",
            ["from"] = "s.whatsapp.net"
        }));

        Node result = await firstTask;
        Assert.AreEqual("result", result.GetAttr("type"));
        Assert.AreEqual(0, pair.Iq.PendingCount);
    }

    [TestMethod]
    public async Task SendIqAsync_CorrelatesFastReplyEvenBeforeSendCompletes()
    {
        StanzaRouter? router = null;
        MockSessionPair? pair = null;

        pair = new MockSessionPair(generation: 3, onClientWire: (wire, codec) =>
        {
            codec.Feed(wire);
            if (codec.TryDecryptNextFrame(out byte[] packed))
            {
                Node req = new BinaryDecoder(BinaryPacker.Unpack(packed)).ReadNode();
                string? id = req.GetAttr("id");
                if (id != null)
                {
                    pair!.InjectServerNode(new Node("iq", new Dictionary<string, NodeValue>
                    {
                        ["id"] = id,
                        ["type"] = "result",
                        ["from"] = "s.whatsapp.net"
                    }, new Node[] { new("pong") }));
                }
            }
        });

        router = new StanzaRouter(pair.ClientSocket, pair.Iq);
        try
        {
            Node response = await pair.Iq.SendIqAsync(PingIq.BuildPing("fast-1"));
            Assert.IsNotNull(response.GetChild("pong"));
            Assert.AreEqual(0, pair.Iq.PendingCount);
        }
        finally
        {
            await router.DisposeAsync();
            await pair.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SendIqAsync_ServerErrorPreservesCodeTextFullStanzaAndHandlesMissingOptionalAttrs()
    {
        await using var pair = new MockSessionPair();
        await using var router = new StanzaRouter(pair.ClientSocket, pair.Iq);

        // 1. Rich error with backoff attribute and child node
        Task<Node> req1 = pair.Iq.SendIqAsync(PingIq.BuildPing("err-rich"));
        _ = await pair.ReadNextClientNodeAsync();

        var errChild = new Node("error", new Dictionary<string, NodeValue>
        {
            ["code"] = "403",
            ["text"] = "forbidden",
            ["type"] = "cancel",
            ["backoff"] = "30"
        }, new Node[] { new("reason", new NodeContent.StringContent("blocked")) });

        pair.InjectServerNode(new Node("iq", new Dictionary<string, NodeValue>
        {
            ["id"] = "err-rich",
            ["type"] = "error",
            ["from"] = "s.whatsapp.net"
        }, new Node[] { errChild }));

        var ex1 = await Assert.ThrowsExceptionAsync<IqException>(() => req1);
        Assert.AreEqual("err-rich", ex1.RequestId);
        Assert.AreEqual(403, ex1.ErrorCode);
        Assert.AreEqual("forbidden", ex1.ErrorText);
        Assert.AreEqual("cancel", ex1.ErrorType);
        Assert.AreEqual("30", ex1.ResponseStanza?.GetChild("error")?.GetAttr("backoff"));
        Assert.IsNotNull(ex1.ResponseStanza?.GetChild("error")?.GetChild("reason"));

        // 2. Minimal error without code/text still raises typed IqException
        Task<Node> req2 = pair.Iq.SendIqAsync(PingIq.BuildPing("err-minimal"));
        _ = await pair.ReadNextClientNodeAsync();
        pair.InjectServerNode(new Node("iq", new Dictionary<string, NodeValue>
        {
            ["id"] = "err-minimal",
            ["type"] = "error",
            ["from"] = "s.whatsapp.net"
        }));

        var ex2 = await Assert.ThrowsExceptionAsync<IqException>(() => req2);
        Assert.IsNull(ex2.ErrorCode);
        Assert.IsNull(ex2.ErrorText);
        Assert.AreEqual(0, pair.Iq.PendingCount);
    }

    [TestMethod]
    public async Task SendIqAsync_TimeoutCancellationAndDisconnect_AlwaysCleanWaiterMap()
    {
        await using var pair = new MockSessionPair();
        await using var router = new StanzaRouter(pair.ClientSocket, pair.Iq);

        // Timeout cleans waiter
        await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            pair.Iq.SendIqAsync(PingIq.BuildPing("iq-timeout"), timeout: TimeSpan.FromMilliseconds(40)));
        _ = await pair.ReadNextClientNodeAsync();
        Assert.AreEqual(0, pair.Iq.PendingCount);

        // Caller cancellation cleans waiter
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            pair.Iq.SendIqAsync(PingIq.BuildPing("iq-cancel"), TimeSpan.FromSeconds(30), cts.Token));
        _ = await pair.ReadNextClientNodeAsync();
        Assert.AreEqual(0, pair.Iq.PendingCount);

        // Disconnect cancels pending waiter cleanly
        Task<Node> hangingTask = pair.Iq.SendIqAsync(PingIq.BuildPing("iq-drop"), TimeSpan.FromSeconds(30));
        _ = await pair.ReadNextClientNodeAsync();
        Assert.AreEqual(1, pair.Iq.PendingCount);

        await pair.CloseServerAsync();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => hangingTask);
        Assert.AreEqual(0, pair.Iq.PendingCount);
    }

    [TestMethod]
    public async Task StanzaRouter_DispatchesAllInboundTagsAndDoesNotSwallowServerInitiatedIqs()
    {
        await using var pair = new MockSessionPair(generation: 7);
        var routed = new ConcurrentQueue<string>();
        var doneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Record(string tag)
        {
            routed.Enqueue(tag);
            if (routed.Count >= 9)
            {
                doneTcs.TrySetResult();
            }
        }

        await using var router = new StanzaRouter(pair.ClientSocket, pair.Iq, new StanzaHandlers
        {
            OnIqRequest = (n, _) => { Record($"iq:{n.GetAttr("type")}"); return ValueTask.CompletedTask; },
            OnIb = (_, _) => { Record("ib"); return ValueTask.CompletedTask; },
            OnSuccess = (_, _) => { Record("success"); return ValueTask.CompletedTask; },
            OnFailure = (_, _) => { Record("failure"); return ValueTask.CompletedTask; },
            OnStreamError = (_, _) => { Record("stream:error"); return ValueTask.CompletedTask; },
            OnAck = (_, _) => { Record("ack"); return ValueTask.CompletedTask; },
            OnReceipt = (_, _) => { Record("receipt"); return ValueTask.CompletedTask; },
            OnNotification = (_, _) => { Record("notification"); return ValueTask.CompletedTask; },
            OnMessage = (_, _) => { Record("message"); return ValueTask.CompletedTask; }
        });

        Assert.AreEqual(7L, router.Generation);

        pair.InjectServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = "srv-1", ["type"] = "set" }));
        pair.InjectServerNode(new Node("ib"));
        pair.InjectServerNode(new Node("success"));
        pair.InjectServerNode(new Node("failure"));
        pair.InjectServerNode(new Node("stream:error"));
        pair.InjectServerNode(new Node("ack"));
        pair.InjectServerNode(new Node("receipt"));
        pair.InjectServerNode(new Node("notification"));
        pair.InjectServerNode(new Node("message"));

        await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEquivalent(
            new[] { "iq:set", "ib", "success", "failure", "stream:error", "ack", "receipt", "notification", "message" },
            routed.ToArray());
    }
}
