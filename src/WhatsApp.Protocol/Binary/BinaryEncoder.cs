using System.Text;

namespace WhatsApp.Protocol.Binary;

public class BinaryEncoder
{
    private readonly MemoryStream _stream;

    public BinaryEncoder(MemoryStream? stream = null)
    {
        _stream = stream ?? new MemoryStream();
    }

    public byte[] ToArray() => _stream.ToArray();

    public void WriteU8(byte value)
    {
        _stream.WriteByte(value);
    }

    public void WriteU16Be(ushort value)
    {
        _stream.WriteByte((byte)(value >> 8));
        _stream.WriteByte((byte)(value & 0xFF));
    }

    public void WriteU20Be(uint value)
    {
        _stream.WriteByte((byte)((value >> 16) & 0x0F));
        _stream.WriteByte((byte)((value >> 8) & 0xFF));
        _stream.WriteByte((byte)(value & 0xFF));
    }

    public void WriteU32Be(uint value)
    {
        _stream.WriteByte((byte)((value >> 24) & 0xFF));
        _stream.WriteByte((byte)((value >> 16) & 0xFF));
        _stream.WriteByte((byte)((value >> 8) & 0xFF));
        _stream.WriteByte((byte)(value & 0xFF));
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        _stream.Write(bytes);
    }

    public void WriteListStart(int listSize)
    {
        if (listSize == 0)
        {
            WriteU8(Constants.ListEmpty);
        }
        else if (listSize < 256)
        {
            WriteU8(Constants.List8);
            WriteU8((byte)listSize);
        }
        else if (listSize <= ushort.MaxValue)
        {
            WriteU8(Constants.List16);
            WriteU16Be((ushort)listSize);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(listSize), $"List size exceeds maximum supported 65535: {listSize}");
        }
    }

    public void WriteBytesWithLen(ReadOnlySpan<byte> bytes)
    {
        int len = bytes.Length;
        if (len < 256)
        {
            WriteU8(Constants.Binary8);
            WriteU8((byte)len);
        }
        else if (len < (1 << 20))
        {
            WriteU8(Constants.Binary20);
            WriteU20Be((uint)len);
        }
        else
        {
            WriteU8(Constants.Binary32);
            WriteU32Be((uint)len);
        }
        WriteBytes(bytes);
    }

    public static bool ValidateNibble(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > Constants.PackedMax)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is not ((>= '0' and <= '9') or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ValidateHex(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > Constants.PackedMax)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    private static byte PackNibbleChar(char c) => c switch
    {
        >= '0' and <= '9' => (byte)(c - '0'),
        '-' => 10,
        '.' => 11,
        '\0' => 15,
        _ => throw new ArgumentException($"Invalid character for nibble packing: {c}")
    };

    private static byte PackHexChar(char c) => c switch
    {
        >= '0' and <= '9' => (byte)(c - '0'),
        >= 'A' and <= 'F' => (byte)(10 + (c - 'A')),
        '\0' => 15,
        _ => throw new ArgumentException($"Invalid character for hex packing: {c}")
    };

    public void WritePackedBytes(string value, byte dataType)
    {
        if (value.Length > Constants.PackedMax)
        {
            throw new ArgumentException($"String too long to be packed: {value.Length}");
        }

        WriteU8(dataType);

        int roundedLen = (value.Length + 1) / 2;
        if (value.Length % 2 != 0)
        {
            roundedLen |= 0x80;
        }
        WriteU8((byte)roundedLen);

        for (int i = 0; i < value.Length; i += 2)
        {
            char c1 = value[i];
            char c2 = (i + 1 < value.Length) ? value[i + 1] : '\0';

            byte hi = dataType == Constants.Nibble8 ? PackNibbleChar(c1) : PackHexChar(c1);
            byte lo = dataType == Constants.Nibble8 ? PackNibbleChar(c2) : PackHexChar(c2);

            WriteU8((byte)((hi << 4) | lo));
        }
    }

    public void WriteJid(Jid jid)
    {
        if (jid.Server == Server.Interop && jid.Integrator != 0)
        {
            WriteU8(Constants.InteropJid);
            WriteString(jid.User);
            WriteU16Be(jid.Device);
            WriteU16Be(jid.Integrator);
            return;
        }

        if (jid.Device > 0 && jid.Server.SupportsAdJid())
        {
            if (jid.Device > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(jid), $"AD_JID device id out of range: {jid.Device}");
            }

            WriteU8(Constants.AdJid);
            WriteU8(jid.Server.ToDomainType());
            WriteU8((byte)jid.Device);
            WriteString(jid.User);
        }
        else
        {
            WriteU8(Constants.JidPair);
            if (string.IsNullOrEmpty(jid.User))
            {
                WriteU8(Constants.ListEmpty);
            }
            else
            {
                WriteString(jid.User);
            }
            WriteString(jid.Server.ToServerString());
        }
    }

    private readonly struct JidMeta
    {
        public readonly string User;
        public readonly string ServerString;
        public readonly Server? Server;
        public readonly byte? Device;

        public JidMeta(string user, string serverString, Server? server, byte? device)
        {
            User = user;
            ServerString = serverString;
            Server = server;
            Device = device;
        }
    }

    private static bool TryParseJidMeta(string input, out JidMeta meta)
    {
        meta = default;
        int sepIdx = input.IndexOf('@');
        if (sepIdx < 0) return false;

        string serverStr = input[(sepIdx + 1)..];
        string userCombined = input[..sepIdx];

        byte? device = null;
        string userAgent = userCombined;

        int colonIdx = userCombined.IndexOf(':');
        if (colonIdx >= 0 && byte.TryParse(userCombined[(colonIdx + 1)..], out byte parsedDevice))
        {
            userAgent = userCombined[..colonIdx];
            device = parsedDevice;
        }

        int userEnd = userAgent.Length;
        int underscoreIdx = userAgent.IndexOf('_');
        if (underscoreIdx >= 0 && byte.TryParse(userAgent[(underscoreIdx + 1)..], out _))
        {
            userEnd = underscoreIdx;
        }

        Server? serverKind = ServerExtensions.TryParseServer(serverStr, out var s) ? s : null;
        if (!serverKind.HasValue)
        {
            return false;
        }

        if (serverKind is Server.Pn or Server.Hosted or Server.HostedLid)
        {
            int dotIdx = userAgent[..userEnd].LastIndexOf('.');
            if (dotIdx >= 0 && byte.TryParse(userAgent[(dotIdx + 1)..userEnd], out _))
            {
                userEnd = dotIdx;
            }
        }

        if (serverKind.HasValue && !serverKind.Value.SupportsAdJid())
        {
            device = null;
        }

        string user = userAgent[..userEnd];
        string finalServer = serverKind?.ToServerString() ?? serverStr;

        meta = new JidMeta(user, finalServer, serverKind, device);
        return true;
    }

    private void WriteJidFromMeta(JidMeta meta)
    {
        if (meta.Device.HasValue && meta.Server.HasValue)
        {
            WriteU8(Constants.AdJid);
            WriteU8(meta.Server.Value.ToDomainType());
            WriteU8(meta.Device.Value);
            WriteString(meta.User);
        }
        else
        {
            WriteU8(Constants.JidPair);
            if (string.IsNullOrEmpty(meta.User))
            {
                WriteU8(Constants.ListEmpty);
            }
            else
            {
                WriteString(meta.User);
            }
            WriteString(meta.ServerString);
        }
    }

    public void WriteString(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            WriteU8(Constants.Binary8);
            WriteU8(0);
            return;
        }

        // Strings longer than PackedMax (127) can't be protocol tokens, packed, or JIDs
        if (s.Length > Constants.PackedMax)
        {
            byte[] rawBytes = Encoding.UTF8.GetBytes(s);
            WriteBytesWithLen(rawBytes);
            return;
        }

        if (TokenDictionary.TryGetToken(s, out byte dict, out byte index))
        {
            if (dict == 0xFF)
            {
                WriteU8(index);
            }
            else
            {
                WriteU8((byte)(Constants.Dictionary0 + dict));
                WriteU8(index);
            }
            return;
        }

        if (ValidateNibble(s))
        {
            WritePackedBytes(s, Constants.Nibble8);
            return;
        }

        if (ValidateHex(s))
        {
            WritePackedBytes(s, Constants.Hex8);
            return;
        }

        if (s.Length <= 48 && s.Contains('@'))
        {
            if (TryParseJidMeta(s, out var meta))
            {
                WriteJidFromMeta(meta);
                return;
            }
        }

        byte[] utf8Bytes = Encoding.UTF8.GetBytes(s);
        WriteBytesWithLen(utf8Bytes);
    }

    public void WriteNode(Node node)
    {
        int numElements = node.Attrs.Count * 2 + 1; // 1 for tag
        if (node.Content != null)
        {
            numElements += 1;
        }

        WriteListStart(numElements);
        WriteString(node.Tag);

        foreach (var (k, v) in node.Attrs)
        {
            WriteString(k);
            if (v is NodeValue.JidValue jidVal)
            {
                WriteJid(jidVal.Jid);
            }
            else
            {
                WriteString(v.ToString());
            }
        }

        if (node.Content != null)
        {
            switch (node.Content)
            {
                case NodeContent.StringContent s:
                    WriteString(s.Text);
                    break;
                case NodeContent.BytesContent b:
                    WriteBytesWithLen(b.Bytes);
                    break;
                case NodeContent.NodesContent n:
                    WriteListStart(n.Nodes.Count);
                    foreach (var child in n.Nodes)
                    {
                        WriteNode(child);
                    }
                    break;
            }
        }
    }
}
