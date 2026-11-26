using Google.Protobuf;
using PbHandshakeMessage = global::Whatsapp.HandshakeMessage;

namespace WhatsApp.Protocol.Noise;

public sealed class ClientHello
{
    public byte[]? Ephemeral { get; set; }
    public byte[]? Static { get; set; }
    public byte[]? Payload { get; set; }
    public bool? UseExtended { get; set; }
    public byte[]? ExtendedCiphertext { get; set; }
    public byte[]? PaddedBytes { get; set; }
    public bool? SendServerHelloPaddedBytes { get; set; }
    public bool? SimulateXxkemFs { get; set; }
    public PbHandshakeMessage.Types.HandshakePqMode? PqMode { get; set; }
    public byte[]? ExtendedEphemeral { get; set; }
}

public sealed class ServerHello
{
    public byte[]? Ephemeral { get; set; }
    public byte[]? Static { get; set; }
    public byte[]? Payload { get; set; }
    public byte[]? ExtendedStatic { get; set; }
    public byte[]? PaddingBytes { get; set; }
    public byte[]? ExtendedCiphertext { get; set; }
}

public sealed class ClientFinish
{
    public byte[]? Static { get; set; }
    public byte[]? Payload { get; set; }
    public byte[]? ExtendedCiphertext { get; set; }
    public byte[]? PaddedBytes { get; set; }
    public bool? SimulateXxkemFs { get; set; }
}

/// <summary>
/// Noise HandshakeMessage wire codec backed by protoc-generated Whatsapp.HandshakeMessage.
/// </summary>
public static class HandshakeMessage
{
    public static byte[] EncodeClientHello(byte[] ephemeralKey)
    {
        var msg = new PbHandshakeMessage
        {
            ClientHello = new PbHandshakeMessage.Types.ClientHello
            {
                Ephemeral = ByteString.CopyFrom(ephemeralKey)
            }
        };
        return msg.ToByteArray();
    }

    public static byte[] EncodeIkClientHello(byte[] ephemeralKey, byte[] staticCiphertext, byte[] payloadCiphertext)
    {
        var msg = new PbHandshakeMessage
        {
            ClientHello = new PbHandshakeMessage.Types.ClientHello
            {
                Ephemeral = ByteString.CopyFrom(ephemeralKey),
                Static = ByteString.CopyFrom(staticCiphertext),
                Payload = ByteString.CopyFrom(payloadCiphertext)
            }
        };
        return msg.ToByteArray();
    }

    public static byte[] EncodeClientFinish(byte[] staticCiphertext, byte[] payloadCiphertext)
    {
        var msg = new PbHandshakeMessage
        {
            ClientFinish = new PbHandshakeMessage.Types.ClientFinish
            {
                Static = ByteString.CopyFrom(staticCiphertext),
                Payload = ByteString.CopyFrom(payloadCiphertext)
            }
        };
        return msg.ToByteArray();
    }

    public static byte[] EncodeServerHello(byte[] ephemeralKey, byte[]? staticCiphertext, byte[] payloadCiphertext)
    {
        var sh = new PbHandshakeMessage.Types.ServerHello
        {
            Ephemeral = ByteString.CopyFrom(ephemeralKey),
            Payload = ByteString.CopyFrom(payloadCiphertext)
        };
        if (staticCiphertext != null)
        {
            sh.Static = ByteString.CopyFrom(staticCiphertext);
        }

        return new PbHandshakeMessage { ServerHello = sh }.ToByteArray();
    }

    public static ServerHello ParseServerHello(ReadOnlySpan<byte> data)
    {
        var parsed = PbHandshakeMessage.Parser.ParseFrom(data);
        var sh = parsed.ServerHello;
        if (sh == null)
        {
            return new ServerHello();
        }

        return new ServerHello
        {
            Ephemeral = sh.HasEphemeral ? sh.Ephemeral.ToByteArray() : null,
            Static = sh.HasStatic ? sh.Static.ToByteArray() : null,
            Payload = sh.HasPayload ? sh.Payload.ToByteArray() : null,
            ExtendedStatic = sh.HasExtendedStatic ? sh.ExtendedStatic.ToByteArray() : null,
            PaddingBytes = sh.HasPaddingBytes ? sh.PaddingBytes.ToByteArray() : null,
            ExtendedCiphertext = sh.HasExtendedCiphertext ? sh.ExtendedCiphertext.ToByteArray() : null
        };
    }

    public static ClientHello ParseClientHello(ReadOnlySpan<byte> data)
    {
        var parsed = PbHandshakeMessage.Parser.ParseFrom(data);
        var ch = parsed.ClientHello;
        if (ch == null)
        {
            return new ClientHello();
        }

        return new ClientHello
        {
            Ephemeral = ch.HasEphemeral ? ch.Ephemeral.ToByteArray() : null,
            Static = ch.HasStatic ? ch.Static.ToByteArray() : null,
            Payload = ch.HasPayload ? ch.Payload.ToByteArray() : null,
            UseExtended = ch.HasUseExtended ? ch.UseExtended : null,
            ExtendedCiphertext = ch.HasExtendedCiphertext ? ch.ExtendedCiphertext.ToByteArray() : null,
            PaddedBytes = ch.HasPaddedBytes ? ch.PaddedBytes.ToByteArray() : null,
            SendServerHelloPaddedBytes = ch.HasSendServerHelloPaddedBytes ? ch.SendServerHelloPaddedBytes : null,
            SimulateXxkemFs = ch.HasSimulateXxkemFs ? ch.SimulateXxkemFs : null,
            PqMode = ch.HasPqMode ? ch.PqMode : null,
            ExtendedEphemeral = ch.HasExtendedEphemeral ? ch.ExtendedEphemeral.ToByteArray() : null
        };
    }

    public static ClientFinish ParseClientFinish(ReadOnlySpan<byte> data)
    {
        var parsed = PbHandshakeMessage.Parser.ParseFrom(data);
        var cf = parsed.ClientFinish;
        if (cf == null)
        {
            return new ClientFinish();
        }

        return new ClientFinish
        {
            Static = cf.HasStatic ? cf.Static.ToByteArray() : null,
            Payload = cf.HasPayload ? cf.Payload.ToByteArray() : null,
            ExtendedCiphertext = cf.HasExtendedCiphertext ? cf.ExtendedCiphertext.ToByteArray() : null,
            PaddedBytes = cf.HasPaddedBytes ? cf.PaddedBytes.ToByteArray() : null,
            SimulateXxkemFs = cf.HasSimulateXxkemFs ? cf.SimulateXxkemFs : null
        };
    }

    public static byte[] EncodePqClientHello(
        byte[] ephemeralKey,
        PbHandshakeMessage.Types.HandshakePqMode pqMode,
        byte[]? extendedEphemeral = null,
        byte[]? extendedCiphertext = null,
        byte[]? staticCiphertext = null,
        byte[]? payloadCiphertext = null,
        bool useExtended = true,
        bool simulateXxkemFs = false,
        byte[]? paddedBytes = null)
    {
        var ch = new PbHandshakeMessage.Types.ClientHello
        {
            Ephemeral = ByteString.CopyFrom(ephemeralKey),
            UseExtended = useExtended,
            PqMode = pqMode,
            SimulateXxkemFs = simulateXxkemFs
        };
        if (staticCiphertext is { Length: > 0 }) ch.Static = ByteString.CopyFrom(staticCiphertext);
        if (payloadCiphertext is { Length: > 0 }) ch.Payload = ByteString.CopyFrom(payloadCiphertext);
        if (extendedEphemeral is { Length: > 0 }) ch.ExtendedEphemeral = ByteString.CopyFrom(extendedEphemeral);
        if (extendedCiphertext is { Length: > 0 }) ch.ExtendedCiphertext = ByteString.CopyFrom(extendedCiphertext);
        if (paddedBytes is { Length: > 0 }) ch.PaddedBytes = ByteString.CopyFrom(paddedBytes);
        return new PbHandshakeMessage { ClientHello = ch }.ToByteArray();
    }

    public static byte[] EncodePqClientFinish(
        byte[] staticCiphertext,
        byte[] payloadCiphertext,
        byte[]? extendedCiphertext = null,
        byte[]? paddedBytes = null,
        bool simulateXxkemFs = false)
    {
        var cf = new PbHandshakeMessage.Types.ClientFinish
        {
            Static = ByteString.CopyFrom(staticCiphertext),
            Payload = ByteString.CopyFrom(payloadCiphertext),
            SimulateXxkemFs = simulateXxkemFs
        };
        if (extendedCiphertext is { Length: > 0 }) cf.ExtendedCiphertext = ByteString.CopyFrom(extendedCiphertext);
        if (paddedBytes is { Length: > 0 }) cf.PaddedBytes = ByteString.CopyFrom(paddedBytes);
        return new PbHandshakeMessage { ClientFinish = cf }.ToByteArray();
    }
}
