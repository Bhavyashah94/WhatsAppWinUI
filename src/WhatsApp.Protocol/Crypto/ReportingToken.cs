using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Whatsapp;

namespace WhatsApp.Protocol.Crypto;

public sealed record ReportingTokenResult(
    byte[] MessageSecret32,
    byte[] ReportingToken16,
    int Version);

/// <summary>
/// Privacy-preserving reporting token derivation &amp; whitelisted protobuf field extractor.
/// Matches live <c>WAWebReportingTokenUtils</c> and <c>wacore/src/reporting_token.rs</c>.
/// </summary>
public static class ReportingTokenGenerator
{
    public const int MessageSecretSize = 32;
    public const int ReportingTokenSize = 16;
    public const int ReportingTokenKeySize = 32;
    public const int DefaultReportingTokenVersion = 2;
    private const string UseCaseReportToken = "Report Token";

    private sealed record ReportingField(uint FieldNumber, ReportingField[]? Subfields = null, bool IsMessage = false);

    private static readonly ReportingField[] ContextInfoSubfields =
    [
        new(21), // forwardingScore
        new(22), // isForwarded
    ];

    private static readonly ReportingField[] ImageSubfields =
    [
        new(2), new(3), new(8), new(11), new(17, ContextInfoSubfields), new(25),
    ];

    private static readonly ReportingField[] ContactSubfields =
    [
        new(1), new(16), new(17, ContextInfoSubfields),
    ];

    private static readonly ReportingField[] LocationSubfields =
    [
        new(3), new(4), new(5), new(16), new(17, ContextInfoSubfields),
    ];

    private static readonly ReportingField[] ExtendedTextSubfields =
    [
        new(1), new(17, ContextInfoSubfields), new(30),
    ];

    private static readonly ReportingField[] DocumentSubfields =
    [
        new(2), new(7), new(10), new(17, ContextInfoSubfields), new(20),
    ];

    private static readonly ReportingField[] AudioSubfields =
    [
        new(2), new(7), new(9), new(17, ContextInfoSubfields), new(21),
    ];

    private static readonly ReportingField[] VideoSubfields =
    [
        new(2), new(6), new(7), new(13), new(17, ContextInfoSubfields), new(20),
    ];

    private static readonly ReportingField[] ProtocolSubfields =
    [
        new(1), new(2), new(14, IsMessage: true), new(15),
    ];

    private static readonly ReportingField[] FutureProofSubfields = [new(1, IsMessage: true)];

    private static readonly ReportingField[] PollCreationSubfields =
    [
        new(2), new(3), new(4), new(5, ContextInfoSubfields),
    ];

    private static readonly ReportingField[] EventSubfields =
    [
        new(1, ContextInfoSubfields), new(2), new(3), new(4), new(7),
    ];

    // Subset covering core & rich message types; unknown wrappers pass cleanly via FutureProofSubfields.
    private static readonly ReportingField[] TopLevelFields =
    [
        new(1),                                     // conversation
        new(3, ImageSubfields),                     // imageMessage
        new(4, ContactSubfields),                   // contactMessage
        new(5, LocationSubfields),                  // locationMessage
        new(6, ExtendedTextSubfields),              // extendedTextMessage
        new(7, DocumentSubfields),                  // documentMessage
        new(8, AudioSubfields),                     // audioMessage
        new(9, VideoSubfields),                     // videoMessage
        new(12, ProtocolSubfields),                 // protocolMessage
        new(18, LocationSubfields),                 // liveLocationMessage
        new(26, ImageSubfields),                    // stickerMessage
        new(37, FutureProofSubfields),              // viewOnceMessage
        new(49, PollCreationSubfields),             // pollCreationMessage
        new(53, FutureProofSubfields),              // documentWithCaptionMessage
        new(55, FutureProofSubfields),              // viewOnceMessageV2
        new(60, PollCreationSubfields),             // pollCreationMessageV2
        new(64, PollCreationSubfields),             // pollCreationMessageV3
        new(67, FutureProofSubfields),              // editedMessage
        new(75, EventSubfields),                    // eventMessage
    ];

    public static bool ShouldIncludeReportingToken(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.ReactionMessage is null
            && message.EncReactionMessage is null
            && message.PollUpdateMessage is null
            && message.KeepInChatMessage is null;
    }

    public static byte[] DeriveReportingTokenKey(
        ReadOnlySpan<byte> messageSecret32,
        string stanzaId,
        string senderJid,
        string remoteJid)
    {
        if (messageSecret32.Length != MessageSecretSize)
        {
            throw new ArgumentException($"Message secret must be {MessageSecretSize} bytes.", nameof(messageSecret32));
        }
        ArgumentException.ThrowIfNullOrEmpty(stanzaId);
        ArgumentNullException.ThrowIfNull(senderJid);
        ArgumentNullException.ThrowIfNull(remoteJid);

        byte[] info = Encoding.UTF8.GetBytes(string.Concat(stanzaId, senderJid, remoteJid, UseCaseReportToken));
        byte[] key = new byte[ReportingTokenKeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, messageSecret32, key, salt: default, info: info);
        return key;
    }

    public static ReportingTokenResult? Generate(
        Message message,
        string stanzaId,
        string senderJid,
        string remoteJid,
        byte[]? existingSecret32 = null,
        int version = DefaultReportingTokenVersion)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!ShouldIncludeReportingToken(message))
        {
            return null;
        }

        byte[] encoded = message.ToByteArray();
        byte[]? content = ExtractWhitelistedContent(encoded, TopLevelFields);
        if (content is not { Length: > 0 })
        {
            return null;
        }

        byte[] secret = existingSecret32 is { Length: MessageSecretSize }
            ? existingSecret32.ToArray()
            : RandomNumberGenerator.GetBytes(MessageSecretSize);

        byte[] rtKey = DeriveReportingTokenKey(secret, stanzaId, senderJid, remoteJid);
        byte[] mac = HMACSHA256.HashData(rtKey, content);
        return new ReportingTokenResult(secret, mac[..ReportingTokenSize], version);
    }

    public static Message AttachMessageContext(Message original, byte[] messageSecret32, int version = DefaultReportingTokenVersion)
    {
        ArgumentNullException.ThrowIfNull(original);
        Message clone = original.Clone();
        clone.MessageContextInfo ??= new MessageContextInfo();
        clone.MessageContextInfo.MessageSecret = ByteString.CopyFrom(messageSecret32);
        clone.MessageContextInfo.ReportingTokenVersion = version;
        return clone;
    }

    private static byte[]? ExtractWhitelistedContent(ReadOnlySpan<byte> data, ReportingField[] whitelist)
    {
        var pieces = new List<(uint FieldNumber, byte[] Bytes)>();
        int pos = 0;

        while (pos < data.Length)
        {
            if (!TryDecodeVarint(data[pos..], out ulong tag, out int tagLen))
            {
                break;
            }
            pos += tagLen;

            uint fieldNumber = (uint)(tag >> 3);
            uint wireType = (uint)(tag & 0x7);
            int fieldStart = pos - tagLen;

            ReportingField? match = null;
            for (int i = 0; i < whitelist.Length; i++)
            {
                if (whitelist[i].FieldNumber == fieldNumber)
                {
                    match = whitelist[i];
                    break;
                }
            }

            switch (wireType)
            {
                case 0: // Varint
                    if (!TryDecodeVarint(data[pos..], out _, out int vLen)) return null;
                    pos += vLen;
                    if (match is not null) pieces.Add((fieldNumber, data[fieldStart..pos].ToArray()));
                    break;
                case 1: // Fixed64
                    if (pos + 8 > data.Length) return null;
                    pos += 8;
                    if (match is not null) pieces.Add((fieldNumber, data[fieldStart..pos].ToArray()));
                    break;
                case 5: // Fixed32
                    if (pos + 4 > data.Length) return null;
                    pos += 4;
                    if (match is not null) pieces.Add((fieldNumber, data[fieldStart..pos].ToArray()));
                    break;
                case 2: // Length-delimited
                    if (!TryDecodeVarint(data[pos..], out ulong lenVal, out int lenSize)) return null;
                    int valStart = pos + lenSize;
                    int valEnd = valStart + (int)lenVal;
                    if (valEnd < valStart || valEnd > data.Length) return null;
                    pos = valEnd;

                    if (match is not null)
                    {
                        if (match.IsMessage || match.Subfields is not null)
                        {
                            ReportingField[] innerList = match.IsMessage ? TopLevelFields : match.Subfields!;
                            byte[]? nested = ExtractWhitelistedContent(data[valStart..valEnd], innerList);
                            if (nested is { Length: > 0 })
                            {
                                pieces.Add((fieldNumber, FrameLengthDelimited(tag, nested)));
                            }
                        }
                        else
                        {
                            pieces.Add((fieldNumber, data[fieldStart..pos].ToArray()));
                        }
                    }
                    break;
                default:
                    return null;
            }
        }

        if (pieces.Count == 0)
        {
            return null;
        }

        // Order pieces stably by ascending protobuf field number.
        var sorted = pieces.OrderBy(p => p.FieldNumber).ToList();
        using var ms = new MemoryStream();
        foreach (var (_, bytes) in sorted)
        {
            ms.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private static byte[] FrameLengthDelimited(ulong tag, ReadOnlySpan<byte> payload)
    {
        Span<byte> tagBuf = stackalloc byte[10];
        Span<byte> lenBuf = stackalloc byte[10];
        int tl = EncodeVarint(tag, tagBuf);
        int ll = EncodeVarint((ulong)payload.Length, lenBuf);
        byte[] result = new byte[tl + ll + payload.Length];
        tagBuf[..tl].CopyTo(result);
        lenBuf[..ll].CopyTo(result.AsSpan(tl));
        payload.CopyTo(result.AsSpan(tl + ll));
        return result;
    }

    private static bool TryDecodeVarint(ReadOnlySpan<byte> buf, out ulong value, out int bytesRead)
    {
        value = 0;
        int shift = 0;
        for (int i = 0; i < buf.Length && i < 10; i++)
        {
            byte b = buf[i];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                bytesRead = i + 1;
                return true;
            }
            shift += 7;
        }
        bytesRead = 0;
        return false;
    }

    private static int EncodeVarint(ulong value, Span<byte> dest)
    {
        int i = 0;
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            dest[i++] = b;
        }
        while (value != 0);
        return i;
    }
}
