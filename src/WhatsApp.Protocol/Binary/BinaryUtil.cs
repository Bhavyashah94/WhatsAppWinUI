namespace WhatsApp.Protocol.Binary;

public static class BinaryUtil
{
    public const byte FormatPlain = Constants.FormatPlain;
    public const byte FormatCompressed = Constants.FormatCompressed;
    public const int MaxDecompressedSize = Constants.MaxDecompressedSize;

    public static byte[] Pack(ReadOnlySpan<byte> nodeBytes) => BinaryPacker.Pack(nodeBytes);
    public static byte[] Unpack(ReadOnlySpan<byte> data) => BinaryPacker.Unpack(data);
    public static void CheckPlainPayload(ReadOnlySpan<byte> data) => BinaryPacker.CheckPlainPayload(data);
    public static byte[] DecompressZlib(ReadOnlySpan<byte> compressed) => BinaryPacker.DecompressZlib(compressed);
    public static byte[] CompressZlib(ReadOnlySpan<byte> data) => BinaryPacker.CompressZlib(data);
    public static byte[] Marshal(Node node) => BinaryPacker.Marshal(node);
    public static Node Unmarshal(ReadOnlySpan<byte> packedData) => BinaryPacker.Unmarshal(packedData);
    public static Node UnmarshalNodeBytes(ReadOnlyMemory<byte> nodeBytes) => BinaryPacker.UnmarshalNodeBytes(nodeBytes);
}
