using System.Buffers;
using System.IO.Pipelines;
using System.Net.WebSockets;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Noise;
using WhatsApp.Transport;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class WebSocketTransportPipelineTests
{
    private sealed class DuplexPipeStream(PipeReader reader, PipeWriter writer) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Buffer.IsEmpty)
                {
                    int toCopy = (int)Math.Min(buffer.Length, result.Buffer.Length);
                    var slice = result.Buffer.Slice(0, toCopy);
                    slice.CopyTo(buffer.Span);
                    reader.AdvanceTo(slice.End);
                    return toCopy;
                }
                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted) return 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return;
            await writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                writer.Complete();
                reader.Complete();
            }
            base.Dispose(disposing);
        }
    }

    private static (WebSocket ClientWs, WebSocket ServerWs, DuplexPipeStream ClientStream, DuplexPipeStream ServerStream) CreateLoopbackWebSockets()
    {
        var c2s = new Pipe();
        var s2c = new Pipe();
        var clientStream = new DuplexPipeStream(s2c.Reader, c2s.Writer);
        var serverStream = new DuplexPipeStream(c2s.Reader, s2c.Writer);

        var clientWs = WebSocket.CreateFromStream(clientStream, new WebSocketCreationOptions { IsServer = false });
        var serverWs = WebSocket.CreateFromStream(serverStream, new WebSocketCreationOptions { IsServer = true });
        return (clientWs, serverWs, clientStream, serverStream);
    }

    [TestMethod]
    public async Task WebSocketTransport_ReassemblesMultiFragmentBinaryMessagesAndDecodesNoiseFrames()
    {
        var (clientWs, serverWs, clientStream, serverStream) = CreateLoopbackWebSockets();
        await using var clientTransport = new WebSocketTransport(clientWs);
        using var serverSocket = serverWs;
        using var cs = clientStream;
        using var ss = serverStream;

        Assert.IsTrue(clientTransport.IsConnected);

        // Encrypt two binary WhatsApp stanzas via NoiseSessionCodec and slice across multiple RFC 6455 fragments
        byte[] s2cKey = Enumerable.Range(11, 32).Select(i => (byte)i).ToArray();
        byte[] c2sKey = Enumerable.Range(51, 32).Select(i => (byte)i).ToArray();
        using var serverCodec = new NoiseSessionCodec(new NoiseKeys(new NoiseCipher(s2cKey), new NoiseCipher(c2sKey)));
        using var clientCodec = new NoiseSessionCodec(new NoiseKeys(new NoiseCipher(c2sKey), new NoiseCipher(s2cKey)));

        byte[] pingWire = PackNode(PingIq.BuildPing("iq-pipe-1"));
        byte[] activeWire = PackNode(PassiveIq.BuildActive("iq-pipe-2"));
        byte[] combinedWire = [.. serverCodec.EncryptFrame(pingWire), .. serverCodec.EncryptFrame(activeWire)];

        // Server sends combined Noise frames split into small RFC 6455 fragments (endOfMessage: false -> false -> true)
        int split1 = combinedWire.Length / 3;
        int split2 = (combinedWire.Length * 2) / 3;
        await serverSocket.SendAsync(combinedWire.AsMemory(0, split1), WebSocketMessageType.Binary, endOfMessage: false, CancellationToken.None);
        await serverSocket.SendAsync(combinedWire.AsMemory(split1, split2 - split1), WebSocketMessageType.Binary, endOfMessage: false, CancellationToken.None);
        await serverSocket.SendAsync(combinedWire.AsMemory(split2), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

        // Client receives via small 13-byte transport reads backed by System.IO.Pipelines
        var decoded = new List<Node>();
        byte[] chunk = new byte[13];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (decoded.Count < 2)
        {
            int n = await clientTransport.ReceiveAsync(chunk, cts.Token);
            Assert.IsTrue(n > 0);
            clientCodec.Feed(chunk.AsSpan(0, n));
            while (clientCodec.TryDecryptNextFrame(out byte[] plain))
            {
                decoded.Add(UnpackNode(plain));
            }
        }

        Assert.AreEqual(2, decoded.Count);
        Assert.AreEqual("w:p", decoded[0].GetAttr("xmlns"));
        Assert.AreEqual("passive", decoded[1].GetAttr("xmlns"));
        Assert.IsNotNull(decoded[1].GetChild("active"));

        // Verify clean Close frame transitions IsConnected and yields 0 on ReceiveAsync
        await serverSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
        int eof = await clientTransport.ReceiveAsync(chunk, cts.Token);
        Assert.AreEqual(0, eof);
        Assert.IsFalse(clientTransport.IsConnected);
        Assert.IsNotNull(clientTransport.DisconnectReason);
        Assert.AreEqual(TransportDisconnectKind.ServerClose, clientTransport.DisconnectReason.Kind);
        Assert.AreEqual(WebSocketCloseStatus.NormalClosure, clientTransport.DisconnectReason.CloseStatus);
        Assert.AreEqual("done", clientTransport.DisconnectReason.Description);
    }

    [TestMethod]
    public async Task WebSocketTransport_SerializesConcurrentSendsAndFiltersNonBinaryFrames()
    {
        var (clientWs, serverWs, clientStream, serverStream) = CreateLoopbackWebSockets();
        await using var clientTransport = new WebSocketTransport(clientWs);
        using var serverSocket = serverWs;
        using var cs = clientStream;
        using var ss = serverStream;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Fire 16 concurrent SendAsync calls simultaneously; SemaphoreSlim must prevent WebSocket InvalidOperationException
        byte[] payload = Enumerable.Repeat((byte)0x5A, 64).ToArray();
        Task[] sends = Enumerable.Range(0, 16)
            .Select(_ => clientTransport.SendAsync(payload, cts.Token))
            .ToArray();

        int receivedCount = 0;
        byte[] sbuf = new byte[128];
        while (receivedCount < 16)
        {
            var r = await serverSocket.ReceiveAsync(sbuf, cts.Token);
            Assert.AreEqual(WebSocketMessageType.Binary, r.MessageType);
            receivedCount += r.Count == payload.Length ? 1 : 0;
        }
        await Task.WhenAll(sends);

        // Server sends an unexpected Text frame followed by a Binary frame; Text must be ignored
        await serverSocket.SendAsync("ignore-me"u8.ToArray(), WebSocketMessageType.Text, endOfMessage: true, cts.Token);
        await serverSocket.SendAsync("ok"u8.ToArray(), WebSocketMessageType.Binary, endOfMessage: true, cts.Token);

        byte[] readBuf = new byte[16];
        int n = await clientTransport.ReceiveAsync(readBuf, cts.Token);
        Assert.AreEqual(2, n);
        CollectionAssert.AreEqual("ok"u8.ToArray(), readBuf[..2]);
    }

    [TestMethod]
    public async Task WebSocketTransport_SendAsyncThrowsAfterDisconnect()
    {
        var (clientWs, serverWs, clientStream, serverStream) = CreateLoopbackWebSockets();
        await using var clientTransport = new WebSocketTransport(clientWs);
        using var serverSocket = serverWs;
        using var cs = clientStream;
        using var ss = serverStream;

        // Read client SendAsync bytes on server, then close
        await clientTransport.SendAsync("ping"u8.ToArray());
        byte[] serverBuf = new byte[64];
        var serverRead = await serverSocket.ReceiveAsync(serverBuf, CancellationToken.None);
        Assert.AreEqual(4, serverRead.Count);

        await clientTransport.DisconnectAsync();
        Assert.IsFalse(clientTransport.IsConnected);
        Assert.AreEqual(TransportDisconnectKind.LocalDisconnect, clientTransport.DisconnectReason?.Kind);
        await Assert.ThrowsExceptionAsync<WebSocketException>(() => clientTransport.SendAsync("after-close"u8.ToArray()));
    }

    private static byte[] PackNode(Node node)
    {
        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        return BinaryPacker.Pack(encoder.ToArray());
    }

    private static Node UnpackNode(byte[] packed)
    {
        byte[] unpacked = BinaryPacker.Unpack(packed);
        return new BinaryDecoder(unpacked).ReadNode();
    }
}
