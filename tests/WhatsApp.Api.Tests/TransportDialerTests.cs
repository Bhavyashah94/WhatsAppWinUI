using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Noise;
using WhatsApp.Transport;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class TransportDialerTests
{
    private sealed class MockTransport(Uri endpoint) : ITransport
    {
        public Uri Endpoint { get; } = endpoint;
        public bool IsConnected => !IsDisposed;
        public bool IsDisposed { get; private set; }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(0);
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    public void BuildDefaultEndpoints_EncodesEdgeRoutingAsUnpaddedBase64Url()
    {
        // Live Chrome cold-boot capture vector: ?ED=CAgIIwgS
        byte[] routingBytes = Base64Url.DecodeFromChars("CAgIIwgS");

        IReadOnlyList<Uri> withEd = WhatsAppEndpointBuilder.BuildDefaultEndpoints(routingBytes);
        Assert.AreEqual(2, withEd.Count);
        Assert.AreEqual("wss://web.whatsapp.com/ws/chat?ED=CAgIIwgS", withEd[0].OriginalString);
        Assert.AreEqual("wss://web.whatsapp.com:5222/ws/chat?ED=CAgIIwgS", withEd[1].OriginalString);

        IReadOnlyList<Uri> plain = WhatsAppEndpointBuilder.BuildDefaultEndpoints();
        Assert.AreEqual("wss://web.whatsapp.com/ws/chat", plain[0].OriginalString);
        Assert.AreEqual("wss://web.whatsapp.com:5222/ws/chat", plain[1].OriginalString);
    }

    [TestMethod]
    public void WithEdgeRoutingParam_PreservesQueryAndFragmentAndIgnoresInvalidInputs()
    {
        byte[] routing = [0xFF, 0xFE, 0x01]; // produces '_' and '-' without '=' padding
        string expectedToken = Base64Url.EncodeToString(routing);

        Assert.AreEqual(
            $"wss://web.whatsapp.com/ws/chat?foo=1&ED={expectedToken}#frag",
            WhatsAppEndpointBuilder.WithEdgeRoutingParam("wss://web.whatsapp.com/ws/chat?foo=1#frag", routing));

        Assert.AreEqual(
            WhatsAppEndpointBuilder.PrimaryWebSocketUrl,
            WhatsAppEndpointBuilder.WithEdgeRoutingParam(WhatsAppEndpointBuilder.PrimaryWebSocketUrl, []));

        byte[] oversized = new byte[NoiseHandshakeHeader.MaxEdgeRoutingLength + 1];
        Assert.AreEqual(
            WhatsAppEndpointBuilder.PrimaryWebSocketUrl,
            WhatsAppEndpointBuilder.WithEdgeRoutingParam(WhatsAppEndpointBuilder.PrimaryWebSocketUrl, oversized));
    }

    [TestMethod]
    public async Task ConnectAsync_FirstWinnerSurvivesAndLosingConnectedSocketIsDisposed()
    {
        var created = new ConcurrentDictionary<Uri, MockTransport>();
        var loserCanFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoints = WhatsAppEndpointBuilder.BuildDefaultEndpoints();

        var factory = new WebSocketTransportFactory(async (uri, ct) =>
        {
            if (uri == endpoints[0])
            {
                var fast = new MockTransport(uri);
                created[uri] = fast;
                return fast;
            }

            // Simulate loser returning a socket right as cancellation fires (un-cancelled return race)
            await loserCanFinish.Task.ConfigureAwait(false);
            var slow = new MockTransport(uri);
            created[uri] = slow;
            return slow;
        });

        ITransport winner = await factory.ConnectAsync(endpoints);
        Assert.AreSame(created[endpoints[0]], winner);
        Assert.IsTrue(winner.IsConnected);

        // Allow losing task to return a socket after winner already returned; cleanup must dispose it
        loserCanFinish.SetResult();
        for (int i = 0; i < 40 && !created.ContainsKey(endpoints[1]); i++)
        {
            await Task.Delay(10);
        }
        for (int i = 0; i < 40 && !created[endpoints[1]].IsDisposed; i++)
        {
            await Task.Delay(10);
        }

        Assert.IsFalse(((MockTransport)winner).IsDisposed);
        Assert.IsTrue(created[endpoints[1]].IsDisposed, "Losing socket must be disposed automatically.");
    }

    [TestMethod]
    public async Task ConnectAsync_PrimaryFails_FallbackWakesImmediatelyWithoutWaitingFullStagger()
    {
        var endpoints = WhatsAppEndpointBuilder.BuildDefaultEndpoints();
        var sw = Stopwatch.StartNew();

        var factory = new WebSocketTransportFactory(
            (uri, ct) =>
            {
                if (uri == endpoints[0])
                {
                    return Task.FromException<ITransport>(new InvalidOperationException("Port 443 blocked"));
                }
                return Task.FromResult<ITransport>(new MockTransport(uri));
            },
            staggerDelay: TimeSpan.FromSeconds(5));

        ITransport connected = await factory.ConnectAsync(endpoints);
        sw.Stop();

        Assert.AreEqual(endpoints[1], ((MockTransport)connected).Endpoint);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(2), $"Fallback took too long ({sw.Elapsed}); stagger did not short-circuit.");
    }

    [TestMethod]
    public async Task ConnectAsync_AllEndpointsFail_ThrowsAggregateException()
    {
        var endpoints = WhatsAppEndpointBuilder.BuildDefaultEndpoints();
        var factory = new WebSocketTransportFactory((_, _) =>
            Task.FromException<ITransport>(new TimeoutException("unreachable")));

        await Assert.ThrowsExceptionAsync<AggregateException>(() => factory.ConnectAsync(endpoints));
    }
}
