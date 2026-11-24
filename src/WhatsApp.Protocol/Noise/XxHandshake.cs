using WhatsApp.Protocol.Binary;

namespace WhatsApp.Protocol.Noise;

public class XxHandshake
{
    private readonly KeyPair _clientStatic;
    private readonly KeyPair _clientEphemeral;
    private readonly byte[] _clientPayload;
    private readonly NoiseState _noise;

    public KeyPair ClientStatic => _clientStatic;
    public KeyPair ClientEphemeral => _clientEphemeral;
    public byte[]? LastServerCertChainBytes { get; private set; }

    public XxHandshake(KeyPair clientStatic, byte[] clientPayload, KeyPair? clientEphemeral = null)
    {
        _clientStatic = clientStatic;
        _clientPayload = clientPayload;
        _clientEphemeral = clientEphemeral ?? Curve25519.GenerateKeyPair();

        _noise = new NoiseState("Noise_XX_25519_AESGCM_SHA256\0\0\0\0", Constants.WaConnHeader);
        _noise.Authenticate(_clientEphemeral.PublicKey);
    }

    public byte[] BuildClientHello()
    {
        return HandshakeMessage.EncodeClientHello(_clientEphemeral.PublicKey);
    }

    public (byte[] ClientFinishBytes, NoiseKeys Keys, byte[] ServerStaticPublic) ProcessServerHello(ReadOnlySpan<byte> serverHelloBytes)
    {
        var serverHello = HandshakeMessage.ParseServerHello(serverHelloBytes);

        if (serverHello.Ephemeral == null || serverHello.Static == null || serverHello.Payload == null)
        {
            throw new InvalidDataException("Incomplete ServerHello message received from WhatsApp server");
        }

        byte[] serverEphemeral = serverHello.Ephemeral;
        byte[] serverStaticCiphertext = serverHello.Static;
        byte[] certCiphertext = serverHello.Payload;

        // 1. Authenticate server ephemeral key
        _noise.Authenticate(serverEphemeral);

        // 2. Mix shared secret: ee (client ephemeral * server ephemeral)
        byte[] eeSecret = Curve25519.CalculateSharedSecret(_clientEphemeral.PrivateKey, serverEphemeral);
        _noise.MixKey(eeSecret);

        // 3. Decrypt server static key
        byte[] serverStaticPublic = _noise.Decrypt(serverStaticCiphertext);

        // 4. Mix shared secret: es (client ephemeral * server static)
        byte[] esSecret = Curve25519.CalculateSharedSecret(_clientEphemeral.PrivateKey, serverStaticPublic);
        _noise.MixKey(esSecret);

        // 5. Decrypt server cert payload
        byte[] certPlaintext = _noise.Decrypt(certCiphertext);
        LastServerCertChainBytes = certPlaintext;

        // 6. Encrypt client static key
        byte[] clientStaticCiphertext = _noise.Encrypt(_clientStatic.PublicKey);

        // 7. Mix shared secret: se (client static * server ephemeral)
        byte[] seSecret = Curve25519.CalculateSharedSecret(_clientStatic.PrivateKey, serverEphemeral);
        _noise.MixKey(seSecret);

        // 8. Encrypt client payload
        byte[] clientPayloadCiphertext = _noise.Encrypt(_clientPayload);

        // 9. Build ClientFinish message
        byte[] clientFinishBytes = HandshakeMessage.EncodeClientFinish(clientStaticCiphertext, clientPayloadCiphertext);

        // 10. Split into final connection ciphers
        NoiseKeys keys = _noise.Split();

        return (clientFinishBytes, keys, serverStaticPublic);
    }
}
