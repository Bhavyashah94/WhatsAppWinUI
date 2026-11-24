using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Signal;

public enum SignalCiphertextType : byte
{
    PreKeyMessage = 0, // wire type "pkmsg"
    SignalMessage = 1  // wire type "msg"
}

public sealed record SignalEncryptedPayload(SignalCiphertextType Type, byte[] Bytes)
{
    public string WireType => Type == SignalCiphertextType.PreKeyMessage ? "pkmsg" : "msg";
}

public sealed record PreKeyBundle(
    uint RegistrationId,
    uint DeviceId,
    uint SignedPreKeyId,
    byte[] SignedPreKeyPublic,
    byte[] SignedPreKeySignature,
    byte[] IdentityKey,
    uint? PreKeyId = null,
    byte[]? PreKeyPublic = null);

public sealed record PendingPreKeyInfo(
    uint? PreKeyId,
    uint SignedPreKeyId,
    byte[] BaseKey);

public sealed class SignalSenderChain
{
    public KeyPair RatchetKeyPair { get; set; }
    public byte[] ChainKey { get; set; } = [];
    public uint Index { get; set; }
}

public sealed class SignalReceivingChain
{
    public byte[] RatchetPublicKey { get; set; } = [];
    public byte[] ChainKey { get; set; } = [];
    public uint Index { get; set; }
}

/// <summary>
/// Full Signal Protocol v3 Double Ratchet session state supporting bidirectional 1:1 E2EE.
/// Retains legacy compatibility properties alongside full sender/receiver ratchet chains and bounded skipped keys.
/// </summary>
public sealed class SignalSession
{
    public const int MaxSkippedKeys = 2000;
    public const uint PeerFutureLimit = 2000;
    public const uint SelfFutureLimit = 25000;
    public const int MaxReceiverChains = 5;

    public byte Version { get; set; } = 3;
    public uint LocalRegistrationId { get; set; }
    public uint RemoteRegistrationId { get; set; }
    public byte[] LocalIdentity { get; set; } = [];
    public byte[] RemoteIdentity { get; set; } = [];
    public byte[] RootKey { get; set; } = [];
    public uint PreviousSendCounter { get; set; }
    public byte[]? AliceBaseKey { get; set; }
    public PendingPreKeyInfo? PendingPreKey { get; set; }

    public SignalSenderChain? SenderChain { get; set; }
    public List<SignalReceivingChain> ReceiverChains { get; } = [];

    /// <summary>
    /// Bounded skipped message keys keyed by <c>(RatchetKeyHex, Counter)</c> for out-of-order decryption.
    /// </summary>
    public Dictionary<(string RatchetKeyHex, uint Counter), byte[]> SkippedMessageKeys { get; } = [];
    public Queue<(string RatchetKeyHex, uint Counter)> SkippedMessageKeyOrder { get; } = new();

    internal static (string RatchetKeyHex, uint Counter) MakeSkippedKey(ReadOnlySpan<byte> ratchetPublicKey, uint counter) =>
        (Convert.ToHexString(ratchetPublicKey), counter);

    internal void StoreSkippedKeys(
        ReadOnlySpan<byte> ratchetPublicKey,
        IReadOnlyList<(uint Counter, byte[] MessageKey)> entries)
    {
        if (entries.Count == 0) return;

        string ratchetHex = Convert.ToHexString(ratchetPublicKey);
        foreach (var (counter, msgKey) in entries)
        {
            var key = (ratchetHex, counter);
            if (!SkippedMessageKeys.ContainsKey(key))
            {
                SkippedMessageKeys[key] = msgKey;
                SkippedMessageKeyOrder.Enqueue(key);
            }
        }

        while (SkippedMessageKeys.Count > MaxSkippedKeys && SkippedMessageKeyOrder.TryDequeue(out var oldest))
        {
            if (SkippedMessageKeys.Remove(oldest, out byte[]? evicted))
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(evicted);
            }
        }
    }

    internal bool TryGetSkippedKey(ReadOnlySpan<byte> ratchetPublicKey, uint counter, out byte[] messageKey) =>
        SkippedMessageKeys.TryGetValue(MakeSkippedKey(ratchetPublicKey, counter), out messageKey!);

    internal void ConsumeSkippedKey(ReadOnlySpan<byte> ratchetPublicKey, uint counter)
    {
        if (SkippedMessageKeys.Remove(MakeSkippedKey(ratchetPublicKey, counter), out byte[]? consumed))
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(consumed);
        }
    }

    // Compatibility properties mapped onto the active receiver chain for existing callers/tests.
    public byte[] ReceivingChainKey
    {
        get => ReceiverChains.Count > 0 ? ReceiverChains[^1].ChainKey : [];
        set
        {
            if (ReceiverChains.Count > 0)
            {
                ReceiverChains[^1].ChainKey = value;
            }
            else
            {
                ReceiverChains.Add(new SignalReceivingChain
                {
                    RatchetPublicKey = _legacyRatchetKey ?? [],
                    ChainKey = value,
                    Index = _legacyCounter
                });
            }
        }
    }

    private byte[]? _legacyRatchetKey;
    public byte[]? CurrentRatchetKey
    {
        get => ReceiverChains.Count > 0 ? ReceiverChains[^1].RatchetPublicKey : _legacyRatchetKey;
        set
        {
            _legacyRatchetKey = value;
            if (ReceiverChains.Count > 0 && value is not null)
            {
                ReceiverChains[^1].RatchetPublicKey = value;
            }
        }
    }

    private uint _legacyCounter;
    public uint Counter
    {
        get => ReceiverChains.Count > 0 ? ReceiverChains[^1].Index : _legacyCounter;
        set
        {
            _legacyCounter = value;
            if (ReceiverChains.Count > 0)
            {
                ReceiverChains[^1].Index = value;
            }
        }
    }

    public KeyPair? LocalRatchetKeyPair
    {
        get => SenderChain?.RatchetKeyPair;
        set
        {
            if (value is null)
            {
                SenderChain = null;
            }
            else if (SenderChain is not null)
            {
                SenderChain.RatchetKeyPair = value.Value;
            }
            else
            {
                SenderChain = new SignalSenderChain { RatchetKeyPair = value.Value };
            }
        }
    }

    public byte[] SendingChainKey
    {
        get => SenderChain?.ChainKey ?? [];
        set
        {
            if (SenderChain is null)
            {
                throw new InvalidOperationException("Cannot set SendingChainKey before LocalRatchetKeyPair is initialized.");
            }
            SenderChain.ChainKey = value;
        }
    }

    public uint SendCounter
    {
        get => SenderChain?.Index ?? 0;
        set
        {
            if (SenderChain is null)
            {
                throw new InvalidOperationException("Cannot set SendCounter before LocalRatchetKeyPair is initialized.");
            }
            SenderChain.Index = value;
        }
    }
}
