using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class Phase8DProtocolTests
{
    private static Node DecodeLiveHex(string hex) =>
        new BinaryDecoder(Convert.FromHexString(hex)).ReadNode();

    private static byte[] EncodeUncompressedNode(Node node)
    {
        var enc = new BinaryEncoder();
        enc.WriteNode(node);
        return enc.ToArray();
    }

    private static void AssertSameNodeSemantics(Node expected, Node actual)
    {
        Assert.AreEqual(expected.Tag, actual.Tag);
        Assert.AreEqual(expected.Attrs.Count, actual.Attrs.Count);
        foreach ((string key, NodeValue val) in expected.Attrs)
        {
            Assert.IsTrue(actual.Attrs.TryGetValue(key, out NodeValue? actualVal), $"Missing attr '{key}'");
            Assert.AreEqual(val.ToString(), actualVal?.ToString(), $"Attr '{key}' mismatch");
        }
    }

    [TestMethod]
    public void PrivacyProtocol_LiveWaWapParity_And_ValidationRules_MatchWebAndRust()
    {
        // Live web.whatsapp.com WAWap.encodeStanzaWithoutHeader hex captures from pageId: 1
        const string livePrivGetHex = "f80a1911fa0003042916e208fc0470672d31f801f801e2";
        const string livePrivSetHex = "f80a1911fa0003045a16e208fc03702d31f801f804e2fc0f61646472657373696e675f6d6f646576f801f8088189616cecc3ecd6ff03123456f801f8070eedd3970cfc13313030303030303030303030303032406c6964fc06706e5f6a6964fc1a313535353030303030303240732e77686174736170702e6e6574";
        const string liveDmSetHex = "f80a1911fa0003045a160a08fc03642d31f801f8030a17ff03604800";
        const string liveRemoveDevHex = "f80a1911fa0003045a16fc026d6408fc03722d31f801f805fc1772656d6f76652d636f6d70616e696f6e2d6465766963650cfc1c31353535303030303030313a3240732e77686174736170702e6e6574ec2efc0e757365725f696e69746961746564";
        const string liveSpamIqHex = "f80a1911fa0003045a16fc047370616d08fc0473702d31f801f804fc097370616d5f6c697374fc097370616d5f666c6f77fc0b4d6573736167654d656e75f801f8081308fc044d5347311aff05170000000006fc1a313535353030303030303240732e77686174736170702e6e6574f801f806fc0372617751a22638fc03010203";
        const string livePresenceNameHex = "f8031f89fc05416c696365";

        Node getReq = PrivacyProtocol.BuildGetPrivacy("pg-1");
        Node decodedGet = DecodeLiveHex(livePrivGetHex);
        AssertSameNodeSemantics(decodedGet, getReq);
        AssertSameNodeSemantics(decodedGet, DecodeLiveHex(Convert.ToHexString(EncodeUncompressedNode(getReq))));

        Node setReq = PrivacyProtocol.BuildSetPrivacyWithDisallowedList(
            "p-1",
            PrivacyCategory.Last,
            "123456",
            [new DisallowedListUserEntry(DisallowedListAction.Add, Jid.Parse("100000000000002@lid"), Jid.Parse("15550000002@s.whatsapp.net"))]);
        Node decodedSet = DecodeLiveHex(livePrivSetHex);
        AssertSameNodeSemantics(decodedSet, setReq);
        Assert.AreEqual("lid", decodedSet.GetChild("privacy")?.GetAttr("addressing_mode"));
        Assert.AreEqual("contact_blacklist", decodedSet.GetChild("privacy")?.GetChild("category")?.GetAttr("value"));
        Assert.AreEqual("123456", decodedSet.GetChild("privacy")?.GetChild("category")?.GetAttr("dhash"));
        AssertSameNodeSemantics(
            decodedSet.GetChild("privacy")!.GetChild("category")!.GetChild("user")!,
            setReq.GetChild("privacy")!.GetChild("category")!.GetChild("user")!);

        Node dmReq = PrivacyProtocol.BuildSetDefaultDisappearingMode("d-1", 604800);
        Node decodedDm = DecodeLiveHex(liveDmSetHex);
        AssertSameNodeSemantics(decodedDm, dmReq);
        AssertSameNodeSemantics(decodedDm.GetChild("disappearing_mode")!, dmReq.GetChild("disappearing_mode")!);

        Node remReq = DeviceManagementIq.BuildRemoveCompanionDevice("r-1", Jid.Parse("15550000001:2@s.whatsapp.net"));
        Node decodedRem = DecodeLiveHex(liveRemoveDevHex);
        AssertSameNodeSemantics(decodedRem, remReq);
        AssertSameNodeSemantics(decodedRem.GetChild("remove-companion-device")!, remReq.GetChild("remove-companion-device")!);

        Node spamReq = SpamReportIq.BuildReportSpam("sp-1", new SpamReportRequest(
            "MSG1",
            1700000000,
            FromJid: Jid.Parse("15550000002@s.whatsapp.net"),
            Flow: SpamFlow.MessageMenu,
            RawMessageBytes: [1, 2, 3],
            MediaType: "text"));
        Node decodedSpam = DecodeLiveHex(liveSpamIqHex);
        AssertSameNodeSemantics(decodedSpam, spamReq);
        AssertSameNodeSemantics(decodedSpam.GetChild("spam_list")!, spamReq.GetChild("spam_list")!);
        AssertSameNodeSemantics(decodedSpam.GetChild("spam_list")!.GetChild("message")!, spamReq.GetChild("spam_list")!.GetChild("message")!);

        Node decodedPres = DecodeLiveHex(livePresenceNameHex);
        AssertSameNodeSemantics(decodedPres, new Node("presence", new Dictionary<string, NodeValue> { ["name"] = "Alice" }));
        CollectionAssert.AreEqual(
            Convert.FromHexString(livePresenceNameHex),
            EncodeUncompressedNode(new Node("presence", new Dictionary<string, NodeValue> { ["name"] = "Alice" })));

        // Live Web CallAdd supports "screen"
        Assert.IsTrue(PrivacyProtocol.IsValidCombination(PrivacyCategory.CallAdd, PrivacyValue.Screen));
        Assert.ThrowsException<ArgumentException>(() => PrivacyProtocol.BuildSetPrivacy("x", PrivacyCategory.Online, PrivacyValue.None));
        Assert.ThrowsException<ArgumentException>(() => PrivacyProtocol.BuildSetPrivacyWithDisallowedList("x", PrivacyCategory.DefenseMode, "0", []));

        // Parse privacy GET & SET responses + notification
        PrivacySettingsSnapshot parsed = PrivacyProtocol.ParseGetPrivacyResponse(new Node("iq", null, new[]
        {
            new Node("privacy", new[]
            {
                new Node("category", new Dictionary<string, NodeValue> { ["name"] = "last", ["value"] = "contact_blacklist", ["dhash"] = "dh-1" }),
                new Node("category", new Dictionary<string, NodeValue> { ["name"] = "calladd", ["value"] = "screen" }),
                new Node("category", new Dictionary<string, NodeValue> { ["name"] = "defense", ["value"] = "on_standard" })
            })
        }));
        Assert.AreEqual(PrivacyValue.ContactBlacklist, parsed.Get(PrivacyCategory.Last)?.Value);
        Assert.AreEqual(PrivacyValue.Screen, parsed.Get(PrivacyCategory.CallAdd)?.Value);
        Assert.AreEqual(PrivacyValue.OnStandard, parsed.Get(PrivacyCategory.DefenseMode)?.Value);
        Assert.AreEqual("dh-2", PrivacyProtocol.ParseSetPrivacyResponse(new Node("iq", null, new[]
        {
            new Node("privacy", new[]
            {
                new Node("category", new Dictionary<string, NodeValue> { ["name"] = "last", ["value"] = "contact_blacklist", ["dhash"] = "dh-2" })
            })
        })).Dhash);
    }

    [TestMethod]
    public void AppStateSchemas_PreKeyRotate_And_SafetyNumber_AreDeterministicAndCanonical()
    {
        CollectionAssert.AreEqual(
            System.Text.Encoding.UTF8.GetBytes("[\"pin_v1\",\"1555@s.whatsapp.net\"]"),
            AppStateSchemas.Pin.BuildIndex("1555@s.whatsapp.net"));
        CollectionAssert.AreEqual(
            System.Text.Encoding.UTF8.GetBytes("[\"setting_disableLinkPreviews\"]"),
            AppStateSchemas.DisableLinkPreviews.BuildIndex());
        Assert.ThrowsException<ArgumentException>(() => AppStateSchemas.Archive.BuildIndex());

        var spk = new byte[32];
        var sig = new byte[64];
        Node rotateIq = PreKeyIq.BuildRotateSignedPreKey("rot-1", 0x00ABCDEFu, spk, sig);
        Assert.AreEqual("encrypt", rotateIq.GetAttr("xmlns"));
        Node skey = rotateIq.GetChild("rotate")!.GetChild("skey")!;
        CollectionAssert.AreEqual(new byte[] { 0xAB, 0xCD, 0xEF }, ((NodeContent.BytesContent)skey.GetChild("id")!.Content!).Bytes);

        // SafetyNumber symmetry across both endpoints
        KeyPair aKey = Curve25519.GenerateKeyPair();
        KeyPair bKey = Curve25519.GenerateKeyPair();
        SafetyNumberFingerprint fpA = SafetyNumber.Compute("100001@lid", aKey.PublicKey, "100002@lid", bKey.PublicKey);
        SafetyNumberFingerprint fpB = SafetyNumber.Compute("100002@lid", bKey.PublicKey, "100001@lid", aKey.PublicKey);
        Assert.AreEqual(60, fpA.DisplayableDigits.Length);
        Assert.AreEqual(52, fpA.ScannableQrBytes.Length);
        Assert.AreEqual(fpA.DisplayableDigits, fpB.DisplayableDigits);
        Assert.IsTrue(fpA.Matches(fpB.ScannableQrBytes));
    }
}
