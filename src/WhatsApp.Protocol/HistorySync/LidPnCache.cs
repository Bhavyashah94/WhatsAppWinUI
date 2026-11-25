using System.Collections.Concurrent;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Protocol.HistorySync;

public enum LidPnLearningSource
{
    Other = 0,
    HistorySync = 1,
    Usync = 2,
    DeviceNotification = 3,
    ContactsNotification = 4,
    UsernameLookup = 5,
    PeerJid = 6,
    BlocklistActive = 7,
}

public sealed record LidPnEntry(
    string PnJid,
    string LidJid,
    long CreatedAtUnixSeconds,
    LidPnLearningSource Source = LidPnLearningSource.Other);

/// <summary>
/// Thread-safe bidirectional cache mapping between Linked IDs (LIDs) and Phone Numbers (PNs),
/// with recency tracking and <see cref="ContactHash"/> lookup for server hash-based notifications.
/// Mirrors <c>whatsapp-rust/src/lid_pn_cache/implementation.rs</c> and <c>WAWebLidPnCache</c>.
/// </summary>
public interface ILidPnCache
{
    void Learn(string pnJid, string lidJid);
    void Learn(string pnJid, string lidJid, LidPnLearningSource source, long? timestampUnixSeconds = null);
    bool TryGetPn(string lidJid, out string? pnJid);
    bool TryGetLid(string pnJid, out string? lidJid);
    IReadOnlyList<string> LookupByContactHash(string base64Hash);
    int Count { get; }
    void Clear();
}

public sealed class LidPnCache : ILidPnCache
{
    private readonly ConcurrentDictionary<string, LidPnEntry> _lidToEntry = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LidPnEntry> _pnToLatestEntry = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _hashToJids = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public LidPnCache(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int Count => _lidToEntry.Count;

    public void Learn(string pnJid, string lidJid) =>
        Learn(pnJid, lidJid, LidPnLearningSource.Other, timestampUnixSeconds: null);

    public void Learn(string pnJid, string lidJid, LidPnLearningSource source, long? timestampUnixSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(pnJid) || string.IsNullOrWhiteSpace(lidJid))
        {
            return;
        }

        string normalizedPn = NormalizeJid(pnJid, "s.whatsapp.net");
        string normalizedLid = NormalizeJid(lidJid, "lid");
        long ts = timestampUnixSeconds ?? _timeProvider.GetUtcNow().ToUnixTimeSeconds();

        var entry = new LidPnEntry(normalizedPn, normalizedLid, ts, source);
        _lidToEntry[normalizedLid] = entry;
        _pnToLatestEntry.AddOrUpdate(
            normalizedPn,
            entry,
            (_, existing) => ts >= existing.CreatedAtUnixSeconds ? entry : existing);

        IndexContactHash(normalizedPn);
        IndexContactHash(normalizedLid);
    }

    public void LearnBatch(IEnumerable<LidPnEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (LidPnEntry entry in entries)
        {
            Learn(entry.PnJid, entry.LidJid, entry.Source, entry.CreatedAtUnixSeconds);
        }
    }

    public void RegisterKnownJid(string jid)
    {
        if (string.IsNullOrWhiteSpace(jid))
        {
            return;
        }

        string normalized = NormalizeJid(jid, "s.whatsapp.net");
        IndexContactHash(normalized);
    }

    public bool TryGetPn(string lidJid, out string? pnJid)
    {
        string normalizedLid = NormalizeJid(lidJid, "lid");
        if (_lidToEntry.TryGetValue(normalizedLid, out LidPnEntry? entry))
        {
            pnJid = entry.PnJid;
            return true;
        }

        pnJid = null;
        return false;
    }

    public bool TryGetLid(string pnJid, out string? lidJid)
    {
        string normalizedPn = NormalizeJid(pnJid, "s.whatsapp.net");
        if (_pnToLatestEntry.TryGetValue(normalizedPn, out LidPnEntry? entry))
        {
            lidJid = entry.LidJid;
            return true;
        }

        lidJid = null;
        return false;
    }

    public IReadOnlyList<string> LookupByContactHash(string base64Hash)
    {
        if (string.IsNullOrWhiteSpace(base64Hash))
        {
            return Array.Empty<string>();
        }

        return _hashToJids.TryGetValue(base64Hash, out ConcurrentDictionary<string, byte>? set)
            ? set.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }

    public IReadOnlyList<LidPnEntry> GetAllEntries() => _lidToEntry.Values.ToArray();

    public void Clear()
    {
        _lidToEntry.Clear();
        _pnToLatestEntry.Clear();
        _hashToJids.Clear();
    }

    private void IndexContactHash(string normalizedJid)
    {
        string hash = ContactHash.ComputeBase64(normalizedJid);
        ConcurrentDictionary<string, byte> jids = _hashToJids.GetOrAdd(
            hash,
            static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        jids[normalizedJid] = 0;
    }

    internal static string NormalizeJid(string raw, string defaultServer)
    {
        string trimmed = raw.Trim();
        int atIndex = trimmed.IndexOf('@');
        if (atIndex == -1)
        {
            string bareUser = StripDevice(trimmed);
            return $"{bareUser}@{defaultServer}";
        }

        string user = StripDevice(trimmed[..atIndex]);
        string server = trimmed[(atIndex + 1)..];

        if (server.Equals("c.us", StringComparison.OrdinalIgnoreCase))
        {
            server = "s.whatsapp.net";
        }
        else if (server.Equals("hosted", StringComparison.OrdinalIgnoreCase))
        {
            server = "s.whatsapp.net";
        }
        else if (server.Equals("hosted.lid", StringComparison.OrdinalIgnoreCase))
        {
            server = "lid";
        }

        return $"{user}@{server.ToLowerInvariant()}";
    }

    private static string StripDevice(string userPart)
    {
        int colon = userPart.IndexOf(':');
        return colon >= 0 ? userPart[..colon] : userPart;
    }
}
