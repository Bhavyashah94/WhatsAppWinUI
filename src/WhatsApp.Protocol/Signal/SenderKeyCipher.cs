using System.Security.Cryptography;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Signal;

/// <summary>
/// Signal Protocol group (<c>@g.us</c>) SenderKey encryption and decryption engine.
/// Mirrors <c>wacore-libsignal</c> <c>engine/sender.rs</c> using <c>"WhisperGroup"</c> HKDF-48B,
/// AES-256-CBC, and XEdDSA message signatures.
/// </summary>
public static class SenderKeyCipher
{
    private static readonly byte[] WhisperGroupInfo = "WhisperGroup"u8.ToArray();
    private static readonly byte[] ZeroSalt32 = new byte[32];

    /// <summary>
    /// Creates a new outbound sender state if none exists yet on <paramref name="record"/>,
    /// returning the serialized <see cref="SenderKeyDistributionMessage"/> (<c>skmsg</c> distribution payload).
    /// </summary>
    public static byte[] CreateDistribution(SenderKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.States.Count == 0 || record.States[0].SigningPrivateKey is null)
        {
            KeyPair signing = Curve25519.GenerateKeyPair();
            byte[] chainKey = RandomNumberGenerator.GetBytes(32);
            uint id = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);

            var state = new SenderKeyState
            {
                Id = id,
                Iteration = 0,
                ChainKey = chainKey,
                SigningPublicKey = signing.PublicKey,
                SigningPrivateKey = signing.PrivateKey
            };

            record.States.Insert(0, state);
            if (record.States.Count > SenderKeyRecord.MaxStates)
            {
                record.States.RemoveRange(SenderKeyRecord.MaxStates, record.States.Count - SenderKeyRecord.MaxStates);
            }
        }

        SenderKeyState current = record.States[0];
        var skdm = new SenderKeyDistributionMessage
        {
            Id = current.Id,
            Iteration = current.Iteration,
            ChainKey = current.ChainKey,
            SigningKey = current.SigningPublicKey
        };
        return skdm.Serialize();
    }

    /// <summary>
    /// Rotates and creates a brand-new sender state (used when group membership shrinks / key rotates).
    /// </summary>
    public static byte[] RotateAndCreateDistribution(SenderKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.States.Clear();
        return CreateDistribution(record);
    }

    /// <summary>
    /// Processes an incoming serialized <see cref="SenderKeyDistributionMessage"/> from a group participant.
    /// Preserves existing consumed iteration &amp; skipped keys if an identical <c>(id, signingKey)</c> re-arrives.
    /// </summary>
    public static void ProcessDistribution(SenderKeyRecord record, byte[] distributionBytes)
    {
        ArgumentNullException.ThrowIfNull(record);
        var skdm = SenderKeyDistributionMessage.Parse(distributionBytes);

        // Preserve established state if re-receiving the same (Id, SigningKey) to prevent replay downgrade.
        int retainedIdx = record.States.FindIndex(
            s => s.Id == skdm.Id && s.SigningPublicKey.AsSpan().SequenceEqual(skdm.SigningKey));

        SenderKeyState state;
        if (retainedIdx >= 0)
        {
            state = record.States[retainedIdx];
            record.States.RemoveAt(retainedIdx);
        }
        else
        {
            state = new SenderKeyState
            {
                Id = skdm.Id,
                Iteration = skdm.Iteration,
                ChainKey = skdm.ChainKey,
                SigningPublicKey = skdm.SigningKey
            };
        }

        record.States.RemoveAll(s => s.Id == skdm.Id);
        record.States.Insert(0, state);
        if (record.States.Count > SenderKeyRecord.MaxStates)
        {
            record.States.RemoveRange(SenderKeyRecord.MaxStates, record.States.Count - SenderKeyRecord.MaxStates);
        }
    }

    /// <summary>
    /// Encrypts a group payload using the sender's active state, signing the serialized body with XEdDSA.
    /// </summary>
    public static byte[] Encrypt(SenderKeyRecord record, ReadOnlySpan<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.States.Count == 0 || record.States[0].SigningPrivateKey is not { Length: 32 } signingPrivate)
        {
            throw new InvalidOperationException("SenderKeyRecord has no active sending state.");
        }

        SenderKeyState state = record.States[0];
        uint iteration = state.Iteration;
        Span<byte> seed = stackalloc byte[32];
        byte[] nextChain = new byte[32];
        SignalCipher.StepChainKey(state.ChainKey, seed, nextChain);

        ExpandGroupKeys(seed, out byte[] cipherKey, out byte[] iv);
        CryptographicOperations.ZeroMemory(seed);

        byte[] ciphertext = SignalCipher.EncryptAesCbc(cipherKey, iv, plaintext);
        var msg = new SenderKeyMessage
        {
            Id = state.Id,
            Iteration = iteration,
            Ciphertext = ciphertext
        };

        byte[] body = msg.SerializeWithoutSignature();
        byte[] signature = XEdDSA.Sign(signingPrivate, body);
        byte[] wire = new byte[body.Length + SenderKeyMessage.SignatureLength];
        body.CopyTo(wire, 0);
        signature.CopyTo(wire.AsSpan(body.Length));

        state.ChainKey = nextChain;
        state.Iteration = checked(iteration + 1);
        return wire;
    }

    /// <summary>
    /// Verifies XEdDSA signature, supports out-of-order skipped seeds up to <see cref="SignalSession.PeerFutureLimit"/>,
    /// rejects duplicates/replays, and decrypts the group <see cref="SenderKeyMessage"/> payload.
    /// </summary>
    public static byte[] Decrypt(SenderKeyRecord record, byte[] wireBytes)
    {
        ArgumentNullException.ThrowIfNull(record);
        var msg = SenderKeyMessage.Parse(wireBytes);
        SenderKeyState state = record.FindState(msg.Id)
            ?? throw new KeyNotFoundException($"No SenderKeyState found for SenderKey ID {msg.Id}.");

        // Always verify XEdDSA signature BEFORE advancing chains or inspecting skipped keys.
        if (!XEdDSA.Verify(state.SigningPublicKey, msg.RawBytesWithoutSignature, msg.Signature))
        {
            throw new CryptographicException("SenderKeyMessage XEdDSA signature verification failed.");
        }

        if (msg.Iteration < state.Iteration)
        {
            if (!state.SkippedMessageSeeds.TryGetValue(msg.Iteration, out byte[]? skippedSeed))
            {
                throw new CryptographicException($"Duplicate or already consumed SenderKey iteration {msg.Iteration}.");
            }

            ExpandGroupKeys(skippedSeed, out byte[] skippedCipher, out byte[] skippedIv);
            byte[] skippedPlaintext = SignalCipher.DecryptAesCbc(skippedCipher, skippedIv, msg.Ciphertext);
            state.SkippedMessageSeeds.Remove(msg.Iteration);
            CryptographicOperations.ZeroMemory(skippedSeed);
            return skippedPlaintext;
        }

        uint gap = msg.Iteration - state.Iteration;
        if (gap > SignalSession.PeerFutureLimit)
        {
            throw new CryptographicException($"SenderKey iteration jump ({gap}) exceeds future limit ({SignalSession.PeerFutureLimit}).");
        }

        Span<byte> currentChain = stackalloc byte[32];
        state.ChainKey.CopyTo(currentChain);

        Span<byte> nextChain = stackalloc byte[32];
        Span<byte> targetSeed = stackalloc byte[32];
        Span<byte> stepSeed = stackalloc byte[32];
        var skipped = gap > 0 ? new List<(uint Iteration, byte[] Seed)>((int)Math.Min(gap, SenderKeyState.MaxSkippedMessageKeys)) : [];

        for (uint i = state.Iteration; i <= msg.Iteration; i++)
        {
            if (i < msg.Iteration)
            {
                SignalCipher.StepChainKey(currentChain, stepSeed, nextChain);
                if (msg.Iteration - i <= SenderKeyState.MaxSkippedMessageKeys)
                {
                    skipped.Add((i, stepSeed.ToArray()));
                }
                CryptographicOperations.ZeroMemory(stepSeed);
            }
            else
            {
                SignalCipher.StepChainKey(currentChain, targetSeed, nextChain);
            }

            nextChain.CopyTo(currentChain);
        }

        byte[] plaintext;
        try
        {
            ExpandGroupKeys(targetSeed, out byte[] cipherKey, out byte[] iv);
            plaintext = SignalCipher.DecryptAesCbc(cipherKey, iv, msg.Ciphertext);
        }
        catch
        {
            foreach (var (_, s) in skipped)
            {
                CryptographicOperations.ZeroMemory(s);
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(targetSeed);
        }

        state.ChainKey = currentChain.ToArray();
        state.Iteration = checked(msg.Iteration + 1);
        state.StoreSkippedSeeds(skipped);
        return plaintext;
    }

    private static void ExpandGroupKeys(ReadOnlySpan<byte> seed, out byte[] cipherKey, out byte[] iv)
    {
        // Rust engine/sender.rs: HKDF("WhisperGroup", 48 bytes) -> IV[0..16] || CipherKey[16..48]
        byte[] expanded = new byte[48];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, seed, expanded, ZeroSalt32, WhisperGroupInfo);
        iv = expanded[..16];
        cipherKey = expanded[16..48];
    }
}
