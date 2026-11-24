using WhatsApp.Protocol.Binary;

namespace WhatsApp.Protocol.Noise;

public class XxFallbackHandshake
{
    private readonly KeyPair _clientStatic;
    private readonly KeyPair _clientEphemeral;
    private readonly byte[] _clientPayload;
    private readonly NoiseState _noise;

    public KeyPair ClientStatic => _clientStatic;
    public KeyPair ClientEphemeral => _clientEphemeral;
    public byte[]? LastServerCertChainBytes { get; private set; }

    public XxFallbackHandshake(KeyPair clientStatic, KeyPair clientEphemeral, byte[] clientPayload)
    {
        _clientStatic = clientStatic;
        _clientEphemeral = clientEphemeral;
        _clientPayload = clientPayload;

        _noise = new NoiseState("Noise_XXfallback_25519_AESGCM_SHA256", Constants.WaConnHeader);
        // Authenticate the ephemeral key that was already sent in the IK ClientHello
        _noise.Authenticate(_clientEphemeral.PublicKey);
    }

    public (byte[] ClientFinishBytes, NoiseKeys Keys, byte[] ServerStaticPublic) ProcessServerHello(ReadOnlySpan<byte> serverHelloBytes)
    {
        var serverHello = HandshakeMessage.ParseServerHello(serverHelloBytes);

        if (serverHello.Ephemeral == null || serverHello.Static == null || serverHello.Payload == null)
        {
            throw new InvalidDataException("Incomplete ServerHello message in XXfallback");
        }

        byte[] serverEphemeral = serverHello.Ephemeral;
        byte[] serverStaticCiphertext = serverHello.Static;
        byte[] certCiphertext = serverHello.Payload;

        // 1. Authenticate server ephemeral key
        _noise.Authenticate(serverEphemeral);

        // 2. ee: DH(clientEphemeral, serverEphemeral)
        byte[] eeSecret = Curve25519.CalculateSharedSecret(_clientEphemeral.PrivateKey, serverEphemeral);
        _noise.MixKey(eeSecret);

        // 3. Decrypt server static key
        byte[] serverStaticPublic = _noise.Decrypt(serverStaticCiphertext);

        // 4. es: DH(clientEphemeral, serverStatic)
        byte[] esSecret = Curve25519.CalculateSharedSecret(_clientEphemeral.PrivateKey, serverStaticPublic);
        _noise.MixKey(esSecret);

        // 5. Decrypt server cert payload
        byte[] certPlaintext = _noise.Decrypt(certCiphertext);
        LastServerCertChainBytes = certPlaintext;

        // 6. Encrypt client static key
        byte[] clientStaticCiphertext = _noise.Encrypt(_clientStatic.PublicKey);

        // 7. se: DH(clientStatic, serverEphemeral)
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
