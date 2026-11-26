using System.Globalization;
using System.IO.Compression;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Protocol.HistorySync;

public enum GroupHistorySkipReason
{
    NoOptedInSuccessfulRecipients = 0,
    SenderNotAuthorized = 1,
    UnsupportedGroup = 2,
    AccountPropsUnavailable = 3,
    GroupPropsUnavailable = 4,
    InvalidProperties = 5,
    SharingDisabled = 6,
    NoEligibleMessages = 7,
    GroupMetadataUnavailable = 8
}

public sealed record GroupHistoryLimits(int MaxMessages, long TimeWindowSeconds);

/// <summary>
/// Pure-protocol Group History Sharing policy resolver, message selector, and zlib
/// <see cref="GroupHistory"/> protobuf bundler/inflater matching <c>WAWebGroupHistoryUtils</c>
/// and <c>reference/whatsapp-rust/src/features/group_history.rs</c>.
/// </summary>
public static class GroupHistoryCodec
{
    public const int DefaultMessageCountLimit = 100;
    public const long DefaultTimeWindowSeconds = 2_592_000; // 30 days
    public const long DefaultReceiverBundleEnforcementSeconds = 1_209_600; // 14 days

    public static GroupHistoryLimits ResolveLimits(
        IReadOnlyDictionary<uint, string>? accountProps,
        GroupPropsResponse? groupProps)
    {
        if (accountProps is null || accountProps.Count == 0)
        {
            throw new InvalidOperationException(nameof(GroupHistorySkipReason.AccountPropsUnavailable));
        }
        if (groupProps is null || string.IsNullOrEmpty(groupProps.Hash))
        {
            throw new InvalidOperationException(nameof(GroupHistorySkipReason.GroupPropsUnavailable));
        }

        bool accountSend = TryReadBoolProp(accountProps, AbPropsIq.PropGroupHistorySend, out bool acctVal)
            ? acctVal
            : false;
        bool groupSend = TryReadBoolProp(groupProps.ExperimentProps, AbPropsIq.PropGroupHistorySendGroupLevel, out bool grpVal)
            ? grpVal
            : false;

        if (!accountSend && !groupSend)
        {
            throw new InvalidOperationException(nameof(GroupHistorySkipReason.SharingDisabled));
        }

        long windowSecs = DefaultTimeWindowSeconds;
        if (accountProps.TryGetValue(AbPropsIq.PropGroupHistoryTimeLimitSecs, out string? winStr))
        {
            if (!long.TryParse(winStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out windowSecs) || windowSecs <= 0)
            {
                throw new InvalidOperationException(nameof(GroupHistorySkipReason.InvalidProperties));
            }
        }

        int maxMessages = DefaultMessageCountLimit;
        if (accountProps.TryGetValue(AbPropsIq.PropGroupHistoryCountLimit, out string? cntStr))
        {
            if (!int.TryParse(cntStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxMessages) || maxMessages <= 0)
            {
                throw new InvalidOperationException(nameof(GroupHistorySkipReason.InvalidProperties));
            }
        }

        return new GroupHistoryLimits(maxMessages, windowSecs);
    }

    public static bool CanUserShareHistory(bool isAdminOrSuperAdmin, MemberShareHistoryMode? mode) =>
        isAdminOrSuperAdmin || mode == MemberShareHistoryMode.AllMemberShare;

    public static bool IsBundleExpired(Message.Types.MessageHistoryMetadata? metadata, long nowUnixSeconds, long enforcementWindowSeconds = DefaultReceiverBundleEnforcementSeconds)
    {
        if (metadata is null || !metadata.HasOldestMessageTimestampInBundle)
        {
            return true;
        }
        long oldest = metadata.OldestMessageTimestampInBundle;
        return oldest < nowUnixSeconds - enforcementWindowSeconds || oldest > nowUnixSeconds;
    }

    public static bool FitsLimits(Message.Types.MessageHistoryMetadata? metadata, GroupHistoryLimits limits, long nowUnixSeconds)
    {
        if (metadata is null || !metadata.HasMessageCount || !metadata.HasOldestMessageTimestampInBundle)
        {
            return false;
        }
        long count = metadata.MessageCount;
        long oldest = metadata.OldestMessageTimestampInBundle;
        return count > 0
            && count <= limits.MaxMessages
            && oldest >= nowUnixSeconds - limits.TimeWindowSeconds
            && oldest <= nowUnixSeconds;
    }

    /// <summary>
    /// Selects eligible acknowledged plaintext messages belonging to <paramref name="groupJid"/>
    /// within the AB-prop time window and count cap, excluding ephemeral records, and returns
    /// sanitized wire copies ordered newest-first.
    /// </summary>
    public static (IReadOnlyList<WebMessageInfo> Messages, long OldestTimestamp) SelectEligibleMessages(
        Jid groupJid,
        IEnumerable<WebMessageInfo> historyMessages,
        long nowUnixSeconds,
        GroupHistoryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(historyMessages);
        if (limits.MaxMessages <= 0)
        {
            return (Array.Empty<WebMessageInfo>(), 0);
        }

        string groupStr = groupJid.ToNonAdString();
        long windowStart = Math.Max(0, nowUnixSeconds - limits.TimeWindowSeconds);

        var candidates = new List<(ulong Timestamp, int Index, WebMessageInfo Projected)>();
        int idx = 0;

        foreach (WebMessageInfo item in historyMessages)
        {
            int curIdx = idx++;
            if (item.Key is not { Id.Length: > 0 } key
                || !string.Equals(key.RemoteJid, groupStr, StringComparison.OrdinalIgnoreCase)
                || item.Message is null)
            {
                continue;
            }

            if (item.Status is not (
                WebMessageInfo.Types.Status.ServerAck
                or WebMessageInfo.Types.Status.DeliveryAck
                or WebMessageInfo.Types.Status.Read
                or WebMessageInfo.Types.Status.Played))
            {
                continue;
            }

            if (!item.HasMessageTimestamp)
            {
                continue;
            }

            ulong ts = item.MessageTimestamp;
            if (item.HasEphemeralExpirationTimestamp
                || item.HasEphemeralDuration
                || item.HasEphemeralStartTimestamp
                || ts < (ulong)windowStart
                || ts > (ulong)nowUnixSeconds)
            {
                continue;
            }

            if (!IsShareablePlainText(item.Message))
            {
                continue;
            }

            var projectedMsg = new Message
            {
                Conversation = item.Message.Conversation
            };
            if (item.Message.MessageContextInfo?.MessageSecret is { Length: > 0 } secret)
            {
                projectedMsg.MessageContextInfo = new MessageContextInfo
                {
                    MessageSecret = secret
                };
            }

            var projected = new WebMessageInfo
            {
                Key = key.Clone(),
                Message = projectedMsg,
                MessageTimestamp = ts,
                Status = item.Status
            };
            if (item.HasParticipant)
            {
                projected.Participant = item.Participant;
            }

            candidates.Add((ts, curIdx, projected));
        }

        if (candidates.Count == 0)
        {
            return (Array.Empty<WebMessageInfo>(), 0);
        }

        // Keep newest `MaxMessages`, deduplicating by message ID (later index breaks ties).
        var ordered = candidates
            .OrderByDescending(c => c.Timestamp)
            .ThenByDescending(c => c.Index);

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<(ulong Timestamp, WebMessageInfo Projected)>(Math.Min(limits.MaxMessages, candidates.Count));
        foreach (var c in ordered)
        {
            if (selected.Count >= limits.MaxMessages)
            {
                break;
            }
            if (seenIds.Add(c.Projected.Key.Id))
            {
                selected.Add((c.Timestamp, c.Projected));
            }
        }

        if (selected.Count == 0)
        {
            return (Array.Empty<WebMessageInfo>(), 0);
        }

        long oldest = (long)selected[^1].Timestamp;
        return (selected.Select(s => s.Projected).ToArray(), oldest);
    }

    public static byte[] CompressGroupHistory(IEnumerable<WebMessageInfo> messages)
    {
        var proto = new GroupHistory();
        proto.Messages.AddRange(messages);
        byte[] encoded = proto.ToByteArray();

        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(encoded, 0, encoded.Length);
        }
        return ms.ToArray();
    }

    public static GroupHistory DecompressGroupHistory(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return GroupHistory.Parser.ParseFrom(output.ToArray());
    }

    public static Message BuildBundleMessage(
        string directPath,
        byte[] fileSha256,
        byte[] fileEncSha256,
        byte[] mediaKey32,
        long mediaKeyTimestampSeconds,
        Message.Types.MessageHistoryMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directPath);
        ArgumentNullException.ThrowIfNull(fileSha256);
        ArgumentNullException.ThrowIfNull(fileEncSha256);
        ArgumentNullException.ThrowIfNull(mediaKey32);
        ArgumentNullException.ThrowIfNull(metadata);

        return new Message
        {
            MessageHistoryBundle = new Message.Types.MessageHistoryBundle
            {
                Mimetype = "application/protobuf",
                FileSha256 = ByteString.CopyFrom(fileSha256),
                MediaKey = ByteString.CopyFrom(mediaKey32),
                FileEncSha256 = ByteString.CopyFrom(fileEncSha256),
                DirectPath = directPath,
                MediaKeyTimestamp = mediaKeyTimestampSeconds,
                MessageHistoryMetadata = metadata.Clone()
            }
        };
    }

    public static Message BuildNoticeMessage(Message.Types.MessageHistoryMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return new Message
        {
            MessageHistoryNotice = new Message.Types.MessageHistoryNotice
            {
                MessageHistoryMetadata = metadata.Clone()
            }
        };
    }

    private static bool IsShareablePlainText(Message msg)
    {
        if (!msg.HasConversation || string.IsNullOrEmpty(msg.Conversation))
        {
            return false;
        }

        // Only plain Conversation (+ optional MessageSecret in MessageContextInfo) is shareable.
        var clone = new Message { Conversation = msg.Conversation };
        if (msg.MessageContextInfo is { } mci)
        {
            var allowedCtx = new MessageContextInfo();
            if (mci.HasMessageSecret) allowedCtx.MessageSecret = mci.MessageSecret;
            if (mci.HasReportingTokenVersion) allowedCtx.ReportingTokenVersion = mci.ReportingTokenVersion;
            if (!ByteString.CopyFrom(msg.MessageContextInfo.ToByteArray()).Equals(ByteString.CopyFrom(allowedCtx.ToByteArray())))
            {
                return false;
            }
            clone.MessageContextInfo = msg.MessageContextInfo.Clone();
        }

        return ByteString.CopyFrom(msg.ToByteArray()).Equals(ByteString.CopyFrom(clone.ToByteArray()));
    }

    private static bool TryReadBoolProp(IReadOnlyDictionary<uint, string> map, uint code, out bool value)
    {
        if (map.TryGetValue(code, out string? raw))
        {
            if (raw is "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }
            if (raw is "0" || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }
        }
        value = false;
        return false;
    }
}
