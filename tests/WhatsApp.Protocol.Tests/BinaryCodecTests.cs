using System.IO.Compression;
using System.Text;

using WhatsApp.Protocol.Binary;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public class BinaryCodecTests
{
    // ==========================================
    // Existing Baseline Tests
    // ==========================================

    [TestMethod]
    public void TestTokenLookup()
    {
        // "message" is single byte token 19 (0x13) in WhatsApp dictionary
        Assert.IsTrue(TokenDictionary.TryGetToken("message", out byte dict, out byte index));
        Assert.AreEqual(0xFF, dict);
        Assert.AreEqual(19, index);
        Assert.AreEqual("message", TokenDictionary.GetSingleToken(19));

        // "receipt" is token 7 (0x07)
        Assert.AreEqual("receipt", TokenDictionary.GetSingleToken(7));
    }

    [TestMethod]
    public void TestJidParsingAndFormatting()
    {
        string raw = "15551234567@s.whatsapp.net";
        var jid = Jid.Parse(raw);
        Assert.AreEqual("15551234567", jid.User);
        Assert.AreEqual(Server.Pn, jid.Server);
        Assert.AreEqual(raw, jid.ToString());

        string adJidRaw = "15551234567:2@s.whatsapp.net";
        var adJid = Jid.Parse(adJidRaw);
        Assert.AreEqual(2, adJid.Device);
        Assert.AreEqual(adJidRaw, adJid.ToString());

        string groupJidRaw = "123456789-987654321@g.us";
        var groupJid = Jid.Parse(groupJidRaw);
        Assert.AreEqual(Server.Group, groupJid.Server);
        Assert.AreEqual(groupJidRaw, groupJid.ToString());
    }

    [TestMethod]
    public void TestSimpleMessageRoundtrip()
    {
        var original = new Node("message");
        original.SetAttr("type", "text");

        var encoder = new BinaryEncoder();
        encoder.WriteNode(original);
        byte[] bytes = encoder.ToArray();

        var decoder = new BinaryDecoder(bytes);
        var decoded = decoder.ReadNode();

        Assert.AreEqual(original.Tag, decoded.Tag);
        Assert.AreEqual("text", decoded.GetAttr("type"));
        Assert.IsNull(decoded.Content);
        Assert.IsTrue(decoder.IsFinished);
    }

    [TestMethod]
    public void TestNodeWithChildrenRoundtrip()
    {
        var original = new Node("iq");
        original.SetAttr("to", "s.whatsapp.net");
        original.SetAttr("type", "get");
        original.SetAttr("id", "12345");

        var pingChild = new Node("ping");
        pingChild.SetAttr("xmlns", "urn:xmpp:ping");

        original.Content = new List<Node> { pingChild };

        var encoder = new BinaryEncoder();
        encoder.WriteNode(original);
        byte[] bytes = encoder.ToArray();

        var decoder = new BinaryDecoder(bytes);
        var decoded = decoder.ReadNode();

        Assert.AreEqual("iq", decoded.Tag);
        Assert.AreEqual("get", decoded.GetAttr("type"));
        Assert.AreEqual("12345", decoded.GetAttr("id"));

        var child = decoded.GetChild("ping");
        Assert.IsNotNull(child);
        Assert.AreEqual("urn:xmpp:ping", child.GetAttr("xmlns"));
    }

    [TestMethod]
    public void TestNodeWithBinaryPayloadRoundtrip()
    {
        byte[] payload = [1, 2, 3, 4, 5, 42, 99, 255];
        var original = new Node("enc", new Dictionary<string, NodeValue> { ["v"] = "2", ["type"] = "msg" }, payload);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(original);
        byte[] bytes = encoder.ToArray();

        var decoder = new BinaryDecoder(bytes);
        var decoded = decoder.ReadNode();

        Assert.AreEqual("enc", decoded.Tag);
        Assert.AreEqual("2", decoded.GetAttr("v"));
        Assert.AreEqual("msg", decoded.GetAttr("type"));

        Assert.IsInstanceOfType(decoded.Content, typeof(NodeContent.BytesContent));
        var bytesContent = (NodeContent.BytesContent)decoded.Content!;
        CollectionAssert.AreEqual(payload, bytesContent.Bytes);
    }

    // ==========================================
    // Decoder Tests Mirroring wacore/binary/src/decoder.rs
    // ==========================================

    [TestMethod]
    public void TestDecodeNode()
    {
        var node = new Node("message", "receipt");

        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] buffer = encoder.ToArray();

        var decoder = new BinaryDecoder(buffer);
        var decoded = decoder.ReadNode();

        Assert.AreEqual("message", decoded.Tag);
        Assert.AreEqual(0, decoded.Attrs.Count);
        Assert.IsInstanceOfType(decoded.Content, typeof(NodeContent.StringContent));
        Assert.AreEqual("receipt", ((NodeContent.StringContent)decoded.Content!).Text);
        Assert.IsTrue(decoder.IsFinished);
    }

    [TestMethod]
    public void TestDecodeNibblePacking()
    {
        string testStr = "-.0123456789";
        var node = new Node("test", testStr);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] buffer = encoder.ToArray();

        var decoder = new BinaryDecoder(buffer);
        var decoded = decoder.ReadNode();

        Assert.AreEqual("test", decoded.Tag);
        Assert.AreEqual(0, decoded.Attrs.Count);
        Assert.IsInstanceOfType(decoded.Content, typeof(NodeContent.StringContent));
        Assert.AreEqual(testStr, ((NodeContent.StringContent)decoded.Content!).Text);
    }

    [TestMethod]
    public void TestInvalidNibbleRejection()
    {
        byte[] invalidData = [1, 0xC0];

        var decoder = new BinaryDecoder(invalidData);
        var ex = Assert.ThrowsException<FormatException>(() => decoder.ReadPacked(Constants.Nibble8));
        StringAssert.Contains(ex.Message, "Invalid nibble token: 12");
    }

    [TestMethod]
    public void TestEmptyInput()
    {
        var decoder = new BinaryDecoder(Array.Empty<byte>());
        Assert.ThrowsException<EndOfStreamException>(() => decoder.ReadNode());
    }

    [TestMethod]
    public void TestTruncatedU16()
    {
        byte[] data = [0x42];
        var decoder = new BinaryDecoder(data);
        Assert.ThrowsException<EndOfStreamException>(() => decoder.ReadU16Be());
    }

    [TestMethod]
    public void TestTruncatedU20()
    {
        byte[] data = [0x42, 0x43];
        var decoder = new BinaryDecoder(data);
        Assert.ThrowsException<EndOfStreamException>(() => decoder.ReadU20Be());
    }

    [TestMethod]
    public void TestTruncatedU32()
    {
        byte[] data = [0x42, 0x43, 0x44];
        var decoder = new BinaryDecoder(data);
        Assert.ThrowsException<EndOfStreamException>(() => decoder.ReadU32Be());
    }

    [TestMethod]
    public void TestBinary8LengthExceedsBuffer()
    {
        byte[] data = [Constants.Binary8, 100, 1, 2, 3, 4, 5];
        var decoder = new BinaryDecoder(data);
        Assert.ThrowsException<EndOfStreamException>(() => decoder.ReadValueAsString());
    }

    [TestMethod]
    public void TestBinary20LengthExceedsBuffer()
    {
        byte[] data = [Constants.Binary20, 0x00, 0x01, 0x00, 1, 2, 3]; // length = 256
        var decoder = new BinaryDecoder(data);
        Assert.ThrowsException<EndOfStreamException>(() => decoder.ReadValueAsString());
    }

    [TestMethod]
    public void TestList8SizeExceedsData()
    {
        byte[] data = [Constants.List8, 10, 1];
        var decoder = new BinaryDecoder(data);
        Assert.ThrowsException<EndOfStreamException>(() => decoder.ReadNode());
    }

    [TestMethod]
    public void MalformedAttributeListsKeepTheirErrors()
    {
        // 1. Truncated key/value
        byte[] truncated = [Constants.List8, 3, Constants.Dictionary0, 0, Constants.Binary8, 2, (byte)'i', (byte)'d'];
        Assert.ThrowsException<EndOfStreamException>(() => new BinaryDecoder(truncated).ReadNode());

        // 2. Non-string key (Constants.ListEmpty)
        byte[] nonStringKey = [Constants.List8, 3, Constants.Dictionary0, 0, Constants.ListEmpty];
        var ex = Assert.ThrowsException<FormatException>(() => new BinaryDecoder(nonStringKey).ReadNode());
        StringAssert.Contains(ex.Message, "Non-string attribute key");

        // 3. Overlong count
        byte[] overlongCount = [Constants.List8, 9, Constants.Dictionary0, 0];
        Assert.ThrowsException<EndOfStreamException>(() => new BinaryDecoder(overlongCount).ReadNode());

        // 4. List size zero
        byte[] emptyList = [Constants.ListEmpty];
        var exZero = Assert.ThrowsException<FormatException>(() => new BinaryDecoder(emptyList).ReadNode());
        StringAssert.Contains(exZero.Message, "Invalid node with 0 list size");
    }

    [TestMethod]
    public void TestInvalidToken()
    {
        byte[] data = [Constants.AdJid]; // No data following
        var decoder = new BinaryDecoder(data);
        Assert.ThrowsException<EndOfStreamException>(() => decoder.ReadValueAsString());
    }

    [TestMethod]
    public void TestAdJidRejectsInvalidDomainType()
    {
        byte[] data = [Constants.AdJid, 2, 1, Constants.Binary8, 3, (byte)'1', (byte)'2', (byte)'3'];
        var decoder = new BinaryDecoder(data);
        var ex = Assert.ThrowsException<FormatException>(() => decoder.ReadValueAsString());
        StringAssert.Contains(ex.Message, "Invalid domain type encoding 2");
    }

    [TestMethod]
    public void TestReadBytesExactLength()
    {
        byte[] data = [1, 2, 3, 4, 5];
        var decoder = new BinaryDecoder(data);
        var bytes = decoder.ReadBytes(5);
        CollectionAssert.AreEqual(data, bytes.ToArray());
        Assert.IsTrue(decoder.IsFinished);
    }

    [TestMethod]
    public void TestReadBytesExceedingLength()
    {
        byte[] data = [1, 2, 3];
        var decoder = new BinaryDecoder(data);
        Assert.ThrowsException<EndOfStreamException>(() => decoder.ReadBytes(5));
    }

    [TestMethod]
    public void TestU20Encoding()
    {
        // 0
        byte[] data0 = [0x00, 0x00, 0x00];
        Assert.AreEqual(0u, new BinaryDecoder(data0).ReadU20Be());

        // 256 (0x100)
        byte[] data256 = [0x00, 0x01, 0x00];
        Assert.AreEqual(256u, new BinaryDecoder(data256).ReadU20Be());

        // 65536 (0x10000)
        byte[] data65536 = [0x01, 0x00, 0x00];
        Assert.AreEqual(65536u, new BinaryDecoder(data65536).ReadU20Be());

        // Max u20 (0xFFFFF = 1048575)
        byte[] dataMax = [0x0F, 0xFF, 0xFF];
        Assert.AreEqual(1048575u, new BinaryDecoder(dataMax).ReadU20Be());
    }

    [TestMethod]
    public void TestBytesLeft()
    {
        byte[] data = [1, 2, 3, 4, 5];
        var decoder = new BinaryDecoder(data);

        Assert.AreEqual(5, decoder.BytesLeft);
        decoder.ReadU8();
        Assert.AreEqual(4, decoder.BytesLeft);
        decoder.ReadU8();
        Assert.AreEqual(3, decoder.BytesLeft);
        decoder.ReadBytes(3);
        Assert.AreEqual(0, decoder.BytesLeft);
        Assert.IsTrue(decoder.IsFinished);
    }

    [TestMethod]
    public void TestHexPackedDecoding()
    {
        byte[] packedData = [3, 0xAB, 0xCD, 0xEF];
        var decoder = new BinaryDecoder(packedData);
        string result = decoder.ReadPacked(Constants.Hex8);
        Assert.AreEqual("ABCDEF", result);
    }

    [TestMethod]
    public void TestNibblePackedOddLength()
    {
        byte[] packedData = [0x82, 0x12, 0x3F];
        var decoder = new BinaryDecoder(packedData);
        string result = decoder.ReadPacked(Constants.Nibble8);
        Assert.AreEqual("123", result);
    }

    [TestMethod]
    public void TestEmptyPackedString()
    {
        byte[] packedData = [0];
        var decoder = new BinaryDecoder(packedData);
        string result = decoder.ReadPacked(Constants.Nibble8);
        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public void TestInvalidNibbleValue12()
    {
        byte[] packedData = [1, 0xC0];
        var decoder = new BinaryDecoder(packedData);
        var ex = Assert.ThrowsException<FormatException>(() => decoder.ReadPacked(Constants.Nibble8));
        StringAssert.Contains(ex.Message, "Invalid nibble token: 12");
    }

    [TestMethod]
    public void TestInvalidNibbleValue13()
    {
        byte[] packedData = [1, 0xD0];
        var decoder = new BinaryDecoder(packedData);
        var ex = Assert.ThrowsException<FormatException>(() => decoder.ReadPacked(Constants.Nibble8));
        StringAssert.Contains(ex.Message, "Invalid nibble token: 13");
    }

    [TestMethod]
    public void TestInvalidNibbleValue14()
    {
        byte[] packedData = [1, 0xE0];
        var decoder = new BinaryDecoder(packedData);
        var ex = Assert.ThrowsException<FormatException>(() => decoder.ReadPacked(Constants.Nibble8));
        StringAssert.Contains(ex.Message, "Invalid nibble token: 14");
    }

    [TestMethod]
    public void PackedScratchHandlesShortLongAndOddValues()
    {
        int[] lengths = [11, 12, 13, 127];
        bool[] oddFlags = [false, true];

        foreach (int length in lengths)
        {
            foreach (bool odd in oddFlags)
            {
                byte[] wire = new byte[length + 1];
                Array.Fill(wire, (byte)0x23);
                wire[0] = (byte)(length | (odd ? 0x80 : 0));

                var sb = new StringBuilder();
                for (int i = 0; i < length; i++) sb.Append("23");
                string expected = sb.ToString();
                if (odd) expected = expected[..^1];

                foreach (byte tag in new[] { Constants.Hex8, Constants.Nibble8 })
                {
                    var decoder = new BinaryDecoder(wire);
                    Assert.AreEqual(expected, decoder.ReadPacked(tag));
                    Assert.IsTrue(decoder.IsFinished);
                }

                // Inject invalid nibble 12
                wire[1] = 0xCD;
                var ex12 = Assert.ThrowsException<FormatException>(() => new BinaryDecoder(wire).ReadPacked(Constants.Nibble8));
                StringAssert.Contains(ex12.Message, "Invalid nibble token: 12");

                // Inject invalid nibble 14
                wire[1] = 0x3E;
                var ex14 = Assert.ThrowsException<FormatException>(() => new BinaryDecoder(wire).ReadPacked(Constants.Nibble8));
                StringAssert.Contains(ex14.Message, "Invalid nibble token: 14");
            }
        }
    }

    [TestMethod]
    public void TestNestedNodesRecursionSafety()
    {
        Node current = new("leaf");
        for (int i = 0; i < 50; i++)
        {
            current = new Node($"level{i}", null, new List<Node> { current });
        }

        var encoder = new BinaryEncoder();
        encoder.WriteNode(current);
        byte[] buffer = encoder.ToArray();

        var decoder = new BinaryDecoder(buffer);
        var decoded = decoder.ReadNode();
        Assert.AreEqual("level49", decoded.Tag);
    }

    private static byte[] EncodeNested(int levels)
    {
        Node current = new("leaf");
        for (int i = 0; i < levels; i++)
        {
            current = new Node($"l{i}", null, new List<Node> { current });
        }
        var encoder = new BinaryEncoder();
        encoder.WriteNode(current);
        return encoder.ToArray();
    }

    [TestMethod]
    public void DeeplyNestedAtCapDecodes()
    {
        byte[] buffer = EncodeNested(Constants.MaxNodeDepth - 1);
        var decoded = new BinaryDecoder(buffer).ReadNode();
        Assert.AreEqual($"l{Constants.MaxNodeDepth - 2}", decoded.Tag);
    }

    [TestMethod]
    public void DeeplyNestedOnePastCapIsRejected()
    {
        byte[] buffer = EncodeNested(Constants.MaxNodeDepth);
        var ex = Assert.ThrowsException<FormatException>(() => new BinaryDecoder(buffer).ReadNode());
        StringAssert.Contains(ex.Message, "Node depth exceeded maximum limit");
    }

    // ==========================================
    // Encoder Tests Mirroring wacore/binary/src/encoder.rs
    // ==========================================

    [TestMethod]
    public void TestNibblePacking()
    {
        string testStr = "-.0123456789";
        var node = new Node("test", testStr);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] buffer = encoder.ToArray();

        var decoder = new BinaryDecoder(buffer);
        var decoded = decoder.ReadNode();

        Assert.AreEqual("test", decoded.Tag);
        Assert.IsInstanceOfType(decoded.Content, typeof(NodeContent.StringContent));
        Assert.AreEqual(testStr, ((NodeContent.StringContent)decoded.Content!).Text);
    }

    [TestMethod]
    public void TestListSizeList8Boundary()
    {
        var encoder = new BinaryEncoder();
        encoder.WriteListStart(255);
        byte[] buffer = encoder.ToArray();

        Assert.AreEqual(2, buffer.Length);
        Assert.AreEqual(Constants.List8, buffer[0]);
        Assert.AreEqual(255, buffer[1]);
    }

    [TestMethod]
    public void TestListSizeList16Boundary()
    {
        var encoder = new BinaryEncoder();
        encoder.WriteListStart(256);
        byte[] buffer = encoder.ToArray();

        Assert.AreEqual(3, buffer.Length);
        Assert.AreEqual(Constants.List16, buffer[0]);
        Assert.AreEqual(0x01, buffer[1]);
        Assert.AreEqual(0x00, buffer[2]);
    }

    [TestMethod]
    public void TestListSizeEmpty()
    {
        var encoder = new BinaryEncoder();
        encoder.WriteListStart(0);
        byte[] buffer = encoder.ToArray();

        Assert.AreEqual(1, buffer.Length);
        Assert.AreEqual(Constants.ListEmpty, buffer[0]);
    }

    [TestMethod]
    public void TestHexValidation()
    {
        Assert.IsTrue(BinaryEncoder.ValidateHex("0123456789ABCDEF"));
        Assert.IsTrue(BinaryEncoder.ValidateHex("DEADBEEF"));
        Assert.IsTrue(BinaryEncoder.ValidateHex("1234"));

        Assert.IsFalse(BinaryEncoder.ValidateHex("abcdef"));
        Assert.IsFalse(BinaryEncoder.ValidateHex("DeadBeef"));
        Assert.IsFalse(BinaryEncoder.ValidateHex("-"));
        Assert.IsFalse(BinaryEncoder.ValidateHex("."));
        Assert.IsFalse(BinaryEncoder.ValidateHex(" "));
        Assert.IsFalse(BinaryEncoder.ValidateHex(string.Empty));
    }

    [TestMethod]
    public void TestNibbleValidation()
    {
        Assert.IsTrue(BinaryEncoder.ValidateNibble("0123456789"));
        Assert.IsTrue(BinaryEncoder.ValidateNibble("-"));
        Assert.IsTrue(BinaryEncoder.ValidateNibble("."));
        Assert.IsTrue(BinaryEncoder.ValidateNibble("123-456.789"));

        Assert.IsFalse(BinaryEncoder.ValidateNibble("abc"));
        Assert.IsFalse(BinaryEncoder.ValidateNibble("123abc"));
        Assert.IsFalse(BinaryEncoder.ValidateNibble("ABC"));
        Assert.IsFalse(BinaryEncoder.ValidateNibble("123!456"));
        Assert.IsFalse(BinaryEncoder.ValidateNibble("@"));
        Assert.IsFalse(BinaryEncoder.ValidateNibble(string.Empty));
    }

    [TestMethod]
    public void TestBinaryLengthBoundaries()
    {
        // BINARY_8: length < 256
        byte[] shortData = new byte[255];
        Array.Fill(shortData, (byte)0x42);
        var encoder1 = new BinaryEncoder();
        encoder1.WriteBytesWithLen(shortData);
        byte[] buf1 = encoder1.ToArray();
        Assert.AreEqual(Constants.Binary8, buf1[0]);
        Assert.AreEqual(255, buf1[1]);

        // BINARY_20: 256 <= length < 2^20
        byte[] mediumData = new byte[256];
        Array.Fill(mediumData, (byte)0x42);
        var encoder2 = new BinaryEncoder();
        encoder2.WriteBytesWithLen(mediumData);
        byte[] buf2 = encoder2.ToArray();
        Assert.AreEqual(Constants.Binary20, buf2[0]);
        Assert.AreEqual(0x00, buf2[1]);
        Assert.AreEqual(0x01, buf2[2]);
        Assert.AreEqual(0x00, buf2[3]);
    }

    [TestMethod]
    public void TestNodeWith255Children()
    {
        var children = Enumerable.Range(0, 255).Select(_ => new Node("child")).ToList();
        var parent = new Node("parent", null, children);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(parent);
        byte[] buffer = encoder.ToArray();

        var decoder = new BinaryDecoder(buffer);
        var decoded = decoder.ReadNode();
        Assert.AreEqual("parent", decoded.Tag);
        Assert.IsInstanceOfType(decoded.Content, typeof(NodeContent.NodesContent));
        Assert.AreEqual(255, ((NodeContent.NodesContent)decoded.Content!).Nodes.Count);
    }

    [TestMethod]
    public void TestNodeWith256Children()
    {
        var children = Enumerable.Range(0, 256).Select(_ => new Node("x")).ToList();
        var parent = new Node("parent", null, children);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(parent);
        byte[] buffer = encoder.ToArray();

        var decoder = new BinaryDecoder(buffer);
        var decoded = decoder.ReadNode();
        Assert.AreEqual("parent", decoded.Tag);
        Assert.IsInstanceOfType(decoded.Content, typeof(NodeContent.NodesContent));
        Assert.AreEqual(256, ((NodeContent.NodesContent)decoded.Content!).Nodes.Count);
    }

    [TestMethod]
    public void TestPackedMaxBoundary()
    {
        string maxNibble = new('0', Constants.PackedMax);
        Assert.IsTrue(BinaryEncoder.ValidateNibble(maxNibble));

        string overMax = new('0', Constants.PackedMax + 1);
        Assert.IsFalse(BinaryEncoder.ValidateNibble(overMax));
    }

    [TestMethod]
    public void TestEmptyStringEncoding()
    {
        var encoder = new BinaryEncoder();
        encoder.WriteString(string.Empty);
        byte[] buffer = encoder.ToArray();

        Assert.AreEqual(2, buffer.Length);
        Assert.AreEqual(Constants.Binary8, buffer[0]);
        Assert.AreEqual(0, buffer[1]);
    }

    [TestMethod]
    public void TestEmptyStringRoundtrip()
    {
        var attrs = new Dictionary<string, NodeValue>
        {
            ["key"] = "",
            [""] = "value"
        };
        var node = new Node("test", attrs, string.Empty);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] buffer = encoder.ToArray();

        var decoder = new BinaryDecoder(buffer);
        var decoded = decoder.ReadNode();

        Assert.AreEqual("test", decoded.Tag);
        Assert.AreEqual("", decoded.GetAttr("key"));
        Assert.AreEqual("value", decoded.GetAttr(""));

        // Content decodes as empty bytes since Binary8(0) was written
        Assert.IsInstanceOfType(decoded.Content, typeof(NodeContent.BytesContent));
        Assert.AreEqual(0, ((NodeContent.BytesContent)decoded.Content!).Bytes.Length);
    }

    [TestMethod]
    public void TestJidLengthHeuristic()
    {
        // Short JID: encoded as JID_PAIR token (48 bytes or less)
        string shortJid = "user@s.whatsapp.net";
        var encoder1 = new BinaryEncoder();
        encoder1.WriteString(shortJid);
        byte[] buf1 = encoder1.ToArray();
        Assert.AreEqual(Constants.JidPair, buf1[0]);

        // Long string (> 48 chars): encoded as raw bytes, not as JID
        string longText = new string('x', 300) + "@s.whatsapp.net";
        var encoder2 = new BinaryEncoder();
        encoder2.WriteString(longText);
        byte[] buf2 = encoder2.ToArray();
        Assert.AreEqual(Constants.Binary20, buf2[0]);

        // Verify roundtrip for long string
        var node = new Node("msg", longText);
        var encoder3 = new BinaryEncoder();
        encoder3.WriteNode(node);
        byte[] buf3 = encoder3.ToArray();

        var decoder = new BinaryDecoder(buf3);
        var decoded = decoder.ReadNode();
        Assert.IsInstanceOfType(decoded.Content, typeof(NodeContent.BytesContent));
        Assert.AreEqual(longText, Encoding.UTF8.GetString(((NodeContent.BytesContent)decoded.Content!).Bytes));
    }

    [TestMethod]
    public void TestJidParserPreservesNonNumericDeviceSuffix()
    {
        string value = "foo:bar@s.whatsapp.net";
        var node = new Node("msg", value);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] buffer = encoder.ToArray();

        var decoder = new BinaryDecoder(buffer);
        var decoded = decoder.ReadNode();
        Assert.IsInstanceOfType(decoded.Content, typeof(NodeContent.StringContent));
        Assert.AreEqual(value, ((NodeContent.StringContent)decoded.Content!).Text);
    }

    [TestMethod]
    public void TestJidParserPreservesNonNumericAgentSuffix()
    {
        string value = "hello_world@s.whatsapp.net";
        var node = new Node("msg", value);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] buffer = encoder.ToArray();

        var decoder = new BinaryDecoder(buffer);
        var decoded = decoder.ReadNode();
        Assert.IsInstanceOfType(decoded.Content, typeof(NodeContent.StringContent));
        Assert.AreEqual(value, ((NodeContent.StringContent)decoded.Content!).Text);
    }

    [TestMethod]
    public void TestAdJidDomainTypeLid()
    {
        var lidJid = Jid.LidDevice("236395184570386", 39);
        var node = new Node("to").SetAttr("jid", lidJid);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] buffer = encoder.ToArray();

        int adJidPos = Array.IndexOf(buffer, Constants.AdJid);
        Assert.IsTrue(adJidPos >= 0, "AD_JID token (0xF7) must be present for device JID");

        byte domainType = buffer[adJidPos + 1];
        Assert.AreEqual(1, domainType, "LID JID must encode domain_type=1 (lid)");

        byte device = buffer[adJidPos + 2];
        Assert.AreEqual(39, device);
    }

    [TestMethod]
    public void TestAdJidDomainTypeWhatsApp()
    {
        var pnJid = Jid.PnDevice("551199887766", 33);
        var node = new Node("to").SetAttr("jid", pnJid);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] buffer = encoder.ToArray();

        int adJidPos = Array.IndexOf(buffer, Constants.AdJid);
        Assert.IsTrue(adJidPos >= 0);

        byte domainType = buffer[adJidPos + 1];
        Assert.AreEqual(0, domainType, "s.whatsapp.net JID must encode domain_type=0");

        byte device = buffer[adJidPos + 2];
        Assert.AreEqual(33, device);
    }

    [TestMethod]
    public void TestAdJidDomainTypeWhatsAppIgnoresHiddenAgent()
    {
        var pnJid = new Jid("551199887766", Server.Pn, 2, 33);
        var node = new Node("to").SetAttr("jid", pnJid);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] buffer = encoder.ToArray();

        int adJidPos = Array.IndexOf(buffer, Constants.AdJid);
        Assert.IsTrue(adJidPos >= 0);

        Assert.AreEqual(0, buffer[adJidPos + 1], "PN JID must encode domain_type=0 even if agent is present");

        var decoded = new BinaryDecoder(buffer).ReadNode();
        string jidAttr = decoded.GetAttr("jid")!;
        var decodedJid = Jid.Parse(jidAttr);
        Assert.AreEqual(pnJid.User, decodedJid.User);
        Assert.AreEqual(pnJid.Device, decodedJid.Device);
        Assert.AreEqual(0, decodedJid.Agent);
    }

    [TestMethod]
    public void TestJidStringVsDirectEncodingMatches()
    {
        Jid[] testCases =
        [
            Jid.LidDevice("236395184570386", 39),
            Jid.PnDevice("551199887766", 33),
            Jid.Lid("236395184570386"),
            Jid.Pn("551199887766"),
            Jid.Parse("5511999887766:99@hosted"),
            Jid.Parse("100000012345678:99@hosted.lid"),
        ];

        foreach (var jid in testCases)
        {
            var nodeStr = new Node("to").SetAttr("jid", jid.ToString());
            var nodeJid = new Node("to").SetAttr("jid", jid);

            var encStr = new BinaryEncoder();
            encStr.WriteNode(nodeStr);
            byte[] bufStr = encStr.ToArray();

            var encJid = new BinaryEncoder();
            encJid.WriteNode(nodeJid);
            byte[] bufJid = encJid.ToArray();

            CollectionAssert.AreEqual(bufStr, bufJid, $"String vs direct Jid encoding must produce identical bytes for {jid}");

            var decoded = new BinaryDecoder(bufJid).ReadNode();
            var decodedJid = Jid.Parse(decoded.GetAttr("jid")!);

            Assert.AreEqual(jid.User, decodedJid.User);
            Assert.AreEqual(jid.Device, decodedJid.Device);
            Assert.AreEqual(jid.Server, decodedJid.Server);
        }
    }

    [TestMethod]
    public void TestDirectConstructedHostedEncodesCorrectDomainType()
    {
        var hosted = new Jid("100000000000001", Server.Hosted, 0, 99);
        var hostedLid = new Jid("100000000000002", Server.HostedLid, 0, 99);

        var cases = new (Jid Jid, byte ExpectedDomainType)[]
        {
            (hosted, 128),
            (hostedLid, 129)
        };

        foreach (var (jid, expectedDomain) in cases)
        {
            var node = new Node("to").SetAttr("jid", jid);
            var encoder = new BinaryEncoder();
            encoder.WriteNode(node);
            byte[] buf = encoder.ToArray();

            int pos = Array.IndexOf(buf, Constants.AdJid);
            Assert.IsTrue(pos >= 0, "AD_JID marker must be present");
            Assert.AreEqual(expectedDomain, buf[pos + 1]);
        }
    }

    [TestMethod]
    public void TestLongStringSkipsClassification()
    {
        string atBoundary = new('0', Constants.PackedMax); // 127 nibble chars
        string overBoundary = new('0', Constants.PackedMax + 1); // 128 chars

        var nodeAt = new Node("test", atBoundary);
        byte[] encAt = BinaryPacker.Marshal(nodeAt);

        var nodeOver = new Node("test", overBoundary);
        byte[] encOver = BinaryPacker.Marshal(nodeOver);

        Assert.IsTrue(encAt.Length < encOver.Length, "127-char nibble string should pack smaller than 128-char raw");

        // The 128-char content must be encoded as BINARY_8 + 128 (raw bytes)
        bool hasRaw128 = false;
        for (int i = 0; i < encOver.Length - 1; i++)
        {
            if (encOver[i] == Constants.Binary8 && encOver[i + 1] == 128)
            {
                hasRaw128 = true;
                break;
            }
        }
        Assert.IsTrue(hasRaw128, "128-char string must contain BINARY_8 + length=128");

        var decodedAt = BinaryPacker.Unmarshal(encAt);
        var decodedOver = BinaryPacker.Unmarshal(encOver);

        Assert.IsInstanceOfType(decodedAt.Content, typeof(NodeContent.StringContent));
        Assert.AreEqual(atBoundary, ((NodeContent.StringContent)decodedAt.Content!).Text);

        Assert.IsInstanceOfType(decodedOver.Content, typeof(NodeContent.BytesContent));
        Assert.AreEqual(overBoundary, Encoding.UTF8.GetString(((NodeContent.BytesContent)decodedOver.Content!).Bytes));
    }

    [TestMethod]
    public void TestBotJidWithDeviceRoundTripsViaJidPair()
    {
        string[] values =
        [
            "867051314767696@bot",
            "867051314767696:0@bot",
            "120363021033254949@g.us",
            "12345@broadcast",
            "12345@newsletter"
        ];

        foreach (var val in values)
        {
            var node = new Node("msg").SetAttr("from", val);

            var encoder = new BinaryEncoder();
            encoder.WriteNode(node);
            byte[] buf = encoder.ToArray();

            Assert.IsFalse(buf.Contains(Constants.AdJid), $"AD_JID must not be emitted for {val} (would lose the server)");

            var decoded = new BinaryDecoder(buf).ReadNode();
            string got = decoded.GetAttr("from")!;

            string expectedServer = val.Split('@')[1];
            Assert.IsTrue(got.EndsWith($"@{expectedServer}"), $"Roundtrip lost server for {val}: got {got}");
        }
    }

    [TestMethod]
    public void TestCallJidRoundTripsViaJidPair()
    {
        var node = new Node("call").SetAttr("from", "12345@call");
        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] buf = encoder.ToArray();

        Assert.IsFalse(buf.Contains(Constants.AdJid), "AD_JID must not be emitted for @call");

        var decoded = new BinaryDecoder(buf).ReadNode();
        Assert.AreEqual("12345@call", decoded.GetAttr("from"));
    }

    [TestMethod]
    public void AdJidDeviceIsOneByteWideUnlikeInterop()
    {
        void Encode(Jid jid)
        {
            var node = new Node("msg").SetAttr("from", jid);
            var encoder = new BinaryEncoder();
            encoder.WriteNode(node);
        }

        // 255 fits in AD_JID
        Encode(Jid.PnDevice("5511987650001", 255));

        // 256 throws ArgumentOutOfRangeException
        var ex = Assert.ThrowsException<ArgumentOutOfRangeException>(() => Encode(Jid.PnDevice("5511987650001", 256)));
        StringAssert.Contains(ex.Message, "out of range");

        // Interop with device 65535 and integrator 7 succeeds
        var interop = new Jid("5511987650001", Server.Interop, 0, 65535, 7);
        var encInterop = new BinaryEncoder();
        encInterop.WriteJid(interop);
        byte[] interopBytes = encInterop.ToArray();
        Assert.AreEqual(Constants.InteropJid, interopBytes[0]);
    }

    [TestMethod]
    public void TestTypedNonAdJidWithDeviceRoundTripsViaJidPair()
    {
        string[] values =
        [
            "867051314767696:0@bot",
            "12345:5@broadcast",
            "67890:9@newsletter"
        ];

        foreach (var val in values)
        {
            var jid = Jid.Parse(val);
            var node = new Node("msg").SetAttr("from", jid);

            var encoder = new BinaryEncoder();
            encoder.WriteNode(node);
            byte[] buf = encoder.ToArray();

            Assert.IsFalse(buf.Contains(Constants.AdJid), $"Typed JID {val} must not emit AD_JID");

            var decoded = new BinaryDecoder(buf).ReadNode();
            var decodedJid = Jid.Parse(decoded.GetAttr("from")!);
            Assert.AreEqual(jid.Server, decodedJid.Server);
            Assert.AreEqual(jid.User, decodedJid.User);
        }
    }

    // ==========================================
    // Format Byte and ZLib Pack/Unpack Tests (util.rs)
    // ==========================================

    [TestMethod]
    public void TestFormatByteConstants()
    {
        Assert.AreEqual(0, Constants.FormatPlain);
        Assert.AreEqual(2, Constants.FormatCompressed);
        Assert.AreEqual(16 * 1024 * 1024, Constants.MaxDecompressedSize);
    }

    [TestMethod]
    public void TestPackAndUnpackPlain()
    {
        var node = new Node("iq", new Dictionary<string, NodeValue> { ["type"] = "get" });
        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] rawNodeBytes = encoder.ToArray();

        // Pack: prefixes FORMAT_PLAIN
        byte[] packed = BinaryPacker.Pack(rawNodeBytes);
        Assert.AreEqual(rawNodeBytes.Length + 1, packed.Length);
        Assert.AreEqual(Constants.FormatPlain, packed[0]);

        // CheckPlainPayload
        BinaryPacker.CheckPlainPayload(packed);

        // Unpack: strips FORMAT_PLAIN
        byte[] unpacked = BinaryPacker.Unpack(packed);
        CollectionAssert.AreEqual(rawNodeBytes, unpacked);

        // Unmarshal
        var unmarshaled = BinaryPacker.Unmarshal(packed);
        Assert.AreEqual("iq", unmarshaled.Tag);
        Assert.AreEqual("get", unmarshaled.GetAttr("type"));
    }

    [TestMethod]
    public void TestPackAndUnpackCompressedZlib()
    {
        // Create large payload with repetitive text
        var children = Enumerable.Range(0, 100)
            .Select(i => new Node("item", new Dictionary<string, NodeValue> { ["index"] = i.ToString(), ["data"] = "payload_content_12345" }))
            .ToList();
        var node = new Node("list", null, children);

        var encoder = new BinaryEncoder();
        encoder.WriteNode(node);
        byte[] rawNodeBytes = encoder.ToArray();

        // Compress using zlib
        byte[] compressedPayload = BinaryPacker.CompressZlib(rawNodeBytes);
        byte[] networkPacket = new byte[compressedPayload.Length + 1];
        networkPacket[0] = Constants.FormatCompressed;
        compressedPayload.CopyTo(networkPacket.AsSpan(1));

        // Unpack handles decompression
        byte[] decompressed = BinaryPacker.Unpack(networkPacket);
        CollectionAssert.AreEqual(rawNodeBytes, decompressed);

        // Unmarshal decodes correctly
        var decodedNode = BinaryPacker.Unmarshal(networkPacket);
        Assert.AreEqual("list", decodedNode.Tag);
        Assert.IsInstanceOfType(decodedNode.Content, typeof(NodeContent.NodesContent));
        Assert.AreEqual(100, ((NodeContent.NodesContent)decodedNode.Content!).Nodes.Count);
    }

    [TestMethod]
    public void TestCheckPlainPayloadRejections()
    {
        // Empty data
        Assert.ThrowsException<InvalidDataException>(() => BinaryPacker.CheckPlainPayload(Array.Empty<byte>()));

        // Format compressed rejected by check plain
        byte[] compressedData = [Constants.FormatCompressed, 1, 2, 3];
        var exComp = Assert.ThrowsException<InvalidDataException>(() => BinaryPacker.CheckPlainPayload(compressedData));
        StringAssert.Contains(exComp.Message, "Unexpected format byte: 2");

        // Format byte with no data following
        byte[] onlyFormatByte = [Constants.FormatPlain];
        var exEmpty = Assert.ThrowsException<InvalidDataException>(() => BinaryPacker.CheckPlainPayload(onlyFormatByte));
        StringAssert.Contains(exEmpty.Message, "Empty data");
    }

    [TestMethod]
    public void TestUnpackEmptyThrows()
    {
        Assert.ThrowsException<InvalidDataException>(() => BinaryPacker.Unpack(Array.Empty<byte>()));
    }
}
