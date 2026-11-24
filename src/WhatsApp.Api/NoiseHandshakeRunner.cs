using System.Buffers;
using System.Globalization;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Noise;
using WhatsApp.WaProto;

namespace WhatsApp.Api;

public enum NoiseHandshakePattern
{
    Xx = 0,
    Ik = 1
}

public sealed class NoiseHandshakeOptions
{
    internal delegate VerifiedServerCertChain CertChainVerifier(
        ReadOnlySpan<byte> certChainBytes,
        ReadOnlySpan<byte> serverStaticPublicKey);

    public static NoiseHandshakeOptions Default { get; } = new();

    /// <summary>
    /// Maximum time to wait for the server's <c>ServerHello</c> response (matches WA Web <c>30s</c>).
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Forces a fresh XX handshake instead of attempting 0-RTT IK resume.
    /// </summary>
    public bool ForceFullHandshake { get; init; }

    /// <summary>
    /// Optional builder override for <c>ClientPayload</c> bytes.
    /// </summary>
    public Func<DeviceCredentials, byte[]>? ClientPayloadBuilder { get; init; }

    internal CertChainVerifier? CertVerifierOverride { get; init; }
}

public sealed record NoiseHandshakeResult(
    NoiseKeys Keys,
    NoiseHandshakePattern PatternSelected,
    bool FallbackTaken,
    byte[]? ServerStaticPublic,
    VerifiedServerCertChain? ServerCertChain,
    byte[] RemainingWireBytes);

/// <summary>
/// Executes WhatsApp Noise handshakes (<c>XX</c>, <c>IK</c>, and automatic <c>IK -> XXfallback</c>)
/// over <see cref="ITransport"/>, matching both <c>web.whatsapp.com</c> and <c>whatsapp-rust</c>.
/// </summary>
public static class NoiseHandshakeRunner
{
    private const int ReceiveBufferSize = 4096;

    public static NoiseHandshakePattern SelectPattern(DeviceCredentials credentials, bool forceFullHandshake = false)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (forceFullHandshake || !credentials.IsRegistered)
        {
            return NoiseHandshakePattern.Xx;
        }

        return credentials.ServerStaticPublic is { Length: 32 }
            ? NoiseHandshakePattern.Ik
            : NoiseHandshakePattern.Xx;
    }

    public static async Task<NoiseHandshakeResult> RunAsync(
        ITransport transport,
        DeviceCredentials credentials,
        NoiseHandshakeOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(credentials);

        options ??= NoiseHandshakeOptions.Default;
        byte[] clientPayload = BuildClientPayload(credentials, options.ClientPayloadBuilder);
        var (preambleHeader, _) = NoiseHandshakeHeader.BuildHandshakeHeader(credentials.EdgeRoutingInfo);

        var pattern = SelectPattern(credentials, options.ForceFullHandshake);
        return pattern == NoiseHandshakePattern.Ik
            ? await RunIkWithFallbackAsync(transport, credentials, clientPayload, preambleHeader, options, ct).ConfigureAwait(false)
            : await RunXxAsync(transport, credentials, clientPayload, preambleHeader, options, ct).ConfigureAwait(false);
    }

    private static async Task<NoiseHandshakeResult> RunXxAsync(
        ITransport transport,
        DeviceCredentials credentials,
        byte[] clientPayload,
        byte[] preambleHeader,
        NoiseHandshakeOptions options,
        CancellationToken ct)
    {
        var xx = new XxHandshake(credentials.NoiseKey, clientPayload);
        byte[] clientHello = xx.BuildClientHello();
        byte[] initialWireFrame = Framing.EncodeFrame(clientHello, preambleHeader);

        await transport.SendAsync(initialWireFrame, ct).ConfigureAwait(false);

        var decoder = new NoiseFrameDecoder();
        byte[] serverHelloFrame = await ReceiveHandshakeFrameAsync(transport, decoder, options.HandshakeTimeout, ct)
            .ConfigureAwait(false);

        var (clientFinishBytes, keys, serverStatic) = xx.ProcessServerHello(serverHelloFrame);
        try
        {
            byte[] certBytes = xx.LastServerCertChainBytes
                ?? throw new InvalidDataException("Missing decrypted server certificate payload in XX handshake.");
            VerifiedServerCertChain verifiedChain = VerifyCert(certBytes, serverStatic, options);

            byte[] finishWireFrame = Framing.EncodeFrame(clientFinishBytes);
            await transport.SendAsync(finishWireFrame, ct).ConfigureAwait(false);

            return new NoiseHandshakeResult(
                keys,
                NoiseHandshakePattern.Xx,
                FallbackTaken: false,
                serverStatic,
                verifiedChain,
                decoder.DrainBufferedBytes());
        }
        catch
        {
            keys.Dispose();
            throw;
        }
    }

    private static async Task<NoiseHandshakeResult> RunIkWithFallbackAsync(
        ITransport transport,
        DeviceCredentials credentials,
        byte[] clientPayload,
        byte[] preambleHeader,
        NoiseHandshakeOptions options,
        CancellationToken ct)
    {
        byte[] cachedServerStatic = credentials.ServerStaticPublic
            ?? throw new InvalidOperationException("Cached ServerStaticPublic is required for IK handshake.");

        var ik = new IkHandshake(credentials.NoiseKey, cachedServerStatic, clientPayload);
        byte[] clientHello = ik.BuildClientHello();
        byte[] initialWireFrame = Framing.EncodeFrame(clientHello, preambleHeader);

        await transport.SendAsync(initialWireFrame, ct).ConfigureAwait(false);

        var decoder = new NoiseFrameDecoder();
        byte[] serverHelloFrame = await ReceiveHandshakeFrameAsync(transport, decoder, options.HandshakeTimeout, ct)
            .ConfigureAwait(false);

        var ikOutcome = ik.ProcessServerHello(serverHelloFrame);
        if (ikOutcome is { } resumed)
        {
            return new NoiseHandshakeResult(
                resumed.Keys,
                NoiseHandshakePattern.Ik,
                FallbackTaken: false,
                cachedServerStatic,
                ServerCertChain: null,
                decoder.DrainBufferedBytes());
        }

        // Server rejected our cached static key (returned non-null ServerHello.Static): switch to XXfallback.
        var fallback = new XxFallbackHandshake(credentials.NoiseKey, ik.ClientEphemeral, clientPayload);
        var (clientFinishBytes, fbKeys, serverStatic) = fallback.ProcessServerHello(serverHelloFrame);
        try
        {
            byte[] certBytes = fallback.LastServerCertChainBytes
                ?? throw new InvalidDataException("Missing decrypted server certificate payload in XXfallback handshake.");
            VerifiedServerCertChain verifiedChain = VerifyCert(certBytes, serverStatic, options);

            // Connection preamble was already sent in the initial IK frame; ClientFinish sends no header.
            byte[] finishWireFrame = Framing.EncodeFrame(clientFinishBytes);
            await transport.SendAsync(finishWireFrame, ct).ConfigureAwait(false);

            return new NoiseHandshakeResult(
                fbKeys,
                NoiseHandshakePattern.Ik,
                FallbackTaken: true,
                serverStatic,
                verifiedChain,
                decoder.DrainBufferedBytes());
        }
        catch
        {
            fbKeys.Dispose();
            throw;
        }
    }

    private static VerifiedServerCertChain VerifyCert(
        ReadOnlySpan<byte> certChainBytes,
        ReadOnlySpan<byte> serverStaticPublicKey,
        NoiseHandshakeOptions options) =>
        options.CertVerifierOverride is { } verifier
            ? verifier(certChainBytes, serverStaticPublicKey)
            : NoiseCertVerifier.VerifyServerCert(certChainBytes, serverStaticPublicKey);

    private static byte[] BuildClientPayload(
        DeviceCredentials credentials,
        Func<DeviceCredentials, byte[]>? builderOverride)
    {
        if (builderOverride is not null)
        {
            return builderOverride(credentials);
        }

        if (credentials.IsRegistered && credentials.Pn is Jid pn)
        {
            if (!ulong.TryParse(pn.User, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong username))
            {
                throw new InvalidDataException($"Registered PN user '{pn.User}' is not a valid numeric WhatsApp ID.");
            }
            return ClientPayloadFactory.BuildLoginPayload(username, pn.Device);
        }

        return ClientPayloadFactory.BuildRegistrationPayload(
            credentials.RegistrationId,
            credentials.IdentityKey.PublicKey,
            credentials.SignedPreKeyId,
            credentials.SignedPreKey.PublicKey,
            credentials.SignedPreKeySignature,
            pushName: credentials.PushName);
    }

    private static async Task<byte[]> ReceiveHandshakeFrameAsync(
        ITransport transport,
        NoiseFrameDecoder decoder,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            while (true)
            {
                if (decoder.TryReadRawFrame(out byte[] frame))
                {
                    return frame;
                }

                int read;
                try
                {
                    read = await transport.ReceiveAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out waiting for Noise ServerHello after {timeout.TotalSeconds:0.#}s.");
                }

                if (read <= 0)
                {
                    throw new EndOfStreamException("Transport closed cleanly or dropped before completing Noise handshake.");
                }

                decoder.Feed(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
