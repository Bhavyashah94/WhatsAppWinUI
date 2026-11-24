using System.IO.Compression;

namespace WhatsApp.Protocol.Binary;

public static class BinaryPacker
{
    public const int MaxDecompressedSize = Constants.MaxDecompressedSize;

    public static byte[] Pack(ReadOnlySpan<byte> nodeBytes)
    {
        byte[] packed = new byte[nodeBytes.Length + 1];
        packed[0] = Constants.FormatPlain;
        nodeBytes.CopyTo(packed.AsSpan(1));
        return packed;
    }

    public static byte[] Unpack(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new InvalidDataException("Empty data");
        }

        byte format = data[0];
        var payload = data[1..];

        if ((format & Constants.FormatCompressed) != 0)
        {
            return DecompressZlib(payload);
        }

        return payload.ToArray();
    }

    public static void CheckPlainPayload(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new InvalidDataException("Empty data");
        }

        if (data[0] != Constants.FormatPlain)
        {
            throw new InvalidDataException($"Unexpected format byte: {data[0]}");
        }

        if (data.Length == 1)
        {
            throw new InvalidDataException("Empty data: format byte has no payload");
        }
    }

    public static byte[] DecompressZlib(ReadOnlySpan<byte> compressed)
    {
        using var inputStream = new MemoryStream(compressed.ToArray());
        using var zlibStream = new ZLibStream(inputStream, CompressionMode.Decompress);
        using var outputStream = new MemoryStream();

        byte[] buffer = new byte[8192];
        int totalRead = 0;
        int read;

        while ((read = zlibStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalRead += read;
            if (totalRead > MaxDecompressedSize)
            {
                throw new InvalidDataException($"Protocol frames larger than {MaxDecompressedSize} bytes after decompression are rejected.");
            }
            outputStream.Write(buffer, 0, read);
        }

        return outputStream.ToArray();
    }

    public static byte[] CompressZlib(ReadOnlySpan<byte> data)
    {
        using var outputStream = new MemoryStream();
        using (var zlibStream = new ZLibStream(outputStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlibStream.Write(data);
        }
        return outputStream.ToArray();
    }

    public static byte[] Marshal(Node node)
    {
        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        return Pack(encoder.ToArray());
    }

    public static Node Unmarshal(ReadOnlySpan<byte> packedData)
    {
        byte[] nodeBytes = Unpack(packedData);
        var decoder = new BinaryDecoder(nodeBytes);
        var node = decoder.ReadNode();
        if (!decoder.IsFinished)
        {
            throw new FormatException($"Leftover data: {decoder.BytesLeft} bytes unread");
        }
        return node;
    }

    public static Node UnmarshalNodeBytes(ReadOnlyMemory<byte> nodeBytes)
    {
        var decoder = new BinaryDecoder(nodeBytes);
        var node = decoder.ReadNode();
        if (!decoder.IsFinished)
        {
            throw new FormatException($"Leftover data: {decoder.BytesLeft} bytes unread");
        }
        return node;
    }
}
