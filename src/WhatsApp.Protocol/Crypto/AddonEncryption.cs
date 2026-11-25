using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Whatsapp;

namespace WhatsApp.Protocol.Crypto;

public enum AddonModificationType
{
    PollVote,
    EncReaction,
    EncComment,
    ReportToken,
    EventResponse,
    EventEdit,
    PollEdit,
    PollAddOption,
    MessageEdit,
}

/// <summary>
/// Secret-encrypted add-on encryption &amp; decryption (HKDF-SHA256 + AES-256-GCM) matching live
/// <c>WAUseCaseSecret.createUseCaseSecret</c>, <c>WAWebAddonEncryption</c>,
/// <c>WAWebPollOptionHashUtils</c>, and <c>wacore/src/{secret_enc_addon,poll,reaction,event,message_edit}.rs</c>.
/// </summary>
public static class AddonEncryption
{
    public const int MessageSecretSize = 32;
    public const int IvSize = 12;
    public const int TagSize = 16;

    public static string GetUseCaseLiteral(AddonModificationType type) => type switch
    {
        AddonModificationType.PollVote => "Poll Vote",
        AddonModificationType.EncReaction => "Enc Reaction",
        AddonModificationType.EncComment => "Enc Comment",
        AddonModificationType.ReportToken => "Report Token",
        AddonModificationType.EventResponse => "Event Response",
        AddonModificationType.EventEdit => "Event Edit",
        AddonModificationType.PollEdit => "Poll Edit",
        AddonModificationType.PollAddOption => "Poll Add Option",
        AddonModificationType.MessageEdit => "Message Edit",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static bool UsesStanzaSenderAad(AddonModificationType type) =>
        type is AddonModificationType.PollVote or AddonModificationType.EventResponse;

    public static byte[] DeriveUseCaseSecret(
        ReadOnlySpan<byte> messageSecret32,
        string stanzaId,
        string parentOriginalSenderJid,
        string modificationSenderJid,
        AddonModificationType type)
    {
        if (messageSecret32.Length != MessageSecretSize)
        {
            throw new ArgumentException($"Message secret must be {MessageSecretSize} bytes.", nameof(messageSecret32));
        }
        ArgumentException.ThrowIfNullOrEmpty(stanzaId);
        ArgumentNullException.ThrowIfNull(parentOriginalSenderJid);
        ArgumentNullException.ThrowIfNull(modificationSenderJid);

        string useCase = GetUseCaseLiteral(type);
        byte[] info = Encoding.UTF8.GetBytes(string.Concat(stanzaId, parentOriginalSenderJid, modificationSenderJid, useCase));
        byte[] key = new byte[MessageSecretSize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, messageSecret32, key, salt: default, info: info);
        return key;
    }

    public static byte[] BuildAad(string stanzaId, string modificationSenderJid, AddonModificationType type)
    {
        if (!UsesStanzaSenderAad(type))
        {
            return Array.Empty<byte>();
        }

        byte[] stanzaBytes = Encoding.UTF8.GetBytes(stanzaId);
        byte[] senderBytes = Encoding.UTF8.GetBytes(modificationSenderJid);
        byte[] aad = new byte[stanzaBytes.Length + 1 + senderBytes.Length];
        stanzaBytes.CopyTo(aad, 0);
        aad[stanzaBytes.Length] = 0x00;
        senderBytes.CopyTo(aad, stanzaBytes.Length + 1);
        return aad;
    }

    public static (byte[] CiphertextWithTag, byte[] Iv12) EncryptAddon(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> messageSecret32,
        string stanzaId,
        string parentOriginalSenderJid,
        string modificationSenderJid,
        AddonModificationType type,
        ReadOnlySpan<byte> ivOverride = default)
    {
        byte[] key = DeriveUseCaseSecret(messageSecret32, stanzaId, parentOriginalSenderJid, modificationSenderJid, type);
        byte[] aad = BuildAad(stanzaId, modificationSenderJid, type);
        byte[] iv = ivOverride.Length == IvSize ? ivOverride.ToArray() : RandomNumberGenerator.GetBytes(IvSize);

        byte[] output = new byte[plaintext.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(iv, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length, TagSize), aad);
        return (output, iv);
    }

    public static byte[] DecryptAddon(
        ReadOnlySpan<byte> encPayloadWithTag,
        ReadOnlySpan<byte> iv12,
        ReadOnlySpan<byte> messageSecret32,
        string stanzaId,
        string parentOriginalSenderJid,
        string modificationSenderJid,
        AddonModificationType type)
    {
        if (iv12.Length != IvSize)
        {
            throw new ArgumentException($"Add-on IV must be {IvSize} bytes.", nameof(iv12));
        }
        if (encPayloadWithTag.Length < TagSize)
        {
            throw new CryptographicException("Add-on ciphertext shorter than 16-byte GCM tag.");
        }

        byte[] key = DeriveUseCaseSecret(messageSecret32, stanzaId, parentOriginalSenderJid, modificationSenderJid, type);
        byte[] aad = BuildAad(stanzaId, modificationSenderJid, type);
        int ptLen = encPayloadWithTag.Length - TagSize;
        byte[] plaintext = new byte[ptLen];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(iv12, encPayloadWithTag[..ptLen], encPayloadWithTag[ptLen..], plaintext, aad);
        return plaintext;
    }

    public static byte[] HashPollOption(string optionName)
    {
        ArgumentNullException.ThrowIfNull(optionName);
        return SHA256.HashData(Encoding.UTF8.GetBytes(optionName));
    }

    public static (byte[] EncPayload, byte[] Iv12) EncryptPollVote(
        IReadOnlyList<byte[]> selectedOptionHashes,
        ReadOnlySpan<byte> pollSecret32,
        string pollMessageId,
        string pollCreatorJid,
        string voterJid,
        ReadOnlySpan<byte> ivOverride = default)
    {
        ArgumentNullException.ThrowIfNull(selectedOptionHashes);
        var vote = new Message.Types.PollVoteMessage();
        foreach (byte[] hash in selectedOptionHashes)
        {
            vote.SelectedOptions.Add(ByteString.CopyFrom(hash));
        }
        return EncryptAddon(vote.ToByteArray(), pollSecret32, pollMessageId, pollCreatorJid, voterJid, AddonModificationType.PollVote, ivOverride);
    }

    public static IReadOnlyList<byte[]> DecryptPollVote(
        ReadOnlySpan<byte> encPayload,
        ReadOnlySpan<byte> iv12,
        ReadOnlySpan<byte> pollSecret32,
        string pollMessageId,
        string pollCreatorJid,
        string voterJid)
    {
        byte[] pt = DecryptAddon(encPayload, iv12, pollSecret32, pollMessageId, pollCreatorJid, voterJid, AddonModificationType.PollVote);
        var vote = Message.Types.PollVoteMessage.Parser.ParseFrom(pt);
        return vote.SelectedOptions.Select(s => s.ToByteArray()).ToArray();
    }

    public static (byte[] EncPayload, byte[] Iv12) EncryptReaction(
        string emojiText,
        long senderTimestampMs,
        ReadOnlySpan<byte> parentSecret32,
        string parentMessageId,
        string parentSenderJid,
        string reactorJid,
        ReadOnlySpan<byte> ivOverride = default)
    {
        // Per WA Web / wacore: inner plaintext carries ONLY text + senderTimestampMs (key stays in outer envelope).
        var inner = new Message.Types.ReactionMessage
        {
            Text = emojiText ?? string.Empty,
            SenderTimestampMs = senderTimestampMs
        };
        return EncryptAddon(inner.ToByteArray(), parentSecret32, parentMessageId, parentSenderJid, reactorJid, AddonModificationType.EncReaction, ivOverride);
    }

    public static Message.Types.ReactionMessage DecryptReaction(
        ReadOnlySpan<byte> encPayload,
        ReadOnlySpan<byte> iv12,
        ReadOnlySpan<byte> parentSecret32,
        string parentMessageId,
        string parentSenderJid,
        string reactorJid)
    {
        byte[] pt = DecryptAddon(encPayload, iv12, parentSecret32, parentMessageId, parentSenderJid, reactorJid, AddonModificationType.EncReaction);
        return Message.Types.ReactionMessage.Parser.ParseFrom(pt);
    }

    public static (byte[] EncPayload, byte[] Iv12) EncryptEventResponse(
        Message.Types.EventResponseMessage response,
        ReadOnlySpan<byte> eventSecret32,
        string eventMessageId,
        string eventCreatorJid,
        string responderJid,
        ReadOnlySpan<byte> ivOverride = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return EncryptAddon(response.ToByteArray(), eventSecret32, eventMessageId, eventCreatorJid, responderJid, AddonModificationType.EventResponse, ivOverride);
    }

    public static Message.Types.EventResponseMessage DecryptEventResponse(
        ReadOnlySpan<byte> encPayload,
        ReadOnlySpan<byte> iv12,
        ReadOnlySpan<byte> eventSecret32,
        string eventMessageId,
        string eventCreatorJid,
        string responderJid)
    {
        byte[] pt = DecryptAddon(encPayload, iv12, eventSecret32, eventMessageId, eventCreatorJid, responderJid, AddonModificationType.EventResponse);
        return Message.Types.EventResponseMessage.Parser.ParseFrom(pt);
    }

    public static (byte[] EncPayload, byte[] Iv12) EncryptSecretMessage(
        Message innerMessage,
        ReadOnlySpan<byte> parentSecret32,
        string targetMessageId,
        string targetSenderJid,
        string modifierJid,
        AddonModificationType type,
        ReadOnlySpan<byte> ivOverride = default)
    {
        ArgumentNullException.ThrowIfNull(innerMessage);
        return EncryptAddon(innerMessage.ToByteArray(), parentSecret32, targetMessageId, targetSenderJid, modifierJid, type, ivOverride);
    }

    public static Message DecryptSecretMessage(
        ReadOnlySpan<byte> encPayload,
        ReadOnlySpan<byte> iv12,
        ReadOnlySpan<byte> parentSecret32,
        string targetMessageId,
        string targetSenderJid,
        string modifierJid,
        AddonModificationType type)
    {
        byte[] pt = DecryptAddon(encPayload, iv12, parentSecret32, targetMessageId, targetSenderJid, modifierJid, type);
        return Message.Parser.ParseFrom(pt);
    }
}
