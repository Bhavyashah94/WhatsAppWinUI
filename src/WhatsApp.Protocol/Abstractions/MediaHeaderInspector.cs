using System.Buffers.Binary;
using System.Text;

namespace WhatsApp.Protocol.Abstractions;

public sealed record MediaHeaderMetadata(
    uint? Width = null,
    uint? Height = null,
    uint? Seconds = null);

/// <summary>
/// Pure-.NET lightweight binary header inspector extracting dimensions (PNG, JPEG, WebP) and
/// duration (OGG/Opus, MP4) without native graphics or codec dependencies.
/// </summary>
public static class MediaHeaderInspector
{
    public static MediaHeaderMetadata InspectFile(string filePath, string? mimeType = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new MediaHeaderMetadata();
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long len = fs.Length;
        if (len <= 0)
        {
            return new MediaHeaderMetadata();
        }

        // Read head (up to 128 KiB) for headers + JPEG SOF markers + MP4 moov/mvhd if early
        int headLen = (int)Math.Min(131_072, len);
        byte[] head = new byte[headLen];
        int readHead = ReadExact(fs, head, 0, headLen);
        MediaHeaderMetadata meta = Inspect(head.AsSpan(0, readHead), mimeType);
        if (meta.Seconds.HasValue || len <= headLen)
        {
            return meta;
        }

        // Large OGG/Opus or MP4 with trailing mvhd/OggS page: scan final 64 KiB
        int tailLen = (int)Math.Min(65_536, len);
        byte[] tail = new byte[tailLen];
        fs.Seek(len - tailLen, SeekOrigin.Begin);
        int readTail = ReadExact(fs, tail, 0, tailLen);
        ReadOnlySpan<byte> tailSpan = tail.AsSpan(0, readTail);

        uint? sec = TryParseOggOpusSeconds(head.AsSpan(0, readHead), tailSpan)
            ?? TryFindMp4MvhdSeconds(tailSpan);
        return sec.HasValue ? meta with { Seconds = sec } : meta;
    }

    public static MediaHeaderMetadata Inspect(ReadOnlySpan<byte> data, string? mimeType = null)
    {
        if (data.Length < 4)
        {
            return new MediaHeaderMetadata();
        }

        if (TryParsePng(data, out uint pw, out uint ph))
        {
            return new MediaHeaderMetadata(Width: pw, Height: ph);
        }
        if (TryParseWebP(data, out uint ww, out uint wh))
        {
            return new MediaHeaderMetadata(Width: ww, Height: wh);
        }
        if (TryParseJpeg(data, out uint jw, out uint jh))
        {
            return new MediaHeaderMetadata(Width: jw, Height: jh);
        }
        if (TryParseOggOpusSeconds(data, data) is uint oggSec)
        {
            return new MediaHeaderMetadata(Seconds: oggSec);
        }
        if (TryFindMp4MvhdSeconds(data) is uint mp4Sec)
        {
            return new MediaHeaderMetadata(Seconds: mp4Sec);
        }

        _ = mimeType;
        return new MediaHeaderMetadata();
    }

    private static int ReadExact(Stream s, byte[] buf, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, offset + total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private static bool TryParsePng(ReadOnlySpan<byte> d, out uint width, out uint height)
    {
        width = height = 0;
        if (d.Length < 24 || d[0] != 0x89 || d[1] != (byte)'P' || d[2] != (byte)'N' || d[3] != (byte)'G')
        {
            return false;
        }
        if (d[12] != (byte)'I' || d[13] != (byte)'H' || d[14] != (byte)'D' || d[15] != (byte)'R')
        {
            return false;
        }
        width = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(16, 4));
        height = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(20, 4));
        return width > 0 && height > 0;
    }

    private static bool TryParseWebP(ReadOnlySpan<byte> d, out uint width, out uint height)
    {
        width = height = 0;
        if (d.Length < 30 || !d[..4].SequenceEqual("RIFF"u8) || !d.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return false;
        }
        ReadOnlySpan<byte> chunk = d.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            width = (uint)(1 + (d[24] | (d[25] << 8) | (d[26] << 16)));
            height = (uint)(1 + (d[27] | (d[28] << 8) | (d[29] << 16)));
            return true;
        }
        if (chunk.SequenceEqual("VP8L"u8) && d.Length >= 25 && d[20] == 0x2F)
        {
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(21, 4));
            width = (bits & 0x3FFF) + 1;
            height = ((bits >> 14) & 0x3FFF) + 1;
            return true;
        }
        if (chunk.SequenceEqual("VP8 "u8) && d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A)
        {
            width = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(26, 2)) & 0x3FFF);
            height = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(28, 2)) & 0x3FFF);
            return width > 0 && height > 0;
        }
        return false;
    }

    private static bool TryParseJpeg(ReadOnlySpan<byte> d, out uint width, out uint height)
    {
        width = height = 0;
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8)
        {
            return false;
        }

        int i = 2;
        while (i + 4 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            while (i < d.Length && d[i] == 0xFF) i++;
            if (i >= d.Length) break;

            byte marker = d[i++];
            if (marker is 0x00 or 0x01 || (marker >= 0xD0 && marker <= 0xD9)) continue;
            if (i + 2 > d.Length) break;

            int segLen = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i, 2));
            if (marker is 0xC0 or 0xC1 or 0xC2 && i + 7 <= d.Length)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 5, 2));
                return width > 0 && height > 0;
            }
            if (segLen < 2) break;
            i += segLen;
        }
        return false;
    }

    private static uint? TryParseOggOpusSeconds(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail)
    {
        if (head.Length < 36 || !head[..4].SequenceEqual("OggS"u8))
        {
            return null;
        }

        ushort preSkip = 0;
        int opusIdx = head.IndexOf("OpusHead"u8);
        if (opusIdx >= 0 && opusIdx + 12 <= head.Length)
        {
            preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(opusIdx + 10, 2));
        }

        ulong? lastGranule = null;
        int pos = 0;
        while (pos + 27 <= tail.Length)
        {
            int rel = tail[pos..].IndexOf("OggS"u8);
            if (rel < 0) break;
            pos += rel;
            if (pos + 27 > tail.Length) break;
            long g = BinaryPrimitives.ReadInt64LittleEndian(tail.Slice(pos + 6, 8));
            if (g > 0)
            {
                lastGranule = (ulong)g;
            }
            byte segments = tail[pos + 26];
            pos += 27 + segments;
        }

        if (lastGranule is ulong totalSamples && totalSamples > preSkip)
        {
            return Math.Max(1u, (uint)Math.Round((totalSamples - preSkip) / 48000.0));
        }
        return null;
    }

    private static uint? TryFindMp4MvhdSeconds(ReadOnlySpan<byte> data)
    {
        int idx = data.IndexOf("mvhd"u8);
        if (idx < 0 || idx + 24 > data.Length)
        {
            return null;
        }

        byte version = data[idx + 4];
        if (version == 0 && idx + 24 <= data.Length)
        {
            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(idx + 16, 4));
            uint duration = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(idx + 20, 4));
            if (timescale > 0 && duration > 0)
            {
                return Math.Max(1u, (uint)Math.Round((double)duration / timescale));
            }
        }
        else if (version == 1 && idx + 36 <= data.Length)
        {
            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(idx + 24, 4));
            ulong duration = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(idx + 28, 8));
            if (timescale > 0 && duration > 0)
            {
                return Math.Max(1u, (uint)Math.Round((double)duration / timescale));
            }
        }
        return null;
    }
}
