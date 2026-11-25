using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Whatsapp;

namespace WhatsApp.Protocol.Crypto;

public enum MediaRetryStatus
{
    GeneralError = 0,
    Success = 1,
    NotFound = 2,
    DecryptionError = 3,
}

public sealed record MediaRetryDecryptedResult(
    MediaRetryStatus Status,
    string? DirectPath);

/// <summary>
/// Encrypts <see cref="ServerErrorReceipt"/> payloads and decrypts <see cref="MediaRetryNotification"/>
/// server responses using HKDF-SHA256 (<c>"WhatsApp Media Retry Notification"</c>) + AES-256-GCM.
/// Matches live <c>WAWebCryptoMediaRetry</c> and <c>wacore/src/media_retry.rs</c>.
/// </summary>
public static class MediaRetryCrypto
{
    public const int IvSizeBytes = 12;
    public const int TagSizeBytes = 16;
    private static ReadOnlySpan<byte> HkdfInfo => "WhatsApp Media Retry Notification"u8;

    public static byte[] DeriveKey(ReadOnlySpan<byte> mediaKey32)
    {
        if (mediaKey32.Length != 32)
        {
            throw new ArgumentException("Media key must be 32 bytes.", nameof(mediaKey32));
        }

        byte[] key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, mediaKey32, key, salt: default, info: HkdfInfo);
        return key;
    }

    public static (byte[] CiphertextWithTag, byte[] Iv12) EncryptServerErrorReceipt(
        ReadOnlySpan<byte> mediaKey32,
        string stanzaId,
        bool keepMediaKey = false,
        ReadOnlySpan<byte> ivOverride = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(stanzaId);
        byte[] key = DeriveKey(mediaKey32);
        byte[] iv = ivOverride.Length == IvSizeBytes
            ? ivOverride.ToArray()
            : RandomNumberGenerator.GetBytes(IvSizeBytes);

        var receipt = new ServerErrorReceipt { StanzaId = stanzaId };
        if (keepMediaKey)
        {
            receipt.KeepMediaKey = true;
        }

        byte[] plaintext = receipt.ToByteArray();
        byte[] aad = Encoding.UTF8.GetBytes(stanzaId);
        byte[] output = new byte[plaintext.Length + TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(iv, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length, TagSizeBytes), aad);
        return (output, iv);
    }

    public static MediaRetryDecryptedResult DecryptMediaRetryNotification(
        ReadOnlySpan<byte> mediaKey32,
        string expectedStanzaId,
        ReadOnlySpan<byte> iv12,
        ReadOnlySpan<byte> ciphertextWithTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedStanzaId);
        if (iv12.Length != IvSizeBytes)
        {
            throw new ArgumentException($"Media retry IV must be {IvSizeBytes} bytes.", nameof(iv12));
        }
        if (ciphertextWithTag.Length < TagSizeBytes)
        {
            throw new CryptographicException("Media retry payload shorter than GCM tag.");
        }

        byte[] key = DeriveKey(mediaKey32);
        byte[] aad = Encoding.UTF8.GetBytes(expectedStanzaId);
        int ptLen = ciphertextWithTag.Length - TagSizeBytes;
        byte[] plaintext = new byte[ptLen];

        using (var aes = new AesGcm(key, TagSizeBytes))
        {
            aes.Decrypt(
                iv12,
                ciphertextWithTag[..ptLen],
                ciphertextWithTag[ptLen..],
                plaintext,
                aad);
        }

        MediaRetryNotification notif = MediaRetryNotification.Parser.ParseFrom(plaintext);
        if (!notif.HasStanzaId || !string.Equals(notif.StanzaId, expectedStanzaId, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                $"MediaRetryNotification stanza_id '{notif.StanzaId}' does not match '{expectedStanzaId}'.");
        }

        MediaRetryStatus status = notif.HasResult
            ? notif.Result switch
            {
                MediaRetryNotification.Types.ResultType.Success => MediaRetryStatus.Success,
                MediaRetryNotification.Types.ResultType.NotFound => MediaRetryStatus.NotFound,
                MediaRetryNotification.Types.ResultType.DecryptionError => MediaRetryStatus.DecryptionError,
                _ => MediaRetryStatus.GeneralError
            }
            : MediaRetryStatus.GeneralError;

        return new MediaRetryDecryptedResult(status, notif.HasDirectPath ? notif.DirectPath : null);
    }
}
