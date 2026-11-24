using System.Security.Cryptography;
using System.Text;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.Noise;
using WhatsApp.WaProto;
using PbClientPayload = global::Whatsapp.ClientPayload;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class ScaffoldContractsTests
{
    [TestMethod]
    public void MediaCipher_RoundTrips_AllSupportedMediaTypes()
    {
        byte[] mediaKey = RandomNumberGenerator.GetBytes(32);
        byte[] plaintext = Encoding.UTF8.GetBytes("Sample AppState / Media / GroupHistory payload");

        foreach (WaMediaType mediaType in new[]
        {
            WaMediaType.History,
            WaMediaType.Image,
            WaMediaType.Video,
            WaMediaType.Audio,
            WaMediaType.Document,
            WaMediaType.AppState,
            WaMediaType.GroupHistory,
            WaMediaType.Sticker
        })
        {
            byte[] encrypted = MediaCipher.EncryptBlob(plaintext, mediaKey, mediaType);
            byte[] encSha = SHA256.HashData(encrypted);
            byte[] plainSha = SHA256.HashData(plaintext);

            byte[] decrypted = MediaCipher.DecryptBlob(encrypted, mediaKey, mediaType, encSha, plainSha);
            CollectionAssert.AreEqual(plaintext, decrypted, $"Failed round-trip for {mediaType}");
        }
    }

    [TestMethod]
    public void ClientPayloadFactory_BuildsValidLoginAndRegistrationPayloads()
    {
        var creds = DeviceCredentials.CreateNew("Test Device");
        Assert.IsFalse(creds.IsRegistered);
        Assert.AreEqual(32, creds.NoiseKey.PublicKey.Length);
        Assert.AreEqual(64, creds.SignedPreKeySignature.Length);
        Assert.IsTrue(XEdDSA.Verify(creds.IdentityKey.PublicKey, [5, .. creds.SignedPreKey.PublicKey], creds.SignedPreKeySignature));

        byte[] regBytes = ClientPayloadFactory.BuildRegistrationPayload(
            creds.RegistrationId,
            creds.IdentityKey.PublicKey,
            creds.SignedPreKeyId,
            creds.SignedPreKey.PublicKey,
            creds.SignedPreKeySignature,
            pushName: creds.PushName);

        var parsedReg = PbClientPayload.Parser.ParseFrom(regBytes);
        Assert.IsFalse(parsedReg.Pull);
        Assert.AreEqual("Test Device", parsedReg.PushName);
        Assert.IsNotNull(parsedReg.DevicePairingData);

        byte[] loginBytes = ClientPayloadFactory.BuildLoginPayload(15550101002UL, deviceId: 12);
        var parsedLogin = PbClientPayload.Parser.ParseFrom(loginBytes);
        Assert.AreEqual(15550101002UL, parsedLogin.Username);
        Assert.AreEqual(12u, parsedLogin.Device);
        Assert.IsTrue(parsedLogin.Pull);
        Assert.IsFalse(parsedLogin.HasPushName);
    }
}
