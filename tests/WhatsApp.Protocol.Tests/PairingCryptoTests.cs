using System.Security.Cryptography;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class PairingCryptoTests
{
    private static DeviceCredentials CreateTestDevice() => DeviceCredentials.CreateNew();

    private static byte[] BuildSignedPayload(
        DeviceCredentials companion,
        byte[] advSecretForHmac,
        ADVEncryptionType? outerAccountType,
        ADVDeviceIdentity details,
        byte[]? accountPrefixOverride = null,
        KeyPair? accountKeyPairOverride = null,
        bool injectUnknownField = true)
    {
        KeyPair accountKp = accountKeyPairOverride ?? Curve25519.GenerateKeyPair();
        byte[] innerDetails = details.ToByteArray();
        if (injectUnknownField)
        {
            // Preserve unknown signed fields byte-for-byte, matching Rust pair.rs tests.
            byte[] tagAndVal = [0xa0, 0x06, 0x01];
            innerDetails = [.. tagAndVal, .. innerDetails];
        }

        byte[] prefix = accountPrefixOverride ??
            (details.HasDeviceType && details.DeviceType == ADVEncryptionType.Hosted
                ? new byte[] { 0x06, 0x05 }
                : new byte[] { 0x06, 0x00 });

        byte[] msgToSign = [.. prefix, .. innerDetails, .. companion.IdentityKey.PublicKey];
        byte[] accountSig = XEdDSA.Sign(accountKp.PrivateKey, msgToSign);

        var signed = new ADVSignedDeviceIdentity
        {
            Details = ByteString.CopyFrom(innerDetails),
            AccountSignatureKey = ByteString.CopyFrom(accountKp.PublicKey),
            AccountSignature = ByteString.CopyFrom(accountSig),
        };

        return WrapHmac(advSecretForHmac, outerAccountType, signed.ToByteArray());
    }

    private static byte[] WrapHmac(
        byte[] advSecret,
        ADVEncryptionType? accountType,
        byte[] signedIdentityBytes)
    {
        using var hmac = new HMACSHA256(advSecret);
        if (accountType == ADVEncryptionType.Hosted)
        {
            byte[] p = [0x06, 0x05];
            hmac.TransformBlock(p, 0, p.Length, null, 0);
        }
        hmac.TransformFinalBlock(signedIdentityBytes, 0, signedIdentityBytes.Length);

        var container = new ADVSignedDeviceIdentityHMAC
        {
            Details = ByteString.CopyFrom(signedIdentityBytes),
            Hmac = ByteString.CopyFrom(hmac.Hash!)
        };
        if (accountType.HasValue)
        {
            container.AccountType = accountType.Value;
        }
        return container.ToByteArray();
    }

    [TestMethod]
    public void MakeQrData_HasFiveFields_WithChromeClientTypeSuffix()
    {
        var state = CreateTestDevice();
        string qr = PairingCrypto.MakeQrData(state, "the-ref", CompanionWebClientType.Chrome);
        string[] parts = qr.Split(',');
        Assert.AreEqual(5, parts.Length);
        Assert.AreEqual("the-ref", parts[0]);
        Assert.AreEqual("1", parts[4]);
    }

    [TestMethod]
    public void MakeQrData_RendersAndRoundTripsEveryClientType()
    {
        var state = CreateTestDevice();
        var cases = new (CompanionWebClientType ClientType, string Wire)[]
        {
            (CompanionWebClientType.Chrome, "1"),
            (CompanionWebClientType.Edge, "2"),
            (CompanionWebClientType.Firefox, "3"),
            (CompanionWebClientType.Ie, "4"),
            (CompanionWebClientType.Opera, "5"),
            (CompanionWebClientType.Safari, "6"),
            (CompanionWebClientType.Electron, "7"),
            (CompanionWebClientType.Uwp, "8"),
            (CompanionWebClientType.OtherWebClient, "9"),
            (CompanionWebClientType.AndroidTablet, "d"),
            (CompanionWebClientType.AndroidPhone, "e"),
            (CompanionWebClientType.AndroidAmbiguous, "f"),
        };

        foreach (var (ct, expectedWire) in cases)
        {
            string qr = PairingCrypto.MakeQrData(state, "the-ref", ct);
            Assert.AreEqual(expectedWire, qr.Split(',').Last(), ct.ToString());

            var parsed = PairingCrypto.ParseQrData(qr);
            Assert.AreEqual("the-ref", parsed.PairingRef);
            CollectionAssert.AreEqual(state.NoiseKey.PublicKey, parsed.NoisePublicKey);
            CollectionAssert.AreEqual(state.IdentityKey.PublicKey, parsed.IdentityPublicKey);
        }
    }

    [TestMethod]
    public void ClientTypeForProps_MatchesWhatsAppWebAndRustMapping()
    {
        var cases = new (DeviceProps.Types.PlatformType Platform, CompanionWebClientType Expected)[]
        {
            (DeviceProps.Types.PlatformType.Chrome, CompanionWebClientType.Chrome),
            (DeviceProps.Types.PlatformType.Firefox, CompanionWebClientType.Firefox),
            (DeviceProps.Types.PlatformType.Safari, CompanionWebClientType.Safari),
            (DeviceProps.Types.PlatformType.Edge, CompanionWebClientType.Edge),
            (DeviceProps.Types.PlatformType.Desktop, CompanionWebClientType.Electron),
            (DeviceProps.Types.PlatformType.Uwp, CompanionWebClientType.Uwp),
            (DeviceProps.Types.PlatformType.AndroidPhone, CompanionWebClientType.Chrome),
            (DeviceProps.Types.PlatformType.AndroidTablet, CompanionWebClientType.Chrome),
            (DeviceProps.Types.PlatformType.AndroidAmbiguous, CompanionWebClientType.Chrome),
            (DeviceProps.Types.PlatformType.IosPhone, CompanionWebClientType.OtherWebClient),
            (DeviceProps.Types.PlatformType.Vr, CompanionWebClientType.OtherWebClient),
            (DeviceProps.Types.PlatformType.Unknown, CompanionWebClientType.OtherWebClient),
        };

        foreach (var (platform, expected) in cases)
        {
            var props = new DeviceProps { PlatformType = platform };
            Assert.AreEqual(expected, PairingCrypto.ClientTypeForProps(props), platform.ToString());
        }

        Assert.AreEqual(CompanionWebClientType.OtherWebClient, PairingCrypto.ClientTypeForProps(new DeviceProps()));
        Assert.AreEqual(CompanionWebClientType.OtherWebClient, PairingCrypto.ClientTypeForProps(null));
    }

    [TestMethod]
    public void ParseQrData_AcceptsLegacyDeepLinkAndFaqSuffix_AndRejectsInvalidInputs()
    {
        var state = CreateTestDevice();
        string modern = PairingCrypto.MakeQrData(state, "r", CompanionWebClientType.Chrome);

        // Legacy 4-field format
        string legacy = string.Join(",", modern.Split(',').Take(4));
        Assert.AreEqual("r", PairingCrypto.ParseQrData(legacy).PairingRef);

        // Deep link prefix
        string prefixed = PairingCrypto.NativeCameraDeepLinkPrefix + modern;
        Assert.AreEqual("r", PairingCrypto.ParseQrData(prefixed).PairingRef);

        // FAQ suffix
        string suffixed = $"{modern},https://faq.whatsapp.com/r/ld";
        Assert.AreEqual("r", PairingCrypto.ParseQrData(suffixed).PairingRef);

        // Rejects too few fields, empty fields, trailing empty, bad base64, wrong key length
        Assert.ThrowsException<FormatException>(() => PairingCrypto.ParseQrData("a,b,c"));
        Assert.ThrowsException<FormatException>(() => PairingCrypto.ParseQrData(",,,,"));
        Assert.ThrowsException<FormatException>(() => PairingCrypto.ParseQrData(legacy + ","));
        Assert.ThrowsException<FormatException>(() => PairingCrypto.ParseQrData("ref,!!notb64!!,!!notb64!!,adv"));

        string shortNoise = Convert.ToBase64String(new byte[16]);
        string idB64 = Convert.ToBase64String(state.IdentityKey.PublicKey);
        string advB64 = Convert.ToBase64String(state.AdvSecretKey);
        Assert.ThrowsException<FormatException>(() => PairingCrypto.ParseQrData($"ref,{shortNoise},{idB64},{advB64}"));
    }

    [TestMethod]
    public void VerifyAndSignDeviceIdentity_AccountAndDeviceTypesAreIndependent()
    {
        var state = CreateTestDevice();
        ADVEncryptionType?[] types = [null, ADVEncryptionType.E2Ee, ADVEncryptionType.Hosted, ADVEncryptionType.NonE2Ee];

        foreach (var outer in types)
        foreach (var accountType in types)
        foreach (var deviceType in types)
        {
            var details = new ADVDeviceIdentity { KeyIndex = 42 };
            if (accountType.HasValue) details.AccountType = accountType.Value;
            if (deviceType.HasValue) details.DeviceType = deviceType.Value;

            byte[] payload = BuildSignedPayload(state, state.AdvSecretKey, outer, details);
            var (result, index) = PairingCrypto.VerifyAndSignDeviceIdentity(state, payload);
            Assert.AreEqual(42u, index);

            // Flipping outer accountType must break HMAC check with 401 hmac-mismatch
            var tamperedOuter = ADVSignedDeviceIdentityHMAC.Parser.ParseFrom(payload);
            tamperedOuter.AccountType = outer == ADVEncryptionType.Hosted
                ? ADVEncryptionType.E2Ee
                : ADVEncryptionType.Hosted;
            var hmacEx = Assert.ThrowsException<PairingException>(() =>
                PairingCrypto.VerifyAndSignDeviceIdentity(state, tamperedOuter.ToByteArray()));
            Assert.AreEqual(401, hmacEx.Code);
            Assert.AreEqual("hmac-mismatch", hmacEx.ErrorText);

            var orig = ADVSignedDeviceIdentity.Parser.ParseFrom(
                ADVSignedDeviceIdentityHMAC.Parser.ParseFrom(payload).Details);
            var signed = ADVSignedDeviceIdentity.Parser.ParseFrom(result);
            Assert.AreEqual(orig.Details, signed.Details);
            Assert.AreEqual(orig.AccountSignature, signed.AccountSignature);
            Assert.AreEqual(orig.AccountSignatureKey, signed.AccountSignatureKey);
            Assert.IsTrue(PairingCrypto.ValidateSignedDeviceIdentity(result, state.IdentityKey.PublicKey));
        }
    }

    [TestMethod]
    public void VerifyAndSignDeviceIdentity_PreservesUnknownDetailsBytesWhenLarge()
    {
        var state = CreateTestDevice();
        var accountKp = Curve25519.GenerateKeyPair();
        byte[] baseDetails = new ADVDeviceIdentity
        {
            KeyIndex = 0,
            DeviceType = ADVEncryptionType.Hosted
        }.ToByteArray();

        // Unknown field 100 with 512 bytes (exact mirror of Rust pair.rs spill test)
        byte[] largeDetails = [.. baseDetails, 0xa2, 0x06, 0x80, 0x04, .. Enumerable.Repeat((byte)0xAB, 512)];
        byte[] payload = WrapHmac(
            state.AdvSecretKey,
            null,
            new ADVSignedDeviceIdentity
            {
                Details = ByteString.CopyFrom(largeDetails),
                AccountSignatureKey = ByteString.CopyFrom(accountKp.PublicKey),
                AccountSignature = ByteString.CopyFrom(XEdDSA.Sign(
                    accountKp.PrivateKey,
                    [0x06, 0x05, .. largeDetails, .. state.IdentityKey.PublicKey]))
            }.ToByteArray());

        var (result, index) = PairingCrypto.VerifyAndSignDeviceIdentity(state, payload);
        Assert.AreEqual(0u, index);

        var signed = ADVSignedDeviceIdentity.Parser.ParseFrom(result);
        CollectionAssert.AreEqual(largeDetails, signed.Details.ToByteArray());
        Assert.IsTrue(PairingCrypto.ValidateSignedDeviceIdentity(result, state.IdentityKey.PublicKey));
    }

    [TestMethod]
    public void VerifyAndSignDeviceIdentity_RejectsCorruptedOrForgedIdentities()
    {
        var state = CreateTestDevice();
        foreach (bool hosted in new[] { false, true })
        {
            var encType = hosted ? ADVEncryptionType.Hosted : ADVEncryptionType.E2Ee;
            byte[] payload = BuildSignedPayload(
                state,
                state.AdvSecretKey,
                encType,
                new ADVDeviceIdentity
                {
                    RawId = 1,
                    Timestamp = 0,
                    KeyIndex = 0,
                    AccountType = encType,
                    DeviceType = encType
                });
            var container = ADVSignedDeviceIdentityHMAC.Parser.ParseFrom(payload);
            var original = ADVSignedDeviceIdentity.Parser.ParseFrom(container.Details);

            // 1. Corrupt signature -> 401 signature-mismatch
            var badSig = original.Clone();
            byte[] s = badSig.AccountSignature.ToByteArray();
            s[0] ^= 1;
            badSig.AccountSignature = ByteString.CopyFrom(s);
            AssertPairingError(state, WrapHmac(state.AdvSecretKey, container.AccountType, badSig.ToByteArray()), 401, "signature-mismatch");

            // 2. Corrupt details -> 401 signature-mismatch
            var badDetails = original.Clone();
            byte[] d = badDetails.Details.ToByteArray();
            d[2] ^= 1;
            badDetails.Details = ByteString.CopyFrom(d);
            AssertPairingError(state, WrapHmac(state.AdvSecretKey, container.AccountType, badDetails.ToByteArray()), 401, "signature-mismatch");

            // 3. Invalid key length -> 401 invalid-key
            var badKeyLen = original.Clone();
            badKeyLen.AccountSignatureKey = ByteString.CopyFrom(new byte[31]);
            AssertPairingError(state, WrapHmac(state.AdvSecretKey, container.AccountType, badKeyLen.ToByteArray()), 401, "invalid-key");

            // 4. Missing signature -> 401 signature-mismatch
            var missingSig = original.Clone();
            missingSig.ClearAccountSignature();
            AssertPairingError(state, WrapHmac(state.AdvSecretKey, container.AccountType, missingSig.ToByteArray()), 401, "signature-mismatch");

            // 5. Malformed details -> 500 internal-error
            var malformedDetails = original.Clone();
            malformedDetails.Details = ByteString.CopyFrom([0xff]);
            AssertPairingError(state, WrapHmac(state.AdvSecretKey, container.AccountType, malformedDetails.ToByteArray()), 500, "internal-error");

            // 6. Missing details -> 500 internal-error
            var missingDetails = original.Clone();
            missingDetails.ClearDetails();
            AssertPairingError(state, WrapHmac(state.AdvSecretKey, container.AccountType, missingDetails.ToByteArray()), 500, "internal-error");

            // 7. Substituted account key -> 401 signature-mismatch
            var subKey = original.Clone();
            subKey.AccountSignatureKey = ByteString.CopyFrom(Curve25519.GenerateKeyPair().PublicKey);
            AssertPairingError(state, WrapHmac(state.AdvSecretKey, container.AccountType, subKey.ToByteArray()), 401, "signature-mismatch");

            // 8. Companion identity substitution -> 401 signature-mismatch
            var otherCompanion = CreateTestDevice();
            otherCompanion.AdvSecretKey = state.AdvSecretKey;
            AssertPairingError(otherCompanion, payload, 401, "signature-mismatch");
        }
    }

    [TestMethod]
    public void VerifyAndSignDeviceIdentity_DoesNotTryAlternateAccountPrefix()
    {
        var state = CreateTestDevice();
        foreach (var outer in new[] { ADVEncryptionType.E2Ee, ADVEncryptionType.Hosted })
        foreach (var deviceType in new[] { ADVEncryptionType.E2Ee, ADVEncryptionType.Hosted })
        {
            byte[] wrongPrefix = deviceType == ADVEncryptionType.Hosted
                ? [0x06, 0x00]
                : [0x06, 0x05];

            byte[] payload = BuildSignedPayload(
                state,
                state.AdvSecretKey,
                outer,
                new ADVDeviceIdentity { KeyIndex = 0, DeviceType = deviceType },
                accountPrefixOverride: wrongPrefix,
                injectUnknownField: false);

            AssertPairingError(state, payload, 401, "signature-mismatch");
        }
    }

    [TestMethod]
    public void VerifyAndSignDeviceIdentity_AuthenticatesHmacBeforeDecodingInnerIdentity()
    {
        var state = CreateTestDevice();
        foreach (ADVEncryptionType? accountType in new ADVEncryptionType?[] { null, ADVEncryptionType.E2Ee, ADVEncryptionType.Hosted })
        {
            byte[] payload = WrapHmac(state.AdvSecretKey, accountType, [0xff]);
            AssertPairingError(state, payload, 500, "internal-error");

            var container = ADVSignedDeviceIdentityHMAC.Parser.ParseFrom(payload);
            byte[] badMac = container.Hmac.ToByteArray();
            badMac[0] ^= 1;
            container.Hmac = ByteString.CopyFrom(badMac);
            AssertPairingError(state, container.ToByteArray(), 401, "hmac-mismatch");
        }
    }

    [TestMethod]
    public void VerifyAndSignDeviceIdentity_RejectsMissingKeyIndex()
    {
        var state = CreateTestDevice();
        byte[] payload = BuildSignedPayload(
            state,
            state.AdvSecretKey,
            ADVEncryptionType.E2Ee,
            new ADVDeviceIdentity { RawId = 1, Timestamp = 0 });

        AssertPairingError(state, payload, 500, "internal-error");
    }

    [TestMethod]
    public void Stanzas_PasskeyMetadataAndClientProps_ParseAccurately()
    {
        var state = CreateTestDevice();
        byte[] diPayload = BuildSignedPayload(
            state,
            state.AdvSecretKey,
            ADVEncryptionType.E2Ee,
            new ADVDeviceIdentity { RawId = 1, KeyIndex = 5, DeviceType = ADVEncryptionType.E2Ee });

        var clientProps = new ClientPairingProps { IsChatDbLidMigrated = true };

        var iq = new Node(
            "iq",
            new Dictionary<string, NodeValue> { ["id"] = "pair-1", ["type"] = "set" },
            new Node[]
            {
                new Node("pair-success", new Node[]
                {
                    new Node("jurisdiction", new Dictionary<string, NodeValue> { ["iso"] = "BR", ["cc"] = "55" }),
                    new Node("encryption-metadata", new Dictionary<string, NodeValue> { ["algorithm"] = "aes-256-gcm" },
                        new Node[]
                        {
                            new Node("encrypted_key", new byte[48]),
                            new Node("nonce", new byte[12]),
                            new Node("encrypted_data", new byte[128]),
                            new Node("auth_tag", new byte[16]),
                        }),
                    new Node("client-props", clientProps.ToByteArray()),
                    new Node("platform", new Dictionary<string, NodeValue> { ["name"] = "android" }),
                    new Node("device-identity", diPayload),
                    new Node("device", new Dictionary<string, NodeValue>
                    {
                        ["jid"] = "5511999999999:57@s.whatsapp.net",
                        ["lid"] = "1234567890:57@lid"
                    }),
                    new Node("biz", new Dictionary<string, NodeValue> { ["name"] = "Test Phone" })
                })
            });

        Assert.IsTrue(PairingStanzas.TryParsePairSuccess(iq, out var parsed, out string err), err);
        Assert.IsNotNull(parsed);
        Assert.AreEqual("pair-1", parsed.RequestId);
        Assert.AreEqual("5511999999999:57@s.whatsapp.net", parsed.DevicePnJid?.ToString());
        Assert.AreEqual("1234567890:57@lid", parsed.DeviceLidJid?.ToString());
        Assert.AreEqual("Test Phone", parsed.PushName);
        Assert.IsTrue(parsed.ClientProps?.IsChatDbLidMigrated);

        var (signedBytes, keyIndex) = PairingCrypto.VerifyAndSignDeviceIdentity(state, parsed.DeviceIdentityBytes);
        Assert.AreEqual(5u, keyIndex);
        Assert.IsTrue(PairingCrypto.ValidateSignedDeviceIdentity(signedBytes, state.IdentityKey.PublicKey));
    }

    [TestMethod]
    public void ExtractPairingProps_AndLidMigratedUpdate_MatchRustContract()
    {
        var bare = new Node("pair-success");
        Assert.IsNull(PairingStanzas.ExtractPairingProps(bare));

        var malformed = new Node("pair-success", new Node[] { new Node("client-props", new byte[] { 0xFF, 0xFF, 0xFF }) });
        Assert.IsNull(PairingStanzas.ExtractPairingProps(malformed));

        var explicitFalse = new Node("pair-success", new Node[]
        {
            new Node("client-props", new ClientPairingProps { IsChatDbLidMigrated = false }.ToByteArray())
        });
        var decodedFalse = PairingStanzas.ExtractPairingProps(explicitFalse);
        Assert.IsNotNull(decodedFalse);
        Assert.IsFalse(decodedFalse.IsChatDbLidMigrated);

        Assert.AreEqual(true, PairingCrypto.ComputeLidMigratedUpdate(true, false));
        Assert.AreEqual(true, PairingCrypto.ComputeLidMigratedUpdate(true, true));
        Assert.AreEqual(false, PairingCrypto.ComputeLidMigratedUpdate(false, true));
        Assert.IsNull(PairingCrypto.ComputeLidMigratedUpdate(false, false));
    }

    private static void AssertPairingError(DeviceCredentials state, byte[] payload, int expectedCode, string expectedText)
    {
        var ex = Assert.ThrowsException<PairingException>(() =>
            PairingCrypto.VerifyAndSignDeviceIdentity(state, payload));
        Assert.AreEqual(expectedCode, ex.Code);
        Assert.AreEqual(expectedText, ex.ErrorText);
    }
}
