using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WhatsApp.Protocol.AppState;

/// <summary>
/// Incremental 128-byte homomorphic lattice hash over little-endian 16-bit lanes,
/// derived with HKDF-SHA256 (<c>"WhatsApp Patch Integrity"</c>).
/// Matches live <c>WALTHash</c> (<c>WAWebSyncdLTHash</c>) and <c>wacore/appstate/src/lthash.rs</c>.
/// </summary>
public static class LtHash
{
    public const int HashSizeBytes = 128;
    public static ReadOnlySpan<byte> PatchIntegrityInfo => "WhatsApp Patch Integrity"u8;

    public static byte[] CreateEmpty() => new byte[HashSizeBytes];

    public static byte[] DeriveItem(ReadOnlySpan<byte> item, int sizeBytes = HashSizeBytes)
    {
        byte[] output = new byte[sizeBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, item, output, ReadOnlySpan<byte>.Empty, PatchIntegrityInfo);
        return output;
    }

    public static byte[] SubtractThenAdd(
        ReadOnlySpan<byte> baseHash,
        IEnumerable<byte[]>? subtractItems,
        IEnumerable<byte[]>? addItems)
    {
        if (baseHash.Length == 0 || (baseHash.Length & 1) != 0)
        {
            throw new ArgumentException("LtHash buffer must have a non-zero even byte length.", nameof(baseHash));
        }

        byte[] output = baseHash.ToArray();
        SubtractThenAddInPlace(output, subtractItems, addItems);
        return output;
    }

    public static void SubtractThenAddInPlace(
        Span<byte> baseHash,
        IEnumerable<byte[]>? subtractItems,
        IEnumerable<byte[]>? addItems)
    {
        if (baseHash.Length == 0 || (baseHash.Length & 1) != 0)
        {
            throw new ArgumentException("LtHash buffer must have a non-zero even byte length.", nameof(baseHash));
        }

        Span<byte> derived = baseHash.Length <= 256
            ? stackalloc byte[baseHash.Length]
            : new byte[baseHash.Length];

        if (subtractItems is not null)
        {
            foreach (byte[] item in subtractItems)
            {
                HKDF.DeriveKey(HashAlgorithmName.SHA256, item, derived, ReadOnlySpan<byte>.Empty, PatchIntegrityInfo);
                ApplyPointwiseUint16(baseHash, derived, subtract: true);
            }
        }

        if (addItems is not null)
        {
            foreach (byte[] item in addItems)
            {
                HKDF.DeriveKey(HashAlgorithmName.SHA256, item, derived, ReadOnlySpan<byte>.Empty, PatchIntegrityInfo);
                ApplyPointwiseUint16(baseHash, derived, subtract: false);
            }
        }
    }

    /// <summary>
    /// Adds or subtracts little-endian <c>uint16</c> lanes modulo 65536
    /// (matches <c>DataView.getUint16(offset, true)</c> in WA Web <c>WALTHash</c>).
    /// </summary>
    public static void ApplyPointwiseUint16(Span<byte> target, ReadOnlySpan<byte> operand, bool subtract)
    {
        if (target.Length != operand.Length)
        {
            throw new ArgumentException("LtHash operand length must match base buffer length.", nameof(operand));
        }
        if ((target.Length & 1) != 0)
        {
            throw new ArgumentException("LtHash buffer length must be even.", nameof(target));
        }

        for (int i = 0; i < target.Length; i += 2)
        {
            ushort a = BinaryPrimitives.ReadUInt16LittleEndian(target.Slice(i, 2));
            ushort b = BinaryPrimitives.ReadUInt16LittleEndian(operand.Slice(i, 2));
            ushort sum = subtract ? unchecked((ushort)(a - b)) : unchecked((ushort)(a + b));
            BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(i, 2), sum);
        }
    }
}
