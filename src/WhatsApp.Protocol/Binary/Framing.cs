namespace WhatsApp.Protocol.Binary;

public static class Framing
{
    public const int FrameLengthSize = 3;
    public const int FrameMaxSize = 1 << 24; // 16MB

    public static byte[] EncodeFrame(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> header = default)
    {
        if (payload.Length >= FrameMaxSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"Payload length ({payload.Length}) exceeds maximum frame size ({FrameMaxSize})");
        }

        int totalLen = header.Length + FrameLengthSize + payload.Length;
        byte[] frame = new byte[totalLen];

        int offset = 0;
        if (!header.IsEmpty)
        {
            header.CopyTo(frame.AsSpan(offset));
            offset += header.Length;
        }

        int payloadLen = payload.Length;
        frame[offset] = (byte)((payloadLen >> 16) & 0xFF);
        frame[offset + 1] = (byte)((payloadLen >> 8) & 0xFF);
        frame[offset + 2] = (byte)(payloadLen & 0xFF);
        offset += FrameLengthSize;

        payload.CopyTo(frame.AsSpan(offset));
        return frame;
    }

    public static bool TryDecodeFrame(ReadOnlySpan<byte> buffer, out byte[] payload, out int consumedBytes)
    {
        payload = Array.Empty<byte>();
        consumedBytes = 0;

        if (buffer.Length < FrameLengthSize)
        {
            return false;
        }

        int payloadLen = (buffer[0] << 16) | (buffer[1] << 8) | buffer[2];
        if (payloadLen >= FrameMaxSize)
        {
            throw new InvalidDataException($"Decoded frame length ({payloadLen}) exceeds maximum allowed ({FrameMaxSize})");
        }

        if (buffer.Length < FrameLengthSize + payloadLen)
        {
            return false;
        }

        payload = buffer.Slice(FrameLengthSize, payloadLen).ToArray();
        consumedBytes = FrameLengthSize + payloadLen;
        return true;
    }
}
