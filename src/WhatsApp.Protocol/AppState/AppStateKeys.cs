using System.Security.Cryptography;

namespace WhatsApp.Protocol.AppState;

/// <summary>
/// Expanded 160-byte AppState (<c>syncd</c>) key schedule derived from a 32-byte master key.
/// Matches live <c>WAWebSyncdCryptoHelper</c> and <c>wacore/appstate/src/keys.rs</c>.
/// </summary>
public sealed record ExpandedAppStateKeys(
    byte[] IndexKey,
    byte[] ValueEncryptionKey,
    byte[] ValueMacKey,
    byte[] SnapshotMacKey,
    byte[] PatchMacKey);

public static class AppStateKeys
{
    public static ReadOnlySpan<byte> MutationKeysInfo => "WhatsApp Mutation Keys"u8;
    public const int ExpandedByteLength = 160;

    /// <summary>
    /// Expands a 32-byte sync key via HKDF-SHA256 (<c>"WhatsApp Mutation Keys"</c>)
    /// into five 32-byte subkeys: <c>Index</c>, <c>ValueEncryption</c>, <c>ValueMac</c>,
    /// <c>SnapshotMac</c>, and <c>PatchMac</c>.
    /// </summary>
    public static ExpandedAppStateKeys Expand(ReadOnlySpan<byte> masterKeyData)
    {
        Span<byte> okm = stackalloc byte[ExpandedByteLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKeyData, okm, ReadOnlySpan<byte>.Empty, MutationKeysInfo);

        return new ExpandedAppStateKeys(
            IndexKey: okm[..32].ToArray(),
            ValueEncryptionKey: okm[32..64].ToArray(),
            ValueMacKey: okm[64..96].ToArray(),
            SnapshotMacKey: okm[96..128].ToArray(),
            PatchMacKey: okm[128..160].ToArray());
    }
}
