using System.Text;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class UsyncProfileAndNotificationProtocolTests
{
    [TestMethod]
    public void ContactHash_And_LidPnCache_MatchLiveWebAndSupportRecencyLookup()
    {
        // Verified live on web.whatsapp.com via WAWebApiContact.getContactHash("100000000000001@lid")
        Assert.AreEqual("s7oK", ContactHash.ComputeBase64("100000000000001@lid"));
        Assert.AreEqual("P2DY", ContactHash.ComputeBase64("100000000000002@s.whatsapp.net"));
        Assert.AreEqual("4ZhY", ContactHash.ComputeBase64("100000000000003:12@c.us"));

        var cache = new LidPnCache();
        cache.Learn("15550001111@c.us", "100000000000001@lid", LidPnLearningSource.HistorySync, 100);
        cache.Learn("15550001111@s.whatsapp.net", "100000000000099@lid", LidPnLearningSource.Usync, 200);
        // Older timestamp does not overwrite newer LID mapping.
        cache.Learn("15550001111@s.whatsapp.net", "100000000000050@lid", LidPnLearningSource.PeerJid, 150);

        Assert.IsTrue(cache.TryGetLid("15550001111@s.whatsapp.net", out string? currentLid));
        Assert.AreEqual("100000000000099@lid", currentLid);
        Assert.IsTrue(cache.TryGetPn("100000000000001@lid", out string? pn1));
        Assert.AreEqual("15550001111@s.whatsapp.net", pn1);

        IReadOnlyList<string> matches = cache.LookupByContactHash("s7oK");
        CollectionAssert.AreEqual(new[] { "100000000000001@lid" }, matches.ToArray());
    }

    [TestMethod]
    public void PairingCrypto_AdvKeyIndexList_SignVerifyAndHostedWork()
    {
        var kp = Curve25519.GenerateKeyPair();
        var kiList = new ADVKeyIndexList
        {
            RawId = 98765,
            Timestamp = 1700000123,
            CurrentIndex = 3,
            AccountType = ADVEncryptionType.Hosted
        };
        kiList.ValidIndexes.AddRange([0u, 2u, 3u]);

        byte[] signed = PairingCrypto.SignKeyIndexList(kp.PrivateKey, kiList, kp.PublicKey);
        PairingCrypto.DecodedKeyIndexList? verified = PairingCrypto.VerifyKeyIndexList(signed, kp.PublicKey);
        Assert.IsNotNull(verified);
        Assert.AreEqual(98765u, verified.RawId);
        Assert.AreEqual(1700000123UL, verified.Timestamp);
        Assert.AreEqual(3u, verified.CurrentIndex);
        Assert.AreEqual(ADVEncryptionType.Hosted, verified.AccountType);
        CollectionAssert.AreEqual(new[] { 0u, 2u, 3u }, verified.ValidIndexes.ToArray());

        // Tampered key fails authentication.
        var otherKp = Curve25519.GenerateKeyPair();
        Assert.IsNull(PairingCrypto.VerifyKeyIndexList(signed, otherKp.PublicKey));

        PairingCrypto.DecodedKeyIndexList? hosted = PairingCrypto.VerifyHostedKeyIndexList(signed, out byte[]? envPub);
        Assert.IsNotNull(hosted);
        CollectionAssert.AreEqual(kp.PublicKey, envPub);
    }

    [TestMethod]
    public void UsyncProtocol_UsernameLookup_UserInfo_And_VerifiedName_RoundTrip()
    {
        Node req = UsyncProtocol.BuildUsernameLookupQuery("iq-u1", "sid-1", "@alice_wa", "1234");
        Assert.AreEqual("usync", req.GetAttr("xmlns"));
        Node? contactQuery = req.GetChild("usync")?.GetChild("query")?.GetChild("contact");
        Assert.IsNotNull(contactQuery);
        Assert.AreEqual("lid", contactQuery.GetAttr("addressing_mode"));

        var details = new VerifiedNameCertificate.Types.Details
        {
            Serial = 42,
            Issuer = "smb:wa",
            VerifiedName = "Alice Shop"
        };
        byte[] certBytes = new VerifiedNameCertificate { Details = details.ToByteString() }.ToByteArray();

        Node foundResp = new("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[]
        {
            new("usync", new Node[]
            {
                new("list", new Node[]
                {
                    new("user", new Dictionary<string, NodeValue>
                    {
                        ["jid"] = Jid.Lid("100000000000555"),
                        ["pn_jid"] = Jid.Pn("15551230000")
                    }, new Node[]
                    {
                        new("contact", new Dictionary<string, NodeValue> { ["type"] = "in", ["username"] = "alice_wa" }),
                        new("business", new Node[] { new("verified_name", certBytes) })
                    })
                })
            })
        });

        UsernameLookupResult parsed = UsyncProtocol.ParseUsernameLookupResponse(foundResp);
        Assert.AreEqual(UsernameLookupStatus.Found, parsed.Status);
        Assert.AreEqual("100000000000555@lid", parsed.LidJid?.ToString());
        Assert.AreEqual("15551230000@s.whatsapp.net", parsed.PnJid?.ToString());
        Assert.AreEqual("alice_wa", parsed.Username);
        Assert.IsTrue(parsed.IsBusiness);
        Assert.AreEqual("Alice Shop", parsed.VerifiedName?.Name);
        Assert.AreEqual("42", parsed.VerifiedName?.Serial);

        // KeyRequired when type="in" but jid omitted:
        Node pinReqResp = new("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[]
        {
            new("usync", new Node[]
            {
                new("list", new Node[]
                {
                    new("user", new Node[]
                    {
                        new("contact", new Dictionary<string, NodeValue> { ["type"] = "in", ["username"] = "alice_wa" })
                    })
                })
            })
        });
        UsernameLookupResult pinParsed = UsyncProtocol.ParseUsernameLookupResponse(pinReqResp);
        Assert.AreEqual(UsernameLookupStatus.KeyRequired, pinParsed.Status);
        Assert.AreEqual("alice_wa", pinParsed.Username);
    }

    [TestMethod]
    public void ProfilePictureIq_HandlesStandard_Cover_Community_And_Errors()
    {
        byte[] tc = [1, 2, 3, 4];
        Node getIq = ProfilePictureIq.BuildGet(
            "pic-1",
            Jid.Pn("15550001234"),
            ProfilePictureType.Cover,
            existingId: "77",
            size: 640,
            tcToken: tc);

        Assert.AreEqual("w:profile:picture", getIq.GetAttr("xmlns"));
        Assert.AreEqual("15550001234@s.whatsapp.net", getIq.GetAttr("target"));
        Node? picNode = getIq.GetChild("picture");
        Assert.IsNotNull(picNode);
        Assert.AreEqual("cover", picNode.GetAttr("type"));
        Assert.AreEqual("640", picNode.GetAttr("size"));
        Assert.IsNotNull(picNode.GetChild("tctoken"));

        Node commIq = ProfilePictureIq.BuildGet("pic-c1", new Jid("1203630001", Server.Group), ProfilePictureType.Preview, isCommunity: true);
        Assert.AreEqual("w:g2", commIq.GetAttr("xmlns"));
        Assert.AreEqual("1203630001@g.us", commIq.GetChild("pictures")?.GetChild("picture")?.GetAttr("parent_group_jid"));

        Node okResp = new("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[]
        {
            new("picture", new Dictionary<string, NodeValue>
            {
                ["id"] = "999",
                ["type"] = "image",
                ["url"] = "https://pps.whatsapp.net/v/pic.jpg",
                ["direct_path"] = "/v/pic.jpg",
                ["hash"] = "abc123",
                ["has_staging"] = "true"
            })
        });
        ProfilePictureLookupResult found = ProfilePictureIq.ParseGetResponse(okResp);
        Assert.AreEqual(ProfilePictureLookupStatus.Found, found.Status);
        Assert.AreEqual("999", found.Picture!.Id);
        Assert.AreEqual(ProfilePictureType.Full, found.Picture.Type);
        Assert.IsTrue(found.Picture.HasStaging);

        Node notFound = new("iq", new Node[] { new("picture", new Node[] { new("error", new Dictionary<string, NodeValue> { ["code"] = "404", ["text"] = "item-not-found" }) }) });
        Assert.AreEqual(ProfilePictureLookupStatus.NotFound, ProfilePictureIq.ParseGetResponse(notFound).Status);

        Node unauth = new("iq", new Node[] { new("picture", new Node[] { new("error", new Dictionary<string, NodeValue> { ["code"] = "401" }) }) });
        Assert.AreEqual(ProfilePictureLookupStatus.NotAuthorized, ProfilePictureIq.ParseGetResponse(unauth).Status);

        Node unchanged = new("iq", new Node[] { new("picture", new Dictionary<string, NodeValue> { ["status"] = "304" }) });
        Assert.AreEqual(ProfilePictureLookupStatus.Unchanged, ProfilePictureIq.ParseGetResponse(unchanged, hadExistingId: true).Status);

        Node rateLimit = new("iq", new Node[] { new("picture", new Node[] { new("error", new Dictionary<string, NodeValue> { ["code"] = "429", ["backoff"] = "60", ["text"] = "rate-overlimit" }) }) });
        var ex = Assert.ThrowsException<ProfilePictureIqException>(() => ProfilePictureIq.ParseGetResponse(rateLimit));
        Assert.AreEqual(429, ex.Code);
        Assert.AreEqual(60, ex.BackoffSeconds);
    }

    [TestMethod]
    public void LiveWeb_UsyncWireVectors_DecodeAndMatchProtocol()
    {
        // Captured verbatim from live web.whatsapp.com WAWebUsync.USyncQuery.$3() -> WAWap.encodeStanzaWithoutHeader
        byte[] liveDevBytes = Convert.FromHexString(
            "f80a1911fa00031679042908ff885797b30575a1084ff801f80c79fc03736964ff885797b30575a1085f8f2d619b6a6d7d13" +
            "f802f8026df802f8030f5445f80176f80271f801f8040e0cfaff8615551234567f03f802f8050f10fc05323a61626373ff0517" +
            "00000000f803760cfaff88100000000000101f76");

        Node liveDev = new BinaryDecoder(liveDevBytes).ReadNode();
        Assert.AreEqual("usync", liveDev.GetAttr("xmlns"));
        Assert.AreEqual("message", liveDev.GetChild("usync")?.GetAttr("context"));
        Node? devUser = liveDev.GetChild("usync")?.GetChild("list")?.GetChild("user");
        Assert.AreEqual("15551234567@s.whatsapp.net", devUser?.GetAttr("jid"));
        Assert.AreEqual("2:abc", devUser?.GetChild("devices")?.GetAttr("device_hash"));
        Assert.AreEqual("100000000000101@lid", devUser?.GetChild("lid")?.GetAttr("jid"));

        byte[] liveUnameBytes = Convert.FromHexString(
            "f80a1911fa00031679042908ff885797b30575a1086ff801f80c79fc03736964ff885797b30575a1087f8f2d619b6a6d7de5" +
            "f802f8026df802f80325fc0f61646472657373696e675f6d6f646576f80244f801f80122f80271f801f8020ef801f80525fc08" +
            "757365726e616d65fc08616c6963655f7761fc0370696eff021234");

        Node liveUname = new BinaryDecoder(liveUnameBytes).ReadNode();
        Node? uContactQuery = liveUname.GetChild("usync")?.GetChild("query")?.GetChild("contact");
        Assert.AreEqual("lid", uContactQuery?.GetAttr("addressing_mode"));
        Node? uContactUser = liveUname.GetChild("usync")?.GetChild("list")?.GetChild("user")?.GetChild("contact");
        Assert.AreEqual("alice_wa", uContactUser?.GetAttr("username"));
        Assert.AreEqual("1234", uContactUser?.GetAttr("pin"));

        byte[] liveInfoBytes = Convert.FromHexString(
            "f80a1911fa00031679042908ff885797b30575a1088ff801f80c79fc03736964ff885797b30575a1089f8f2d619b6aed0a7da0" +
            "f802f8026df806f80244f801f80122f80121f8030f5445f80176f801fc08757365726e616d65f8010af80271f801f8040e0cfaff" +
            "88100000000000101f76f801f80290fc0401020304");

        Node liveInfo = new BinaryDecoder(liveInfoBytes).ReadNode();
        Assert.AreEqual("full", liveInfo.GetChild("usync")?.GetAttr("mode"));
        Assert.IsNotNull(liveInfo.GetChild("usync")?.GetChild("query")?.GetChild("disappearing_mode"));
        byte[]? decodedTc = (liveInfo.GetChild("usync")?.GetChild("list")?.GetChild("user")?.GetChild("tctoken")?.Content as NodeContent.BytesContent)?.Bytes;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, decodedTc);
    }

    [TestMethod]
    public void NotificationStanzas_ParsesDevices_Encrypt_Groups_And_Contacts()
    {
        // Devices remove > add > update precedence & lid device mismatch rejection
        Node remNotif = new("notification", new Dictionary<string, NodeValue>
        {
            ["type"] = "devices",
            ["from"] = Jid.Lid("100000000000001"),
            ["t"] = "1700000000"
        }, new Node[]
        {
            new("remove", new Node[]
            {
                new("device", new Dictionary<string, NodeValue> { ["jid"] = Jid.LidDevice("100000000000001", 2) }),
                new("key-index-list", new Dictionary<string, NodeValue> { ["ts"] = "1700000000" })
            }),
            new("update", new Dictionary<string, NodeValue> { ["hash"] = "s7oK" })
        });
        ParsedDevicesNotification? rem = NotificationStanzas.ParseDevicesNotification(remNotif);
        Assert.IsNotNull(rem);
        Assert.AreEqual(DeviceNotificationOp.Remove, rem.Operation);
        Assert.AreEqual(2u, rem.Devices[0].DeviceId);

        // Device mismatch between jid and lid rejects notification (WAWebHandleDeviceNotification)
        Node mismatch = new("notification", new Dictionary<string, NodeValue> { ["type"] = "devices", ["from"] = Jid.Pn("15550001111") }, new Node[]
        {
            new("remove", new Node[]
            {
                new("device", new Dictionary<string, NodeValue>
                {
                    ["jid"] = Jid.PnDevice("15550001111", 2),
                    ["lid"] = Jid.LidDevice("100000000000001", 3)
                }),
                new("key-index-list", new Dictionary<string, NodeValue> { ["ts"] = "1700000000" })
            })
        });
        Assert.IsNull(NotificationStanzas.ParseDevicesNotification(mismatch));

        // Encrypt pq_count first + count child present
        Node pqFirst = new("notification", new Dictionary<string, NodeValue> { ["type"] = "encrypt", ["from"] = StanzaBuilder.ServerPn }, new Node[]
        {
            new("pq_count", new Dictionary<string, NodeValue> { ["value"] = "10" }),
            new("count", new Dictionary<string, NodeValue> { ["value"] = "3" })
        });
        ParsedEncryptNotification? enc = NotificationStanzas.ParseEncryptNotification(pqFirst);
        Assert.AreEqual(EncryptNotificationKind.PreKeyLow, enc?.Kind);
        Assert.IsTrue(enc?.HasCount);
        Assert.IsTrue(enc?.HasPqCount);
        Assert.AreEqual(3, enc?.RemainingPreKeyCount);

        // Group dirty vs remove
        Node grpDirty = new("notification", new Dictionary<string, NodeValue> { ["type"] = "w:gp2", ["from"] = StanzaBuilder.ServerPn }, new Node[]
        {
            new("groups_dirty", new Node[] { new("group", new Dictionary<string, NodeValue> { ["jid"] = new Jid("120363001", Server.Group) }) })
        });
        ParsedGroupNotification? dirtyParsed = NotificationStanzas.ParseGroupNotification(grpDirty);
        Assert.IsTrue(dirtyParsed?.IsDirtyNotification);
        Assert.AreEqual("120363001@g.us", dirtyParsed!.DirtyGroupJids[0].ToString());
    }
}
