using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WhatsApp.Calling;

public sealed class SrtpKeys
{
    public byte[] CipherKey { get; }
    public byte[] Salt { get; }
    public byte[] AuthKey { get; }

    public SrtpKeys(byte[] cipherKey16, byte[] salt14, byte[] authKey20)
    {
        CipherKey = cipherKey16;
        Salt = salt14;
        AuthKey = authKey20;
    }

    public override string ToString() => "SrtpKeys([redacted])";
}

public sealed record SrtpKeyingMaterial(byte[] MasterKey16, byte[] MasterSalt14)
{
    public override string ToString() => "SrtpKeyingMaterial([redacted])";
}

public sealed record LibsrtpSessionKeys(byte[] SessionKey16, byte[] SessionSalt14, byte[] AuthKey20)
{
    public override string ToString() => "LibsrtpSessionKeys([redacted])";
}

public enum HbhSrtcpMode
{
    Directional,
    Shared,
}

public sealed class SendRocTracker
{
    private uint _roc;
    private ushort _lastSeq;
    private bool _initialized;

    public uint Advance(ushort seq)
    {
        if (!_initialized)
        {
            _lastSeq = seq;
            _initialized = true;
            return _roc;
        }

        if (seq - _lastSeq < -32768)
        {
            _roc++;
        }
        _lastSeq = seq;
        return _roc;
    }
}

public sealed class RecvRocTracker
{
    private uint _roc;
    private ushort _highestSeq;
    private bool _initialized;

    public uint EstimateRoc(ushort seq)
    {
        if (!_initialized)
        {
            return 0;
        }

        if (_highestSeq < 0x8000)
        {
            return seq - _highestSeq > 0x8000 ? (_roc > 0 ? _roc - 1 : 0) : _roc;
        }

        return _highestSeq - 0x8000 > seq ? _roc + 1 : _roc;
    }

    public void Commit(ushort seq, uint roc)
    {
        if (!_initialized)
        {
            _highestSeq = seq;
            _roc = roc;
            _initialized = true;
            return;
        }

        if (roc > _roc)
        {
            _roc = roc;
            _highestSeq = seq;
        }
        else if (roc == _roc && seq > _highestSeq)
        {
            _highestSeq = seq;
        }
    }
}

/// <summary>
/// E2E 1:1 SRTP/SRTCP + WARP MESSAGE-INTEGRITY + HBH two-stage SFU SRTP/SRTCP ciphers
/// matching <c>wacore/src/voip/{e2e_srtp,hbh_srtp,warp}.rs</c> bit-for-bit.
/// </summary>
public static class SrtpCipher
{
    public const int HbhKeyLength = 30;
    public const int HbhSaltSeedLength = 14;
    public const int WarpMiTagLength = 4;
    public const int SrtcpAuthTagLength = 10;
    public const int RtcpHeaderLength = 8;

    private static readonly byte[] NullSalt32 = new byte[32];

    private const string HbhUplinkSaltLabel = "uplink hbh srtcp salt";
    private const string HbhUplinkKeyLabel = "uplink hbh srtcp key";
    private const string HbhDownlinkSaltLabel = "downlink hbh srtcp salt";
    private const string HbhDownlinkKeyLabel = "downlink hbh srtcp key";
    private const string HbhSharedSrtcpSaltLabel = "hbh srtcp salt";
    private const string HbhSharedSrtcpKeyLabel = "hbh srtcp key";
    private const string HbhSharedSrtpSaltLabel = "hbh srtp salt";
    private const string HbhSharedSrtpKeyLabel = "hbh srtp key";

    private const byte LabelRtpEncryption = 0x00;
    private const byte LabelRtpAuth = 0x01;
    private const byte LabelRtpSalt = 0x02;
    private const byte LabelRtcpEncryption = 0x03;
    private const byte LabelRtcpAuth = 0x04;
    private const byte LabelRtcpSalt = 0x05;

    public static SrtpKeys DeriveE2eRtpKeys(ReadOnlySpan<byte> callKey32, string participantId)
    {
        byte[] master = CallCrypto.DeriveWaHkdf46(callKey32, participantId);
        ReadOnlySpan<byte> mk = master.AsSpan(0, 16);
        ReadOnlySpan<byte> ms = master.AsSpan(16, 14);
        return new SrtpKeys(
            AesCmKdf(mk, ms, LabelRtpEncryption, 16),
            AesCmKdf(mk, ms, LabelRtpSalt, 14),
            AesCmKdf(mk, ms, LabelRtpAuth, 20));
    }

    public static SrtpKeys DeriveE2eRtcpKeys(ReadOnlySpan<byte> callKey32, string participantId)
    {
        byte[] master = CallCrypto.DeriveWaHkdf46(callKey32, participantId);
        ReadOnlySpan<byte> mk = master.AsSpan(0, 16);
        ReadOnlySpan<byte> ms = master.AsSpan(16, 14);
        return new SrtpKeys(
            AesCmKdf(mk, ms, LabelRtcpEncryption, 16),
            AesCmKdf(mk, ms, LabelRtcpSalt, 14),
            AesCmKdf(mk, ms, LabelRtcpAuth, 20));
    }

    public static byte[] BuildE2eRtpIv(ReadOnlySpan<byte> salt14, uint ssrc, uint roc, ushort seq)
    {
        byte[] iv = new byte[16];
        int n = Math.Min(salt14.Length, 14);
        int off = 14 - n;
        salt14[..n].CopyTo(iv.AsSpan(off));

        iv[4] ^= (byte)(ssrc >> 24);
        iv[5] ^= (byte)(ssrc >> 16);
        iv[6] ^= (byte)(ssrc >> 8);
        iv[7] ^= (byte)ssrc;

        ulong packetIndex = ((ulong)roc << 16) | seq;
        ushort hi16 = (ushort)((packetIndex >> 32) & 0xFFFF);
        uint lo32 = (uint)(packetIndex & 0xFFFF_FFFF);

        iv[8] ^= (byte)(hi16 >> 8);
        iv[9] ^= (byte)hi16;
        iv[10] ^= (byte)(lo32 >> 24);
        iv[11] ^= (byte)(lo32 >> 16);
        iv[12] ^= (byte)(lo32 >> 8);
        iv[13] ^= (byte)lo32;
        return iv;
    }

    /// <summary>
    /// Full 128-bit big-endian AES-CTR encryption/decryption (matching Rust <c>Ctr128BE&lt;Aes128&gt;</c>).
    /// </summary>
    public static void CryptAes128CtrInPlace(ReadOnlySpan<byte> key16, ReadOnlySpan<byte> iv16, Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        using var aes = Aes.Create();
        aes.Key = key16.ToArray();
        Span<byte> counter = stackalloc byte[16];
        iv16.CopyTo(counter);
        Span<byte> keystream = stackalloc byte[16];

        int offset = 0;
        while (offset < buffer.Length)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);
            int chunk = Math.Min(16, buffer.Length - offset);
            for (int i = 0; i < chunk; i++)
            {
                buffer[offset + i] ^= keystream[i];
            }
            offset += chunk;

            for (int b = 15; b >= 0; b--)
            {
                if (++counter[b] != 0) break;
            }
        }
    }

    public static byte[] ComputeWarpMiTag(ReadOnlySpan<byte> authKey20, ReadOnlySpan<byte> packetWithoutTag, uint roc, int tagLen = WarpMiTagLength)
    {
        Span<byte> input = new byte[packetWithoutTag.Length + 4];
        packetWithoutTag.CopyTo(input);
        BinaryPrimitives.WriteUInt32BigEndian(input[packetWithoutTag.Length..], roc);
        byte[] full = HMACSHA1.HashData(authKey20, input);
        return full[..Math.Clamp(tagLen, 1, 20)];
    }

    public static bool VerifyWarpMiTag(ReadOnlySpan<byte> authKey20, ReadOnlySpan<byte> packetWithoutTag, uint roc, ReadOnlySpan<byte> receivedTag, int tagLen = WarpMiTagLength)
    {
        if (tagLen is < 1 or > 20 || receivedTag.Length != tagLen)
        {
            return false;
        }
        byte[] expected = ComputeWarpMiTag(authKey20, packetWithoutTag, roc, tagLen);
        return CryptographicOperations.FixedTimeEquals(expected, receivedTag);
    }

    public static byte[] ProtectE2eRtpPacket(
        SrtpKeys sendKeys,
        SendRocTracker rocTracker,
        uint ssrc,
        ushort seq,
        ReadOnlySpan<byte> encodedRtpHeader,
        ReadOnlySpan<byte> plaintextPayload,
        int warpMiTagLen = WarpMiTagLength)
    {
        uint roc = rocTracker.Advance(seq);
        byte[] iv = BuildE2eRtpIv(sendKeys.Salt, ssrc, roc, seq);
        byte[] output = new byte[encodedRtpHeader.Length + plaintextPayload.Length + warpMiTagLen];
        encodedRtpHeader.CopyTo(output);
        Span<byte> payloadSpan = output.AsSpan(encodedRtpHeader.Length, plaintextPayload.Length);
        plaintextPayload.CopyTo(payloadSpan);
        CryptAes128CtrInPlace(sendKeys.CipherKey, iv, payloadSpan);

        byte[] tag = ComputeWarpMiTag(sendKeys.AuthKey, output.AsSpan(0, encodedRtpHeader.Length + plaintextPayload.Length), roc, warpMiTagLen);
        tag.CopyTo(output.AsSpan(encodedRtpHeader.Length + plaintextPayload.Length));
        return output;
    }

    public static byte[]? UnprotectE2eRtpPacket(
        SrtpKeys recvKeys,
        RecvRocTracker rocTracker,
        ReadOnlySpan<byte> protectedPacket,
        int headerLength,
        uint ssrc,
        ushort seq,
        int warpMiTagLen = WarpMiTagLength)
    {
        if (protectedPacket.Length < headerLength + warpMiTagLen)
        {
            return null;
        }

        int payloadLen = protectedPacket.Length - headerLength - warpMiTagLen;
        ReadOnlySpan<byte> bodyWithoutTag = protectedPacket[..(headerLength + payloadLen)];
        ReadOnlySpan<byte> receivedTag = protectedPacket[(headerLength + payloadLen)..];

        uint roc = rocTracker.EstimateRoc(seq);
        if (!VerifyWarpMiTag(recvKeys.AuthKey, bodyWithoutTag, roc, receivedTag, warpMiTagLen))
        {
            return null;
        }

        rocTracker.Commit(seq, roc);
        byte[] plaintext = bodyWithoutTag[headerLength..].ToArray();
        byte[] iv = BuildE2eRtpIv(recvKeys.Salt, ssrc, roc, seq);
        CryptAes128CtrInPlace(recvKeys.CipherKey, iv, plaintext);
        return plaintext;
    }

    public static byte[] ProtectSrtcp(SrtpKeys keys, uint senderSsrc, uint index, ReadOnlySpan<byte> rtcpPlain)
    {
        int split = Math.Min(rtcpPlain.Length, RtcpHeaderLength);
        byte[] iv = BuildE2eRtpIv(keys.Salt, senderSsrc, index >> 16, (ushort)(index & 0xFFFF));

        byte[] outBuf = new byte[rtcpPlain.Length + 4 + SrtcpAuthTagLength];
        rtcpPlain.CopyTo(outBuf);
        CryptAes128CtrInPlace(keys.CipherKey, iv, outBuf.AsSpan(split, rtcpPlain.Length - split));

        BinaryPrimitives.WriteUInt32BigEndian(outBuf.AsSpan(rtcpPlain.Length, 4), 0x8000_0000u | (index & 0x7FFF_FFFFu));
        byte[] fullTag = HMACSHA1.HashData(keys.AuthKey, outBuf.AsSpan(0, rtcpPlain.Length + 4));
        fullTag.AsSpan(0, SrtcpAuthTagLength).CopyTo(outBuf.AsSpan(rtcpPlain.Length + 4));
        return outBuf;
    }

    public static (byte[] PlainRtcp, uint Index)? UnprotectSrtcp(SrtpKeys keys, uint senderSsrc, ReadOnlySpan<byte> srtcpPacket)
    {
        if (srtcpPacket.Length < RtcpHeaderLength + 4 + SrtcpAuthTagLength)
        {
            return null;
        }

        int tagStart = srtcpPacket.Length - SrtcpAuthTagLength;
        byte[] fullExpected = HMACSHA1.HashData(keys.AuthKey, srtcpPacket[..tagStart]);
        if (!CryptographicOperations.FixedTimeEquals(fullExpected.AsSpan(0, SrtcpAuthTagLength), srtcpPacket[tagStart..]))
        {
            return null;
        }

        int idxStart = tagStart - 4;
        uint index = BinaryPrimitives.ReadUInt32BigEndian(srtcpPacket.Slice(idxStart, 4)) & 0x7FFF_FFFFu;
        byte[] iv = BuildE2eRtpIv(keys.Salt, senderSsrc, index >> 16, (ushort)(index & 0xFFFF));

        byte[] plain = srtcpPacket[..idxStart].ToArray();
        CryptAes128CtrInPlace(keys.CipherKey, iv, plain.AsSpan(RtcpHeaderLength));
        return (plain, index);
    }

    // ─── HBH SFU Key Derivation & 2-Byte-Carry libsrtp AES-ICM ───────────

    public static byte[]? DeriveHbhSrtcpKeyUplink(ReadOnlySpan<byte> hbhKey30) =>
        DeriveHbhKeyWithLabels(hbhKey30, HbhUplinkSaltLabel, HbhUplinkKeyLabel);

    public static byte[]? DeriveHbhSrtcpKeyDownlink(ReadOnlySpan<byte> hbhKey30) =>
        DeriveHbhKeyWithLabels(hbhKey30, HbhDownlinkSaltLabel, HbhDownlinkKeyLabel);

    public static byte[]? DeriveHbhSrtpKeyShared(ReadOnlySpan<byte> hbhKey30) =>
        DeriveHbhKeyWithLabels(hbhKey30, HbhSharedSrtpSaltLabel, HbhSharedSrtpKeyLabel);

    public static byte[]? DeriveHbhSrtcpKeyShared(ReadOnlySpan<byte> hbhKey30) =>
        DeriveHbhKeyWithLabels(hbhKey30, HbhSharedSrtcpSaltLabel, HbhSharedSrtcpKeyLabel);

    public static SrtpKeyingMaterial? KeyingFromHbh(ReadOnlySpan<byte> hbhCryptoKey30)
    {
        if (hbhCryptoKey30.Length != HbhKeyLength)
        {
            return null;
        }
        return new SrtpKeyingMaterial(hbhCryptoKey30[..16].ToArray(), hbhCryptoKey30[16..30].ToArray());
    }

    public static LibsrtpSessionKeys ExpandLibsrtpSessionKeys(SrtpKeyingMaterial keying, bool rtcpLabels = false)
    {
        byte encLbl = rtcpLabels ? LabelRtcpEncryption : LabelRtpEncryption;
        byte authLbl = rtcpLabels ? LabelRtcpAuth : LabelRtpAuth;
        byte saltLbl = rtcpLabels ? LabelRtcpSalt : LabelRtpSalt;

        byte[] sessionKey = new byte[16];
        byte[] sessionSalt = new byte[14];
        byte[] authKey = new byte[20];
        DeriveLibsrtpBytes(keying.MasterKey16, keying.MasterSalt14, encLbl, sessionKey);
        DeriveLibsrtpBytes(keying.MasterKey16, keying.MasterSalt14, saltLbl, sessionSalt);
        DeriveLibsrtpBytes(keying.MasterKey16, keying.MasterSalt14, authLbl, authKey);
        return new LibsrtpSessionKeys(sessionKey, sessionSalt, authKey);
    }

    public static SrtpKeys? DeriveHbhSrtcpKeysUplink(ReadOnlySpan<byte> hbhKey30, HbhSrtcpMode mode = HbhSrtcpMode.Directional)
    {
        byte[]? raw = mode == HbhSrtcpMode.Directional ? DeriveHbhSrtcpKeyUplink(hbhKey30) : DeriveHbhSrtcpKeyShared(hbhKey30);
        if (raw is null || KeyingFromHbh(raw) is not { } km) return null;
        LibsrtpSessionKeys expanded = ExpandLibsrtpSessionKeys(km, rtcpLabels: true);
        return new SrtpKeys(expanded.SessionKey16, expanded.SessionSalt14, expanded.AuthKey20);
    }

    public static SrtpKeys? DeriveHbhSrtcpKeysDownlink(ReadOnlySpan<byte> hbhKey30, HbhSrtcpMode mode = HbhSrtcpMode.Directional)
    {
        byte[]? raw = mode == HbhSrtcpMode.Directional ? DeriveHbhSrtcpKeyDownlink(hbhKey30) : DeriveHbhSrtcpKeyShared(hbhKey30);
        if (raw is null || KeyingFromHbh(raw) is not { } km) return null;
        LibsrtpSessionKeys expanded = ExpandLibsrtpSessionKeys(km, rtcpLabels: true);
        return new SrtpKeys(expanded.SessionKey16, expanded.SessionSalt14, expanded.AuthKey20);
    }

    public static byte[] BuildHbhRtpIcmNonce(uint ssrc, ulong packetIndex)
    {
        byte[] iv = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(iv.AsSpan(4, 4), ssrc);
        BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(8, 8), packetIndex << 16);
        return iv;
    }

    public static void CryptHbhRtpPayloadInPlace(LibsrtpSessionKeys session, uint ssrc, ulong packetIndex, Span<byte> payload)
    {
        byte[] iv = BuildHbhRtpIcmNonce(ssrc, packetIndex);
        AesIcmCryptInPlace(session.SessionKey16, session.SessionSalt14, iv, payload);
    }

    /// <summary>
    /// libsrtp 2-byte-carry AES-ICM (bytes 15 then 14 carry) matching <c>hbh_srtp.rs</c>.
    /// </summary>
    public static void AesIcmCryptInPlace(ReadOnlySpan<byte> key16, ReadOnlySpan<byte> salt14, ReadOnlySpan<byte> iv16, Span<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        using var aes = Aes.Create();
        aes.Key = key16.ToArray();

        Span<byte> counter = stackalloc byte[16];
        salt14[..14].CopyTo(counter);
        for (int i = 0; i < 16; i++)
        {
            counter[i] ^= iv16[i];
        }

        Span<byte> keystream = stackalloc byte[16];
        int offset = 0;
        while (offset < data.Length)
        {
            aes.EncryptEcb(counter, keystream, PaddingMode.None);
            int chunk = Math.Min(16, data.Length - offset);
            for (int i = 0; i < chunk; i++)
            {
                data[offset + i] ^= keystream[i];
            }
            offset += chunk;

            if (++counter[15] == 0)
            {
                counter[14]++;
            }
        }
    }

    private static byte[] AesCmKdf(ReadOnlySpan<byte> masterKey16, ReadOnlySpan<byte> masterSalt14, byte label, int outLen)
    {
        byte[] iv = new byte[16];
        masterSalt14[..14].CopyTo(iv);
        iv[7] ^= label;
        byte[] outBuf = new byte[outLen];
        CryptAes128CtrInPlace(masterKey16, iv, outBuf);
        return outBuf;
    }

    private static void DeriveLibsrtpBytes(ReadOnlySpan<byte> masterKey16, ReadOnlySpan<byte> masterSalt14, byte label, Span<byte> outBuf)
    {
        outBuf.Clear();
        Span<byte> iv = stackalloc byte[16];
        iv[7] = label;
        AesIcmCryptInPlace(masterKey16, masterSalt14, iv, outBuf);
    }

    private static byte[]? DeriveHbhKeyWithLabels(ReadOnlySpan<byte> hbhKey30, string saltLabel, string keyLabel)
    {
        if (hbhKey30.Length != HbhKeyLength)
        {
            return null;
        }

        ReadOnlySpan<byte> saltSeed = hbhKey30[..HbhSaltSeedLength];
        ReadOnlySpan<byte> keySeed = hbhKey30[HbhSaltSeedLength..];

        Span<byte> derivedSalt = stackalloc byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, saltSeed, derivedSalt, NullSalt32, Encoding.UTF8.GetBytes(saltLabel));

        byte[] output = new byte[HbhKeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, keySeed, output, derivedSalt, Encoding.UTF8.GetBytes(keyLabel));
        return output;
    }
}
