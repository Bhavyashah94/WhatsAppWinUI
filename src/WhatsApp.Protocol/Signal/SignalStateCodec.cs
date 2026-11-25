using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Protocol.Signal;

/// <summary>
/// Protobuf persistence codecs for <see cref="SignalSession"/> (<see cref="RecordStructure"/> / <see cref="SessionStructure"/>)
/// and <see cref="SenderKeyRecord"/> (<see cref="SenderKeyRecordStructure"/>).
/// </summary>
public static class SignalStateCodec
{
    public static byte[] SerializeSession(SignalSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var proto = new SessionStructure
        {
            SessionVersion = session.Version,
            LocalIdentityPublic = ByteString.CopyFrom(session.LocalIdentity),
            RemoteIdentityPublic = ByteString.CopyFrom(session.RemoteIdentity),
            RootKey = ByteString.CopyFrom(session.RootKey),
            PreviousCounter = session.PreviousSendCounter,
            RemoteRegistrationId = session.RemoteRegistrationId,
            LocalRegistrationId = session.LocalRegistrationId
        };

        if (session.AliceBaseKey is { Length: > 0 } aliceBase)
        {
            proto.AliceBaseKey = ByteString.CopyFrom(aliceBase);
        }

        if (session.PendingPreKey is { } pending)
        {
            var pkProto = new SessionStructure.Types.PendingPreKey
            {
                SignedPreKeyId = unchecked((int)pending.SignedPreKeyId),
                BaseKey = ByteString.CopyFrom(pending.BaseKey)
            };
            if (pending.PreKeyId is uint preKeyId)
            {
                pkProto.PreKeyId = preKeyId;
            }
            proto.PendingPreKey = pkProto;
        }

        if (session.SenderChain is { } sender)
        {
            proto.SenderChain = new SessionStructure.Types.Chain
            {
                SenderRatchetKey = ByteString.CopyFrom(sender.RatchetKeyPair.PublicKey),
                SenderRatchetKeyPrivate = ByteString.CopyFrom(sender.RatchetKeyPair.PrivateKey),
                ChainKey = new SessionStructure.Types.Chain.Types.ChainKey
                {
                    Index = sender.Index,
                    Key = ByteString.CopyFrom(sender.ChainKey)
                }
            };
        }

        foreach (var recv in session.ReceiverChains)
        {
            var chainProto = new SessionStructure.Types.Chain
            {
                SenderRatchetKey = ByteString.CopyFrom(recv.RatchetPublicKey),
                ChainKey = new SessionStructure.Types.Chain.Types.ChainKey
                {
                    Index = recv.Index,
                    Key = ByteString.CopyFrom(recv.ChainKey)
                }
            };

            string ratchetHex = Convert.ToHexString(recv.RatchetPublicKey);
            foreach (var ((keyHex, counter), materialOrSeed) in session.SkippedMessageKeys)
            {
                if (!string.Equals(keyHex, ratchetHex, StringComparison.Ordinal))
                {
                    continue;
                }

                byte[] cipherKey;
                byte[] macKey;
                byte[] iv;
                if (materialOrSeed.Length == 80)
                {
                    cipherKey = materialOrSeed[..32];
                    macKey = materialOrSeed[32..64];
                    iv = materialOrSeed[64..80];
                }
                else
                {
                    SignalCipher.ExpandMessageKeys(materialOrSeed, out cipherKey, out macKey, out iv);
                }

                chainProto.MessageKeys.Add(new SessionStructure.Types.Chain.Types.MessageKey
                {
                    Index = counter,
                    CipherKey = ByteString.CopyFrom(cipherKey),
                    MacKey = ByteString.CopyFrom(macKey),
                    Iv = ByteString.CopyFrom(iv)
                });
            }

            proto.ReceiverChains.Add(chainProto);
        }

        return new RecordStructure
        {
            CurrentSession = proto
        }.ToByteArray();
    }

    public static SignalSession DeserializeSession(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new InvalidDataException("Empty Signal session bytes.");
        }

        // Canonical format wraps SessionStructure inside RecordStructure (field 1, wire type 2 = 0x0A);
        // keep bare SessionStructure fallback (field 1, varint 0x08) for backwards compatibility.
        SessionStructure proto;
        if (bytes[0] != 0x08)
        {
            var record = RecordStructure.Parser.ParseFrom(bytes);
            proto = record.CurrentSession
                ?? throw new InvalidDataException("RecordStructure is missing currentSession.");
        }
        else
        {
            proto = SessionStructure.Parser.ParseFrom(bytes);
        }
        var session = new SignalSession
        {
            Version = (byte)(proto.HasSessionVersion ? proto.SessionVersion : 3),
            LocalRegistrationId = proto.LocalRegistrationId,
            RemoteRegistrationId = proto.RemoteRegistrationId,
            LocalIdentity = PreKeySignalMessage.StripKeyPrefix(proto.LocalIdentityPublic.ToByteArray()),
            RemoteIdentity = PreKeySignalMessage.StripKeyPrefix(proto.RemoteIdentityPublic.ToByteArray()),
            RootKey = proto.RootKey.ToByteArray(),
            PreviousSendCounter = proto.PreviousCounter,
            AliceBaseKey = proto.HasAliceBaseKey
                ? PreKeySignalMessage.StripKeyPrefix(proto.AliceBaseKey.ToByteArray())
                : null
        };

        if (proto.PendingPreKey is { } pending)
        {
            session.PendingPreKey = new PendingPreKeyInfo(
                pending.HasPreKeyId ? pending.PreKeyId : null,
                unchecked((uint)pending.SignedPreKeyId),
                PreKeySignalMessage.StripKeyPrefix(pending.BaseKey.ToByteArray()));
        }

        if (proto.SenderChain is { } sender)
        {
            session.SenderChain = new SignalSenderChain
            {
                RatchetKeyPair = new KeyPair(
                    sender.SenderRatchetKeyPrivate.ToByteArray(),
                    PreKeySignalMessage.StripKeyPrefix(sender.SenderRatchetKey.ToByteArray())),
                ChainKey = sender.ChainKey?.Key.ToByteArray() ?? [],
                Index = sender.ChainKey?.Index ?? 0
            };
        }

        foreach (var recvProto in proto.ReceiverChains)
        {
            byte[] ratchetPub = PreKeySignalMessage.StripKeyPrefix(recvProto.SenderRatchetKey.ToByteArray());
            session.ReceiverChains.Add(new SignalReceivingChain
            {
                RatchetPublicKey = ratchetPub,
                ChainKey = recvProto.ChainKey?.Key.ToByteArray() ?? [],
                Index = recvProto.ChainKey?.Index ?? 0
            });

            if (recvProto.MessageKeys.Count > 0)
            {
                var skipped = new List<(uint Counter, byte[] Material)>(recvProto.MessageKeys.Count);
                foreach (var mk in recvProto.MessageKeys)
                {
                    byte[] combined80 = new byte[80];
                    mk.CipherKey.Span.CopyTo(combined80);
                    mk.MacKey.Span.CopyTo(combined80.AsSpan(32));
                    mk.Iv.Span.CopyTo(combined80.AsSpan(64));
                    skipped.Add((mk.Index, combined80));
                }
                session.StoreSkippedKeys(ratchetPub, skipped);
            }
        }

        return session;
    }

    public static byte[] SerializeSenderKeyRecord(SenderKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var proto = new SenderKeyRecordStructure();
        foreach (var state in record.States)
        {
            var stateProto = new SenderKeyStateStructure
            {
                SenderKeyId = state.Id,
                SenderChainKey = new SenderKeyStateStructure.Types.SenderChainKey
                {
                    Iteration = state.Iteration,
                    Seed = ByteString.CopyFrom(state.ChainKey)
                },
                SenderSigningKey = new SenderKeyStateStructure.Types.SenderSigningKey
                {
                    Public = ByteString.CopyFrom(state.SigningPublicKey)
                }
            };

            if (state.SigningPrivateKey is { Length: > 0 } priv)
            {
                stateProto.SenderSigningKey.Private = ByteString.CopyFrom(priv);
            }

            foreach (var (iter, seed) in state.SkippedMessageSeeds)
            {
                stateProto.SenderMessageKeys.Add(new SenderKeyStateStructure.Types.SenderMessageKey
                {
                    Iteration = iter,
                    Seed = ByteString.CopyFrom(seed)
                });
            }

            proto.SenderKeyStates.Add(stateProto);
        }

        return proto.ToByteArray();
    }

    public static SenderKeyRecord DeserializeSenderKeyRecord(ReadOnlySpan<byte> bytes)
    {
        var proto = SenderKeyRecordStructure.Parser.ParseFrom(bytes);
        var record = new SenderKeyRecord();

        foreach (var stateProto in proto.SenderKeyStates)
        {
            var state = new SenderKeyState
            {
                Id = stateProto.SenderKeyId,
                Iteration = stateProto.SenderChainKey?.Iteration ?? 0,
                ChainKey = stateProto.SenderChainKey?.Seed.ToByteArray() ?? [],
                SigningPublicKey = PreKeySignalMessage.StripKeyPrefix(
                    stateProto.SenderSigningKey?.Public.ToByteArray() ?? []),
                SigningPrivateKey = stateProto.SenderSigningKey is { HasPrivate: true }
                    ? stateProto.SenderSigningKey.Private.ToByteArray()
                    : null
            };

            if (stateProto.SenderMessageKeys.Count > 0)
            {
                var skipped = stateProto.SenderMessageKeys
                    .Select(mk => (mk.Iteration, mk.Seed.ToByteArray()))
                    .ToArray();
                state.StoreSkippedSeeds(skipped);
            }

            record.States.Add(state);
        }

        return record;
    }
}
