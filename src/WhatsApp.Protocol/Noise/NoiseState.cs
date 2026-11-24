using System.Security.Cryptography;
using System.Text;

namespace WhatsApp.Protocol.Noise;

public sealed class NoiseKeys : IDisposable
{
    public NoiseCipher WriteCipher { get; }
    public NoiseCipher ReadCipher { get; }

    public NoiseKeys(NoiseCipher writeCipher, NoiseCipher readCipher)
    {
        WriteCipher = writeCipher;
        ReadCipher = readCipher;
    }

    public void Dispose()
    {
        WriteCipher.Dispose();
        ReadCipher.Dispose();
    }
}

public class NoiseState
{
    private byte[] _hash;
    private byte[] _salt;
    private byte[] _key;
    private uint _counter;

    public byte[] Hash => (byte[])_hash.Clone();
    public byte[] Salt => (byte[])_salt.Clone();
    public uint Counter => _counter;

    public NoiseState(string pattern, ReadOnlySpan<byte> prologue)
    {
        byte[] patternBytes = Encoding.ASCII.GetBytes(pattern);
        if (patternBytes.Length <= 32)
        {
            _hash = new byte[32];
            patternBytes.CopyTo(_hash, 0);
        }
        else
        {
            _hash = SHA256.HashData(patternBytes);
        }

        _salt = (byte[])_hash.Clone();
        _key = (byte[])_hash.Clone();
        _counter = 0;

        Authenticate(prologue);
    }

    public void Authenticate(ReadOnlySpan<byte> data)
    {
        byte[] combined = new byte[_hash.Length + data.Length];
        _hash.CopyTo(combined, 0);
        data.CopyTo(combined.AsSpan(_hash.Length));
        _hash = SHA256.HashData(combined);
    }

    public void MixKey(byte[] inputKeyMaterial)
    {
        _counter = 0;
        var (newSalt, newKey) = ExtractAndExpand(inputKeyMaterial);
        _salt = newSalt;
        _key = newKey;
    }

    private (byte[] salt, byte[] key) ExtractAndExpand(byte[]? ikm)
    {
        byte[] input = ikm ?? Array.Empty<byte>();
        byte[] okm = new byte[64];

        HKDF.DeriveKey(HashAlgorithmName.SHA256, input, okm, _salt, Array.Empty<byte>());

        byte[] writeKey = new byte[32];
        byte[] readKey = new byte[32];

        Array.Copy(okm, 0, writeKey, 0, 32);
        Array.Copy(okm, 32, readKey, 0, 32);

        return (writeKey, readKey);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        if (_counter == uint.MaxValue)
        {
            throw new OverflowException("Noise counter exhausted");
        }
        using var cipher = new NoiseCipher(_key);
        byte[] ciphertext = cipher.EncryptWithCounter(_counter++, plaintext, _hash);
        Authenticate(ciphertext);
        return ciphertext;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext)
    {
        if (_counter == uint.MaxValue)
        {
            throw new OverflowException("Noise counter exhausted");
        }
        using var cipher = new NoiseCipher(_key);
        byte[] plaintext = cipher.DecryptWithCounter(_counter++, ciphertext, _hash);
        Authenticate(ciphertext);
        return plaintext;
    }

    public NoiseKeys Split()
    {
        var (writeKey, readKey) = ExtractAndExpand(null);
        return new NoiseKeys(new NoiseCipher(writeKey), new NoiseCipher(readKey));
    }
}
