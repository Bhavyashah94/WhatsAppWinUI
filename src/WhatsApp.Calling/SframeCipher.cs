using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace WhatsApp.Calling;

public enum SframeDecryptStatus
{
    Decrypted,
    Plaintext,
    AuthFailed,
}

public sealed record SframeDecryptResult(SframeDecryptStatus Status, byte[] Data);

/// <summary>
/// SFrame end-to-end media encryption (AES-128-GCM with 16-byte nonce = <c>8 zeros || LE u64 counter</c>,
/// layout <c>[ciphertext || 16B tag || LEB128 varint-header]</c> without AAD) matching
/// <c>wacore/src/voip/sframe/implementation.rs</c>.
/// </summary>
public sealed class SframeSession
{
    public const string KdfLabel = "e2e sframe key";
    public const int AesKeySize = 16;
    public const int GcmTagSize = 16;
    private const int HeaderMaxSize = 21;

    private readonly byte[] _encryptKey16;
    private readonly byte[] _decryptKey16;
    private ulong _txCounter;

    public string SelfParticipantId { get; }
    public string PeerParticipantId { get; }

    public SframeSession(byte[] encryptKey16, byte[] decryptKey16, string selfParticipantId, string peerParticipantId)
    {
        ArgumentNullException.ThrowIfNull(encryptKey16);
        ArgumentNullException.ThrowIfNull(decryptKey16);
        _encryptKey16 = encryptKey16.Length >= AesKeySize ? encryptKey16[..AesKeySize] : throw new ArgumentException("Key must be >= 16 bytes.");
        _decryptKey16 = decryptKey16.Length >= AesKeySize ? decryptKey16[..AesKeySize] : throw new ArgumentException("Key must be >= 16 bytes.");
        SelfParticipantId = selfParticipantId;
        PeerParticipantId = peerParticipantId;
    }

    public static SframeSession? TryCreate(ReadOnlySpan<byte> callKey32, string selfJid, string peerJid)
    {
        if (callKey32.Length != 32)
        {
            return null;
        }
        string selfId = CallCrypto.FormatParticipantId(selfJid);
        string peerId = CallCrypto.FormatParticipantId(peerJid);
        byte[]? sendKey = DeriveParticipantKey32(callKey32, peerId);
        byte[]? recvKey = DeriveParticipantKey32(callKey32, selfId);
        if (sendKey is null || recvKey is null)
        {
            return null;
        }
        return new SframeSession(sendKey[..AesKeySize], recvKey[..AesKeySize], selfId, peerId);
    }

    public static string BuildInfoLabel(string participantId) => $"{KdfLabel}{participantId}";

    public static byte[]? DeriveParticipantKey32(ReadOnlySpan<byte> callKey32, string participantId)
    {
        if (callKey32.Length != 32)
        {
            return null;
        }
        ReadOnlySpan<byte> salt = callKey32[..16];
        ReadOnlySpan<byte> ikm = callKey32[16..32];
        byte[] key32 = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, key32, salt, Encoding.UTF8.GetBytes(BuildInfoLabel(participantId)));
        return key32;
    }

    public static byte[] CounterToIv16(ulong counter)
    {
        byte[] iv = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(iv.AsSpan(8, 8), counter);
        return iv;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        ulong counter = _txCounter++;
        byte[] iv = CounterToIv16(counter);
        byte[] header = BuildHeader(counter, 0);

        byte[] sealedPayload = EncryptGcm16Nonce(_encryptKey16, iv, plaintext);
        byte[] result = new byte[sealedPayload.Length + header.Length];
        sealedPayload.CopyTo(result, 0);
        header.CopyTo(result, sealedPayload.Length);
        return result;
    }

    public SframeDecryptResult Decrypt(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < GcmTagSize + 3)
        {
            return new SframeDecryptResult(SframeDecryptStatus.Plaintext, frame.ToArray());
        }

        int headerLen = frame[^1];
        if (headerLen < 3 || headerLen > frame.Length)
        {
            return new SframeDecryptResult(SframeDecryptStatus.Plaintext, frame.ToArray());
        }

        int headerStart = frame.Length - headerLen;
        ReadOnlySpan<byte> header = frame[headerStart..];
        ReadOnlySpan<byte> ciphertext = frame[..headerStart];
        if (ciphertext.Length < GcmTagSize + 1)
        {
            return new SframeDecryptResult(SframeDecryptStatus.Plaintext, frame.ToArray());
        }

        if (!TryParseHeader(header, out ulong counter, out _))
        {
            return new SframeDecryptResult(SframeDecryptStatus.Plaintext, frame.ToArray());
        }

        byte[] iv = CounterToIv16(counter);
        byte[]? plain = TryDecryptGcm16Nonce(_decryptKey16, iv, ciphertext);
        return plain is not null
            ? new SframeDecryptResult(SframeDecryptStatus.Decrypted, plain)
            : new SframeDecryptResult(SframeDecryptStatus.AuthFailed, frame.ToArray());
    }

    internal static byte[] BuildHeader(ulong counter, ulong keyId)
    {
        Span<byte> buf = stackalloc byte[HeaderMaxSize];
        int n = WriteVarint(buf, counter);
        n += WriteVarint(buf[n..], keyId);
        buf[n] = (byte)(n + 1);
        return buf[..(n + 1)].ToArray();
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> header, out ulong counter, out ulong keyId)
    {
        counter = 0;
        keyId = 0;
        if (header.Length < 2 || header[^1] != header.Length)
        {
            return false;
        }
        ReadOnlySpan<byte> body = header[..^1];
        if (!TryReadVarint(body, 0, out counter, out int next))
        {
            return false;
        }
        return TryReadVarint(body, next, out keyId, out _);
    }

    private static byte[] EncryptGcm16Nonce(byte[] key16, byte[] iv16, ReadOnlySpan<byte> plaintext)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(forEncryption: true, new AeadParameters(new KeyParameter(key16), GcmTagSize * 8, iv16, ReadOnlySpan<byte>.Empty.ToArray()));
        byte[] output = new byte[cipher.GetOutputSize(plaintext.Length)];
        int len = cipher.ProcessBytes(plaintext.ToArray(), 0, plaintext.Length, output, 0);
        cipher.DoFinal(output, len);
        return output;
    }

    private static byte[]? TryDecryptGcm16Nonce(byte[] key16, byte[] iv16, ReadOnlySpan<byte> ciphertextWithTag)
    {
        try
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(forEncryption: false, new AeadParameters(new KeyParameter(key16), GcmTagSize * 8, iv16, ReadOnlySpan<byte>.Empty.ToArray()));
            byte[] input = ciphertextWithTag.ToArray();
            byte[] output = new byte[cipher.GetOutputSize(input.Length)];
            int len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            int finalLen = cipher.DoFinal(output, len);
            int total = len + finalLen;
            return total == output.Length ? output : output[..total];
        }
        catch (InvalidCipherTextException)
        {
            return null;
        }
    }

    private static int WriteVarint(Span<byte> dest, ulong value)
    {
        int n = 0;
        while (value > 0x7F)
        {
            dest[n++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        dest[n++] = (byte)(value & 0xFF);
        return n;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, int offset, out ulong value, out int nextOffset)
    {
        value = 0;
        nextOffset = offset;
        int shift = 0;
        for (int i = offset; i < data.Length; i++)
        {
            byte b = data[i];
            value |= (ulong)(b & 0x7F) << shift;
            nextOffset = i + 1;
            if ((b & 0x80) == 0)
            {
                return true;
            }
            shift += 7;
            if (shift > 63)
            {
                return false;
            }
        }
        return false;
    }
}
