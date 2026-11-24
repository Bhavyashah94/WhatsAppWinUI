using Google.Protobuf;
using PbHandshakeMessage = global::Whatsapp.HandshakeMessage;

namespace WhatsApp.Protocol.Noise;

public sealed class ClientHello
{
    public byte[]? Ephemeral { get; set; }
    public byte[]? Static { get; set; }
    public byte[]? Payload { get; set; }
}

public sealed class ServerHello
{
    public byte[]? Ephemeral { get; set; }
    public byte[]? Static { get; set; }
    public byte[]? Payload { get; set; }
}

public sealed class ClientFinish
{
    public byte[]? Static { get; set; }
    public byte[]? Payload { get; set; }
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
            Payload = sh.HasPayload ? sh.Payload.ToByteArray() : null
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
            Payload = ch.HasPayload ? ch.Payload.ToByteArray() : null
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
            Payload = cf.HasPayload ? cf.Payload.ToByteArray() : null
        };
    }
}
