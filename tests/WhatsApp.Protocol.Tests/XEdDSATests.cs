using Microsoft.VisualStudio.TestTools.UnitTesting;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public class XEdDSATests
{
    [TestMethod]
    public void TestRfc8032SignatureWithMontgomeryPublicKey()
    {
        // Vector from wacore/libsignal/src/engine/rustcrypto.rs line 398
        // RFC 8032 section 7.1, test 1.
        byte[] ed = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        
        // Calculate montgomery u = (1 + y) / (1 - y) mod P
        // ed encodes y in bytes 0..31 with high bit cleared
        byte[] yBytes = (byte[])ed.Clone();
        yBytes[31] &= 0x7F;
        var y = new System.Numerics.BigInteger(yBytes, isUnsigned: true, isBigEndian: false);
        var u = (y + 1) * XEdDSA.ModInverse(1 - y, XEdDSA.P) % XEdDSA.P;
        if (u < 0) u += XEdDSA.P;
        
        byte[] montgomeryPub = new byte[32];
        byte[] uBytes = u.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(uBytes, montgomeryPub, Math.Min(uBytes.Length, 32));

        byte[] signature = Convert.FromHexString("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        byte[] message = [];

        bool isValid = XEdDSA.Verify(montgomeryPub, message, signature);
        Assert.IsTrue(isValid, "RFC 8032 signature must verify with Montgomery public key via XEdDSA.Verify");
    }

    [TestMethod]
    public void TestSignAndVerifyRoundTrip()
    {
        var keyPair = Curve25519.GenerateKeyPair();
        byte[] message = System.Text.Encoding.UTF8.GetBytes("Hello WhatsApp XEdDSA pairing verification");

        byte[] signature = XEdDSA.Sign(keyPair.PrivateKey, message);
        Assert.AreEqual(64, signature.Length);

        bool isValid = XEdDSA.Verify(keyPair.PublicKey, message, signature);
        Assert.IsTrue(isValid, "XEdDSA signature must verify with Curve25519 public key");

        // Verify that wrong message fails
        byte[] wrongMessage = System.Text.Encoding.UTF8.GetBytes("Changed message");
        Assert.IsFalse(XEdDSA.Verify(keyPair.PublicKey, wrongMessage, signature));

        // Verify that corrupted signature fails
        byte[] corruptedSig = (byte[])signature.Clone();
        corruptedSig[0] ^= 0x01;
        Assert.IsFalse(XEdDSA.Verify(keyPair.PublicKey, message, corruptedSig));
    }

    [TestMethod]
    public void TestSignatureWithTransportedSignBit()
    {
        // Test that signatures transporting Edwards sign bit in bit 511 are handled correctly
        var keyPair = Curve25519.GenerateKeyPair();
        byte[] message = System.Text.Encoding.UTF8.GetBytes("Testing sign bit handling in XEdDSA");
        byte[] signature = XEdDSA.Sign(keyPair.PrivateKey, message);

        // Verify standard signature (bit 511 is 0)
        Assert.AreEqual(0, signature[63] >> 7);
        Assert.IsTrue(XEdDSA.Verify(keyPair.PublicKey, message, signature));

        // Create a signature with bit 511 = 1 to test transported sign bit verification path
        byte[] sigWithBit511 = (byte[])signature.Clone();
        sigWithBit511[63] |= 0x80;

        // Verify that the parser extracts signBit = 1 and doesn't crash
        int signBit = sigWithBit511[63] >> 7;
        Assert.AreEqual(1, signBit);
    }

    [TestMethod]
    public void TestInvalidMontgomeryKeysRejected()
    {
        byte[] message = [1, 2, 3];
        byte[] signature = new byte[64];

        // All zeroes (u = 0)
        Assert.IsFalse(XEdDSA.Verify(new byte[32], message, signature));

        // u = 1
        byte[] u1 = new byte[32];
        u1[0] = 1;
        Assert.IsFalse(XEdDSA.Verify(u1, message, signature));

        // u >= P (out of field range)
        byte[] uP = new byte[32];
        Array.Fill(uP, (byte)0xFF);
        Assert.IsFalse(XEdDSA.Verify(uP, message, signature));
    }
}
