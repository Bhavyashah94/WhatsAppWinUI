using WhatsApp.Api;
using WhatsApp.Storage.Sqlite;
using WhatsApp.Transport;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class ApiScaffoldTests
{
    [TestMethod]
    public async Task WhatsAppApiClient_ConstructsAndDisposesCleanly()
    {
        await using var backend = new SqliteBackend(":memory:");
        var transportFactory = new WebSocketTransportFactory();
        using var httpClient = new WhatsAppHttpClient();
        await using var api = new WhatsAppApiClient(backend, transportFactory, httpClient);

        Assert.IsNotNull(api.Events);
    }
}
