using System.IO.Compression;

namespace WhatsApp.Protocol.HistorySync;

/// <summary>
/// Handles decompression of History Sync protobuf streams.
/// Supports RFC 1950 (zlib-wrapped deflate) and RFC 1951 (raw deflate).
/// </summary>
public static class HistorySyncInflater
{
    public const int DefaultMaxDecompressedBytes = 64 * 1024 * 1024; // 64 MB safety limit matching Rust

    /// <summary>
    /// Decompresses a compressed history sync payload into raw protobuf bytes.
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> compressedData, int maxDecompressedBytes = DefaultMaxDecompressedBytes)
    {
        if (compressedData.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        // WhatsApp history blobs use standard RFC 1950 zlib framing (header byte 0x78)
        bool isZlib = compressedData.Length >= 2 && compressedData[0] == 0x78;

        using var inMs = new MemoryStream(compressedData.ToArray());
        using Stream decompressor = isZlib
            ? new ZLibStream(inMs, CompressionMode.Decompress)
            : new DeflateStream(inMs, CompressionMode.Decompress);

        using var outMs = new MemoryStream();
        byte[] buffer = new byte[8192];
        int totalRead = 0;
        int read;

        while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalRead += read;
            if (totalRead > maxDecompressedBytes)
            {
                throw new InvalidOperationException($"Decompressed history payload exceeded maximum safety limit of {maxDecompressedBytes} bytes");
            }
            outMs.Write(buffer, 0, read);
        }

        return outMs.ToArray();
    }

    /// <summary>
    /// Compresses a protobuf byte array using zlib (RFC 1950) for testing and fixture generation.
    /// </summary>
    public static byte[] CompressZlib(byte[] uncompressed)
    {
        using var outMs = new MemoryStream();
        using (var zlib = new ZLibStream(outMs, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(uncompressed, 0, uncompressed.Length);
        }
        return outMs.ToArray();
    }
}
