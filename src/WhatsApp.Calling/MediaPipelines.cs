namespace WhatsApp.Calling;

public sealed record AudioFrame(
    byte[] EncodedPayload,
    uint SampleRate = 16_000,
    int DurationMs = 20,
    bool IsDtx = false);

public sealed record VideoFrame(
    byte[] AnnexBOrAccessUnit,
    uint Timestamp90k,
    bool IsKeyframe = false,
    byte Orientation = 0);

/// <summary>
/// Codec-agnostic audio seam so <see cref="CallEngine"/> stays pure Sans-I/O and hardware-free
/// while native/managed codecs (Concentus Opus, WASAPI, MediaFoundation) plug in cleanly in CLI/Desktop.
/// </summary>
public interface IAudioCodec
{
    string Name { get; }
    byte[] EncodePcm16(ReadOnlySpan<short> pcm16Mono);
    short[] DecodePcm16(ReadOnlySpan<byte> encodedPayload);
}

public interface IVideoCodec
{
    string Name { get; }
    byte[] EncodeFrame(ReadOnlySpan<byte> rawFrame, bool forceKeyframe);
    byte[] DecodeAccessUnit(ReadOnlySpan<byte> accessUnit);
}

public sealed class PassThroughAudioCodec : IAudioCodec
{
    public string Name => "passthrough-opus";

    public byte[] EncodePcm16(ReadOnlySpan<short> pcm16Mono)
    {
        byte[] bytes = new byte[pcm16Mono.Length * 2];
        for (int i = 0; i < pcm16Mono.Length; i++)
        {
            bytes[i * 2] = (byte)(pcm16Mono[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((pcm16Mono[i] >> 8) & 0xFF);
        }
        return bytes;
    }

    public short[] DecodePcm16(ReadOnlySpan<byte> encodedPayload)
    {
        short[] pcm = new short[encodedPayload.Length / 2];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(encodedPayload[i * 2] | (encodedPayload[i * 2 + 1] << 8));
        }
        return pcm;
    }
}

public sealed class JitterBuffer
{
    private readonly SortedDictionary<ushort, byte[]> _frames = new();
    private readonly int _maxDepth;

    public JitterBuffer(int maxDepth = 50)
    {
        _maxDepth = Math.Max(4, maxDepth);
    }

    public int Count => _frames.Count;

    public void Push(ushort seq, byte[] payload)
    {
        _frames[seq] = payload;
        while (_frames.Count > _maxDepth)
        {
            ushort oldest = _frames.Keys.First();
            _frames.Remove(oldest);
        }
    }

    public bool TryPopNext(out ushort seq, out byte[] payload)
    {
        if (_frames.Count == 0)
        {
            seq = 0;
            payload = Array.Empty<byte>();
            return false;
        }
        seq = _frames.Keys.First();
        payload = _frames[seq];
        _frames.Remove(seq);
        return true;
    }
}

/// <summary>
/// H.264 Annex-B / FU-A fragmentation &amp; IDR keyframe detection for WhatsApp video calls.
/// </summary>
public static class H264Packetizer
{
    public const int MaxMtuPayload = 1150;

    public static bool ContainsIdr(ReadOnlySpan<byte> accessUnit)
    {
        for (int i = 0; i + 4 < accessUnit.Length; i++)
        {
            if (accessUnit[i] == 0 && accessUnit[i + 1] == 0
                && ((accessUnit[i + 2] == 1 && i + 3 < accessUnit.Length && (accessUnit[i + 3] & 0x1F) == 5)
                    || (accessUnit[i + 2] == 0 && accessUnit[i + 3] == 1 && i + 4 < accessUnit.Length && (accessUnit[i + 4] & 0x1F) == 5)))
            {
                return true;
            }
        }
        return accessUnit.Length > 0 && (accessUnit[0] & 0x1F) == 5;
    }

    public static IReadOnlyList<byte[]> PacketizeNalu(ReadOnlySpan<byte> nalu, int maxFragmentBytes = MaxMtuPayload)
    {
        if (nalu.Length <= maxFragmentBytes)
        {
            return [nalu.ToArray()];
        }

        byte naluHeader = nalu[0];
        byte fNri = (byte)(naluHeader & 0xE0);
        byte nalType = (byte)(naluHeader & 0x1F);
        ReadOnlySpan<byte> body = nalu[1..];
        int chunkMax = Math.Max(64, maxFragmentBytes - 2);

        var packets = new List<byte[]>();
        int offset = 0;
        bool first = true;
        while (offset < body.Length)
        {
            int take = Math.Min(chunkMax, body.Length - offset);
            bool last = offset + take >= body.Length;

            byte[] frag = new byte[2 + take];
            frag[0] = (byte)(fNri | 28); // FU-A indicator
            frag[1] = (byte)((first ? 0x80 : 0x00) | (last ? 0x40 : 0x00) | nalType);
            body.Slice(offset, take).CopyTo(frag.AsSpan(2));
            packets.Add(frag);

            first = false;
            offset += take;
        }
        return packets;
    }
}
