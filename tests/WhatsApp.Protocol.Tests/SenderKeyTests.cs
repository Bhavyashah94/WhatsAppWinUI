using System.Security.Cryptography;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class SenderKeyTests
{
    [TestMethod]
    public void WireMessages_RoundTripCleanly()
    {
        byte[] chain = Enumerable.Repeat((byte)0x41, 32).ToArray();
        byte[] signPub = Enumerable.Repeat((byte)0x42, 32).ToArray();
        var skdm = new SenderKeyDistributionMessage
        {
            Id = 987654,
            Iteration = 3,
            ChainKey = chain,
            SigningKey = signPub
        };

        byte[] skdmWire = skdm.Serialize();
        Assert.AreEqual(0x33, skdmWire[0]);
        var parsedSkdm = SenderKeyDistributionMessage.Parse(skdmWire);
        Assert.AreEqual(skdm.Id, parsedSkdm.Id);
        Assert.AreEqual(skdm.Iteration, parsedSkdm.Iteration);
        CollectionAssert.AreEqual(chain, parsedSkdm.ChainKey);
        CollectionAssert.AreEqual(signPub, parsedSkdm.SigningKey);

        byte[] sig = Enumerable.Repeat((byte)0x7F, 64).ToArray();
        var skmsg = new SenderKeyMessage
        {
            Id = 987654,
            Iteration = 4,
            Ciphertext = [1, 2, 3, 4, 5]
        };
        byte[] skmsgWire = skmsg.Serialize(sig);
        Assert.AreEqual(0x33, skmsgWire[0]);
        var parsedMsg = SenderKeyMessage.Parse(skmsgWire);
        Assert.AreEqual(skmsg.Id, parsedMsg.Id);
        Assert.AreEqual(skmsg.Iteration, parsedMsg.Iteration);
        CollectionAssert.AreEqual(skmsg.Ciphertext, parsedMsg.Ciphertext);
        CollectionAssert.AreEqual(sig, parsedMsg.Signature);
    }

    [TestMethod]
    public void GroupMessaging_RoundTripOutOfOrderTamperedSignatureAndReplay()
    {
        var aliceSenderRecord = new SenderKeyRecord();
        var bobReceiverRecord = new SenderKeyRecord();
        var charlieReceiverRecord = new SenderKeyRecord();

        // Alice creates distribution and sends to Bob & Charlie
        byte[] skdmBytes = SenderKeyCipher.CreateDistribution(aliceSenderRecord);
        SenderKeyCipher.ProcessDistribution(bobReceiverRecord, skdmBytes);
        SenderKeyCipher.ProcessDistribution(charlieReceiverRecord, skdmBytes);

        byte[] g0 = SenderKeyCipher.Encrypt(aliceSenderRecord, "group-msg-0"u8);
        byte[] g1 = SenderKeyCipher.Encrypt(aliceSenderRecord, "group-msg-1"u8);
        byte[] g2 = SenderKeyCipher.Encrypt(aliceSenderRecord, "group-msg-2"u8);
        byte[] g3 = SenderKeyCipher.Encrypt(aliceSenderRecord, "group-msg-3"u8);

        // Bob receives in order
        CollectionAssert.AreEqual("group-msg-0"u8.ToArray(), SenderKeyCipher.Decrypt(bobReceiverRecord, g0));
        CollectionAssert.AreEqual("group-msg-1"u8.ToArray(), SenderKeyCipher.Decrypt(bobReceiverRecord, g1));

        // Charlie receives out-of-order (#3 first, then #1, #0, #2)
        CollectionAssert.AreEqual("group-msg-3"u8.ToArray(), SenderKeyCipher.Decrypt(charlieReceiverRecord, g3));

        // Tampered XEdDSA signature must throw before consuming chain/skipped state
        byte[] tamperedG1 = (byte[])g1.Clone();
        tamperedG1[^1] ^= 0x55;
        Assert.ThrowsException<CryptographicException>(() => SenderKeyCipher.Decrypt(charlieReceiverRecord, tamperedG1));

        CollectionAssert.AreEqual("group-msg-1"u8.ToArray(), SenderKeyCipher.Decrypt(charlieReceiverRecord, g1));
        CollectionAssert.AreEqual("group-msg-0"u8.ToArray(), SenderKeyCipher.Decrypt(charlieReceiverRecord, g0));
        CollectionAssert.AreEqual("group-msg-2"u8.ToArray(), SenderKeyCipher.Decrypt(charlieReceiverRecord, g2));

        // Replay of g1 must fail on Charlie
        Assert.ThrowsException<CryptographicException>(() => SenderKeyCipher.Decrypt(charlieReceiverRecord, g1));

        // Re-delivering the exact same SKDM bytes later must NOT reset Bob's iteration back to 0!
        SenderKeyCipher.ProcessDistribution(bobReceiverRecord, skdmBytes);
        Assert.AreEqual(2u, bobReceiverRecord.States[0].Iteration);
        CollectionAssert.AreEqual("group-msg-2"u8.ToArray(), SenderKeyCipher.Decrypt(bobReceiverRecord, g2));
    }

    [TestMethod]
    public void KeyRotation_RetainsUpToMaxStatesAndFutureJumpIsBounded()
    {
        var sender = new SenderKeyRecord();
        var receiver = new SenderKeyRecord();

        byte[] dist1 = SenderKeyCipher.CreateDistribution(sender);
        SenderKeyCipher.ProcessDistribution(receiver, dist1);
        byte[] oldMsg = SenderKeyCipher.Encrypt(sender, "before-rotation"u8);

        // Simulate 5 key rotations (sender rotates out, receiver keeps MaxStates = 5)
        for (int i = 0; i < SenderKeyRecord.MaxStates; i++)
        {
            byte[] nextDist = SenderKeyCipher.RotateAndCreateDistribution(sender);
            SenderKeyCipher.ProcessDistribution(receiver, nextDist);
        }

        Assert.AreEqual(SenderKeyRecord.MaxStates, receiver.States.Count);
        Assert.ThrowsException<KeyNotFoundException>(() => SenderKeyCipher.Decrypt(receiver, oldMsg));

        // Latest rotated key works cleanly
        byte[] latestMsg = SenderKeyCipher.Encrypt(sender, "after-rotation"u8);
        CollectionAssert.AreEqual("after-rotation"u8.ToArray(), SenderKeyCipher.Decrypt(receiver, latestMsg));
    }
}
