using System.Buffers.Text;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Transport;

/// <summary>
/// Builds WhatsApp Web WebSocket connection URIs (<c>:443</c> primary and <c>:5222</c> fallback)
/// with optional unpadded base64url <c>?ED=</c> edge-routing parameters.
/// </summary>
public static class WhatsAppEndpointBuilder
{
    public const string PrimaryWebSocketUrl = "wss://web.whatsapp.com/ws/chat";
    public const string FallbackWebSocketUrl = "wss://web.whatsapp.com:5222/ws/chat";
    public const string DefaultOrigin = "https://web.whatsapp.com";

    /// <summary>
    /// Returns the dual-port Happy Eyeballs endpoints (<c>:443</c> first, <c>:5222</c> fallback),
    /// attaching <c>?ED=&lt;base64url&gt;</c> when valid edge-routing bytes are supplied.
    /// </summary>
    public static IReadOnlyList<Uri> BuildDefaultEndpoints(ReadOnlySpan<byte> edgeRoutingInfo = default) =>
    [
        new Uri(WithEdgeRoutingParam(PrimaryWebSocketUrl, edgeRoutingInfo)),
        new Uri(WithEdgeRoutingParam(FallbackWebSocketUrl, edgeRoutingInfo))
    ];

    /// <summary>
    /// Appends unpadded base64url <c>ED</c> query parameter before any <c>#fragment</c>,
    /// returning <paramref name="url"/> unchanged if <paramref name="edgeRoutingInfo"/> is empty or exceeds
    /// <see cref="NoiseHandshakeHeader.MaxEdgeRoutingLength"/>.
    /// </summary>
    public static string WithEdgeRoutingParam(string url, ReadOnlySpan<byte> edgeRoutingInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (edgeRoutingInfo.IsEmpty || edgeRoutingInfo.Length > NoiseHandshakeHeader.MaxEdgeRoutingLength)
        {
            return url;
        }

        string encoded = Base64Url.EncodeToString(edgeRoutingInfo);
        int fragmentIdx = url.IndexOf('#');
        string basePart = fragmentIdx >= 0 ? url[..fragmentIdx] : url;
        string? fragmentPart = fragmentIdx >= 0 ? url[fragmentIdx..] : null;

        char separator = basePart.Contains('?') ? '&' : '?';
        return fragmentPart is null
            ? $"{basePart}{separator}ED={encoded}"
            : $"{basePart}{separator}ED={encoded}{fragmentPart}";
    }
}
