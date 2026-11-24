using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WhatsApp.Protocol.Noise;

public sealed class NoiseCipher : IDisposable
{
    public const int TagLength = 16;
    public const int NonceLength = 12;

    private readonly AesGcm _aesGcm;

    public NoiseCipher(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("Key must be 32 bytes for AES-256", nameof(key));
        }
        _aesGcm = new AesGcm(key, TagLength);
    }

    public static byte[] GenerateIv(uint counter)
    {
        byte[] iv = new byte[NonceLength];
        BinaryPrimitives.WriteUInt32BigEndian(iv.AsSpan(8), counter);
        return iv;
    }

    public byte[] EncryptWithCounter(uint counter, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        byte[] nonce = GenerateIv(counter);
        byte[] output = new byte[plaintext.Length + TagLength];

        Span<byte> ciphertext = output.AsSpan(0, plaintext.Length);
        Span<byte> tag = output.AsSpan(plaintext.Length, TagLength);

        _aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return output;
    }

    public byte[] DecryptWithCounter(uint counter, ReadOnlySpan<byte> encryptedPayload, ReadOnlySpan<byte> associatedData = default)
    {
        if (encryptedPayload.Length < TagLength)
        {
            throw new CryptographicException("Ciphertext too short to contain tag");
        }

        byte[] nonce = GenerateIv(counter);
        int cipherLen = encryptedPayload.Length - TagLength;

        ReadOnlySpan<byte> ciphertext = encryptedPayload.Slice(0, cipherLen);
        ReadOnlySpan<byte> tag = encryptedPayload.Slice(cipherLen, TagLength);

        byte[] plaintext = new byte[cipherLen];
        _aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }

    public void Dispose()
    {
        _aesGcm.Dispose();
    }
}
