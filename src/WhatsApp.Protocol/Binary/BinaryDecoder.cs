using System.Text;

namespace WhatsApp.Protocol.Binary;

public class BinaryDecoder
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _position;

    public BinaryDecoder(ReadOnlyMemory<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public bool IsFinished => _position >= _data.Length;
    public int BytesLeft => _data.Length - _position;
    public int Position => _position;

    private void CheckEos(int length)
    {
        if (BytesLeft < length)
        {
            throw new EndOfStreamException($"Unexpected end of stream. Expected {length} bytes, but only {BytesLeft} remain.");
        }
    }

    public byte ReadU8()
    {
        CheckEos(1);
        return _data.Span[_position++];
    }

    public ushort ReadU16Be()
    {
        CheckEos(2);
        var span = _data.Span.Slice(_position, 2);
        _position += 2;
        return (ushort)((span[0] << 8) | span[1]);
    }

    public uint ReadU20Be()
    {
        CheckEos(3);
        var span = _data.Span.Slice(_position, 3);
        _position += 3;
        return ((uint)(span[0] & 0x0F) << 16) | ((uint)span[1] << 8) | span[2];
    }

    public uint ReadU32Be()
    {
        CheckEos(4);
        var span = _data.Span.Slice(_position, 4);
        _position += 4;
        return ((uint)span[0] << 24) | ((uint)span[1] << 16) | ((uint)span[2] << 8) | span[3];
    }

    public ReadOnlyMemory<byte> ReadBytes(int length)
    {
        CheckEos(length);
        var mem = _data.Slice(_position, length);
        _position += length;
        return mem;
    }

    public string ReadString(int length)
    {
        var mem = ReadBytes(length);
        return Encoding.UTF8.GetString(mem.Span);
    }

    public int ReadListSize(byte tag)
    {
        return tag switch
        {
            Constants.ListEmpty => 0,
            Constants.List8 => ReadU8(),
            Constants.List16 => ReadU16Be(),
            _ => throw new FormatException($"Invalid list tag token: {tag}")
        };
    }

    public Jid ReadJidPair()
    {
        string user = ReadValueAsString() ?? string.Empty;
        string serverStr = ReadValueAsString() ?? string.Empty;

        if (!ServerExtensions.TryParseServer(serverStr, out var server))
        {
            throw new FormatException($"JID_PAIR unknown server: {serverStr}");
        }

        return new Jid(user, server);
    }

    public Jid ReadAdJid()
    {
        byte agent = ReadU8();
        ushort device = ReadU8();
        string user = ReadValueAsString() ?? throw new FormatException("AD_JID missing user");

        var server = agent switch
        {
            0 => Server.Pn,
            1 => Server.Lid,
            128 => Server.Hosted,
            129 => Server.HostedLid,
            var n when (n & 128) != 0 && (n & 1) == 0 => Server.Hosted,
            _ => throw new FormatException($"read_ad_jid - Invalid domain type encoding {agent}")
        };

        return new Jid(user, server, 0, device);
    }

    public Jid ReadInteropJid()
    {
        string user = ReadValueAsString() ?? throw new FormatException("INTEROP_JID missing user");
        ushort device = ReadU16Be();
        ushort integrator = ReadU16Be();
        string serverStr = ReadValueAsString() ?? string.Empty;
        if (serverStr != "interop")
        {
            throw new FormatException($"Invalid INTEROP_JID server: {serverStr}");
        }

        return new Jid(user, Server.Interop, 0, device, integrator);
    }

    public Jid ReadFbJid()
    {
        string user = ReadValueAsString() ?? throw new FormatException("FB_JID missing user");
        ushort device = ReadU16Be();
        string serverStr = ReadValueAsString() ?? string.Empty;
        if (serverStr != "msgr")
        {
            throw new FormatException($"Invalid FB_JID server: {serverStr}");
        }

        return new Jid(user, Server.Messenger, 0, device);
    }

    public string? ReadValueAsString()
    {
        byte tag = ReadU8();
        return ReadValueAsStringFromTag(tag);
    }

    public string? ReadValueAsStringFromTag(byte tag)
    {
        switch (tag)
        {
            case Constants.ListEmpty:
                return null;
            case Constants.Binary8:
                return ReadString(ReadU8());
            case Constants.Binary20:
                return ReadString((int)ReadU20Be());
            case Constants.Binary32:
                return ReadString((int)ReadU32Be());
            case Constants.JidPair:
                return ReadJidPair().ToString();
            case Constants.AdJid:
                return ReadAdJid().ToString();
            case Constants.InteropJid:
                return ReadInteropJid().ToString();
            case Constants.FbJid:
                return ReadFbJid().ToString();
            case Constants.Nibble8:
            case Constants.Hex8:
                return ReadPacked(tag);
            case >= Constants.Dictionary0 and <= Constants.Dictionary3:
                byte subIndex = ReadU8();
                return TokenDictionary.GetDoubleToken(tag - Constants.Dictionary0, subIndex);
            default:
                return TokenDictionary.GetSingleToken(tag);
        }
    }

    public string ReadPacked(byte tag)
    {
        byte packedLenByte = ReadU8();
        bool isHalfByte = (packedLenByte & 0x80) != 0;
        int len = packedLenByte & 0x7F;

        if (len == 0) return string.Empty;

        var packedData = ReadBytes(len).Span;
        var sb = new StringBuilder(len * 2);

        for (int i = 0; i < packedData.Length; i++)
        {
            byte b = packedData[i];
            byte high = (byte)((b >> 4) & 0x0F);
            byte low = (byte)(b & 0x0F);

            if (tag == Constants.Hex8)
            {
                sb.Append(UnpackHex(high));
                sb.Append(UnpackHex(low));
            }
            else if (tag == Constants.Nibble8)
            {
                sb.Append(UnpackNibble(high));
                sb.Append(UnpackNibble(low));
            }
            else
            {
                throw new FormatException($"Invalid packed tag: {tag}");
            }
        }

        if (isHalfByte && sb.Length > 0)
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    private static char UnpackHex(byte value) => value switch
    {
        <= 9 => (char)('0' + value),
        <= 15 => (char)('A' + (value - 10)),
        _ => throw new FormatException($"Invalid hex nibble: {value}")
    };

    private static char UnpackNibble(byte value) => value switch
    {
        <= 9 => (char)('0' + value),
        10 => '-',
        11 => '.',
        15 => '\0',
        _ => throw new FormatException($"Invalid nibble token: {value}")
    };

    public Dictionary<string, NodeValue> ReadAttributes(int count)
    {
        var attrs = new Dictionary<string, NodeValue>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string key = ReadValueAsString() ?? throw new FormatException("Non-string attribute key");
            string val = ReadValueAsString() ?? string.Empty;
            attrs[key] = new NodeValue.StringValue(val);
        }
        return attrs;
    }

    public NodeContent? ReadContentFromTag(byte tag, int depth)
    {
        if (depth >= Constants.MaxNodeDepth)
        {
            throw new FormatException($"Node depth exceeded maximum limit ({Constants.MaxNodeDepth})");
        }

        switch (tag)
        {
            case Constants.ListEmpty:
                return null;
            case Constants.List8:
            case Constants.List16:
                int count = ReadListSize(tag);
                var nodes = new List<Node>(count);
                for (int i = 0; i < count; i++)
                {
                    nodes.Add(ReadNodeAt(depth + 1));
                }
                return new NodeContent.NodesContent(nodes);
            case Constants.Binary8:
                return new NodeContent.BytesContent(ReadBytes(ReadU8()).ToArray());
            case Constants.Binary20:
                return new NodeContent.BytesContent(ReadBytes((int)ReadU20Be()).ToArray());
            case Constants.Binary32:
                return new NodeContent.BytesContent(ReadBytes((int)ReadU32Be()).ToArray());
            default:
                string? s = ReadValueAsStringFromTag(tag);
                return s != null ? new NodeContent.StringContent(s) : null;
        }
    }

    public Node ReadNode() => ReadNodeAt(0);

    public Node ReadNodeAt(int depth)
    {
        if (depth >= Constants.MaxNodeDepth)
        {
            throw new FormatException($"Node depth exceeded maximum limit ({Constants.MaxNodeDepth})");
        }

        byte listTag = ReadU8();
        int listSize = ReadListSize(listTag);
        if (listSize == 0)
        {
            throw new FormatException("Invalid node with 0 list size");
        }

        string tag = ReadValueAsString() ?? throw new FormatException("Invalid node with missing tag");
        int attrCount = (listSize - 1) / 2;
        bool hasContent = (listSize % 2) == 0;

        var attrs = ReadAttributes(attrCount);
        NodeContent? content = null;

        if (hasContent)
        {
            byte contentTag = ReadU8();
            content = ReadContentFromTag(contentTag, depth);
        }

        return new Node(tag, attrs, content);
    }
}
