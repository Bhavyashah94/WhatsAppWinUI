using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Whatsapp;

namespace WhatsApp.Protocol.AppState;

public sealed record DecodedAppStateMutation(
    SyncdMutation.Types.SyncdOperation Operation,
    IReadOnlyList<string> Index,
    SyncActionValue? ActionValue,
    int Version);

public sealed record AppStateMutationMacs(byte[] IndexMac, byte[] ValueMac);

/// <summary>
/// Encrypts/encodes outbound <see cref="SyncdMutation"/>s and decrypts/authenticates
/// inbound <see cref="SyncdRecord"/>s using AES-256-CBC (PKCS#7), HMAC-SHA512 content MAC,
/// and HMAC-SHA256 index MAC. Mirrors <c>decode.rs</c> and <c>encode.rs</c>.
/// </summary>
public static class AppStateMutationCodec
{
    public const int IvSizeBytes = 16;
    public const int MacSizeBytes = 32;

    public static (SyncdMutation Mutation, byte[] ValueMac) EncodeRecord(
        SyncdMutation.Types.SyncdOperation operation,
        ReadOnlySpan<byte> indexJsonUtf8,
        SyncActionValue value,
        ExpandedAppStateKeys keys,
        ReadOnlySpan<byte> keyId,
        ReadOnlySpan<byte> iv16,
        int actionVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(keys);
        if (iv16.Length != IvSizeBytes)
        {
            throw new ArgumentException("IV must be exactly 16 bytes.", nameof(iv16));
        }

        byte[] plaintext = new SyncActionData
        {
            Index = ByteString.CopyFrom(indexJsonUtf8),
            Value = value.Clone(),
            Version = actionVersion
        }.ToByteArray();

        using var aes = Aes.Create();
        aes.Key = keys.ValueEncryptionKey;
        byte[] ciphertext = aes.EncryptCbc(plaintext, iv16, PaddingMode.PKCS7);

        byte[] content = new byte[IvSizeBytes + ciphertext.Length];
        iv16.CopyTo(content);
        ciphertext.CopyTo(content.AsSpan(IvSizeBytes));

        byte[] valueMac = AppStateHasher.GenerateContentMac(operation, content, keyId, keys.ValueMacKey);
        byte[] valueBlob = new byte[content.Length + MacSizeBytes];
        content.CopyTo(valueBlob, 0);
        valueMac.CopyTo(valueBlob, content.Length);

        byte[] indexMac = AppStateHasher.GenerateIndexMac(indexJsonUtf8, keys.IndexKey);

        var mutation = new SyncdMutation
        {
            Operation = operation,
            Record = new SyncdRecord
            {
                Index = new SyncdIndex { Blob = ByteString.CopyFrom(indexMac) },
                Value = new SyncdValue { Blob = ByteString.CopyFrom(valueBlob) },
                KeyId = new KeyId { Id = ByteString.CopyFrom(keyId) }
            }
        };

        return (mutation, valueMac);
    }

    public static (DecodedAppStateMutation Mutation, AppStateMutationMacs Macs) DecodeRecord(
        SyncdMutation.Types.SyncdOperation operation,
        SyncdRecord record,
        ExpandedAppStateKeys keys,
        ReadOnlySpan<byte> keyId,
        bool validateMacs = true)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(keys);

        if (record.Value is not { HasBlob: true })
        {
            throw new AppStateException(AppStateErrorKind.MissingValueBlob, "SyncdRecord missing value blob.");
        }

        ReadOnlySpan<byte> valueBlob = record.Value.Blob.Span;
        if (valueBlob.Length < IvSizeBytes + MacSizeBytes)
        {
            throw new AppStateException(AppStateErrorKind.ValueBlobTooShort, $"Value blob too short ({valueBlob.Length} bytes).");
        }

        ReadOnlySpan<byte> content = valueBlob[..^MacSizeBytes];
        ReadOnlySpan<byte> iv = content[..IvSizeBytes];
        ReadOnlySpan<byte> ciphertext = content[IvSizeBytes..];
        ReadOnlySpan<byte> valueMac = valueBlob[^MacSizeBytes..];

        if (validateMacs)
        {
            byte[] expectedContentMac = AppStateHasher.GenerateContentMac(operation, content, keyId, keys.ValueMacKey);
            if (!CryptographicOperations.FixedTimeEquals(expectedContentMac, valueMac))
            {
                throw new AppStateException(AppStateErrorKind.MismatchingContentMac, "Content MAC verification failed.");
            }
        }

        byte[] plaintext;
        try
        {
            using var aes = Aes.Create();
            aes.Key = keys.ValueEncryptionKey;
            plaintext = aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException ex)
        {
            throw new AppStateException(AppStateErrorKind.DecryptionFailed, "Failed to AES-256-CBC decrypt SyncActionData.", ex);
        }

        SyncActionData actionData;
        try
        {
            actionData = SyncActionData.Parser.ParseFrom(plaintext);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new AppStateException(AppStateErrorKind.DecodeFailed, "Failed to parse decrypted SyncActionData protobuf.", ex);
        }

        if (record.Index is not { HasBlob: true })
        {
            throw new AppStateException(AppStateErrorKind.MissingIndexMac, "SyncdRecord missing index MAC.");
        }

        byte[] storedIndexMac = record.Index.Blob.ToByteArray();
        if (validateMacs)
        {
            ReadOnlySpan<byte> rawIndex = actionData.HasIndex ? actionData.Index.Span : ReadOnlySpan<byte>.Empty;
            byte[] expectedIndexMac = AppStateHasher.GenerateIndexMac(rawIndex, keys.IndexKey);
            if (!CryptographicOperations.FixedTimeEquals(expectedIndexMac, storedIndexMac))
            {
                throw new AppStateException(AppStateErrorKind.MismatchingIndexMac, "Index MAC verification failed.");
            }
        }

        IReadOnlyList<string> parsedIndex = [];
        if (actionData.HasIndex && actionData.Index.Length > 0)
        {
            try
            {
                parsedIndex = JsonSerializer.Deserialize<string[]>(actionData.Index.Span) ?? [];
            }
            catch (JsonException)
            {
                parsedIndex = [];
            }
        }

        var decoded = new DecodedAppStateMutation(
            Operation: operation,
            Index: parsedIndex,
            ActionValue: actionData.Value,
            Version: actionData.HasVersion ? actionData.Version : 0);

        return (decoded, new AppStateMutationMacs(storedIndexMac, valueMac.ToArray()));
    }
}
