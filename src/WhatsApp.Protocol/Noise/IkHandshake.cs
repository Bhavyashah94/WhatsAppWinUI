using WhatsApp.Protocol.Binary;

namespace WhatsApp.Protocol.Noise;

public class IkHandshake
{
    private readonly KeyPair _clientStatic;
    private readonly KeyPair _clientEphemeral;
    private readonly byte[] _serverStaticPub;
    private readonly byte[] _clientPayload;
    private readonly NoiseState _noise;

    public KeyPair ClientStatic => _clientStatic;
    public KeyPair ClientEphemeral => _clientEphemeral;

    public IkHandshake(KeyPair clientStatic, byte[] serverStaticPub, byte[] clientPayload, KeyPair? clientEphemeral = null)
    {
        _clientStatic = clientStatic;
        _serverStaticPub = serverStaticPub;
        _clientPayload = clientPayload;
        _clientEphemeral = clientEphemeral ?? Curve25519.GenerateKeyPair();

        _noise = new NoiseState("Noise_IK_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);
    }

    public byte[] BuildClientHello()
    {
        // 1. Pre-message: <- s (responder static public)
        _noise.Authenticate(_serverStaticPub);

        // 2. -> e (client ephemeral public)
        _noise.Authenticate(_clientEphemeral.PublicKey);

        // 3. es: DH(clientEphemeral.PrivateKey, serverStaticPub)
        byte[] esSecret = Curve25519.CalculateSharedSecret(_clientEphemeral.PrivateKey, _serverStaticPub);
        _noise.MixKey(esSecret);

        // 4. -> s (client static public, encrypted)
        byte[] encryptedStatic = _noise.Encrypt(_clientStatic.PublicKey);

        // 5. ss: DH(clientStatic.PrivateKey, serverStaticPub)
        byte[] ssSecret = Curve25519.CalculateSharedSecret(_clientStatic.PrivateKey, _serverStaticPub);
        _noise.MixKey(ssSecret);

        // 6. 0-RTT payload (encrypted)
        byte[] encryptedPayload = _noise.Encrypt(_clientPayload);

        return HandshakeMessage.EncodeIkClientHello(_clientEphemeral.PublicKey, encryptedStatic, encryptedPayload);
    }

    /// <summary>
    /// Processes the ServerHello response.
    /// If the server rejected IK (serverHello.Static is not null), returns null so the caller can switch to XXfallback.
    /// If the server accepted IK, returns the established NoiseKeys and decrypted certificate plaintext.
    /// </summary>
    public (NoiseKeys Keys, byte[] CertPlaintext)? ProcessServerHello(ReadOnlySpan<byte> serverHelloBytes)
    {
        var serverHello = HandshakeMessage.ParseServerHello(serverHelloBytes);

        if (serverHello.Static != null)
        {
            // Server returned static ciphertext, meaning server rejected our cached static key.
            return null;
        }

        if (serverHello.Ephemeral == null || serverHello.Payload == null)
        {
            throw new InvalidDataException("Incomplete ServerHello in IK handshake");
        }

        byte[] serverEphemeral = serverHello.Ephemeral;
        byte[] certPayload = serverHello.Payload;

        // <- e: authenticate server ephemeral
        _noise.Authenticate(serverEphemeral);

        // ee: DH(clientEphemeral.PrivateKey, serverEphemeral)
        byte[] eeSecret = Curve25519.CalculateSharedSecret(_clientEphemeral.PrivateKey, serverEphemeral);
        _noise.MixKey(eeSecret);

        // se: DH(clientStatic.PrivateKey, serverEphemeral)
        byte[] seSecret = Curve25519.CalculateSharedSecret(_clientStatic.PrivateKey, serverEphemeral);
        _noise.MixKey(seSecret);

        // Decrypt cert payload
        byte[] certPlaintext = _noise.Decrypt(certPayload);

        // Split into final connection ciphers
        NoiseKeys keys = _noise.Split();

        return (keys, certPlaintext);
    }
}
