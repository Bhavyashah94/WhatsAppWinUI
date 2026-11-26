using System.Globalization;
using System.Text;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum RelayCandidateKind
{
    Normal,
    Xpop,
}

public enum RelayLatencyKind
{
    Normal,
    Xpop,
}

public sealed record RelayAddress(
    byte Protocol,
    string? Ipv4,
    string? Ipv6,
    ushort Port);

public sealed record RelayEndpoint(
    uint RelayId,
    string RelayName,
    uint TokenId,
    uint AuthTokenId,
    bool IsFna,
    byte[]? Ipv4Te2Bytes,
    IReadOnlyList<RelayAddress> Addresses,
    uint? C2rRttMs);

public sealed record RelayCandidate(
    RelayCandidateKind Kind,
    RelayEndpoint Endpoint,
    uint? XrttMs,
    uint? MaxPeerC2rRttMs);

public sealed record RelayData(
    byte[]? HbhKey,
    byte[]? HbhKeyAscii,
    byte[]? RelayKey,
    byte[]? RelayKeyAscii,
    uint? WarpMiTagLen,
    string? Uuid,
    uint? SelfPid,
    uint? PeerPid,
    IReadOnlyList<byte[]> RelayTokens,
    IReadOnlyList<byte[]> AuthTokens,
    IReadOnlyList<RelayEndpoint> Endpoints,
    IReadOnlyList<RelayCandidate> Candidates);

public readonly record struct PackedRelayLatency(uint Raw)
{
    public byte Round => (byte)((Raw >> 25) & 63);

    /// <summary>
    /// Signed 24-bit RTT in milliseconds, or <c>null</c> when equal to the unknown/unreachable sentinel (<c>0x7fffff</c>).
    /// </summary>
    public int? RttMs
    {
        get
        {
            int val = unchecked((int)(Raw << 8)) >> 8;
            return val == 0x7F_FFFF ? null : val;
        }
    }

    public static PackedRelayLatency FromRttMs(uint rttMs) => new(0x0200_0000u + rttMs);
    public static string EncodeWire(uint rttMs) => FromRttMs(rttMs).Raw.ToString(CultureInfo.InvariantCulture);
}

public sealed record RelayLatencyEntry(
    RelayLatencyKind Kind,
    PackedRelayLatency Latency,
    byte[] Address,
    string? RelayName = null);

public sealed record RelayLatencyReport(IReadOnlyList<RelayLatencyEntry> Entries);

/// <summary>
/// Parses <c>&lt;relay&gt;</c> server allocations and <c>&lt;relaylatency&gt;</c> entries
/// matching <c>wacore/src/voip_control/relay_parse.rs</c> and <c>wacore/src/stanza/relay_latency.rs</c>.
/// </summary>
public static class RelayParser
{
    public const int HbhKeyLength = 30;
    public const int MaxRelayTokens = 64;
    public const int MaxRelayLatencyEntries = 8;
    public const ushort DefaultRelayPort = 3478;

    public static Node? FindRelay(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.Equals(root.Tag, "relay", StringComparison.Ordinal))
        {
            return root;
        }

        if (root.Content is NodeContent.NodesContent nc)
        {
            foreach (Node child in nc.Nodes)
            {
                Node? found = FindRelay(child);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        return null;
    }

    public static byte[]? DecodeHbhKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return null;
        }

        byte[] decoded = TryDecodeBase64(bytes) ?? bytes.ToArray();
        if (decoded.Length != HbhKeyLength && TryDecodeBase64(decoded) is { Length: HbhKeyLength } inner)
        {
            decoded = inner;
        }
        return decoded.Length == HbhKeyLength ? decoded : null;
    }

    public static byte[] DecodeRelayKey(ReadOnlySpan<byte> bytes) =>
        TryDecodeBase64(bytes) ?? bytes.ToArray();

    public static RelayData? ParseRelayData(Node relayNode)
    {
        ArgumentNullException.ThrowIfNull(relayNode);
        byte[]? keyBytes = GetChildBytes(relayNode, "key");
        byte[]? hbhBytes = GetChildBytes(relayNode, "hbh_key");
        byte[]? warpLenBytes = GetChildBytes(relayNode, "warp_mi_tag_len");
        uint? warpMiTagLen = warpLenBytes is { Length: > 0 }
            && uint.TryParse(Encoding.UTF8.GetString(warpLenBytes).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint wlen)
            && wlen > 0
            ? wlen
            : null;

        IReadOnlyList<byte[]> relayTokens = ParseIndexedTokens(relayNode, "token");
        IReadOnlyList<byte[]> authTokens = ParseIndexedTokens(relayNode, "auth_token");
        List<RelayCandidate>? candidates = ParseRelayCandidates(relayNode);
        if (candidates is null)
        {
            return null;
        }

        var endpoints = candidates.Select(c => c.Endpoint).ToArray();
        uint? selfPid = uint.TryParse(relayNode.GetAttr("self_pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint sp) ? sp : null;
        uint? peerPid = uint.TryParse(relayNode.GetAttr("peer_pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pp) ? pp : null;

        return new RelayData(
            hbhBytes is not null ? DecodeHbhKey(hbhBytes) : null,
            hbhBytes,
            keyBytes is not null ? DecodeRelayKey(keyBytes) : null,
            keyBytes,
            warpMiTagLen,
            relayNode.GetAttr("uuid"),
            selfPid,
            peerPid,
            relayTokens,
            authTokens,
            endpoints,
            candidates);
    }

    public static IReadOnlyList<RelayLatencyEntry> ParseRelayLatencyEntries(Node relayLatencyNode)
    {
        ArgumentNullException.ThrowIfNull(relayLatencyNode);
        var list = new List<RelayLatencyEntry>();
        foreach (Node child in relayLatencyNode.GetChildren("te"))
        {
            if (list.Count >= MaxRelayLatencyEntries)
            {
                break;
            }
            byte[]? addr = GetNodeBytes(child);
            if (addr is not { Length: 6 or 18 })
            {
                continue;
            }

            RelayLatencyKind kind;
            uint rawLatency;
            if (int.TryParse(child.GetAttr("latency"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int norm))
            {
                kind = RelayLatencyKind.Normal;
                rawLatency = unchecked((uint)norm);
            }
            else if (int.TryParse(child.GetAttr("xlatency"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int xpop))
            {
                kind = RelayLatencyKind.Xpop;
                rawLatency = unchecked((uint)xpop);
            }
            else
            {
                continue;
            }

            list.Add(new RelayLatencyEntry(kind, new PackedRelayLatency(rawLatency), addr, child.GetAttr("relay_name")));
        }
        return list;
    }

    private static List<RelayCandidate>? ParseRelayCandidates(Node relayNode)
    {
        var candidates = new List<RelayCandidate>();
        var indexByKey = new Dictionary<(uint RelayId, RelayCandidateKind Kind), int>();

        foreach (Node te2 in relayNode.GetChildren("te2"))
        {
            byte[]? bytes = GetNodeBytes(te2);
            if (bytes is null)
            {
                continue;
            }

            uint relayId = uint.TryParse(te2.GetAttr("relay_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint rid) ? rid : 0;
            string relayName = te2.GetAttr("relay_name") ?? string.Empty;
            uint? normalToken = uint.TryParse(te2.GetAttr("token_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint nt) ? nt : null;
            uint? xpopToken = uint.TryParse(te2.GetAttr("xtoken_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint xt) ? xt : null;
            uint authTokenId = uint.TryParse(te2.GetAttr("auth_token_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint at) ? at : 0;
            bool isFna = string.Equals(te2.GetAttr("fna"), "1", StringComparison.Ordinal)
                || string.Equals(te2.GetAttr("fna"), "true", StringComparison.OrdinalIgnoreCase);
            byte proto = byte.TryParse(te2.GetAttr("protocol"), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte pr) ? pr : (byte)0;
            uint? c2rRtt = uint.TryParse(te2.GetAttr("c2r_rtt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint cr) ? cr : null;
            uint? xrtt = uint.TryParse(te2.GetAttr("xrtt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint xr) ? xr : null;
            uint? maxPeerRtt = uint.TryParse(te2.GetAttr("max_peer_c2r_rtt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint mpr) ? mpr : null;

            RelayAddress? addr = ParseTe2Address(bytes, proto);
            if (addr is null)
            {
                continue;
            }
            byte[]? ipv4Raw = bytes.Length == 6 ? bytes.ToArray() : null;

            var bindings = new List<(RelayCandidateKind Kind, uint TokenId)>(2);
            if (normalToken is uint nTok) bindings.Add((RelayCandidateKind.Normal, nTok));
            if (xpopToken is uint xTok) bindings.Add((RelayCandidateKind.Xpop, xTok));
            if (bindings.Count == 0)
            {
                bindings.Add((RelayCandidateKind.Normal, 0));
            }

            foreach (var (kind, tokId) in bindings)
            {
                var key = (relayId, kind);
                if (indexByKey.TryGetValue(key, out int idx))
                {
                    RelayCandidate existing = candidates[idx];
                    if (existing.Endpoint.TokenId != tokId || existing.Endpoint.AuthTokenId != authTokenId)
                    {
                        return null;
                    }

                    var addrs = new List<RelayAddress>(existing.Endpoint.Addresses);
                    int replaceAt = addrs.FindIndex(a => a.Protocol == addr.Protocol && (a.Ipv4 is not null) == (addr.Ipv4 is not null));
                    if (replaceAt >= 0) addrs[replaceAt] = addr;
                    else addrs.Add(addr);

                    candidates[idx] = existing with
                    {
                        Endpoint = existing.Endpoint with
                        {
                            Ipv4Te2Bytes = ipv4Raw ?? existing.Endpoint.Ipv4Te2Bytes,
                            Addresses = addrs,
                            C2rRttMs = c2rRtt ?? existing.Endpoint.C2rRttMs
                        },
                        XrttMs = xrtt ?? existing.XrttMs,
                        MaxPeerC2rRttMs = maxPeerRtt ?? existing.MaxPeerC2rRttMs
                    };
                }
                else
                {
                    var ep = new RelayEndpoint(
                        relayId,
                        relayName,
                        tokId,
                        authTokenId,
                        isFna,
                        ipv4Raw,
                        [addr],
                        c2rRtt);
                    indexByKey[key] = candidates.Count;
                    candidates.Add(new RelayCandidate(kind, ep, xrtt, maxPeerRtt));
                }
            }
        }

        return candidates;
    }

    private static RelayAddress? ParseTe2Address(byte[] bytes, byte protocol)
    {
        if (bytes.Length == 6)
        {
            string ipv4 = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.{bytes[3]}";
            ushort port = (ushort)((bytes[4] << 8) | bytes[5]);
            return new RelayAddress(protocol, ipv4, null, port);
        }
        if (bytes.Length == 18)
        {
            var sb = new StringBuilder(39);
            for (int i = 0; i < 16; i += 2)
            {
                if (i > 0) sb.Append(':');
                sb.Append(((bytes[i] << 8) | bytes[i + 1]).ToString("x", CultureInfo.InvariantCulture));
            }
            ushort port = (ushort)((bytes[16] << 8) | bytes[17]);
            return new RelayAddress(protocol, null, sb.ToString(), port);
        }
        return null;
    }

    private static List<byte[]> ParseIndexedTokens(Node parent, string tag)
    {
        var tokens = new List<byte[]>();
        foreach (Node node in parent.GetChildren(tag))
        {
            byte[]? data = GetNodeBytes(node);
            if (data is null)
            {
                continue;
            }
            int id = int.TryParse(node.GetAttr("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedId) && parsedId >= 0
                ? parsedId
                : tokens.Count;
            if (id >= MaxRelayTokens)
            {
                continue;
            }
            while (tokens.Count <= id)
            {
                tokens.Add(Array.Empty<byte>());
            }
            tokens[id] = data;
        }
        return tokens;
    }

    private static byte[]? GetChildBytes(Node parent, string tag) =>
        parent.GetChild(tag) is { } child ? GetNodeBytes(child) : null;

    private static byte[]? GetNodeBytes(Node node) => node.Content switch
    {
        NodeContent.BytesContent bc => bc.Bytes,
        NodeContent.StringContent sc => Encoding.UTF8.GetBytes(sc.Text),
        _ => null
    };

    private static byte[]? TryDecodeBase64(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }

        if (text.Length < 4)
        {
            return null;
        }
        foreach (char c in text)
        {
            bool ok = char.IsAsciiLetterOrDigit(c) || c == '+' || c == '/' || c == '=';
            if (!ok) return null;
        }

        int mod = text.Length % 4;
        string padded = mod == 0 ? text : text + new string('=', 4 - mod);
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch
        {
            return null;
        }
    }
}
