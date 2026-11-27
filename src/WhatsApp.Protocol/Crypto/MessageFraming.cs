using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Binary;

namespace WhatsApp.Protocol.Crypto;

/// <summary>
/// Pure WhatsApp E2EE message padding, <c>DeviceSentMessage</c> wrapping, SKDM wrapping,
/// and participant list hashing (<c>phashV2</c>) verified against live <c>WAWebPhashUtils</c>
/// and <c>wacore/src/messages.rs</c>.
/// </summary>
public static class MessageFraming
{
    public const int DefaultPaddingVersion = 2;

    /// <summary>
    /// Appends uniform random 1..16 bytes matching WhatsApp v2 E2EE padding (<c>(rand &amp; 0x0F) + 1</c>).
    /// </summary>
    public static byte[] PadMessageV2(ReadOnlySpan<byte> plaintext, byte? padLengthOverride = null)
    {
        byte padLen = padLengthOverride ?? (byte)((RandomNumberGenerator.GetInt32(0, 16)) + 1);
        if (padLen is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(padLengthOverride), "Padding length must be 1..16.");
        }

        byte[] output = new byte[plaintext.Length + padLen];
        plaintext.CopyTo(output);
        output.AsSpan(plaintext.Length).Fill(padLen);
        return output;
    }

    public static byte[] EncodeAndPadMessage(Message message, byte? padLengthOverride = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        return PadMessageV2(message.ToByteArray(), padLengthOverride);
    }

    public static byte[] EncodeAndPadSkdmWrapper(string groupJid, ReadOnlySpan<byte> axolotlSkdmBytes, byte? padLengthOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupJid);
        var wrapper = new Message
        {
            SenderKeyDistributionMessage = new Message.Types.SenderKeyDistributionMessage
            {
                GroupId = groupJid,
                AxolotlSenderKeyDistributionMessage = ByteString.CopyFrom(axolotlSkdmBytes)
            }
        };
        return EncodeAndPadMessage(wrapper, padLengthOverride);
    }

    /// <summary>
    /// Wraps an outbound DM message in <see cref="Message.Types.DeviceSentMessage"/> for own companion devices,
    /// hoisting <see cref="Message.MessageContextInfo"/> to the outer wrapper matching WA Web.
    /// </summary>
    public static Message WrapDeviceSentMessage(Message message, string destinationJid)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationJid);

        Message inner = message.Clone();
        MessageContextInfo? hoisted = inner.MessageContextInfo;
        inner.MessageContextInfo = null;

        return new Message
        {
            DeviceSentMessage = new Message.Types.DeviceSentMessage
            {
                DestinationJid = destinationJid,
                Message = inner
            },
            MessageContextInfo = hoisted
        };
    }

    /// <summary>
    /// Unwraps a <see cref="Message.Types.DeviceSentMessage"/> received from a companion device,
    /// restoring outer hoisted <see cref="Message.MessageContextInfo"/> fields (such as <c>MessageSecret</c>)
    /// onto the inner <see cref="Message"/> matching <c>WAWebDeviceSentMessageProtoUtils.unwrapDeviceSentMessage</c>.
    /// </summary>
    public static Message UnwrapDeviceSentMessage(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.DeviceSentMessage?.Message is not { } inner)
        {
            return message;
        }

        Message unwrapped = inner.Clone();
        if (message.MessageContextInfo is { } outerCtx)
        {
            MessageContextInfo merged = outerCtx.Clone();
            if (unwrapped.MessageContextInfo is { } innerCtx)
            {
                if (innerCtx.ThreadId.Count > 0)
                {
                    merged.ThreadId.Clear();
                }
                merged.MergeFrom(innerCtx);
            }
            unwrapped.MessageContextInfo = merged;
        }
        return unwrapped;
    }

    /// <summary>
    /// Validates and strips WhatsApp PKCS7-style random padding (<c>version != 3</c>), returning raw protobuf bytes.
    /// </summary>
    public static ReadOnlySpan<byte> UnpadMessage(ReadOnlySpan<byte> paddedPlaintext, int paddingVersion = DefaultPaddingVersion)
    {
        if (paddingVersion == 3)
        {
            return paddedPlaintext;
        }
        if (paddedPlaintext.IsEmpty)
        {
            throw new InvalidDataException("Plaintext is empty, cannot unpad.");
        }

        int padLen = paddedPlaintext[^1];
        if (padLen == 0 || padLen > paddedPlaintext.Length)
        {
            throw new InvalidDataException($"Invalid E2EE padding length {padLen}.");
        }

        ReadOnlySpan<byte> padding = paddedPlaintext[(paddedPlaintext.Length - padLen)..];
        for (int i = 0; i < padding.Length; i++)
        {
            if (padding[i] != padLen)
            {
                throw new InvalidDataException("Corrupt E2EE padding bytes.");
            }
        }

        return paddedPlaintext[..(paddedPlaintext.Length - padLen)];
    }

    public static Message DecodeAndUnpadMessage(ReadOnlySpan<byte> paddedPlaintext, int paddingVersion = DefaultPaddingVersion)
    {
        ReadOnlySpan<byte> raw = UnpadMessage(paddedPlaintext, paddingVersion);
        return Message.Parser.ParseFrom(raw);
    }

    /// <summary>
    /// Computes the participant list hash (<c>"2:" + base64NoPad(SHA256(sorted AD JIDs)[0..6])</c>)
    /// matching live <c>WAWebPhashUtils.phashV2</c>.
    /// </summary>
    public static string ComputeParticipantListHash(IEnumerable<Jid> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        string[] formatted = devices
            .Select(FormatPhashJid)
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToArray();

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string item in formatted)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(item));
        }

        byte[] digest = sha.GetHashAndReset();
        string b64 = Convert.ToBase64String(digest, 0, 6).TrimEnd('=');
        return string.Concat("2:", b64);
    }

    internal static string FormatPhashJid(Jid jid) =>
        $"{jid.User}.0:{jid.Device}@{jid.Server.ToServerString()}";
}
