using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Protocol.HistorySync;

/// <summary>
/// HistorySync-specific wrapper over <see cref="MediaCipher"/>.
/// </summary>
public static class HistorySyncCrypto
{
    public static readonly byte[] HistoryInfo = "WhatsApp History Keys"u8.ToArray();
    public static readonly byte[] ImageInfo = "WhatsApp Image Keys"u8.ToArray();
    public static readonly byte[] StickerInfo = "WhatsApp Image Keys"u8.ToArray();

    public const int MediaMacSize = MediaCipher.MediaMacSize;
    public const int AesBlockSize = MediaCipher.AesBlockSize;
    public const int KeyLength = MediaCipher.MediaKeyLength;
    public const int ExpandedKeyLength = MediaCipher.ExpandedKeyLength;

    public static (byte[] Iv, byte[] CipherKey, byte[] MacKey) DeriveMediaKeys(ReadOnlySpan<byte> mediaKey, ReadOnlySpan<byte> info)
        => MediaCipher.DeriveMediaKeys(mediaKey, info);

    public static byte[] DecryptHistoryChunk(ReadOnlySpan<byte> encBlob, ReadOnlySpan<byte> mediaKey, ReadOnlySpan<byte> expectedEncSha256 = default)
        => MediaCipher.DecryptBlob(encBlob, mediaKey, WaMediaType.History, expectedEncSha256);

    public static byte[] DecryptBlob(
        ReadOnlySpan<byte> encBlob,
        ReadOnlySpan<byte> mediaKey,
        ReadOnlySpan<byte> info,
        ReadOnlySpan<byte> expectedEncSha256 = default)
        => MediaCipher.DecryptBlob(encBlob, mediaKey, info, expectedEncSha256);

    public static byte[] EncryptBlob(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> mediaKey, ReadOnlySpan<byte> info)
        => MediaCipher.EncryptBlob(plaintext, mediaKey, info);
}
