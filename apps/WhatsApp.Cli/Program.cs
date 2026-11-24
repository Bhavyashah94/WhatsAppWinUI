using WhatsApp.Api;
using WhatsApp.Storage.Sqlite;
using WhatsApp.Transport;

Console.WriteLine("WhatsApp.Cli (.NET 10) scaffold ready.");
_ = typeof(WhatsAppApiClient);
_ = typeof(SqliteBackend);
_ = typeof(WebSocketTransportFactory);
