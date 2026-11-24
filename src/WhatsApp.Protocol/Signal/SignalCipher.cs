using System.Security.Cryptography;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Signal;

/// <summary>
/// Signal Protocol v3 X3DH and Double Ratchet cipher operations.
/// Implements bidirectional session setup, outbound encryption, and inbound decryption with zero-alloc chain stepping.
/// </summary>
public static class SignalCipher
{
    private static readonly byte[] WhisperTextInfo = "WhisperText"u8.ToArray();
    private static readonly byte[] WhisperRatchetInfo = "WhisperRatchet"u8.ToArray();
    private static readonly byte[] WhisperMessageKeysInfo = "WhisperMessageKeys"u8.ToArray();
    private static readonly byte[] ZeroSalt32 = new byte[32];

    /// <summary>
    /// Performs X3DH initiator key agreement against a recipient's <see cref="PreKeyBundle"/>,
    /// verifies the signed pre-key XEdDSA signature, performs the initial Double Ratchet step,
    /// and returns a ready-to-encrypt <see cref="SignalSession"/>.
    /// </summary>
    public static SignalSession InitiateSession(
        PreKeyBundle bundle,
        KeyPair localIdentity,
        uint localRegistrationId,
        KeyPair? baseKeyOverride = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ValidateKey32(bundle.IdentityKey, nameof(bundle.IdentityKey));
        ValidateKey32(bundle.SignedPreKeyPublic, nameof(bundle.SignedPreKeyPublic));

        if (!XEdDSA.Verify(
            bundle.IdentityKey,
            PreKeySignalMessage.TagKey(bundle.SignedPreKeyPublic),
            bundle.SignedPreKeySignature))
        {
            throw new CryptographicException("Recipient signed pre-key XEdDSA signature verification failed.");
        }

        if (bundle.PreKeyPublic is not null)
        {
            ValidateKey32(bundle.PreKeyPublic, nameof(bundle.PreKeyPublic));
        }

        KeyPair baseKey = baseKeyOverride ?? Curve25519.GenerateKeyPair();
        bool hasOneTimeKey = bundle.PreKeyPublic is not null && bundle.PreKeyId.HasValue;

        // X3DH Initiator:
        // DH1 = DH(IK_A, SPK_B), DH2 = DH(EK_A, IK_B), DH3 = DH(EK_A, SPK_B), DH4 = DH(EK_A, OPK_B)
        byte[] dh1 = Curve25519.CalculateSharedSecret(localIdentity.PrivateKey, bundle.SignedPreKeyPublic);
        byte[] dh2 = Curve25519.CalculateSharedSecret(baseKey.PrivateKey, bundle.IdentityKey);
        byte[] dh3 = Curve25519.CalculateSharedSecret(baseKey.PrivateKey, bundle.SignedPreKeyPublic);

        int inputLen = 32 + 32 + 32 + 32 + (hasOneTimeKey ? 32 : 0);
        byte[] input = new byte[inputLen];
        input.AsSpan(0, 32).Fill(0xFF);
        dh1.CopyTo(input.AsSpan(32));
        dh2.CopyTo(input.AsSpan(64));
        dh3.CopyTo(input.AsSpan(96));

        if (hasOneTimeKey)
        {
            byte[] dh4 = Curve25519.CalculateSharedSecret(baseKey.PrivateKey, bundle.PreKeyPublic!);
            dh4.CopyTo(input.AsSpan(128));
            CryptographicOperations.ZeroMemory(dh4);
        }

        CryptographicOperations.ZeroMemory(dh1);
        CryptographicOperations.ZeroMemory(dh2);
        CryptographicOperations.ZeroMemory(dh3);

        byte[] secrets = HKDF.DeriveKey(HashAlgorithmName.SHA256, input, 64, ZeroSalt32, WhisperTextInfo);
        CryptographicOperations.ZeroMemory(input);

        byte[] initialRoot = secrets[..32];
        byte[] initialChain = secrets[32..];

        // Initiator immediately generates an ephemeral ratchet keypair for sending
        KeyPair senderRatchet = Curve25519.GenerateKeyPair();
        var (nextRoot, sendingChain) = RootStep(initialRoot, senderRatchet.PrivateKey, bundle.SignedPreKeyPublic);

        var session = new SignalSession
        {
            Version = 3,
            LocalRegistrationId = localRegistrationId,
            RemoteRegistrationId = bundle.RegistrationId,
            LocalIdentity = (byte[])localIdentity.PublicKey.Clone(),
            RemoteIdentity = (byte[])bundle.IdentityKey.Clone(),
            RootKey = nextRoot,
            PreviousSendCounter = 0,
            AliceBaseKey = (byte[])baseKey.PublicKey.Clone(),
            PendingPreKey = new PendingPreKeyInfo(
                bundle.PreKeyId,
                bundle.SignedPreKeyId,
                (byte[])baseKey.PublicKey.Clone()),
            SenderChain = new SignalSenderChain
            {
                RatchetKeyPair = senderRatchet,
                ChainKey = sendingChain,
                Index = 0
            }
        };

        session.ReceiverChains.Add(new SignalReceivingChain
        {
            RatchetPublicKey = (byte[])bundle.SignedPreKeyPublic.Clone(),
            ChainKey = initialChain,
            Index = 0
        });

        return session;
    }

    /// <summary>
    /// Encrypts plaintext using the session's active sending ratchet chain, returning either
    /// a <see cref="PreKeySignalMessage"/> (while awaiting peer reply) or a compact <see cref="SignalMessage"/>.
    /// </summary>
    public static SignalEncryptedPayload Encrypt(
        SignalSession session,
        ReadOnlySpan<byte> plaintext,
        KeyPair localIdentity,
        uint localRegistrationId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.SenderChain is null || session.SenderChain.ChainKey.Length != 32)
        {
            throw new InvalidOperationException("SignalSession does not have an initialized sending chain.");
        }

        uint counter = session.SenderChain.Index;
        byte[] nextChainKey = new byte[32];
        byte[] messageKey = new byte[32];
        StepChainKey(session.SenderChain.ChainKey, messageKey, nextChainKey);

        ExpandMessageKeys(messageKey, out byte[] cipherKey, out byte[] macKey, out byte[] iv);
        CryptographicOperations.ZeroMemory(messageKey);

        byte[] ciphertext = EncryptAesCbc(cipherKey, iv, plaintext);
        var signalMsg = new SignalMessage
        {
            Version = session.Version,
            RatchetKey = session.SenderChain.RatchetKeyPair.PublicKey,
            Counter = counter,
            PreviousCounter = session.PreviousSendCounter,
            Ciphertext = ciphertext
        };

        byte[] rawWithoutMac = signalMsg.SerializeWithoutMac();
        byte[] mac8 = ComputeMac8(macKey, localIdentity.PublicKey, session.RemoteIdentity, rawWithoutMac);
        byte[] signalWireBytes = CombineBodyAndMac(rawWithoutMac, mac8);

        // Advance sending state only after building wire bytes cleanly
        session.SenderChain.ChainKey = nextChainKey;
        session.SenderChain.Index = checked(counter + 1);

        if (session.PendingPreKey is { } pending)
        {
            var pkmsg = new PreKeySignalMessage
            {
                Version = session.Version,
                PreKeyId = pending.PreKeyId,
                BaseKey = pending.BaseKey,
                IdentityKey = localIdentity.PublicKey,
                MessageBytes = signalWireBytes,
                RegistrationId = localRegistrationId,
                SignedPreKeyId = pending.SignedPreKeyId
            };
            return new SignalEncryptedPayload(SignalCiphertextType.PreKeyMessage, pkmsg.Serialize());
        }

        return new SignalEncryptedPayload(SignalCiphertextType.SignalMessage, signalWireBytes);
    }

    /// <summary>
    /// Decrypts a <see cref="PreKeySignalMessage"/>, establishing or reusing the responder session and deriving
    /// an active sending ratchet chain so the responder can immediately reply.
    /// </summary>
    public static (byte[] Plaintext, SignalSession Session) DecryptPreKey(
        PreKeySignalMessage pkmsg,
        KeyPair localIdentity,
        KeyPair localSignedPreKey,
        KeyPair? localOneTimePreKey = null,
        SignalSession? existingSession = null)
    {
        ArgumentNullException.ThrowIfNull(pkmsg);
        var innerMsg = SignalMessage.Parse(pkmsg.MessageBytes);

        // Reuse already-established session if this pkmsg carries the same Alice BaseKey (e.g. M2 sent before Bob replied)
        if (existingSession is not null
            && existingSession.AliceBaseKey is { } baseKey
            && baseKey.AsSpan().SequenceEqual(pkmsg.BaseKey)
            && existingSession.RemoteIdentity.AsSpan().SequenceEqual(pkmsg.IdentityKey))
        {
            byte[] plaintext = DecryptSignal(innerMsg, existingSession, localIdentity);
            return (plaintext, existingSession);
        }

        // Responder X3DH:
        // DH1 = DH(SPK_B, IK_A), DH2 = DH(IK_B, EK_A), DH3 = DH(SPK_B, EK_A), DH4 = DH(OPK_B, EK_A)
        byte[] dh1 = Curve25519.CalculateSharedSecret(localSignedPreKey.PrivateKey, pkmsg.IdentityKey);
        byte[] dh2 = Curve25519.CalculateSharedSecret(localIdentity.PrivateKey, pkmsg.BaseKey);
        byte[] dh3 = Curve25519.CalculateSharedSecret(localSignedPreKey.PrivateKey, pkmsg.BaseKey);

        bool hasOneTimeKey = localOneTimePreKey != null && pkmsg.PreKeyId.HasValue;
        int totalDhLen = 32 + 32 + 32 + 32 + (hasOneTimeKey ? 32 : 0);
        byte[] input = new byte[totalDhLen];
        input.AsSpan(0, 32).Fill(0xFF);
        dh1.CopyTo(input.AsSpan(32));
        dh2.CopyTo(input.AsSpan(64));
        dh3.CopyTo(input.AsSpan(96));

        if (hasOneTimeKey)
        {
            byte[] dh4 = Curve25519.CalculateSharedSecret(localOneTimePreKey!.Value.PrivateKey, pkmsg.BaseKey);
            dh4.CopyTo(input.AsSpan(128));
            CryptographicOperations.ZeroMemory(dh4);
        }

        CryptographicOperations.ZeroMemory(dh1);
        CryptographicOperations.ZeroMemory(dh2);
        CryptographicOperations.ZeroMemory(dh3);

        byte[] secrets = HKDF.DeriveKey(HashAlgorithmName.SHA256, input, 64, ZeroSalt32, WhisperTextInfo);
        CryptographicOperations.ZeroMemory(input);

        byte[] rootKey = secrets[..32];
        byte[] initialChain = secrets[32..];

        byte[] chainKey = initialChain;
        KeyPair senderRatchet = localSignedPreKey;
        byte[] sendingChainKey = (byte[])initialChain.Clone();

        bool initiatorRatcheted = !innerMsg.RatchetKey.AsSpan().SequenceEqual(localSignedPreKey.PublicKey);
        if (initiatorRatcheted)
        {
            (rootKey, chainKey) = RootStep(rootKey, localSignedPreKey.PrivateKey, innerMsg.RatchetKey);
        }

        var (decryptedPlaintext, advancedRecvChain, skippedKeys) = DecryptWithChain(
            innerMsg,
            chainKey,
            startCounter: 0,
            remoteIdentityKey: pkmsg.IdentityKey,
            localIdentityKey: localIdentity.PublicKey);

        if (initiatorRatcheted)
        {
            senderRatchet = Curve25519.GenerateKeyPair();
            (rootKey, sendingChainKey) = RootStep(rootKey, senderRatchet.PrivateKey, innerMsg.RatchetKey);
        }

        var session = new SignalSession
        {
            Version = pkmsg.Version,
            LocalIdentity = (byte[])localIdentity.PublicKey.Clone(),
            RemoteIdentity = (byte[])pkmsg.IdentityKey.Clone(),
            RemoteRegistrationId = pkmsg.RegistrationId,
            RootKey = rootKey,
            PreviousSendCounter = 0,
            AliceBaseKey = (byte[])pkmsg.BaseKey.Clone(),
            SenderChain = new SignalSenderChain
            {
                RatchetKeyPair = senderRatchet,
                ChainKey = sendingChainKey,
                Index = 0
            }
        };

        session.ReceiverChains.Add(new SignalReceivingChain
        {
            RatchetPublicKey = (byte[])innerMsg.RatchetKey.Clone(),
            ChainKey = advancedRecvChain,
            Index = innerMsg.Counter + 1
        });
        session.StoreSkippedKeys(innerMsg.RatchetKey, skippedKeys);

        return (decryptedPlaintext, session);
    }

    /// <summary>
    /// Decrypts a regular <see cref="SignalMessage"/> using the session's active sending/receiving ratchet keys.
    /// </summary>
    public static byte[] DecryptSignal(
        SignalMessage msg,
        SignalSession session,
        KeyPair localIdentity)
    {
        ArgumentNullException.ThrowIfNull(session);
        KeyPair ratchetKey = session.LocalRatchetKeyPair
            ?? throw new InvalidOperationException("SignalSession does not have a LocalRatchetKeyPair.");
        return DecryptSignal(msg, session, localIdentity, ratchetKey);
    }

    /// <summary>
    /// Decrypts a regular <see cref="SignalMessage"/> using an established session.
    /// Supports bounded out-of-order skipped keys across ratchet turns, rejects duplicates/future floods,
    /// verifies MAC before committing any state mutation, and clears any pending PreKey state.
    /// </summary>
    public static byte[] DecryptSignal(
        SignalMessage msg,
        SignalSession session,
        KeyPair localIdentity,
        KeyPair localEphemeralKey)
    {
        ArgumentNullException.ThrowIfNull(msg);
        ArgumentNullException.ThrowIfNull(session);

        var matchingChain = session.ReceiverChains.FirstOrDefault(
            c => c.RatchetPublicKey.AsSpan().SequenceEqual(msg.RatchetKey));

        if (matchingChain is not null)
        {
            if (msg.Counter < matchingChain.Index)
            {
                if (!session.TryGetSkippedKey(msg.RatchetKey, msg.Counter, out byte[] skippedMsgKey))
                {
                    throw new CryptographicException($"Duplicate or already consumed Signal message counter {msg.Counter}.");
                }

                // Verify MAC & decrypt BEFORE removing the skipped key so wrong-MAC probes cannot erase it.
                byte[] skippedPlaintext = AuthenticateAndDecryptMessageKey(
                    msg,
                    skippedMsgKey,
                    session.RemoteIdentity,
                    localIdentity.PublicKey);

                session.ConsumeSkippedKey(msg.RatchetKey, msg.Counter);
                session.PendingPreKey = null;
                return skippedPlaintext;
            }

            var (plaintext, nextChain, newSkippedKeys) = DecryptWithChain(
                msg,
                matchingChain.ChainKey,
                startCounter: matchingChain.Index,
                remoteIdentityKey: session.RemoteIdentity,
                localIdentityKey: localIdentity.PublicKey);

            matchingChain.ChainKey = nextChain;
            matchingChain.Index = msg.Counter + 1;
            session.StoreSkippedKeys(msg.RatchetKey, newSkippedKeys);
            session.PendingPreKey = null;
            return plaintext;
        }

        // Also check skipped keys in case an old receiver chain was evicted beyond MaxReceiverChains.
        if (session.TryGetSkippedKey(msg.RatchetKey, msg.Counter, out byte[] oldSkippedKey))
        {
            byte[] oldPlaintext = AuthenticateAndDecryptMessageKey(
                msg,
                oldSkippedKey,
                session.RemoteIdentity,
                localIdentity.PublicKey);
            session.ConsumeSkippedKey(msg.RatchetKey, msg.Counter);
            session.PendingPreKey = null;
            return oldPlaintext;
        }

        // New remote ratchet key: compute receiving ratchet step first, authenticate MAC, then rotate sending ratchet.
        var (rootAfterRecv, recvChain) = RootStep(session.RootKey, localEphemeralKey.PrivateKey, msg.RatchetKey);
        var (ratchetedPlaintext, nextRecvChain, ratchetedSkipped) = DecryptWithChain(
            msg,
            recvChain,
            startCounter: 0,
            remoteIdentityKey: session.RemoteIdentity,
            localIdentityKey: localIdentity.PublicKey);

        KeyPair freshSendRatchet = Curve25519.GenerateKeyPair();
        var (rootAfterSend, nextSendChain) = RootStep(rootAfterRecv, freshSendRatchet.PrivateKey, msg.RatchetKey);

        session.PreviousSendCounter = session.SendCounter > 0 ? session.SendCounter - 1 : 0;
        session.RootKey = rootAfterSend;
        session.SenderChain = new SignalSenderChain
        {
            RatchetKeyPair = freshSendRatchet,
            ChainKey = nextSendChain,
            Index = 0
        };

        if (session.ReceiverChains.Count >= SignalSession.MaxReceiverChains)
        {
            session.ReceiverChains.RemoveAt(0);
        }
        session.ReceiverChains.Add(new SignalReceivingChain
        {
            RatchetPublicKey = (byte[])msg.RatchetKey.Clone(),
            ChainKey = nextRecvChain,
            Index = msg.Counter + 1
        });
        session.StoreSkippedKeys(msg.RatchetKey, ratchetedSkipped);
        session.PendingPreKey = null;
        return ratchetedPlaintext;
    }

    private static (byte[] Plaintext, byte[] AdvancedChainKey, List<(uint Counter, byte[] MessageKey)> SkippedKeys) DecryptWithChain(
        SignalMessage msg,
        byte[] chainKey,
        uint startCounter,
        byte[] remoteIdentityKey,
        byte[] localIdentityKey)
    {
        if (msg.Counter < startCounter)
        {
            throw new CryptographicException($"Message counter {msg.Counter} is behind chain counter {startCounter}.");
        }

        uint gap = msg.Counter - startCounter;
        uint futureLimit = remoteIdentityKey.AsSpan().SequenceEqual(localIdentityKey)
            ? SignalSession.SelfFutureLimit
            : SignalSession.PeerFutureLimit;

        if (gap > futureLimit)
        {
            throw new CryptographicException($"Signal message counter jump ({gap}) exceeds future limit ({futureLimit}).");
        }

        Span<byte> currentChain = stackalloc byte[32];
        chainKey.CopyTo(currentChain);

        Span<byte> nextChain = stackalloc byte[32];
        Span<byte> targetMessageKey = stackalloc byte[32];
        Span<byte> stepMsgKey = stackalloc byte[32];
        var skippedKeys = gap > 0 ? new List<(uint Counter, byte[] MessageKey)>((int)Math.Min(gap, SignalSession.MaxSkippedKeys)) : [];

        for (uint i = startCounter; i <= msg.Counter; i++)
        {
            if (i < msg.Counter)
            {
                StepChainKey(currentChain, stepMsgKey, nextChain);
                if (msg.Counter - i <= SignalSession.MaxSkippedKeys)
                {
                    skippedKeys.Add((i, stepMsgKey.ToArray()));
                }
                CryptographicOperations.ZeroMemory(stepMsgKey);
            }
            else
            {
                StepChainKey(currentChain, targetMessageKey, nextChain);
            }

            nextChain.CopyTo(currentChain);
        }

        try
        {
            byte[] plaintext = AuthenticateAndDecryptMessageKey(msg, targetMessageKey, remoteIdentityKey, localIdentityKey);
            return (plaintext, currentChain.ToArray(), skippedKeys);
        }
        catch
        {
            foreach (var (_, skipped) in skippedKeys)
            {
                CryptographicOperations.ZeroMemory(skipped);
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(targetMessageKey);
        }
    }

    private static byte[] AuthenticateAndDecryptMessageKey(
        SignalMessage msg,
        ReadOnlySpan<byte> messageKey,
        byte[] remoteIdentityKey,
        byte[] localIdentityKey)
    {
        byte[] cipherKey;
        byte[] macKey;
        byte[] iv;
        if (messageKey.Length == 80)
        {
            cipherKey = messageKey[..32].ToArray();
            macKey = messageKey[32..64].ToArray();
            iv = messageKey[64..80].ToArray();
        }
        else
        {
            ExpandMessageKeys(messageKey, out cipherKey, out macKey, out iv);
        }

        byte[] computedMac = ComputeMac8(macKey, remoteIdentityKey, localIdentityKey, msg.RawBytesWithoutMac);
        if (!CryptographicOperations.FixedTimeEquals(computedMac, msg.Mac.AsSpan(0, 8)))
        {
            throw new CryptographicException("SignalMessage MAC verification failed");
        }

        return DecryptAesCbc(cipherKey, iv, msg.Ciphertext);
    }

    private static (byte[] NextRootKey, byte[] ChainKey) RootStep(
        ReadOnlySpan<byte> currentRootKey,
        byte[] privateKey,
        byte[] publicKey)
    {
        byte[] shared = Curve25519.CalculateSharedSecret(privateKey, publicKey);
        byte[] derived = new byte[64];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, derived, currentRootKey, WhisperRatchetInfo);
        CryptographicOperations.ZeroMemory(shared);
        return (derived[..32], derived[32..]);
    }

    internal static void StepChainKey(ReadOnlySpan<byte> chainKey, Span<byte> messageKey, Span<byte> nextChainKey)
    {
        Span<byte> seedMsg = [0x01];
        Span<byte> seedNext = [0x02];
        if (!HMACSHA256.TryHashData(chainKey, seedMsg, messageKey, out int writtenMsg) || writtenMsg != 32
            || !HMACSHA256.TryHashData(chainKey, seedNext, nextChainKey, out int writtenNext) || writtenNext != 32)
        {
            throw new CryptographicException("Failed to derive Signal chain key step.");
        }
    }

    internal static void ExpandMessageKeys(ReadOnlySpan<byte> messageKey, out byte[] cipherKey, out byte[] macKey, out byte[] iv)
    {
        byte[] keyMaterial = new byte[80];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, messageKey, keyMaterial, ZeroSalt32, WhisperMessageKeysInfo);
        cipherKey = keyMaterial[..32];
        macKey = keyMaterial[32..64];
        iv = keyMaterial[64..80];
    }

    private static byte[] ComputeMac8(
        byte[] macKey,
        byte[] senderIdentityKey,
        byte[] recipientIdentityKey,
        byte[] rawBytesWithoutMac)
    {
        byte[] authData = new byte[33 + 33 + rawBytesWithoutMac.Length];
        authData[0] = 5;
        senderIdentityKey.CopyTo(authData.AsSpan(1));
        authData[33] = 5;
        recipientIdentityKey.CopyTo(authData.AsSpan(34));
        rawBytesWithoutMac.CopyTo(authData.AsSpan(66));

        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(macKey, authData, hash);
        return hash[..8].ToArray();
    }

    private static byte[] CombineBodyAndMac(byte[] body, byte[] mac8)
    {
        byte[] output = new byte[body.Length + 8];
        body.CopyTo(output, 0);
        mac8.CopyTo(output.AsSpan(body.Length));
        return output;
    }

    internal static byte[] EncryptAesCbc(byte[] cipherKey, byte[] iv, ReadOnlySpan<byte> plaintext)
    {
        int padLen = 16 - (plaintext.Length % 16);
        byte[] padded = new byte[plaintext.Length + padLen];
        plaintext.CopyTo(padded);
        padded.AsSpan(plaintext.Length).Fill((byte)padLen);

        using var aes = Aes.Create();
        aes.Key = cipherKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(padded, 0, padded.Length);
    }

    internal static byte[] DecryptAesCbc(byte[] cipherKey, byte[] iv, byte[] ciphertext)
    {
        using var aes = Aes.Create();
        aes.Key = cipherKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        byte[] decryptedPadded = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return UnpadPlaintext(decryptedPadded);
    }

    public static byte[] UnpadPlaintext(byte[] padded)
    {
        if (padded.Length == 0)
            throw new CryptographicException("Decrypted plaintext is empty");

        int padLen = padded[^1];
        if (padLen <= 0 || padLen > padded.Length || padLen > 16)
        {
            throw new CryptographicException($"Invalid PKCS7 padding length: {padLen}");
        }

        for (int i = padded.Length - padLen; i < padded.Length; i++)
        {
            if (padded[i] != padLen)
                throw new CryptographicException("Invalid PKCS7 padding bytes");
        }

        byte[] unpadded = new byte[padded.Length - padLen];
        Buffer.BlockCopy(padded, 0, unpadded, 0, unpadded.Length);
        return unpadded;
    }

    /// <summary>
    /// Strips the WhatsApp application-layer message padding (version 2 or 3).
    /// </summary>
    public static byte[] UnpadWhatsAppMessage(byte[] plaintext, int version = 2)
    {
        if (version == 3 || plaintext.Length == 0)
            return plaintext;

        int padLen = plaintext[^1];
        if (padLen > 0 && padLen <= plaintext.Length)
        {
            bool valid = true;
            for (int i = plaintext.Length - padLen; i < plaintext.Length; i++)
            {
                if (plaintext[i] != padLen)
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                var unpadded = new byte[plaintext.Length - padLen];
                Buffer.BlockCopy(plaintext, 0, unpadded, 0, unpadded.Length);
                return unpadded;
            }
        }

        return plaintext;
    }

    private static void ValidateKey32(byte[] key, string name)
    {
        if (key is null || key.Length != 32)
        {
            throw new ArgumentException("Key must be 32 bytes.", name);
        }
    }
}
