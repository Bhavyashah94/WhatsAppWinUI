using System.Security.Cryptography;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Api;

/// <summary>
/// Runtime client for sending encrypted threaded CAG comments (<c>EncCommentMessage</c>)
/// under parent Community Announcement Group posts using HKDF-SHA256 (<c>"Enc Comment"</c>) + AES-256-GCM.
/// </summary>
public sealed class CommentClient
{
    private readonly MessageSender _messageSender;
    private readonly IWhatsAppBackend _backend;
    private readonly DeviceCredentials _credentials;
    private readonly TimeProvider _timeProvider;

    public CommentClient(
        MessageSender messageSender,
        IWhatsAppBackend backend,
        DeviceCredentials credentials,
        TimeProvider? timeProvider = null)
    {
        _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<SendMessageResult> SendTextCommentAsync(
        Jid groupJid,
        MessageKey parentKey,
        string text,
        IReadOnlyList<Jid> groupParticipants,
        byte[]? parentMessageSecretOverride = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var body = new Message { ExtendedTextMessage = new Message.Types.ExtendedTextMessage { Text = text } };
        return SendCommentMessageAsync(groupJid, parentKey, body, groupParticipants, parentMessageSecretOverride, ct);
    }

    public async Task<SendMessageResult> SendCommentMessageAsync(
        Jid groupJid,
        MessageKey parentKey,
        Message commentBody,
        IReadOnlyList<Jid> groupParticipants,
        byte[]? parentMessageSecretOverride = null,
        CancellationToken ct = default)
    {
        if (!groupJid.IsGroup())
        {
            throw new ArgumentException($"CAG comments require a @g.us group JID, got '{groupJid}'.", nameof(groupJid));
        }
        ArgumentNullException.ThrowIfNull(parentKey);
        ArgumentException.ThrowIfNullOrEmpty(parentKey.Id);
        ArgumentNullException.ThrowIfNull(commentBody);
        ArgumentNullException.ThrowIfNull(groupParticipants);

        Jid commenter = (_credentials.Lid?.ToNonAd() ?? _credentials.Pn?.ToNonAd())
            ?? throw new InvalidOperationException("Local user JID is required to send comments.");

        byte[]? secret = parentMessageSecretOverride;
        if (secret is null)
        {
            var stored = await _backend.MsgSecrets.GetMsgSecretAsync(parentKey.Id, ct).ConfigureAwait(false);
            secret = stored?.Secret;
        }
        if (secret is not { Length: 32 })
        {
            throw new InvalidOperationException($"Missing 32-byte parent messageSecret for '{parentKey.Id}'.");
        }

        string parentSender = parentKey.HasParticipant && !string.IsNullOrEmpty(parentKey.Participant)
            ? Jid.Parse(parentKey.Participant).ToNonAdString()
            : commenter.ToString();

        (byte[] encPayload, byte[] iv12) = AddonEncryption.EncryptComment(
            commentBody,
            secret,
            parentKey.Id,
            parentSender,
            commenter.ToString());

        MessageKey keyCopy = parentKey.Clone();
        if (!keyCopy.HasParticipant || string.IsNullOrEmpty(keyCopy.Participant))
        {
            keyCopy.Participant = parentSender;
        }

        byte[] commentSecret = RandomNumberGenerator.GetBytes(32);
        Message envelope = BroadcastIqs.BuildEncCommentMessage(keyCopy, encPayload, iv12, commentSecret);
        SendMessageResult res = await _messageSender.SendGroupMessageAsync(groupJid, envelope, groupParticipants, ct: ct).ConfigureAwait(false);

        long nowSec = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await _backend.MsgSecrets.PutMsgSecretsAsync([(res.MessageId, commentSecret, nowSec)], ct).ConfigureAwait(false);
        return res;
    }
}
