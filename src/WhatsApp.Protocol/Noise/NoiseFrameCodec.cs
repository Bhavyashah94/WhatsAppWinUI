using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using WhatsApp.Protocol.Binary;

namespace WhatsApp.Protocol.Noise;

/// <summary>
/// Helpers for WhatsApp Noise connection preambles (<c>WA\x06\x03</c> and optional <c>ED\x00\x01</c> edge-routing pre-intro).
/// </summary>
public static class NoiseHandshakeHeader
{
    public const int MaxEdgeRoutingLength = 0xFF_FFFF;
    private static ReadOnlySpan<byte> EdgeRoutingMagic => "ED\x00\x01"u8;

    /// <summary>
    /// Builds the <c>ED\x00\x01 || len_be24 || routingInfo</c> pre-intro bytes.
    /// </summary>
    public static byte[] BuildEdgeRoutingPreIntro(ReadOnlySpan<byte> routingInfo)
    {
        if (routingInfo.Length > MaxEdgeRoutingLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(routingInfo),
                $"Edge routing info length ({routingInfo.Length}) exceeds {MaxEdgeRoutingLength} bytes.");
        }

        byte[] preIntro = new byte[4 + Framing.FrameLengthSize + routingInfo.Length];
        EdgeRoutingMagic.CopyTo(preIntro);
        WriteLength24(preIntro.AsSpan(4), routingInfo.Length);
        routingInfo.CopyTo(preIntro.AsSpan(4 + Framing.FrameLengthSize));
        return preIntro;
    }

    /// <summary>
    /// Builds the initial connection preamble (<c>[ED pre-intro] + WA\x06\x03</c>).
    /// Falls back silently to <c>WA\x06\x03</c> if <paramref name="edgeRoutingInfo"/> is empty or oversized.
    /// </summary>
    public static (byte[] Header, bool UsedEdgeRouting) BuildHandshakeHeader(ReadOnlySpan<byte> edgeRoutingInfo = default)
    {
        if (edgeRoutingInfo.IsEmpty || edgeRoutingInfo.Length > MaxEdgeRoutingLength)
        {
            return (Constants.WaConnHeader.ToArray(), false);
        }

        byte[] header = new byte[4 + Framing.FrameLengthSize + edgeRoutingInfo.Length + Constants.WaConnHeader.Length];
        EdgeRoutingMagic.CopyTo(header);
        WriteLength24(header.AsSpan(4), edgeRoutingInfo.Length);
        edgeRoutingInfo.CopyTo(header.AsSpan(4 + Framing.FrameLengthSize));
        Constants.WaConnHeader.CopyTo(header.AsSpan(4 + Framing.FrameLengthSize + edgeRoutingInfo.Length));
        return (header, true);
    }

    internal static void WriteLength24(Span<byte> destination, int length)
    {
        destination[0] = (byte)((length >> 16) & 0xFF);
        destination[1] = (byte)((length >> 8) & 0xFF);
        destination[2] = (byte)(length & 0xFF);
    }

    internal static int ReadLength24(ReadOnlySpan<byte> source)
    {
        int length = (source[0] << 16) | (source[1] << 8) | source[2];
        if (length >= Framing.FrameMaxSize)
        {
            throw new InvalidDataException($"Decoded Noise frame length ({length}) exceeds maximum ({Framing.FrameMaxSize}).");
        }
        return length;
    }
}

/// <summary>
/// Stateful counter-synchronized AES-256-GCM frame encoder matching WhatsApp's 3-byte length-prefixed Noise transport.
/// Once poisoned (for example after a partial/ambiguous transport failure), further sends fail permanently.
/// </summary>
public sealed class NoiseFrameEncoder : IDisposable
{
    private readonly NoiseCipher _cipher;
    private uint _counter;
    private bool _poisoned;
    private bool _disposed;

    public NoiseFrameEncoder(NoiseCipher cipher)
    {
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
    }

    public uint Counter => _counter;
    public bool IsPoisoned => _poisoned;

    public void Poison() => _poisoned = true;

    public byte[] EncodeFrame(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> handshakeHeader = default)
    {
        var writer = new ArrayBufferWriter<byte>(handshakeHeader.Length + Framing.FrameLengthSize + plaintext.Length + NoiseCipher.TagLength);
        EncodeFrameInto(plaintext, writer, handshakeHeader);
        return writer.WrittenSpan.ToArray();
    }

    public int EncodeFrameInto(
        ReadOnlySpan<byte> plaintext,
        IBufferWriter<byte> destination,
        ReadOnlySpan<byte> handshakeHeader = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_poisoned)
        {
            throw new InvalidOperationException("NoiseFrameEncoder has been poisoned; key cannot be reused.");
        }
        if (_counter == uint.MaxValue)
        {
            throw new OverflowException("Noise write counter exhausted.");
        }

        int ciphertextLen = checked(plaintext.Length + NoiseCipher.TagLength);
        if (ciphertextLen >= Framing.FrameMaxSize)
        {
            throw new ArgumentOutOfRangeException(nameof(plaintext), $"Encrypted frame length ({ciphertextLen}) exceeds maximum ({Framing.FrameMaxSize}).");
        }

        byte[] ciphertext = _cipher.EncryptWithCounter(_counter, plaintext);
        if (ciphertext.Length != ciphertextLen)
        {
            throw new CryptographicException($"Unexpected encrypted payload size {ciphertext.Length}, expected {ciphertextLen}.");
        }

        int totalLen = handshakeHeader.Length + Framing.FrameLengthSize + ciphertext.Length;
        Span<byte> target = destination.GetSpan(totalLen);
        int offset = 0;

        if (!handshakeHeader.IsEmpty)
        {
            handshakeHeader.CopyTo(target);
            offset += handshakeHeader.Length;
        }

        NoiseHandshakeHeader.WriteLength24(target.Slice(offset, Framing.FrameLengthSize), ciphertext.Length);
        offset += Framing.FrameLengthSize;

        ciphertext.CopyTo(target.Slice(offset));
        destination.Advance(totalLen);
        _counter++;
        return totalLen;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cipher.Dispose();
    }
}

/// <summary>
/// Incremental stream decoder for 3-byte big-endian length-prefixed Noise frames across arbitrary WebSocket/TCP chunk boundaries.
/// </summary>
public sealed class NoiseFrameDecoder
{
    private byte[] _buffer = new byte[4096];
    private int _offset;
    private int _length;
    private int _pendingFrameLength = -1;

    public int BufferedBytes => _length - _offset;

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return;

        int unread = _length - _offset;
        if (_length + chunk.Length > _buffer.Length)
        {
            Compact();
            if (_length + chunk.Length > _buffer.Length)
            {
                int newSize = Math.Max(_buffer.Length * 2, unread + chunk.Length);
                Array.Resize(ref _buffer, newSize);
            }
        }

        chunk.CopyTo(_buffer.AsSpan(_length));
        _length += chunk.Length;
    }

    /// <summary>
    /// Attempts to decode the next complete raw length-prefixed frame payload (ciphertext or handshake protobuf bytes).
    /// </summary>
    public bool TryReadRawFrame(out byte[] framePayload)
    {
        int unread = _length - _offset;
        if (_pendingFrameLength < 0)
        {
            if (unread < Framing.FrameLengthSize)
            {
                framePayload = [];
                return false;
            }

            _pendingFrameLength = NoiseHandshakeHeader.ReadLength24(_buffer.AsSpan(_offset, Framing.FrameLengthSize));
        }

        int totalNeeded = Framing.FrameLengthSize + _pendingFrameLength;
        if (unread < totalNeeded)
        {
            framePayload = [];
            return false;
        }

        framePayload = _buffer.AsSpan(_offset + Framing.FrameLengthSize, _pendingFrameLength).ToArray();
        _offset += totalNeeded;
        _pendingFrameLength = -1;

        if (_offset == _length)
        {
            _offset = 0;
            _length = 0;
        }
        else if (_offset > _buffer.Length / 2)
        {
            Compact();
        }

        return true;
    }

    /// <summary>
    /// Extracts any unread trailing bytes currently buffered in the decoder and resets internal state.
    /// Used after completing a Noise handshake to hand coalesced transport bytes directly to
    /// <see cref="NoiseSessionCodec"/>.
    /// </summary>
    public byte[] DrainBufferedBytes()
    {
        int unread = _length - _offset;
        if (unread <= 0)
        {
            _offset = 0;
            _length = 0;
            _pendingFrameLength = -1;
            return [];
        }

        byte[] trailing = _buffer.AsSpan(_offset, unread).ToArray();
        _offset = 0;
        _length = 0;
        _pendingFrameLength = -1;
        return trailing;
    }

    private void Compact()
    {
        if (_offset == 0) return;

        int unread = _length - _offset;
        if (unread > 0)
        {
            Buffer.BlockCopy(_buffer, _offset, _buffer, 0, unread);
        }
        _offset = 0;
        _length = unread;
    }
}

/// <summary>
/// Full-duplex Noise transport session codec combining <see cref="NoiseFrameEncoder"/>,
/// <see cref="NoiseFrameDecoder"/>, and monotonic read-counter AES-256-GCM decryption.
/// </summary>
public sealed class NoiseSessionCodec : IDisposable
{
    private readonly NoiseFrameEncoder _encoder;
    private readonly NoiseCipher _readCipher;
    private readonly NoiseFrameDecoder _decoder = new();
    private uint _readCounter;
    private bool _disposed;

    public NoiseSessionCodec(NoiseKeys keys, ReadOnlySpan<byte> initialRemainder = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _encoder = new NoiseFrameEncoder(keys.WriteCipher);
        _readCipher = keys.ReadCipher;
        if (!initialRemainder.IsEmpty)
        {
            _decoder.Feed(initialRemainder);
        }
    }

    public uint WriteCounter => _encoder.Counter;
    public uint ReadCounter => _readCounter;
    public int BufferedBytes => _decoder.BufferedBytes;
    public bool IsEncoderPoisoned => _encoder.IsPoisoned;

    public byte[] EncryptFrame(ReadOnlySpan<byte> plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _encoder.EncodeFrame(plaintext);
    }

    public int EncryptFrameInto(ReadOnlySpan<byte> plaintext, IBufferWriter<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _encoder.EncodeFrameInto(plaintext, destination);
    }

    public void Feed(ReadOnlySpan<byte> wireChunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _decoder.Feed(wireChunk);
    }

    public bool TryDecryptNextFrame(out byte[] plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_decoder.TryReadRawFrame(out byte[] encryptedFrame))
        {
            plaintext = [];
            return false;
        }

        if (_readCounter == uint.MaxValue)
        {
            throw new OverflowException("Noise read counter exhausted.");
        }

        // Only advance the read counter after AES-GCM authentication succeeds.
        plaintext = _readCipher.DecryptWithCounter(_readCounter, encryptedFrame);
        _readCounter++;
        return true;
    }

    public void PoisonEncoder() => _encoder.Poison();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoder.Dispose();
        _readCipher.Dispose();
    }
}
