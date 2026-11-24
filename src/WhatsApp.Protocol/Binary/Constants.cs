namespace WhatsApp.Protocol.Binary;

public static class Constants
{
    public const byte DictVersion = 3;
    public const byte WaMagicValue = 6;
    public static readonly byte[] WaConnHeader = [(byte)'W', (byte)'A', WaMagicValue, DictVersion];

    // Format Bytes
    public const byte FormatPlain = 0;
    public const byte FormatCompressed = 2;
    public const int MaxDecompressedSize = 16 * 1024 * 1024; // 16 MiB

    // Special Tags
    public const byte ListEmpty = 0;
    public const byte Dictionary0 = 236;
    public const byte Dictionary1 = 237;
    public const byte Dictionary2 = 238;
    public const byte Dictionary3 = 239;

    public const byte InteropJid = 245;
    public const byte FbJid = 246;
    public const byte AdJid = 247;
    public const byte List8 = 248;
    public const byte List16 = 249;
    public const byte JidPair = 250;
    public const byte Hex8 = 251;
    public const byte Binary8 = 252;
    public const byte Binary20 = 253;
    public const byte Binary32 = 254;
    public const byte Nibble8 = 255;

    public const byte PackedMax = 127;
    public const int SingleByteMax = 256;
    public const int MaxNodeDepth = 128;
}
