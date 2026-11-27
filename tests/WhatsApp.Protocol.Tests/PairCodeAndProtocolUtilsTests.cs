using System.Security.Cryptography;
using System.Text;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class PairCodeAndProtocolUtilsTests
{
    [TestMethod]
    public void PairCodeCrypto_Crockford_Derive_EncryptDecrypt_And_PrepareBundle_WorkCleanly()
    {
        Assert.AreEqual("11111111", PairCodeCrypto.EncodeCrockford([0, 0, 0, 0, 0]));
        Assert.AreEqual("ZZZZZZZZ", PairCodeCrypto.EncodeCrockford([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));

        string code = PairCodeCrypto.GenerateCode();
        Assert.AreEqual(8, code.Length);
        Assert.IsTrue(PairCodeCrypto.ValidateCode(code));
        Assert.IsFalse(PairCodeCrypto.ValidateCode("ABCD0123")); // contains 0 and I
        Assert.IsFalse(PairCodeCrypto.ValidateCode("ABCDOIUL"));

        var eph = Curve25519.GenerateKeyPair();
        byte[] wrapped = PairCodeCrypto.EncryptEphemeralPublicKey(eph.PublicKey, code);
        Assert.AreEqual(80, wrapped.Length);
        CollectionAssert.AreEqual(eph.PublicKey, PairCodeCrypto.DecryptPrimaryEphemeralPublicKey(wrapped, code));

        var primaryEph = Curve25519.GenerateKeyPair();
        var primaryId = Curve25519.GenerateKeyPair();
        var compId = Curve25519.GenerateKeyPair();
        WrappedKeyBundleResult bundle = PairCodeCrypto.PrepareKeyBundle(
            eph,
            primaryEph.PublicKey,
            primaryId.PublicKey,
            compId);
        Assert.AreEqual(32 + 12 + 96 + 16, bundle.WrappedBundle.Length);
        Assert.AreEqual(32, bundle.AdvSecretKey.Length);
    }

    [TestMethod]
    public void PairCodeCrypto_CompanionHelloAndFinish_UseCanonicalDisplayAndZeroNonce()
    {
        var props = new DeviceProps { Os = "CustomBrand", PlatformType = DeviceProps.Types.PlatformType.Edge };
        var (platformId, display) = PairCodeCrypto.ResolveCompanionPlatform(props);
        Assert.AreEqual(CompanionWebClientType.Edge, platformId);
        Assert.AreEqual("Edge (Linux)", display);

        var (winId, winDisplay) = PairCodeCrypto.ResolveCompanionPlatform(
            new DeviceProps { Os = "Windows 11", PlatformType = DeviceProps.Types.PlatformType.Chrome });
        Assert.AreEqual("Chrome (Windows)", winDisplay);

        byte[] noisePub = Enumerable.Repeat((byte)0x11, 32).ToArray();
        byte[] wrappedEph = Enumerable.Repeat((byte)0x22, 80).ToArray();
        Node helloIq = PairCodeCrypto.BuildCompanionHelloIq("iq-1", "15551234567", noisePub, wrappedEph, winId, winDisplay);
        Node reg = helloIq.GetChild("link_code_companion_reg")!;
        Assert.AreEqual("companion_hello", reg.GetAttr("stage"));
        byte[] nonce = ((NodeContent.BytesContent)reg.GetChild("link_code_pairing_nonce")!.Content!).Bytes;
        CollectionAssert.AreEqual(new byte[] { 0x00 }, nonce);

        byte[] refBytes = [0xDE, 0xAD, 0xBE, 0xEF];
        var respIq = new Node("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[]
        {
            new("link_code_companion_reg", new Node[] { new("link_code_pairing_ref", refBytes) })
        });
        CollectionAssert.AreEqual(refBytes, PairCodeCrypto.ParseCompanionHelloResponse(respIq));
    }

    [TestMethod]
    public void AbPropsIq_MediaConnParser_And_TcTokenProtocol_ParseSpecCompliantPayloads()
    {
        Node getProps = AbPropsIq.BuildGetProps("p-1", hash: "abc", refreshId: 42);
        Assert.AreEqual("abt", getProps.GetAttr("xmlns"));
        Assert.AreEqual("abc", getProps.GetChild("props")!.GetAttr("hash"));

        Node propsResp = new("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[]
        {
            new("props", new Dictionary<string, NodeValue>
            {
                ["ab_key"] = "key1",
                ["hash"] = "h2",
                ["refresh"] = "86400",
                ["refresh_id"] = "7",
                ["delta_update"] = "true"
            },
            new Node[]
            {
                new("prop", new Dictionary<string, NodeValue> { ["config_code"] = "5138", ["config_value"] = "true" }),
                new("prop", new Dictionary<string, NodeValue> { ["config_code"] = "999", ["config_value"] = "180" })
            })
        });
        AbPropsResponse ab = AbPropsIq.ParseResponse(propsResp);
        Assert.AreEqual("key1", ab.AbKey);
        Assert.IsTrue(ab.DeltaUpdate);
        Assert.AreEqual("true", ab.ExperimentProps[5138]);
        Assert.AreEqual("180", ab.ExperimentProps[999]);

        Node mcResp = new("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[]
        {
            new("media_conn", new Dictionary<string, NodeValue> { ["auth"] = "tok-xyz", ["ttl"] = "3600", ["max_buckets"] = "8" }, new Node[]
            {
                new("host", new Dictionary<string, NodeValue> { ["hostname"] = "mmg-fallback.whatsapp.net", ["type"] = "fallback" }),
                new("host", new Dictionary<string, NodeValue> { ["hostname"] = "mmg.whatsapp.net", ["type"] = "primary", ["fallback_hostname"] = "fb.mmg.whatsapp.net" },
                    new Node[] { new("upload"), new("download") })
            })
        });
        MediaConnInfo mc = MediaConnParser.ParseResponse(mcResp);
        Assert.AreEqual("tok-xyz", mc.Auth);
        Assert.AreEqual("mmg.whatsapp.net", mc.Hosts[0].Hostname);
        CollectionAssert.AreEqual(
            new[] { "mmg.whatsapp.net", "fb.mmg.whatsapp.net", "mmg-fallback.whatsapp.net" },
            mc.GetOrderedHosts().ToArray());

        // TcToken exact WA Web bucket cutoff arithmetic (bucket=604800, numBuckets=4 -> cutoff = (floor(now/604800) - 3) * 604800)
        long bucket = 604_800;
        long now = 10 * bucket + 1234;
        long cutoff = TcTokenProtocol.GetExpirationCutoffUnix(now);
        Assert.AreEqual(7 * bucket, cutoff);
        Assert.IsTrue(TcTokenProtocol.IsTokenExpired(7 * bucket - 1, now));
        Assert.IsFalse(TcTokenProtocol.IsTokenExpired(7 * bucket, now));
        Assert.IsFalse(TcTokenProtocol.ShouldSendNewToken(10 * bucket + 10, now));
        Assert.IsTrue(TcTokenProtocol.ShouldSendNewToken(9 * bucket + 10, now));
    }

    [TestMethod]
    public async Task AppStateKeyShareHelper_EnforcesSelfSenderCheck()
    {
        var creds = DeviceCredentials.CreateNew();
        creds.Pn = Jid.PnDevice("15550101001", 2);
        creds.Lid = Jid.LidDevice("100000000000101", 2);

        Assert.IsTrue(AppStateKeyShareHelper.IsSelfSender(Jid.Pn("15550101001"), creds));
        Assert.IsTrue(AppStateKeyShareHelper.IsSelfSender(Jid.LidDevice("100000000000101", 0), creds));
        Assert.IsFalse(AppStateKeyShareHelper.IsSelfSender(Jid.Pn("15559999999"), creds));
        Assert.IsFalse(AppStateKeyShareHelper.IsSelfSender(Jid.Lid("999999999999999"), creds));
        await Task.CompletedTask;
    }

    [TestMethod]
    public void LiveWeb_WawapAndWebCrypto_ExactWireByteParity()
    {
        // Live crypto.subtle PBKDF2-SHA256 (131,072) + AES-256-CTR captured from live web.whatsapp.com
        const string livePairCode = "ABCD2345";
        byte[] liveSalt = Enumerable.Repeat((byte)0x3A, 32).ToArray();
        byte[] liveIv = Enumerable.Repeat((byte)0x4B, 16).ToArray();
        byte[] liveEph = Enumerable.Repeat((byte)0x5C, 32).ToArray();
        byte[] liveWrappedExpected = Convert.FromHexString(
            "3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a" +
            "4b4b4b4b4b4b4b4b4b4b4b4b4b4b4b4b" +
            "3e1e7b6c32b6ed9c90ffa09ec79d98086c8d91e3e9a2e791870ba89ad9c762d4");

        byte[] csWrapped = PairCodeCrypto.EncryptEphemeralPublicKey(liveEph, livePairCode, liveSalt, liveIv);
        CollectionAssert.AreEqual(liveWrappedExpected, csWrapped);
        CollectionAssert.AreEqual(liveEph, PairCodeCrypto.DecryptPrimaryEphemeralPublicKey(liveWrappedExpected, livePairCode));

        // Live WAWap.encodeStanzaWithoutHeader captures from web.whatsapp.com:
        byte[] liveHelloBytes = Convert.FromHexString(
            "f80a1916fc026d64045a11fa000308fc096c6976652d70632d31f801f808fc176c696e6b5f636f64655f636f6d70616e696f6e5f726567" +
            "0cfc1a313535353132333435363740732e77686174736170702e6e6574ec8bfc0f636f6d70616e696f6e5f68656c6c6ffc1d73686f756c" +
            "645f73686f775f707573685f6e6f74696669636174696f6e9bf805f802fc316c696e6b5f636f64655f70616972696e675f777261707065" +
            "645f636f6d70616e696f6e5f657068656d6572616c5f707562fc50" + new string('2', 160) + "f802fc1d" +
            "636f6d70616e696f6e5f7365727665725f617574685f6b65795f707562fc20" + new string('1', 64) +
            "f802fc15636f6d70616e696f6e5f706c6174666f726d5f6964fc0131f802fc1a636f6d70616e696f6e5f706c617466" +
            "6f726d5f646973706c6179fc104368726f6d65202857696e646f777329f802fc176c696e6b5f636f64655f70616972696e675f6e6f6e6365" +
            "fc0100");

        Node liveHelloNode = new BinaryDecoder(liveHelloBytes).ReadNode();
        Assert.AreEqual("iq", liveHelloNode.Tag);
        Assert.AreEqual("live-pc-1", liveHelloNode.GetAttr("id"));
        Assert.AreEqual("md", liveHelloNode.GetAttr("xmlns"));
        Node? liveReg = liveHelloNode.GetChild("link_code_companion_reg");
        Assert.IsNotNull(liveReg);
        Assert.AreEqual("companion_hello", liveReg.GetAttr("stage"));
        Assert.AreEqual("true", liveReg.GetAttr("should_show_push_notification"));
        CollectionAssert.AreEqual(
            new byte[] { 0x00 },
            ((NodeContent.BytesContent)liveReg.GetChild("link_code_pairing_nonce")!.Content!).Bytes);

        byte[] liveMcBytes = Convert.FromHexString(
            "f8081906fa000308fc096c6976652d6d632d310414f801f8083aa9fc0d6c6976652d617574682d746f6bbdff02360063cff802f8057246" +
            "8604bcf80872468b045d2cfc1366622e6d6d672e77686174736170702e6e6574f802f80175f80158");

        MediaConnInfo liveMc = MediaConnParser.ParseResponse(new BinaryDecoder(liveMcBytes).ReadNode());
        Assert.AreEqual("live-auth-tok", liveMc.Auth);
        Assert.AreEqual(3600L, liveMc.TtlSeconds);
        Assert.AreEqual(12, liveMc.MaxBuckets);
        Assert.AreEqual(2, liveMc.Hosts.Count);
        Assert.AreEqual("mmg.whatsapp.net", liveMc.Hosts[0].Hostname);
        Assert.AreEqual("fb.mmg.whatsapp.net", liveMc.Hosts[0].FallbackHostname);
        Assert.IsTrue(liveMc.Hosts[0].SupportsUpload);
        Assert.IsTrue(liveMc.Hosts[0].SupportsDownload);
        Assert.AreEqual("mmg-fallback.whatsapp.net", liveMc.Hosts[1].Hostname);
        Assert.AreEqual(MediaHostKind.Fallback, liveMc.Hosts[1].Kind);

        byte[] livePropsBytes = Convert.FromHexString(
            "f8081906fa000308fc096c6976652d61622d310414f801f80e5cec0355ec9bfc076c6976652d61625ffc03683939ec32ff8386400fec" +
            "6bec79fc0c64656c74615f7570646174659bf802f8054f23eed220ff03604800f8054f23ff8324160f209b");

        AbPropsResponse liveProps = AbPropsIq.ParseResponse(new BinaryDecoder(livePropsBytes).ReadNode());
        Assert.AreEqual("live-ab", liveProps.AbKey);
        Assert.AreEqual("h99", liveProps.Hash);
        Assert.AreEqual(86400, liveProps.RefreshSeconds);
        Assert.AreEqual(5u, liveProps.RefreshId);
        Assert.IsTrue(liveProps.DeltaUpdate);
        Assert.AreEqual("604800", liveProps.ExperimentProps[865]);
        Assert.AreEqual("true", liveProps.ExperimentProps[24160]);

        byte[] liveTcBytes = Convert.FromHexString(
            "f8081906fa000308fc096c6976652d74632d310414f801f802ec44f801f808c50cfc13313030303030303030303030313031406c69641aff" +
            "05170000000004d9fc050102030405");

        IReadOnlyList<ParsedTcToken> liveTc = TcTokenProtocol.ParseTokensResponse(new BinaryDecoder(liveTcBytes).ReadNode());
        Assert.AreEqual(1, liveTc.Count);
        Assert.AreEqual("100000000000101@lid", liveTc[0].Jid?.ToString());
        Assert.AreEqual(1700000000L, liveTc[0].Timestamp);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, liveTc[0].Token);
    }
}
