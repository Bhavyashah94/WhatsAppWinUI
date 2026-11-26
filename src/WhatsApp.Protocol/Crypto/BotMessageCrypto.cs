using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Whatsapp;

namespace WhatsApp.Protocol.Crypto;

/// <summary>
/// Inputs needed to derive the per-message bot AES-256-GCM key and AAD for <c>&lt;enc type="msmsg"&gt;</c>.
/// Mirrors live <c>WAWebBotMessageSecret.decryptMsmsgBotMessage</c> and <c>wacore/src/bot_message.rs</c>.
/// </summary>
public sealed record BotMessageContext(
    string MsgId,
    string TargetSenderUserJid,
    string BotUserJid);

/// <summary>
/// Encrypts and decrypts Meta AI / fbid bot <c>&lt;enc type="msmsg"&gt;</c> envelopes.
/// Two-pass HKDF-SHA256 over the outbound 32-byte <c>messageSecret</c>, followed by unpadded AES-256-GCM
/// with <c>msgID || 0x00 || botUserJid</c> as AAD.
/// </summary>
public static class BotMessageCrypto
{
    public const int SecretSize = 32;
    public const int IvSize = 12;
    public const int TagSize = 16;
    private static readonly byte[] BotMessageInfo = "Bot Message"u8.ToArray();

    public static byte[] DeriveBaseBotKey(ReadOnlySpan<byte> messageSecret32)
    {
        if (messageSecret32.Length != SecretSize)
        {
            throw new ArgumentException(
                $"Message secret must be {SecretSize} bytes, got {messageSecret32.Length}.",
                nameof(messageSecret32));
        }

        byte[] baseKey = new byte[SecretSize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, messageSecret32, baseKey, salt: default, info: BotMessageInfo);
        return baseKey;
    }

    public static byte[] DerivePerMessageKey(ReadOnlySpan<byte> baseKey32, BotMessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(context.MsgId);
        ArgumentException.ThrowIfNullOrEmpty(context.TargetSenderUserJid);
        ArgumentException.ThrowIfNullOrEmpty(context.BotUserJid);

        byte[] info = Encoding.UTF8.GetBytes(string.Concat(
            context.MsgId,
            context.TargetSenderUserJid,
            context.BotUserJid));
        byte[] key = new byte[SecretSize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, baseKey32, key, salt: default, info: info);
        return key;
    }

    public static byte[] BuildAad(BotMessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        byte[] msgBytes = Encoding.UTF8.GetBytes(context.MsgId);
        byte[] botBytes = Encoding.UTF8.GetBytes(context.BotUserJid);
        byte[] aad = new byte[msgBytes.Length + 1 + botBytes.Length];
        msgBytes.CopyTo(aad, 0);
        aad[msgBytes.Length] = 0x00;
        botBytes.CopyTo(aad, msgBytes.Length + 1);
        return aad;
    }

    /// <summary>
    /// Decrypts raw AES-256-GCM <c>enc_payload</c> (<c>ciphertext || 16-byte tag</c>) using <paramref name="encIv12"/>.
    /// Returns raw unpadded protobuf <c>wa.Message</c> bytes.
    /// </summary>
    public static byte[] DecryptRawPayload(
        ReadOnlySpan<byte> messageSecret32,
        ReadOnlySpan<byte> encIv12,
        ReadOnlySpan<byte> encPayloadWithTag,
        BotMessageContext context)
    {
        if (encIv12.Length != IvSize)
        {
            throw new CryptographicException($"Invalid msmsg IV length: expected {IvSize}, got {encIv12.Length}.");
        }
        if (encPayloadWithTag.Length < TagSize)
        {
            throw new CryptographicException($"Invalid msmsg payload length: need >= {TagSize}, got {encPayloadWithTag.Length}.");
        }

        byte[] baseKey = DeriveBaseBotKey(messageSecret32);
        byte[] key = DerivePerMessageKey(baseKey, context);
        byte[] aad = BuildAad(context);

        int plainLen = encPayloadWithTag.Length - TagSize;
        byte[] plaintext = new byte[plainLen];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(encIv12, encPayloadWithTag[..plainLen], encPayloadWithTag[plainLen..], plaintext, aad);
        return plaintext;
    }

    /// <summary>
    /// Parses a <see cref="MessageSecretMessage"/> protobuf envelope from <c>&lt;enc type="msmsg"&gt;</c>
    /// and decrypts the inner <c>Message</c> bytes, trying <paramref name="fallbackContext"/> (e.g.
    /// <c>edit_target_id</c> vs stanza <c>id</c>) when primary authentication fails.
    /// </summary>
    public static byte[] DecryptMsmsgEnvelope(
        ReadOnlySpan<byte> msmsgProtoBytes,
        ReadOnlySpan<byte> messageSecret32,
        BotMessageContext primaryContext,
        BotMessageContext? fallbackContext = null)
    {
        ArgumentNullException.ThrowIfNull(primaryContext);
        MessageSecretMessage envelope = MessageSecretMessage.Parser.ParseFrom(msmsgProtoBytes);
        if (!envelope.HasEncIv || !envelope.HasEncPayload)
        {
            throw new InvalidDataException("MessageSecretMessage missing required encIv or encPayload.");
        }

        ReadOnlySpan<byte> iv = envelope.EncIv.Span;
        ReadOnlySpan<byte> payload = envelope.EncPayload.Span;

        try
        {
            return DecryptRawPayload(messageSecret32, iv, payload, primaryContext);
        }
        catch (CryptographicException) when (fallbackContext is not null)
        {
            return DecryptRawPayload(messageSecret32, iv, payload, fallbackContext);
        }
    }

    /// <summary>
    /// Encrypts unpadded <paramref name="plaintextProto"/> into a <see cref="MessageSecretMessage"/> envelope
    /// for <c>&lt;enc type="msmsg"&gt;</c>.
    /// </summary>
    public static (byte[] MsmsgProtoBytes, byte[] CiphertextWithTag, byte[] Iv12) EncryptMsmsgEnvelope(
        ReadOnlySpan<byte> plaintextProto,
        ReadOnlySpan<byte> messageSecret32,
        BotMessageContext context,
        ReadOnlySpan<byte> ivOverride = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        byte[] baseKey = DeriveBaseBotKey(messageSecret32);
        byte[] key = DerivePerMessageKey(baseKey, context);
        byte[] aad = BuildAad(context);

        byte[] iv = ivOverride.Length == IvSize ? ivOverride.ToArray() : RandomNumberGenerator.GetBytes(IvSize);
        byte[] output = new byte[plaintextProto.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(iv, plaintextProto, output.AsSpan(0, plaintextProto.Length), output.AsSpan(plaintextProto.Length, TagSize), aad);

        var envelope = new MessageSecretMessage
        {
            Version = 1,
            EncIv = ByteString.CopyFrom(iv),
            EncPayload = ByteString.CopyFrom(output)
        };
        return (envelope.ToByteArray(), output, iv);
    }
}
