using System.Collections.Concurrent;

namespace WhatsApp.Protocol.HistorySync;

/// <summary>
/// Thread-safe bidirectional cache mapping between Linked IDs (LIDs) and Phone Numbers (PNs).
/// Conforms to Rust reference whatsapp_rust::lid_pn_cache and WAWebLidPnCache.
/// </summary>
public interface ILidPnCache
{
    void Learn(string pnJid, string lidJid);
    bool TryGetPn(string lidJid, out string? pnJid);
    bool TryGetLid(string pnJid, out string? lidJid);
    int Count { get; }
    void Clear();
}

public sealed class LidPnCache : ILidPnCache
{
    private readonly ConcurrentDictionary<string, string> _lidToPn = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _pnToLid = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _lidToPn.Count;

    public void Learn(string pnJid, string lidJid)
    {
        if (string.IsNullOrWhiteSpace(pnJid) || string.IsNullOrWhiteSpace(lidJid))
        {
            return;
        }

        string normalizedPn = NormalizeJid(pnJid, "s.whatsapp.net");
        string normalizedLid = NormalizeJid(lidJid, "lid");

        _lidToPn[normalizedLid] = normalizedPn;
        _pnToLid[normalizedPn] = normalizedLid;
    }

    public bool TryGetPn(string lidJid, out string? pnJid)
    {
        string normalizedLid = NormalizeJid(lidJid, "lid");
        return _lidToPn.TryGetValue(normalizedLid, out pnJid);
    }

    public bool TryGetLid(string pnJid, out string? lidJid)
    {
        string normalizedPn = NormalizeJid(pnJid, "s.whatsapp.net");
        return _pnToLid.TryGetValue(normalizedPn, out lidJid);
    }

    public void Clear()
    {
        _lidToPn.Clear();
        _pnToLid.Clear();
    }

    private static string NormalizeJid(string raw, string defaultServer)
    {
        int atIndex = raw.IndexOf('@');
        if (atIndex == -1)
        {
            return $"{raw}@{defaultServer}";
        }

        string user = raw[..atIndex];
        string server = raw[(atIndex + 1)..];

        // Normalize @c.us to @s.whatsapp.net for phone numbers
        if (server.Equals("c.us", StringComparison.OrdinalIgnoreCase))
        {
            server = "s.whatsapp.net";
        }

        return $"{user}@{server.ToLowerInvariant()}";
    }
}
