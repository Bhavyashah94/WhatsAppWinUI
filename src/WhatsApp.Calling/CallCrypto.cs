using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Noise;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Calling;

/// <summary>
/// VoIP key derivation, collision-free WASM SSRC generation, and Signal call-key envelope helpers
/// matching <c>wacore/src/voip_control/{kdf,ssrc}.rs</c>.
/// </summary>
public static class CallCrypto
{
    public const int CallKeySize = 32;
    public const uint AudioSsrcSlotWord = 0;
    public const uint VideoSsrcSlotWord = 2;
    public const uint VideoFecSsrcSlotWord = 3;
    public const uint AppDataSsrcSlotWord = 6;
    public const uint HbhFecTxSsrcSlotWord = 7;
    public const uint HbhFecRxSsrcSlotWord = 8;

    public static readonly uint[] WasmRelayStreamSlotWords = [0, 1, 4, 2, 3, 5, 7, 8, 6];

    public static byte[] GenerateCallKey() => RandomNumberGenerator.GetBytes(CallKeySize);

    /// <summary>
    /// Formats participant ID for E2E-SRTP / SFrame HKDF info: keeps an existing <c>:N@lid</c>
    /// suffix, appends <c>:0</c> to bare <c>@lid</c>, and passes non-LID JIDs through unchanged.
    /// </summary>
    public static string FormatParticipantId(string jid)
    {
        ArgumentNullException.ThrowIfNull(jid);
        int slash = jid.IndexOf('/');
        string bare = (slash >= 0 ? jid[..slash] : jid).Trim();
        int at = bare.LastIndexOf('@');
        if (at <= 0)
        {
            return bare;
        }

        string user = bare[..at];
        string domain = bare[(at + 1)..];
        if (string.Equals(domain, "lid", StringComparison.Ordinal) && !user.Contains(':', StringComparison.Ordinal))
        {
            return $"{user}:0@{domain}";
        }
        return bare;
    }

    public static byte[] DeriveWaHkdf46(ReadOnlySpan<byte> ikm32, string participantId)
    {
        if (ikm32.Length < CallKeySize)
        {
            throw new ArgumentException($"callKey must be at least {CallKeySize} bytes.", nameof(ikm32));
        }

        byte[] okm = new byte[46];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm32[..CallKeySize],
            okm,
            new byte[32],
            Encoding.UTF8.GetBytes(participantId));
        return okm;
    }

    public static uint DeriveParticipantSsrc(string callId, string participantId, uint slotWord)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentException.ThrowIfNullOrEmpty(participantId);

        Span<byte> salt = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(salt, slotWord);
        Span<byte> okm = stackalloc byte[4];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(callId),
            okm,
            salt,
            Encoding.UTF8.GetBytes(participantId));
        return BinaryPrimitives.ReadUInt32LittleEndian(okm);
    }

    public static uint DeriveVideoParticipantSsrc(string callId, string participantId) =>
        DeriveParticipantSsrc(callId, participantId, VideoSsrcSlotWord);

    public static uint[] DeriveWasmRelayStreamSsrcs(string callId, string participantId)
    {
        var ssrcs = new uint[WasmRelayStreamSlotWords.Length];
        for (int i = 0; i < WasmRelayStreamSlotWords.Length; i++)
        {
            ssrcs[i] = DeriveParticipantSsrc(callId, participantId, WasmRelayStreamSlotWords[i]);
        }
        return ssrcs;
    }

    public static (string EncType, byte[] Ciphertext) EncryptOfferCallKey(
        ReadOnlySpan<byte> callKey,
        SignalSession session,
        KeyPair ownIdentityKey,
        uint ownRegistrationId)
    {
        ArgumentNullException.ThrowIfNull(session);

        var msg = new Message
        {
            Call = new Message.Types.Call
            {
                CallKey = ByteString.CopyFrom(callKey)
            }
        };
        byte[] padded = MessageFraming.EncodeAndPadMessage(msg);
        SignalEncryptedPayload enc = SignalCipher.Encrypt(session, padded, ownIdentityKey, ownRegistrationId);
        return (enc.WireType, enc.Bytes);
    }

    public static (byte[] CallKey, SignalSession UpdatedSession) DecryptOfferCallKey(
        string encType,
        byte[] ciphertext,
        KeyPair ownIdentityKey,
        KeyPair signedPreKey,
        KeyPair? oneTimePreKey,
        SignalSession? existingSession)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        byte[] padded;
        SignalSession updated;
        if (string.Equals(encType, "pkmsg", StringComparison.Ordinal))
        {
            WhatsApp.Protocol.Signal.PreKeySignalMessage pkmsg = WhatsApp.Protocol.Signal.PreKeySignalMessage.Parse(ciphertext);
            (padded, updated) = SignalCipher.DecryptPreKey(pkmsg, ownIdentityKey, signedPreKey, oneTimePreKey, existingSession);
        }
        else if (string.Equals(encType, "msg", StringComparison.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(existingSession);
            WhatsApp.Protocol.Signal.SignalMessage smsg = WhatsApp.Protocol.Signal.SignalMessage.Parse(ciphertext);
            padded = SignalCipher.DecryptSignal(smsg, existingSession, ownIdentityKey);
            updated = existingSession;
        }
        else
        {
            throw new NotSupportedException($"Unsupported call offer enc type '{encType}'.");
        }

        Message decoded = MessageFraming.DecodeAndUnpadMessage(padded);
        if (decoded.Call is not { HasCallKey: true } callMsg || callMsg.CallKey.Length < CallKeySize)
        {
            throw new InvalidDataException("Decrypted offer payload missing valid Message.Call.CallKey.");
        }
        return (callMsg.CallKey.ToByteArray(), updated);
    }
}
