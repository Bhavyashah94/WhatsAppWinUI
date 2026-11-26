using System.Globalization;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum CallActionKind
{
    Offer,
    OfferNotice,
    PreAccept,
    Accept,
    Reject,
    Terminate,
    Transport,
    RelayLatency,
    VideoState,
    MuteV2,
    Interruption,
    FlowControl,
    Notify,
    GroupUpdate,
    EncRekey,
    WaitingRoomUpdate,
    UserAction,
    ScreenShare,
    RelayInfoUpdate,
    Unknown,
}

public enum CallVideoState
{
    Disabled = 0,
    Enabled = 1,
    Paused = 2,
    UpgradeRequest = 3,
    UpgradeAccept = 4,
    UpgradeReject = 5,
    Stopped = 6,
    UpgradeRejectByTimeout = 7,
    UpgradeCancel = 8,
    UpgradeCancelByTimeout = 9,
    UnknownPeer = 10,
    UpgradeRequestV2 = 11,
    Error = 20,
}

public enum CapabilityBit
{
    Set,
    Clear,
    Unknown,
}

public sealed record CallAudioCodec(string Enc, uint Rate);

public sealed record OfferEnc(string EncType, int Version, byte[] Ciphertext);

public sealed record OfferRecipientEnc(Jid? To, OfferEnc Enc);

public sealed record CallPeerCapability(uint? Version, byte[] Bytes);

public sealed record IncomingCallStanza(
    Jid From,
    string StanzaId,
    string? Notify,
    string? Platform,
    string? Version,
    Jid? Participant,
    Jid? Recipient,
    long? TimestampSeconds,
    bool IsOffline,
    CallActionKind ActionKind,
    string CallId,
    Jid CallCreator,
    Jid? CallerPn = null,
    string? CallerCountryCode = null,
    string? DeviceClass = null,
    bool Joinable = false,
    bool IsVideo = false,
    bool IsGroup = false,
    Jid? GroupJid = null,
    string? CallerUsername = null,
    IReadOnlyList<CallAudioCodec>? AudioCodecs = null,
    string? Reason = null,
    uint? DurationSeconds = null,
    uint? AudioDurationSeconds = null,
    string? P2pCandRound = null,
    string? TransportMessageType = null,
    CallVideoState? VideoState = null,
    byte? VideoOrientation = null,
    string? VideoDec = null,
    bool? Muted = null,
    IReadOnlyList<OfferRecipientEnc>? OfferEncs = null,
    RelayData? Relay = null,
    RelayLatencyReport? RelayLatency = null,
    string? PeerAbtestBucket = null,
    string? PeerAbtestBucketIdList = null,
    CallPeerCapability? PeerCapability = null,
    GroupCallUpdate? GroupUpdate = null,
    GroupCallEncRekey? EncRekey = null,
    WaitingRoom? WaitingRoom = null,
    bool? RaisedHand = null,
    ScreenShare? ScreenShare = null);

public sealed record OfferDeviceKey(
    Jid DeviceJid,
    byte[] Ciphertext,
    string EncType = "pkmsg");

/// <summary>
/// Wire parser &amp; builders for 1:1 / shared <c>&lt;call&gt;</c> signaling stanzas, matching live
/// <c>WAWebHandleVoipCall</c> and <c>wacore/src/stanza/call.rs</c>.
/// Preserves server-enforced <c>&lt;offer&gt;</c> child order to avoid error 439.
/// </summary>
public static class CallStanzas
{
    public const int CapabilityHeaderLen = 2;
    public const uint CapabilityVersion = 1;
    public const uint MlowCapabilityIndex = 31;

    public const string TerminateReasonAcceptedElsewhere = "accepted_elsewhere";
    public const string TerminateReasonScreenedElsewhere = "screened_elsewhere";
    public const string TerminateReasonRejectedElsewhere = "rejected_elsewhere";
    public const string TerminateReasonTimeout = "timeout";
    public const string TerminateReasonGroupCallEnded = "group_call_ended";

    public const string RejectReasonBusy = "busy";
    public const string RejectReasonEnc = "enc";

    public const string VideoEncH264 = "h.264";
    public const string VideoDecH264 = "H264";

    public static readonly string[] DefaultAudioRates = ["8000", "16000"];

    public static readonly byte[] CapabilityOffer = [0x01, 0x05, 0xF7, 0x09, 0xE0, 0xBB, 0x13];
    public static readonly byte[] CapabilityPreAccept = [0x01, 0x05, 0xF7, 0x09, 0xE0, 0xBB, 0x07];
    public static readonly byte[] CapabilityVideoOffer = [0x01, 0x05, 0xF7, 0x09, 0xE4, 0xBB, 0x03];

    public static readonly byte[] CapabilityStandardOpusOffer = WithoutMlowCapability(CapabilityOffer);
    public static readonly byte[] CapabilityStandardOpusPreAccept = WithoutMlowCapability(CapabilityPreAccept);
    public static readonly byte[] CapabilityStandardOpusVideoOffer = WithoutMlowCapability(CapabilityVideoOffer);

    private static byte[] WithoutMlowCapability(byte[] blob)
    {
        byte[] copy = (byte[])blob.Clone();
        int byteIdx = CapabilityHeaderLen + (int)(MlowCapabilityIndex / 8);
        if (byteIdx < copy.Length)
        {
            copy[byteIdx] &= unchecked((byte)~(1 << (int)(MlowCapabilityIndex % 8)));
        }
        return copy;
    }

    public static CapabilityBit QueryCapabilityBit(uint? version, ReadOnlySpan<byte> bytes, uint index)
    {
        if (version is null || version < CapabilityVersion)
        {
            return CapabilityBit.Clear;
        }
        if (bytes.Length < CapabilityHeaderLen || bytes[0] != CapabilityVersion)
        {
            return CapabilityBit.Clear;
        }
        int declaredLen = bytes[1];
        ReadOnlySpan<byte> mask = bytes[CapabilityHeaderLen..];
        if (declaredLen > mask.Length)
        {
            return CapabilityBit.Clear;
        }
        int targetByte = (int)(index / 8) & 31;
        if (targetByte >= declaredLen)
        {
            return CapabilityBit.Clear;
        }
        return (mask[targetByte] & (1 << (int)(index % 8))) != 0
            ? CapabilityBit.Set
            : CapabilityBit.Clear;
    }

    public static bool MlowAfterPeerCapability(bool localEnabled, CapabilityBit peerBit) =>
        localEnabled && peerBit != CapabilityBit.Clear;

    public static IncomingCallStanza? ParseIncomingCall(Node stanza)
    {
        ArgumentNullException.ThrowIfNull(stanza);
        if (!string.Equals(stanza.Tag, "call", StringComparison.Ordinal))
        {
            return null;
        }

        if (stanza.Content is not NodeContent.NodesContent nc)
        {
            return null;
        }

        Node? actionChild = null;
        CallActionKind kind = CallActionKind.Unknown;
        foreach (Node child in nc.Nodes)
        {
            kind = MapActionKind(child.Tag);
            if (kind != CallActionKind.Unknown)
            {
                actionChild = child;
                break;
            }
        }

        if (actionChild is null)
        {
            return null;
        }

        if (stanza.GetAttr("from") is not { Length: > 0 } fromStr || !Jid.TryParse(fromStr, out Jid from))
        {
            throw new InvalidDataException("<call> missing or invalid 'from' attribute.");
        }

        string stanzaId = stanza.GetAttr("id") ?? string.Empty;
        string? notify = stanza.GetAttr("notify") is { Length: > 0 } n ? n : null;
        string? platform = stanza.GetAttr("platform");
        string? version = stanza.GetAttr("version");
        Jid? participant = TryParseAttrJid(stanza, "participant");
        Jid? recipient = TryParseAttrJid(stanza, "recipient");
        long? ts = long.TryParse(stanza.GetAttr("t"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedTs)
            ? parsedTs
            : null;
        bool isOffline = stanza.GetAttr("offline") is not null;

        string callId = actionChild.GetAttr("call-id")
            ?? throw new InvalidDataException($"<{actionChild.Tag}> missing 'call-id' attribute.");
        if (actionChild.GetAttr("call-creator") is not { Length: > 0 } creatorStr || !Jid.TryParse(creatorStr, out Jid callCreator))
        {
            throw new InvalidDataException($"<{actionChild.Tag}> missing or invalid 'call-creator' attribute.");
        }

        Jid? callerPn = null;
        string? callerCountry = null;
        string? deviceClass = null;
        bool joinable = false;
        bool isVideo = false;
        bool isGroup = false;
        Jid? groupJid = null;
        string? callerUsername = null;
        IReadOnlyList<CallAudioCodec>? audioCodecs = null;
        string? reason = null;
        uint? duration = null;
        uint? audioDuration = null;
        string? p2pRound = null;
        string? transportMsgType = null;
        CallVideoState? videoState = null;
        byte? videoOrientation = null;
        string? videoDec = null;
        bool? muted = null;
        IReadOnlyList<OfferRecipientEnc>? offerEncs = null;
        RelayData? relayData = null;
        RelayLatencyReport? relayLatency = null;
        string? peerAbBucket = null;
        string? peerAbList = null;
        CallPeerCapability? peerCap = null;
        GroupCallUpdate? groupUpdate = null;
        GroupCallEncRekey? encRekey = null;
        WaitingRoom? waitingRoom = null;
        bool? raisedHand = null;
        ScreenShare? screenShare = null;

        switch (kind)
        {
            case CallActionKind.Offer:
            {
                callerPn = TryParseAttrJid(actionChild, "caller_pn");
                callerCountry = actionChild.GetAttr("caller_country_code");
                deviceClass = actionChild.GetAttr("device_class");
                joinable = string.Equals(actionChild.GetAttr("joinable"), "1", StringComparison.Ordinal);
                groupJid = TryParseAttrJid(actionChild, "group-jid");
                callerUsername = actionChild.GetAttr("username");
                isVideo = actionChild.GetChild("video") is not null;
                if (actionChild.GetChild("video") is { } vidNode)
                {
                    videoOrientation = ParseVideoOrientation(vidNode);
                }
                audioCodecs = ParseAudioCodecs(actionChild);
                offerEncs = ParseOfferEncs(actionChild);
                relayData = RelayParser.FindRelay(stanza) is { } rn ? RelayParser.ParseRelayData(rn) : null;
                groupUpdate = GroupCallStanzas.ParseGroupInviteSnapshot(actionChild);
                isGroup = groupJid is not null || (groupUpdate?.Participants.Count > 0);
                if (actionChild.GetChild("metadata") is { } metaNode)
                {
                    peerAbBucket = metaNode.GetAttr("peer_abtest_bucket");
                    peerAbList = metaNode.GetAttr("peer_abtest_bucket_id_list");
                }
                if (actionChild.GetChild("capability") is { } capNode)
                {
                    uint? capVer = uint.TryParse(capNode.GetAttr("ver"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint cv) ? cv : null;
                    byte[] capBytes = capNode.Content is NodeContent.BytesContent cbc ? cbc.Bytes : Array.Empty<byte>();
                    peerCap = new CallPeerCapability(capVer, capBytes);
                }
                break;
            }
            case CallActionKind.OfferNotice:
                isVideo = string.Equals(actionChild.GetAttr("media"), "video", StringComparison.Ordinal);
                isGroup = string.Equals(actionChild.GetAttr("type"), "group", StringComparison.Ordinal);
                break;
            case CallActionKind.PreAccept:
                audioCodecs = ParseAudioCodecs(actionChild);
                isVideo = actionChild.GetChild("video") is not null;
                break;
            case CallActionKind.Accept:
                audioCodecs = ParseAudioCodecs(actionChild);
                isVideo = actionChild.GetChild("video") is not null;
                if (actionChild.GetChild("video") is { } vidAcc)
                {
                    videoOrientation = ParseVideoOrientation(vidAcc);
                }
                break;
            case CallActionKind.Reject:
                reason = actionChild.GetAttr("reason");
                break;
            case CallActionKind.Terminate:
                reason = actionChild.GetAttr("reason");
                duration = uint.TryParse(actionChild.GetAttr("duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint d) ? d : null;
                audioDuration = uint.TryParse(actionChild.GetAttr("audio_duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ad) ? ad : null;
                break;
            case CallActionKind.Transport:
                p2pRound = actionChild.GetAttr("p2p-cand-round");
                transportMsgType = actionChild.GetAttr("transport-message-type");
                break;
            case CallActionKind.RelayLatency:
            {
                IReadOnlyList<RelayLatencyEntry> entries = RelayParser.ParseRelayLatencyEntries(actionChild);
                if (entries.Count > 0)
                {
                    relayLatency = new RelayLatencyReport(entries);
                }
                break;
            }
            case CallActionKind.VideoState:
            {
                if (!int.TryParse(actionChild.GetAttr("state"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int vsRaw))
                {
                    throw new InvalidDataException("<video> missing or invalid 'state' attribute.");
                }
                videoState = (CallVideoState)vsRaw;
                videoOrientation = ParseVideoOrientation(actionChild);
                videoDec = actionChild.GetAttr("dec");
                break;
            }
            case CallActionKind.MuteV2:
                muted = string.Equals(actionChild.GetAttr("mute-state"), "1", StringComparison.Ordinal);
                break;
            case CallActionKind.GroupUpdate:
                groupUpdate = GroupCallStanzas.ParseGroupUpdate(actionChild);
                groupJid = groupUpdate.GroupJid;
                isGroup = true;
                joinable = groupUpdate.Joinable;
                isVideo = string.Equals(groupUpdate.Media, "video", StringComparison.Ordinal);
                break;
            case CallActionKind.EncRekey:
                encRekey = GroupCallStanzas.ParseGroupEncRekey(actionChild);
                isGroup = true;
                break;
            case CallActionKind.WaitingRoomUpdate:
                waitingRoom = GroupCallStanzas.ParseWaitingRoomUpdate(actionChild);
                isGroup = true;
                isVideo = waitingRoom.Media == CallLinkMedia.Video;
                break;
            case CallActionKind.UserAction:
                raisedHand = GroupCallStanzas.ParseRaiseHand(actionChild);
                isGroup = true;
                break;
            case CallActionKind.ScreenShare:
                screenShare = GroupCallStanzas.ParseScreenShare(actionChild);
                isGroup = true;
                break;
        }

        return new IncomingCallStanza(
            from,
            stanzaId,
            notify,
            platform,
            version,
            participant,
            recipient,
            ts,
            isOffline,
            kind,
            callId,
            callCreator,
            callerPn,
            callerCountry,
            deviceClass,
            joinable,
            isVideo,
            isGroup,
            groupJid,
            callerUsername,
            audioCodecs,
            reason,
            duration,
            audioDuration,
            p2pRound,
            transportMsgType,
            videoState,
            videoOrientation,
            videoDec,
            muted,
            offerEncs,
            relayData,
            relayLatency,
            peerAbBucket,
            peerAbList,
            peerCap,
            groupUpdate,
            encRekey,
            waitingRoom,
            raisedHand,
            screenShare);
    }

    public static Node BuildOffer(
        string callId,
        Jid to,
        Jid callCreator,
        IReadOnlyList<OfferDeviceKey> deviceKeys,
        string? wrapperId = null,
        byte[]? privacyToken = null,
        byte[]? capability = null,
        byte[]? deviceIdentity = null,
        bool multiDevice = false,
        bool video = false,
        IReadOnlyList<string>? audioRates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentNullException.ThrowIfNull(deviceKeys);
        IReadOnlyList<string> rates = audioRates is { Count: > 0 } ? audioRates : DefaultAudioRates;

        // Mandatory WA server order: privacy -> audio* -> [video] -> net(medium=3) -> [capability] -> destination|enc -> encopt -> [device-identity]
        var children = new List<Node>();
        if (privacyToken is { Length: > 0 })
        {
            children.Add(new Node("privacy", privacyToken));
        }
        foreach (string rate in rates)
        {
            children.Add(AudioOpusNode(rate));
        }
        if (video)
        {
            children.Add(VideoOfferNode());
        }
        children.Add(new Node("net", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["medium"] = "3" }));
        if (capability is { Length: > 0 })
        {
            children.Add(CapabilityNode(capability));
        }

        if (deviceKeys.Count > 1 || multiDevice)
        {
            var tos = deviceKeys.Select(dk => new Node(
                "to",
                new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = dk.DeviceJid },
                new[] { EncNode(dk) })).ToArray();
            children.Add(new Node("destination", tos));
        }
        else if (deviceKeys.Count == 1)
        {
            children.Add(EncNode(deviceKeys[0]));
        }

        children.Add(EncoptNode());
        if (deviceIdentity is { Length: > 0 })
        {
            children.Add(new Node("device-identity", deviceIdentity));
        }

        return CallWrap(to, wrapperId, OfferActionNode("offer", callId, callCreator, children));
    }

    public static Node BuildPreAccept(
        string callId,
        Jid to,
        Jid callCreator,
        string wrapperId,
        IReadOnlyList<string>? audioRates = null,
        bool video = false,
        byte[]? capability = null)
    {
        IReadOnlyList<string> rates = audioRates is { Count: > 0 } ? audioRates : DefaultAudioRates;
        byte[] cap = capability ?? (video ? CapabilityOffer : CapabilityPreAccept);

        var children = new List<Node>();
        foreach (string rate in rates)
        {
            children.Add(AudioOpusNode(rate));
        }
        if (video)
        {
            children.Add(VideoPreAcceptNode());
        }
        children.Add(EncoptNode());
        children.Add(CapabilityNode(cap));

        return CallWrap(to, wrapperId, OfferActionNode("preaccept", callId, callCreator, children));
    }

    public static Node BuildAccept(
        string callId,
        Jid to,
        Jid callCreator,
        string wrapperId,
        IReadOnlyList<string>? audioRates = null,
        byte[]? relayTe = null,
        byte[]? rte = null,
        byte[]? voipSettings = null,
        byte[]? capability = null,
        bool video = false,
        string? peerAbtestBucket = null,
        string? peerAbtestBucketIdList = null)
    {
        IReadOnlyList<string> rates = audioRates is { Count: > 0 } ? audioRates : DefaultAudioRates;
        var children = new List<Node>();
        foreach (string rate in rates)
        {
            children.Add(AudioOpusNode(rate));
        }
        if (video)
        {
            children.Add(VideoAcceptNode());
        }
        if (relayTe is { Length: > 0 })
        {
            children.Add(new Node("te", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["priority"] = "2" }, relayTe));
        }
        children.Add(new Node("net", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["medium"] = "2" }));
        children.Add(EncoptNode());
        if (!string.IsNullOrEmpty(peerAbtestBucket) || !string.IsNullOrEmpty(peerAbtestBucketIdList))
        {
            var metaAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(peerAbtestBucket)) metaAttrs["peer_abtest_bucket"] = peerAbtestBucket;
            if (!string.IsNullOrEmpty(peerAbtestBucketIdList)) metaAttrs["peer_abtest_bucket_id_list"] = peerAbtestBucketIdList;
            children.Add(new Node("metadata", metaAttrs));
        }
        if (capability is { Length: > 0 })
        {
            children.Add(CapabilityNode(capability));
        }
        if (rte is { Length: > 0 })
        {
            children.Add(new Node("rte", rte));
        }
        if (voipSettings is { Length: > 0 })
        {
            children.Add(new Node("voip_settings", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["uncompressed"] = "1" }, voipSettings));
        }

        return CallWrap(to, wrapperId, OfferActionNode("accept", callId, callCreator, children));
    }

    public static Node BuildReject(
        string callId,
        Jid to,
        Jid callCreator,
        string wrapperId,
        string? reason = null)
    {
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-id"] = callId,
            ["call-creator"] = callCreator,
            ["count"] = "0"
        };
        if (!string.IsNullOrEmpty(reason))
        {
            attrs["reason"] = reason;
        }
        return CallWrap(to, wrapperId, new Node("reject", attrs));
    }

    public static Node BuildTerminate(
        string callId,
        Jid to,
        Jid callCreator,
        string? wrapperId = null,
        string? reason = null)
    {
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-id"] = callId,
            ["call-creator"] = callCreator
        };
        if (!string.IsNullOrEmpty(reason))
        {
            attrs["reason"] = reason;
        }
        return CallWrap(to, wrapperId, new Node("terminate", attrs));
    }

    public static Node BuildTransport(
        string callId,
        Jid to,
        Jid callCreator,
        string? p2pCandRound = null,
        string? transportMessageType = null,
        byte[]? relayTe = null)
    {
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-id"] = callId,
            ["call-creator"] = callCreator
        };
        if (!string.IsNullOrEmpty(p2pCandRound)) attrs["p2p-cand-round"] = p2pCandRound;
        if (!string.IsNullOrEmpty(transportMessageType)) attrs["transport-message-type"] = transportMessageType;

        var children = new List<Node>(2);
        if (relayTe is { Length: > 0 })
        {
            children.Add(new Node("te", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["priority"] = "1" }, relayTe));
        }
        var netAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["medium"] = "2" };
        if (!string.Equals(transportMessageType, "9", StringComparison.Ordinal))
        {
            netAttrs["protocol"] = "0";
        }
        children.Add(new Node("net", netAttrs));

        return CallWrap(to, null, new Node("transport", attrs, new NodeContent.NodesContent(children)));
    }

    public static Node BuildRelayLatency(
        string callId,
        Jid to,
        Jid callCreator,
        IReadOnlyList<RelayLatencyEntry> entries,
        IReadOnlyList<Jid>? devices = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > RelayParser.MaxRelayLatencyEntries)
        {
            throw new ArgumentException($"Relay latency entries cannot exceed {RelayParser.MaxRelayLatencyEntries}.", nameof(entries));
        }

        var children = new List<Node>(entries.Count + 1);
        foreach (RelayLatencyEntry entry in entries)
        {
            if (entry.Address.Length is not (6 or 18))
            {
                throw new ArgumentException("Relay latency address must be 6 or 18 bytes.", nameof(entries));
            }
            string attrName = entry.Kind == RelayLatencyKind.Xpop ? "xlatency" : "latency";
            var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                [attrName] = unchecked((int)entry.Latency.Raw).ToString(CultureInfo.InvariantCulture)
            };
            if (!string.IsNullOrEmpty(entry.RelayName))
            {
                attrs["relay_name"] = entry.RelayName;
            }
            children.Add(new Node("te", attrs, entry.Address));
        }

        if (devices is { Count: > 0 })
        {
            children.Add(new Node("destination", devices.Select(d => new Node("to", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = d })).ToArray()));
        }

        return CallWrap(to, null, OfferActionNode("relaylatency", callId, callCreator, children));
    }

    public static Node BuildVideoState(
        string callId,
        Jid to,
        string wrapperId,
        Jid callCreator,
        CallVideoState state,
        string? dec = null,
        byte? deviceOrientation = null)
    {
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-id"] = callId,
            ["call-creator"] = callCreator,
            ["state"] = ((int)state).ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrEmpty(dec)) attrs["dec"] = dec;
        if (state == CallVideoState.UpgradeRequestV2) attrs["voip_settings"] = "video";
        if (deviceOrientation is byte o) attrs["device_orientation"] = o.ToString(CultureInfo.InvariantCulture);

        return CallWrap(to, wrapperId, new Node("video", attrs));
    }

    public static Node BuildMuteV2(
        string callId,
        Jid to,
        Jid callCreator,
        string wrapperId,
        bool muted)
    {
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-id"] = callId,
            ["call-creator"] = callCreator,
            ["mute-state"] = muted ? "1" : "0"
        };
        return CallWrap(to, wrapperId, new Node("mute_v2", attrs));
    }

    public static Node BuildHeartbeat(string callId, Jid callCreator, string wrapperId)
    {
        var action = new Node("heartbeat", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-id"] = callId,
            ["call-creator"] = callCreator
        });
        return new Node("call", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["to"] = new Jid(callId, Server.Call),
            ["id"] = wrapperId
        }, new[] { action });
    }

    public static Node? BuildOfferAckReceipt(IncomingCallStanza call, Jid? ownAd = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.ActionKind != CallActionKind.Offer || string.IsNullOrEmpty(call.StanzaId))
        {
            return null;
        }

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["to"] = call.From,
            ["id"] = call.StanzaId
        };
        if (ownAd is Jid ad)
        {
            attrs["from"] = ad;
        }

        var offer = new Node("offer", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-id"] = call.CallId,
            ["call-creator"] = call.CallCreator
        });
        return new Node("receipt", attrs, new[] { offer });
    }

    public static Node? BuildTypedCallAck(Node originalCall, string actionType)
    {
        ArgumentNullException.ThrowIfNull(originalCall);
        if (!string.Equals(originalCall.Tag, "call", StringComparison.Ordinal)
            || string.IsNullOrEmpty(actionType)
            || originalCall.GetAttr("id") is not { Length: > 0 } id
            || originalCall.GetAttr("from") is not { Length: > 0 } fromStr
            || !Jid.TryParse(fromStr, out Jid from))
        {
            return null;
        }

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["class"] = "call",
            ["id"] = id,
            ["to"] = from,
            ["type"] = actionType
        };

        if (originalCall.GetAttr("participant") is { Length: > 0 } partStr
            && !string.Equals(partStr, fromStr, StringComparison.Ordinal)
            && Jid.TryParse(partStr, out Jid partJid))
        {
            attrs["participant"] = partJid;
        }
        if (originalCall.GetAttr("recipient") is { Length: > 0 } recStr && Jid.TryParse(recStr, out Jid recJid))
        {
            attrs["recipient"] = recJid;
        }

        return new Node("ack", attrs);
    }

    private static CallActionKind MapActionKind(string tag) => tag switch
    {
        "offer" => CallActionKind.Offer,
        "offer_notice" => CallActionKind.OfferNotice,
        "preaccept" => CallActionKind.PreAccept,
        "accept" => CallActionKind.Accept,
        "reject" => CallActionKind.Reject,
        "terminate" => CallActionKind.Terminate,
        "transport" => CallActionKind.Transport,
        "relaylatency" => CallActionKind.RelayLatency,
        "video" => CallActionKind.VideoState,
        "mute_v2" => CallActionKind.MuteV2,
        "interruption" => CallActionKind.Interruption,
        "flowcontrol" => CallActionKind.FlowControl,
        "notify" => CallActionKind.Notify,
        "group_update" => CallActionKind.GroupUpdate,
        "enc_rekey" => CallActionKind.EncRekey,
        "waiting_room_update" => CallActionKind.WaitingRoomUpdate,
        "user_action" => CallActionKind.UserAction,
        "screen_share" => CallActionKind.ScreenShare,
        "relay_info_update" => CallActionKind.RelayInfoUpdate,
        _ => CallActionKind.Unknown
    };

    private static List<CallAudioCodec> ParseAudioCodecs(Node parent)
    {
        var list = new List<CallAudioCodec>();
        foreach (Node audio in parent.GetChildren("audio"))
        {
            string enc = audio.GetAttr("enc") ?? string.Empty;
            if (!uint.TryParse(audio.GetAttr("rate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint rate))
            {
                continue;
            }
            list.Add(new CallAudioCodec(enc, rate));
        }
        return list;
    }

    private static List<OfferRecipientEnc> ParseOfferEncs(Node offerNode)
    {
        var list = new List<OfferRecipientEnc>();
        if (offerNode.GetChild("enc") is { } bareEnc && ParseSingleOfferEnc(bareEnc) is { } parsedBare)
        {
            list.Add(new OfferRecipientEnc(null, parsedBare));
        }
        else if (offerNode.GetChild("destination") is { } dest)
        {
            foreach (Node toNode in dest.GetChildren("to"))
            {
                if (TryParseAttrJid(toNode, "jid") is Jid targetJid
                    && toNode.GetChild("enc") is { } childEnc
                    && ParseSingleOfferEnc(childEnc) is { } parsedChild)
                {
                    list.Add(new OfferRecipientEnc(targetJid, parsedChild));
                }
            }
        }
        return list;
    }

    private static OfferEnc? ParseSingleOfferEnc(Node encNode)
    {
        if (encNode.Content is not NodeContent.BytesContent bc || bc.Bytes.Length == 0)
        {
            return null;
        }
        string encType = encNode.GetAttr("type") ?? "pkmsg";
        int version = int.TryParse(encNode.GetAttr("v"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 2;
        return new OfferEnc(encType, version, bc.Bytes);
    }

    private static byte? ParseVideoOrientation(Node node) =>
        byte.TryParse(node.GetAttr("device_orientation"), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte o) && o <= 3
            ? o
            : null;

    internal static Jid? TryParseAttrJid(Node node, string attrName) =>
        node.GetAttr(attrName) is { Length: > 0 } s && Jid.TryParse(s, out Jid j) ? j : null;

    internal static Node AudioOpusNode(string rate) =>
        new("audio", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["enc"] = "opus", ["rate"] = rate });

    internal static Node VideoOfferNode() =>
        new("video", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["enc"] = VideoEncH264,
            ["dec"] = VideoDecH264,
            ["screen_width"] = "0",
            ["screen_height"] = "0",
            ["device_orientation"] = "0"
        });

    private static Node VideoAcceptNode() =>
        new("video", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["dec"] = VideoDecH264,
            ["device_orientation"] = "0"
        });

    private static Node VideoPreAcceptNode() =>
        new("video", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["dec"] = VideoDecH264,
            ["device_orientation"] = "0",
            ["screen_width"] = "0",
            ["screen_height"] = "0"
        });

    internal static Node EncoptNode() =>
        new("encopt", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["keygen"] = "2" });

    internal static Node CapabilityNode(byte[] blob, uint version = 1) =>
        new("capability", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["ver"] = version.ToString(CultureInfo.InvariantCulture) }, blob);

    internal static Node EncNode(OfferDeviceKey dk) =>
        new("enc", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["v"] = "2",
            ["type"] = dk.EncType,
            ["count"] = "0"
        }, dk.Ciphertext);

    internal static Node OfferActionNode(string tag, string callId, Jid callCreator, IReadOnlyList<Node> children) =>
        new(tag, new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-id"] = callId,
            ["call-creator"] = callCreator
        }, new NodeContent.NodesContent(children));

    internal static Node CallWrap(Jid to, string? id, Node action)
    {
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["to"] = to };
        if (!string.IsNullOrEmpty(id))
        {
            attrs["id"] = id;
        }
        return new Node("call", attrs, new[] { action });
    }
}
