using System.Globalization;
using System.Text;
using Google.Protobuf;
using Whatsapp;

namespace WhatsApp.Protocol.Binary.Stanzas;

/// <summary>
/// Parsed <c>&lt;pair-success&gt;</c> metadata from an inbound WhatsApp pairing IQ.
/// </summary>
public sealed record PairSuccessPayload(
    string RequestId,
    byte[] DeviceIdentityBytes,
    Jid? DevicePnJid,
    Jid? DeviceLidJid,
    string? PushName,
    ClientPairingProps? ClientProps);

/// <summary>
/// Stanza builders and parsers for inbound QR pairing (<c>pair-device</c>, <c>pair-success</c>,
/// <c>pair-device-sign</c>, and pairing error/result acknowledgments).
/// Mirrors <c>wacore/src/pair.rs</c>.
/// </summary>
public static class PairingStanzas
{
    /// <summary>
    /// Builds an empty result acknowledgment IQ for an inbound server IQ (e.g. <c>pair-device</c>).
    /// </summary>
    public static Node BuildResultAck(string reqId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reqId);
        return new Node(
            "iq",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["to"] = StanzaBuilder.ServerPn,
                ["id"] = reqId,
                ["type"] = "result"
            });
    }

    /// <summary>
    /// Builds an IQ error response (e.g. 401 <c>hmac-mismatch</c>, 401 <c>signature-mismatch</c>,
    /// 500 <c>internal-error</c>) matching <c>PairUtils::build_pair_error_node</c>.
    /// </summary>
    public static Node BuildPairError(string reqId, int code, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reqId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var errorNode = new Node(
            "error",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["code"] = code.ToString(CultureInfo.InvariantCulture),
                ["text"] = text
            });

        return new Node(
            "iq",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["to"] = StanzaBuilder.ServerPn,
                ["id"] = reqId,
                ["type"] = "error"
            },
            new Node[] { errorNode });
    }

    /// <summary>
    /// Builds the <c>pair-device-sign</c> IQ result response carrying the companion's
    /// self-signed <see cref="ADVSignedDeviceIdentity"/> bytes and <paramref name="keyIndex"/>.
    /// </summary>
    public static Node BuildPairSuccessResponse(
        string reqId,
        byte[] selfSignedDeviceIdentityBytes,
        uint keyIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reqId);
        ArgumentNullException.ThrowIfNull(selfSignedDeviceIdentityBytes);

        var deviceIdentityNode = new Node(
            "device-identity",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["key-index"] = keyIndex.ToString(CultureInfo.InvariantCulture)
            },
            selfSignedDeviceIdentityBytes);

        var pairSignNode = new Node("pair-device-sign", new Node[] { deviceIdentityNode });

        return new Node(
            "iq",
            new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["to"] = StanzaBuilder.ServerPn,
                ["id"] = reqId,
                ["type"] = "result"
            },
            new Node[] { pairSignNode });
    }

    /// <summary>
    /// Extracts the <c>&lt;ref&gt;</c> token list from an inbound <c>&lt;iq type="set"&gt;&lt;pair-device&gt;</c> stanza.
    /// </summary>
    public static bool TryParsePairDeviceRefs(Node iqNode, out string reqId, out IReadOnlyList<string> refs)
    {
        reqId = string.Empty;
        refs = Array.Empty<string>();

        if (iqNode.Tag != "iq")
        {
            return false;
        }

        string? id = iqNode.GetAttr("id");
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        var pairDevice = iqNode.GetChild("pair-device");
        if (pairDevice is null)
        {
            return false;
        }

        var parsed = new List<string>();
        foreach (var child in pairDevice.GetChildren("ref"))
        {
            string? value = child.Content switch
            {
                NodeContent.StringContent s => s.Text,
                NodeContent.BytesContent b => Encoding.UTF8.GetString(b.Bytes),
                _ => null
            };
            if (!string.IsNullOrEmpty(value))
            {
                parsed.Add(value);
            }
        }

        reqId = id;
        refs = parsed;
        return true;
    }

    /// <summary>
    /// Parses an inbound <c>&lt;iq type="set"&gt;&lt;pair-success&gt;</c> stanza, extracting
    /// <c>&lt;device-identity&gt;</c>, <c>&lt;device jid="..." lid="..."/&gt;</c>, optional
    /// <c>&lt;biz name="..."/&gt;</c>, and optional <c>&lt;client-props&gt;</c> while ignoring
    /// extra sibling nodes such as <c>&lt;encryption-metadata&gt;</c>.
    /// </summary>
    public static bool TryParsePairSuccess(Node iqNode, out PairSuccessPayload? payload, out string error)
    {
        payload = null;
        error = string.Empty;

        if (iqNode.Tag != "iq")
        {
            error = "Expected <iq> stanza.";
            return false;
        }

        string? reqId = iqNode.GetAttr("id");
        if (string.IsNullOrEmpty(reqId))
        {
            error = "Missing iq id attribute.";
            return false;
        }

        var pairSuccess = iqNode.GetChild("pair-success");
        if (pairSuccess is null)
        {
            error = "Missing <pair-success> child.";
            return false;
        }

        var deviceIdentityNode = pairSuccess.GetChild("device-identity");
        if (deviceIdentityNode?.Content is not NodeContent.BytesContent diBytes || diBytes.Bytes.Length == 0)
        {
            error = "Missing <device-identity> bytes.";
            return false;
        }

        var deviceNode = pairSuccess.GetChild("device");
        if (deviceNode is null)
        {
            error = "Missing <device> element.";
            return false;
        }

        Jid? pnJid = null;
        string? jidAttr = deviceNode.GetAttr("jid");
        if (!string.IsNullOrEmpty(jidAttr) && Jid.TryParse(jidAttr, out var parsedPn))
        {
            pnJid = parsedPn;
        }

        Jid? lidJid = null;
        string? lidAttr = deviceNode.GetAttr("lid");
        if (!string.IsNullOrEmpty(lidAttr) && Jid.TryParse(lidAttr, out var parsedLid))
        {
            lidJid = parsedLid;
        }

        if (pnJid is null && lidJid is null)
        {
            error = "Missing or invalid <device jid/lid> attributes.";
            return false;
        }

        string? pushName = pairSuccess.GetChild("biz")?.GetAttr("name");
        var clientProps = ExtractPairingProps(pairSuccess);

        payload = new PairSuccessPayload(
            reqId,
            diBytes.Bytes,
            pnJid,
            lidJid,
            pushName,
            clientProps);
        return true;
    }

    /// <summary>
    /// Decodes the optional <c>&lt;client-props&gt;</c> child protobuf inside <c>&lt;pair-success&gt;</c>.
    /// Returns <c>null</c> when absent or malformed (matching <c>PairUtils::extract_pairing_props</c>).
    /// </summary>
    public static ClientPairingProps? ExtractPairingProps(Node pairSuccessNode)
    {
        ArgumentNullException.ThrowIfNull(pairSuccessNode);

        var child = pairSuccessNode.GetChild("client-props");
        if (child?.Content is not NodeContent.BytesContent bytesContent || bytesContent.Bytes.Length == 0)
        {
            return null;
        }

        try
        {
            return ClientPairingProps.Parser.ParseFrom(bytesContent.Bytes);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
