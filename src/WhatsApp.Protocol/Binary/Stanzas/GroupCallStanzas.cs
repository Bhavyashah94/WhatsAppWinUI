using System.Buffers.Binary;
using System.Globalization;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum CallLinkMedia
{
    Audio,
    Video,
}

public enum ScreenShareState
{
    Started = 1,
    Stopped = 2,
}

public sealed record GroupCallDevice(
    Jid Jid,
    string? Platform = null,
    uint? Pid = null,
    uint? CapabilityVersion = null,
    byte[]? Capability = null);

public sealed record GroupCallParticipant(
    Jid Jid,
    IReadOnlyList<GroupCallDevice> Devices,
    Jid? Pn = null,
    string? State = null,
    string? ParticipantType = null,
    uint? Error = null)
{
    public bool IsConnected => string.Equals(State, "connected", StringComparison.Ordinal);
}

public sealed record GroupCallRelayEndpoint(
    uint RelayId,
    uint TokenId,
    uint AuthTokenId,
    string RelayName,
    string? DomainName,
    uint? RttMs,
    bool IsFna,
    byte[] Address,
    string? Ipv4,
    ushort? Port);

public sealed record GroupCallRelay(
    uint? TransactionId,
    uint? SelfPid,
    string Uuid,
    string ParticipantUuid,
    bool AttributePadding,
    uint? WarpMiTagLen,
    byte[] Key,
    byte[] HbhKey,
    IReadOnlyList<byte[]> Tokens,
    IReadOnlyList<byte[]> AuthTokens,
    IReadOnlyList<GroupCallRelayEndpoint> Endpoints)
{
    public override string ToString() => $"GroupCallRelay(uuid={Uuid}, endpoints={Endpoints.Count})";
}

public sealed record GroupCallUpdate(
    string CallId,
    Jid CallCreator,
    Jid? GroupJid,
    uint TransactionId,
    string Media,
    uint ConnectedLimit,
    bool Joinable,
    bool AvUpgradable,
    bool RekeyRequested,
    IReadOnlyList<GroupCallParticipant> Participants,
    GroupCallRelay? Relay = null);

public sealed record GroupCallEncRekey(
    string CallId,
    Jid CallCreator,
    uint TransactionId,
    uint KeyGeneration,
    string EncryptionType,
    uint EncryptionVersion,
    byte[] Ciphertext)
{
    public override string ToString() =>
        $"GroupCallEncRekey(callId={CallId}, tx={TransactionId}, enc={EncryptionType})";
}

public sealed record CallLink(string Token, CallLinkMedia Media)
{
    public string Url => $"https://call.whatsapp.com/{Media.ToWireString()}/{Token}";
}

public sealed record CallLinkPreview(
    string Token,
    CallLinkMedia Media,
    Jid Creator,
    Jid? CreatorPn,
    bool WaitingRoomEnabled,
    bool IsAdmin);

public sealed record WaitingRoomUser(Jid Jid, Jid? Pn, string State);

public sealed record WaitingRoom(
    string CallId,
    Jid CallCreator,
    string LinkToken,
    CallLinkMedia Media,
    bool Enabled,
    bool IsAdmin,
    uint? TransactionId,
    IReadOnlyList<WaitingRoomUser> Users);

public sealed record CallLinkJoin(
    string Token,
    CallLinkMedia Media,
    string CallId,
    Jid CallCreator,
    bool WaitingRoomEnabled,
    bool InWaitingRoom,
    bool IsAdmin,
    WaitingRoom? WaitingRoom,
    GroupCallUpdate? Group);

public sealed record ScreenShare(
    ScreenShareState State,
    uint Version,
    uint? ScreenShareId = null);

public static class CallLinkMediaExtensions
{
    public static string ToWireString(this CallLinkMedia media) => media switch
    {
        CallLinkMedia.Audio => "audio",
        CallLinkMedia.Video => "video",
        _ => throw new ArgumentOutOfRangeException(nameof(media), media, null)
    };

    public static CallLinkMedia Parse(string value) => value switch
    {
        "audio" => CallLinkMedia.Audio,
        "video" => CallLinkMedia.Video,
        _ => throw new InvalidDataException($"Unsupported call-link media '{value}'.")
    };
}

/// <summary>
/// Wire builders &amp; parsers for group calls, call links, keygen-v2 <c>&lt;enc_rekey&gt;</c>,
/// waiting rooms, raise-hand (<c>&lt;user_action&gt;</c>), and <c>&lt;screen_share&gt;</c>,
/// matching <c>wacore/src/stanza/group_call.rs</c> and live WhatsApp Web signaling.
/// </summary>
public static class GroupCallStanzas
{
    public const int GroupCallMaxParticipants = 32;
    public const int MaxRelayTokens = 64;
    public const int MaxWaitingRoomUsers = 128;

    private static readonly Jid CallServiceJid = new("", Server.Call);

    private enum SnapshotEnvelope
    {
        Offer,
        Update,
        Ack,
    }

    public static int MinRemoteParticipants(bool isGroupBound) => isGroupBound ? 1 : 2;

    // ─── Group Call Offers & Joins ────────────────────────────────────────

    public static Node BuildInitialGroupOffer(
        string callId,
        string requestId,
        Jid callCreator,
        IReadOnlyList<GroupCallParticipant> participants,
        Jid? groupJid = null,
        uint audioRate = 16000,
        bool video = false)
    {
        ValidateCallIdentity(callId, requestId, callCreator);
        ArgumentNullException.ThrowIfNull(participants);

        int minRemote = MinRemoteParticipants(groupJid is not null);
        if (participants.Count < 1 + minRemote)
        {
            throw new ArgumentException(
                groupJid is not null
                    ? "Group-bound offer requires self and at least one remote participant."
                    : "Ad-hoc group offer requires self and at least two remote participants.",
                nameof(participants));
        }
        if (participants.Count > GroupCallMaxParticipants)
        {
            throw new ArgumentOutOfRangeException(
                nameof(participants),
                $"Group offer exceeds maximum of {GroupCallMaxParticipants} participants.");
        }
        if (audioRate == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioRate), "Audio rate must be non-zero.");
        }
        if (groupJid is { } gj && gj.Server != Server.Group)
        {
            throw new ArgumentException("group_jid must use @g.us.", nameof(groupJid));
        }

        IReadOnlyList<Node> users = BuildGroupUsers(participants, callCreator, video);
        var children = new List<Node>
        {
            CallStanzas.AudioOpusNode(audioRate.ToString(CultureInfo.InvariantCulture)),
        };
        if (video)
        {
            children.Add(VideoOfferNode(0));
        }
        children.Add(new Node("net", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["medium"] = "3" }));
        children.Add(new Node("group_info", new Dictionary<string, NodeValue>(StringComparer.Ordinal), new NodeContent.NodesContent(users)));

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-id"] = callId,
            ["call-creator"] = callCreator,
        };
        if (groupJid is { } grp)
        {
            attrs["group-jid"] = grp;
        }
        var offer = new Node("offer", attrs, new NodeContent.NodesContent(children));
        return CallStanzas.CallWrap(new Jid(callId, Server.Call), requestId, offer);
    }

    public static Node BuildGroupInviteOffer(
        string callId,
        string requestId,
        Jid to,
        Jid callCreator,
        IReadOnlyList<Jid> targetDevices,
        IReadOnlyList<GroupCallParticipant> participants,
        bool video = false,
        byte deviceOrientation = 0)
    {
        ValidateCallIdentity(callId, requestId, callCreator);
        if (string.IsNullOrEmpty(to.User))
        {
            throw new ArgumentException("Group invite target is required.", nameof(to));
        }
        if (targetDevices is null || targetDevices.Count == 0)
        {
            throw new ArgumentException("At least one target device is required.", nameof(targetDevices));
        }
        if (participants is null || participants.Count == 0)
        {
            throw new ArgumentException("Group invite requires an active roster.", nameof(participants));
        }
        if (deviceOrientation > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceOrientation), "Device orientation must be in 0..=3.");
        }

        var children = new List<Node> { CallStanzas.AudioOpusNode("16000") };
        if (video)
        {
            children.Add(VideoOfferNode(deviceOrientation));
        }
        children.Add(new Node("net", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["medium"] = "2" }));
        var destNodes = targetDevices
            .Select(d => new Node("to", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = d }))
            .ToArray();
        children.Add(new Node("destination", destNodes));
        children.Add(new Node("group_info", BuildGroupUsers(participants, null, false)));

        return CallStanzas.CallWrap(to, requestId, CallStanzas.OfferActionNode("offer", callId, callCreator, children));
    }

    public static Node BuildActiveGroupPreAccept(string callId, Jid callCreator, string requestId, bool video = false)
    {
        ValidateCallIdentity(callId, requestId, callCreator);
        return CallStanzas.BuildPreAccept(
            callId,
            new Jid(callId, Server.Call),
            callCreator,
            requestId,
            ["16000"],
            video);
    }

    public static Node BuildActiveGroupAccept(string callId, Jid callCreator, string requestId, bool video = false)
    {
        ValidateCallIdentity(callId, requestId, callCreator);
        return CallStanzas.BuildAccept(
            callId,
            new Jid(callId, Server.Call),
            callCreator,
            requestId,
            ["16000"],
            video: video);
    }

    // ─── Shared Epoch Keygen-v2 Rekeying ──────────────────────────────────

    public static Node BuildGroupEncRekey(
        string callId,
        string requestId,
        Jid to,
        Jid callCreator,
        uint transactionId,
        OfferDeviceKey deviceKey,
        byte[]? deviceIdentity = null)
    {
        ValidateCallIdentity(callId, requestId, callCreator);
        ArgumentNullException.ThrowIfNull(deviceKey);
        if (transactionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionId), "Group epoch transaction-id must be non-zero.");
        }
        if (deviceKey.DeviceJid != to)
        {
            throw new ArgumentException("deviceKey.DeviceJid must match target recipient.", nameof(deviceKey));
        }
        if (deviceKey.Ciphertext.Length == 0)
        {
            throw new ArgumentException("Epoch ciphertext cannot be empty.", nameof(deviceKey));
        }
        if (deviceKey.EncType is not ("msg" or "pkmsg"))
        {
            throw new ArgumentException($"Unsupported enc_rekey enc type '{deviceKey.EncType}'.", nameof(deviceKey));
        }
        if (deviceKey.EncType == "pkmsg" && (deviceIdentity is null || deviceIdentity.Length == 0))
        {
            throw new ArgumentException("Pre-key group epoch requires companion device-identity.", nameof(deviceIdentity));
        }

        var children = new List<Node>
        {
            CallStanzas.EncoptNode(),
            CallStanzas.EncNode(deviceKey)
        };
        if (deviceIdentity is { Length: > 0 })
        {
            children.Add(new Node("device-identity", deviceIdentity));
        }

        var action = new Node("enc_rekey", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-id"] = callId,
            ["call-creator"] = callCreator,
            ["transaction-id"] = transactionId.ToString(CultureInfo.InvariantCulture),
        }, new NodeContent.NodesContent(children));
        return CallStanzas.CallWrap(to, requestId, action);
    }

    public static Node BuildGroupEncRekeyRetryReceipt(
        string stanzaId,
        Jid peerJid,
        string callId,
        Jid callCreator,
        uint registrationId,
        uint currentRetryCount = 0)
    {
        byte[] regBe = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(regBe, registrationId);

        var rekeyChild = new Node("enc_rekey", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-creator"] = callCreator,
            ["call-id"] = callId,
            ["count"] = (currentRetryCount + 1).ToString(CultureInfo.InvariantCulture),
        });
        var regChild = new Node("registration", regBe);

        return new Node("receipt", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["to"] = peerJid,
            ["id"] = stanzaId,
            ["type"] = "enc_rekey_retry",
        }, new[] { rekeyChild, regChild });
    }

    public static GroupCallEncRekey ParseGroupEncRekey(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!string.Equals(node.Tag, "enc_rekey", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected <enc_rekey>, got <{node.Tag}>.");
        }

        string callId = node.GetAttr("call-id")
            ?? throw new InvalidDataException("<enc_rekey> missing 'call-id'.");
        Jid creator = CallStanzas.TryParseAttrJid(node, "call-creator")
            ?? throw new InvalidDataException("<enc_rekey> missing/invalid 'call-creator'.");
        uint txId = ParseRequiredUInt(node, "transaction-id");

        Node encopt = node.GetChild("encopt")
            ?? throw new InvalidDataException("<enc_rekey> missing <encopt>.");
        Node enc = node.GetChild("enc")
            ?? throw new InvalidDataException("<enc_rekey> missing <enc>.");

        uint keygen = ParseRequiredUInt(encopt, "keygen");
        string encType = enc.GetAttr("type")
            ?? throw new InvalidDataException("<enc> missing 'type'.");
        uint encVer = ParseRequiredUInt(enc, "v");

        if (keygen != 2 || encVer != 2 || encType is not ("msg" or "pkmsg"))
        {
            throw new InvalidDataException($"Unsupported group epoch encryption (keygen={keygen}, v={encVer}, type={encType}).");
        }
        if (enc.Content is not NodeContent.BytesContent bc || bc.Bytes.Length == 0)
        {
            throw new InvalidDataException("<enc_rekey>/<enc> has empty ciphertext.");
        }

        return new GroupCallEncRekey(callId, creator, txId, keygen, encType, encVer, bc.Bytes);
    }

    // ─── Call Links ───────────────────────────────────────────────────────

    public static Node BuildCallLinkCreate(CallLinkMedia media, string requestId)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            throw new ArgumentException("Request ID is required.", nameof(requestId));
        }
        var action = new Node("link_create", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["media"] = media.ToWireString()
        });
        return CallStanzas.CallWrap(CallServiceJid, requestId, action);
    }

    public static Node BuildCallLinkQuery(string token, CallLinkMedia media, string requestId)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Call link token is required.", nameof(token));
        }
        if (string.IsNullOrEmpty(requestId))
        {
            throw new ArgumentException("Request ID is required.", nameof(requestId));
        }
        var action = new Node("link_query", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["token"] = token,
            ["media"] = media.ToWireString(),
        });
        return CallStanzas.CallWrap(CallServiceJid, requestId, action);
    }

    public static Node BuildCallLinkJoin(
        string token,
        CallLinkMedia media,
        string requestId,
        byte[]? capability = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Call link token is required.", nameof(token));
        }
        if (string.IsNullOrEmpty(requestId))
        {
            throw new ArgumentException("Request ID is required.", nameof(requestId));
        }
        byte[] cap = capability ?? (media == CallLinkMedia.Video
            ? CallStanzas.CapabilityVideoOffer
            : CallStanzas.CapabilityOffer);

        var children = new List<Node> { CallStanzas.AudioOpusNode("16000") };
        if (media == CallLinkMedia.Video)
        {
            children.Add(new Node("video", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
            {
                ["dec"] = CallStanzas.VideoDecH264,
                ["device_orientation"] = "0",
            }));
        }
        children.Add(new Node("net", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["medium"] = "2" }));
        children.Add(CallStanzas.CapabilityNode(cap, 1));

        var action = new Node("link_join", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["token"] = token,
            ["media"] = media.ToWireString(),
        }, new NodeContent.NodesContent(children));
        return CallStanzas.CallWrap(CallServiceJid, requestId, action);
    }

    public static CallLink ParseCallLinkCreateAck(Node ack)
    {
        Node child = ValidatedCallAckChild(ack, "link_create");
        string token = child.GetAttr("token")
            ?? throw new InvalidDataException("<link_create> ack missing 'token'.");
        string mediaRaw = child.GetAttr("media")
            ?? throw new InvalidDataException("<link_create> ack missing 'media'.");
        return new CallLink(token, CallLinkMediaExtensions.Parse(mediaRaw));
    }

    public static CallLinkPreview ParseCallLinkQueryAck(Node ack)
    {
        Node child = ValidatedCallAckChild(ack, "link_query");
        string token = child.GetAttr("token")
            ?? throw new InvalidDataException("<link_query> ack missing 'token'.");
        string mediaRaw = child.GetAttr("media")
            ?? throw new InvalidDataException("<link_query> ack missing 'media'.");
        Jid creator = CallStanzas.TryParseAttrJid(child, "link_creator")
            ?? throw new InvalidDataException("<link_query> ack missing 'link_creator'.");
        Jid? creatorPn = CallStanzas.TryParseAttrJid(child, "link_creator_pn");
        Node? waiting = child.GetChild("waiting_room");
        bool waitingEnabled = string.Equals(waiting?.GetAttr("enabled"), "1", StringComparison.Ordinal);
        bool isAdmin = string.Equals(waiting?.GetAttr("is_admin"), "1", StringComparison.Ordinal);
        return new CallLinkPreview(token, CallLinkMediaExtensions.Parse(mediaRaw), creator, creatorPn, waitingEnabled, isAdmin);
    }

    public static CallLinkJoin ParseCallLinkJoinAck(Node ack, string requestedToken)
    {
        if (string.IsNullOrEmpty(requestedToken))
        {
            throw new ArgumentException("Requested call-link token cannot be empty.", nameof(requestedToken));
        }
        ValidateCallAck(ack, "link_join");
        WaitingRoom? waiting = ack.GetChild("waiting_room") is { } wr ? ParseWaitingRoom(wr) : null;
        GroupCallUpdate? group = ParseInitialGroupCallAck(ack);

        if (waiting is null && group is null)
        {
            throw new InvalidDataException("<link_join> ack has neither <waiting_room> nor <group_info>.");
        }
        if (waiting is not null && !string.Equals(waiting.LinkToken, requestedToken, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Waiting room token differs from requested call link.");
        }
        if (group is null && waiting is { Enabled: false })
        {
            throw new InvalidDataException("<link_join> waiting-room-only ack is not enabled.");
        }

        CallLinkMedia media = waiting?.Media ?? CallLinkMediaExtensions.Parse(group!.Media);
        string callId = waiting?.CallId ?? group!.CallId;
        Jid creator = waiting?.CallCreator ?? group!.CallCreator;
        bool waitingEnabled = waiting?.Enabled ?? false;
        bool isAdmin = waiting?.IsAdmin ?? false;

        if (group is not null && (!string.Equals(group.CallId, callId, StringComparison.Ordinal) || group.CallCreator != creator))
        {
            throw new InvalidDataException("Waiting-room and admitted group identities differ.");
        }
        if (group is not null && !string.Equals(group.Media, media.ToWireString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Waiting-room and admitted group media modes differ.");
        }

        bool inWaitingRoom = waiting is not null && waitingEnabled && group is null;
        return new CallLinkJoin(requestedToken, media, callId, creator, waitingEnabled, inWaitingRoom, isAdmin, waiting, group);
    }

    // ─── Waiting Room, Raise Hand & Screen Share ──────────────────────────

    public static Node BuildWaitingRoomToggle(string callId, Jid creator, bool enabled, string requestId)
    {
        ValidateCallIdentity(callId, requestId, creator);
        return BuildWaitingRoomRequest(
            "waiting_room_toggle",
            callId,
            creator,
            requestId,
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["enabled"] = enabled ? "1" : "0" });
    }

    public static Node BuildWaitingRoomHeartbeat(string callId, Jid creator, string requestId)
    {
        ValidateCallIdentity(callId, requestId, creator);
        return BuildWaitingRoomRequest(
            "heartbeat",
            callId,
            creator,
            requestId,
            new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["type"] = "waiting_room" });
    }

    public static Node BuildWaitingRoomAdmit(string callId, Jid creator, Jid user, string requestId)
    {
        ValidateCallIdentity(callId, requestId, creator);
        return BuildWaitingRoomUserAction("waiting_room_admit", callId, creator, user, requestId);
    }

    public static Node BuildWaitingRoomDeny(string callId, Jid creator, Jid user, string requestId)
    {
        ValidateCallIdentity(callId, requestId, creator);
        return BuildWaitingRoomUserAction("waiting_room_deny", callId, creator, user, requestId);
    }

    public static Node BuildRaiseHand(string callId, Jid to, Jid creator, string requestId, bool raised)
    {
        ValidateCallIdentity(callId, requestId, creator);
        if (string.IsNullOrEmpty(to.User))
        {
            throw new ArgumentException("Raise-hand target is required.", nameof(to));
        }

        var inner = new Node("raise_hand", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["raise-hand-state"] = raised ? "1" : "0"
        });
        var action = new Node("user_action", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-id"] = callId,
            ["call-creator"] = creator,
            ["action"] = "raise_hand",
        }, new[] { inner });
        return CallStanzas.CallWrap(to, requestId, action);
    }

    public static Node BuildScreenShare(
        string callId,
        Jid to,
        Jid creator,
        string requestId,
        ScreenShareState state,
        uint? screenShareId = null)
    {
        ValidateCallIdentity(callId, requestId, creator);
        if (string.IsNullOrEmpty(to.User))
        {
            throw new ArgumentException("Screen-share target is required.", nameof(to));
        }

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["call-id"] = callId,
            ["call-creator"] = creator,
            ["screenshare_state"] = ((int)state).ToString(CultureInfo.InvariantCulture),
            ["version"] = "2",
        };
        if (screenShareId is uint id)
        {
            attrs["screen_share_id"] = id.ToString(CultureInfo.InvariantCulture);
        }
        return CallStanzas.CallWrap(to, requestId, new Node("screen_share", attrs));
    }

    // ─── Inbound Group / Waiting Room / Participant Parsers ───────────────

    public static GroupCallUpdate? ParseGroupInviteSnapshot(Node offer) =>
        ParseGroupSnapshot(offer, SnapshotEnvelope.Offer);

    public static GroupCallUpdate ParseGroupUpdate(Node groupUpdateNode) =>
        ParseGroupSnapshot(groupUpdateNode, SnapshotEnvelope.Update)
        ?? throw new InvalidDataException("<group_update> missing <group_info>.");

    public static GroupCallUpdate? ParseInitialGroupCallAck(Node ack) =>
        ParseGroupSnapshot(ack, SnapshotEnvelope.Ack);

    public static WaitingRoom ParseWaitingRoomUpdate(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!string.Equals(node.Tag, "waiting_room_update", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected <waiting_room_update>, got <{node.Tag}>.");
        }

        string callId = node.GetAttr("call-id")
            ?? throw new InvalidDataException("<waiting_room_update> missing 'call-id'.");
        Jid creator = CallStanzas.TryParseAttrJid(node, "call-creator")
            ?? throw new InvalidDataException("<waiting_room_update> missing/invalid 'call-creator'.");

        Node wr = node.GetChild("waiting_room")
            ?? throw new InvalidDataException("<waiting_room_update> missing <waiting_room>.");
        WaitingRoom room = ParseWaitingRoom(wr);
        if (!string.Equals(room.CallId, callId, StringComparison.Ordinal) || room.CallCreator != creator)
        {
            throw new InvalidDataException("Waiting room update identity mismatch.");
        }
        return room;
    }

    public static bool ParseRaiseHand(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!string.Equals(node.Tag, "user_action", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected <user_action>, got <{node.Tag}>.");
        }
        if (!string.Equals(node.GetAttr("action"), "raise_hand", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported <user_action> action '{node.GetAttr("action")}'.");
        }
        Node rh = node.GetChild("raise_hand")
            ?? throw new InvalidDataException("<user_action> missing <raise_hand> child.");
        return rh.GetAttr("raise-hand-state") switch
        {
            "1" => true,
            "0" => false,
            var bad => throw new InvalidDataException($"Invalid raise-hand-state '{bad}'.")
        };
    }

    public static ScreenShare ParseScreenShare(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!string.Equals(node.Tag, "screen_share", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected <screen_share>, got <{node.Tag}>.");
        }
        uint stateCode = ParseRequiredUInt(node, "screenshare_state");
        if (stateCode is not (1 or 2))
        {
            throw new InvalidDataException($"Unsupported screenshare_state {stateCode}.");
        }
        uint version = ParseRequiredUInt(node, "version");
        uint? shareId = TryParseOptionalUInt(node, "screen_share_id");
        return new ScreenShare((ScreenShareState)stateCode, version, shareId);
    }

    // ─── Internal Helpers ─────────────────────────────────────────────────

    private static GroupCallUpdate? ParseGroupSnapshot(Node node, SnapshotEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(node);
        string expectedTag = envelope switch
        {
            SnapshotEnvelope.Offer => "offer",
            SnapshotEnvelope.Update => "group_update",
            SnapshotEnvelope.Ack => "ack",
            _ => throw new ArgumentOutOfRangeException(nameof(envelope))
        };
        if (!string.Equals(node.Tag, expectedTag, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected <{expectedTag}>, got <{node.Tag}>.");
        }

        if (envelope == SnapshotEnvelope.Ack)
        {
            string? err = node.GetAttr("error");
            if (!string.IsNullOrEmpty(err) && err is not ("0" or "207"))
            {
                throw new InvalidDataException($"Group offer ack rejected with error {err}.");
            }
        }

        Node? groupInfo = node.GetChild("group_info");
        if (groupInfo is null)
        {
            return envelope == SnapshotEnvelope.Update
                ? throw new InvalidDataException("<group_update> missing <group_info>.")
                : null;
        }

        Node identityNode = envelope == SnapshotEnvelope.Ack ? groupInfo : node;
        string callId = identityNode.GetAttr("call-id")
            ?? throw new InvalidDataException($"<{identityNode.Tag}> missing 'call-id'.");
        Jid callCreator = CallStanzas.TryParseAttrJid(identityNode, "call-creator")
            ?? throw new InvalidDataException($"<{identityNode.Tag}> missing/invalid 'call-creator'.");

        GroupCallUpdate update = ParseGroupInfo(groupInfo, envelope, callId, callCreator);
        if (envelope == SnapshotEnvelope.Offer)
        {
            Jid? outerGrp = CallStanzas.TryParseAttrJid(node, "group-jid");
            if (outerGrp is not null && update.GroupJid is not null && outerGrp != update.GroupJid)
            {
                throw new InvalidDataException("Outer offer group-jid does not match <group_info>.");
            }
            bool joinable = update.Joinable || string.Equals(node.GetAttr("joinable"), "1", StringComparison.Ordinal);
            update = update with { GroupJid = outerGrp ?? update.GroupJid, Joinable = joinable };
        }
        else if (envelope == SnapshotEnvelope.Update)
        {
            bool avUpgradable = node.GetChild("av_upgrade") is { } av
                && string.Equals(av.GetAttr("av-upgradable"), "1", StringComparison.Ordinal);
            GroupCallRelay? relay = node.GetChild("relay") is { } r ? ParseGroupRelay(r) : null;
            update = update with { AvUpgradable = avUpgradable, Relay = relay };
        }
        else
        {
            GroupCallRelay? relay = node.GetChild("relay") is { } r ? ParseGroupRelay(r) : null;
            update = update with { Relay = relay };
        }

        return update;
    }

    private static GroupCallUpdate ParseGroupInfo(
        Node groupInfo,
        SnapshotEnvelope envelope,
        string callId,
        Jid callCreator)
    {
        Jid? groupJid = CallStanzas.TryParseAttrJid(groupInfo, "group-jid");
        string media = groupInfo.GetAttr("media")
            ?? throw new InvalidDataException("<group_info> missing 'media'.");

        uint transactionId = envelope == SnapshotEnvelope.Ack
            ? TryParseOptionalUInt(groupInfo, "transaction-id") ?? 0
            : ParseRequiredUInt(groupInfo, "transaction-id");
        uint connectedLimit = envelope == SnapshotEnvelope.Ack
            ? TryParseOptionalUInt(groupInfo, "connected-limit") ?? 0
            : ParseRequiredUInt(groupInfo, "connected-limit");

        bool joinable = string.Equals(groupInfo.GetAttr("joinable"), "1", StringComparison.Ordinal);
        bool rekeyRequested = string.Equals(groupInfo.GetAttr("rekey"), "1", StringComparison.Ordinal);

        var participants = new List<GroupCallParticipant>();
        foreach (Node child in groupInfo.GetChildren("user"))
        {
            participants.Add(ParseGroupParticipant(child));
        }

        return new GroupCallUpdate(
            callId,
            callCreator,
            groupJid,
            transactionId,
            media,
            connectedLimit,
            joinable,
            false,
            rekeyRequested,
            participants);
    }

    private static GroupCallParticipant ParseGroupParticipant(Node node)
    {
        Jid jid = CallStanzas.TryParseAttrJid(node, "jid")
            ?? throw new InvalidDataException("Group call <user> missing/invalid 'jid'.");
        Jid? pn = CallStanzas.TryParseAttrJid(node, "user_pn");
        string? state = node.GetAttr("state") is { Length: > 0 } st ? st : null;
        string? participantType = node.GetAttr("type");
        uint? error = TryParseOptionalUInt(node, "error");

        var devices = new List<GroupCallDevice>();
        foreach (Node dev in node.GetChildren("device"))
        {
            devices.Add(ParseGroupDevice(dev));
        }
        return new GroupCallParticipant(jid, devices, pn, state, participantType, error);
    }

    private static GroupCallDevice ParseGroupDevice(Node node)
    {
        Jid jid = CallStanzas.TryParseAttrJid(node, "jid")
            ?? throw new InvalidDataException("Group call <device> missing/invalid 'jid'.");
        string? platform = node.GetAttr("platform");
        uint? pid = TryParseOptionalUInt(node, "pid");

        uint? capVer = null;
        byte[]? capBytes = null;
        if (node.GetChild("capability") is { } cap)
        {
            capVer = TryParseOptionalUInt(cap, "ver");
            capBytes = cap.Content is NodeContent.BytesContent bc ? bc.Bytes : Array.Empty<byte>();
        }
        return new GroupCallDevice(jid, platform, pid, capVer, capBytes);
    }

    private static GroupCallRelay ParseGroupRelay(Node node)
    {
        uint? txId = TryParseOptionalUInt(node, "transaction-id");
        uint? selfPid = TryParseOptionalUInt(node, "self_pid");
        string uuid = node.GetAttr("uuid") ?? throw new InvalidDataException("Group <relay> missing 'uuid'.");
        string participantUuid = node.GetAttr("participant_uuid")
            ?? throw new InvalidDataException("Group <relay> missing 'participant_uuid'.");
        bool attrPad = string.Equals(node.GetAttr("attribute_padding"), "1", StringComparison.Ordinal);
        uint? warpMiTagLen = TryParseOptionalUInt(node, "warp_mi_tag_len");

        byte[] ContentBytes(string tag) =>
            node.GetChild(tag)?.Content is NodeContent.BytesContent bc ? bc.Bytes : Array.Empty<byte>();

        var endpoints = node.GetChildren("te2").Select(ParseGroupRelayEndpoint).ToList();
        return new GroupCallRelay(
            txId,
            selfPid,
            uuid,
            participantUuid,
            attrPad,
            warpMiTagLen,
            ContentBytes("key"),
            ContentBytes("hbh_key"),
            ParseIndexedTokens(node, "token"),
            ParseIndexedTokens(node, "auth_token"),
            endpoints);
    }

    private static GroupCallRelayEndpoint ParseGroupRelayEndpoint(Node node)
    {
        uint relayId = ParseRequiredUInt(node, "relay_id");
        uint tokenId = ParseRequiredUInt(node, "token_id");
        uint authTokenId = ParseRequiredUInt(node, "auth_token_id");
        string relayName = node.GetAttr("relay_name")
            ?? throw new InvalidDataException("<te2> missing 'relay_name'.");
        string? domainName = node.GetAttr("domain_name");
        uint? rttMs = TryParseOptionalUInt(node, "c2r_rtt");
        bool isFna = string.Equals(node.GetAttr("is_fna"), "1", StringComparison.Ordinal);
        byte[] addr = node.Content is NodeContent.BytesContent bc ? bc.Bytes : Array.Empty<byte>();

        string? ipv4 = null;
        ushort? port = null;
        if (addr.Length == 6)
        {
            ipv4 = $"{addr[0]}.{addr[1]}.{addr[2]}.{addr[3]}";
            port = BinaryPrimitives.ReadUInt16BigEndian(addr.AsSpan(4, 2));
        }

        return new GroupCallRelayEndpoint(relayId, tokenId, authTokenId, relayName, domainName, rttMs, isFna, addr, ipv4, port);
    }

    private static IReadOnlyList<byte[]> ParseIndexedTokens(Node parent, string tag)
    {
        var list = new List<byte[]>();
        foreach (Node child in parent.GetChildren(tag))
        {
            if (child.Content is not NodeContent.BytesContent bc)
            {
                continue;
            }
            int idx = int.TryParse(child.GetAttr("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0
                ? parsed
                : list.Count;
            if (idx >= MaxRelayTokens)
            {
                continue;
            }
            while (list.Count <= idx)
            {
                list.Add(Array.Empty<byte>());
            }
            list[idx] = bc.Bytes;
        }
        return list;
    }

    private static WaitingRoom ParseWaitingRoom(Node node)
    {
        if (!string.Equals(node.Tag, "waiting_room", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected <waiting_room>, got <{node.Tag}>.");
        }

        string callId = node.GetAttr("call-id") ?? throw new InvalidDataException("<waiting_room> missing 'call-id'.");
        Jid creator = CallStanzas.TryParseAttrJid(node, "call-creator")
            ?? throw new InvalidDataException("<waiting_room> missing 'call-creator'.");
        string token = node.GetAttr("link-token") ?? throw new InvalidDataException("<waiting_room> missing 'link-token'.");
        CallLinkMedia media = CallLinkMediaExtensions.Parse(
            node.GetAttr("media") ?? throw new InvalidDataException("<waiting_room> missing 'media'."));
        bool enabled = string.Equals(node.GetAttr("enabled"), "1", StringComparison.Ordinal);
        bool isAdmin = string.Equals(node.GetAttr("is_admin"), "1", StringComparison.Ordinal);
        uint? txId = TryParseOptionalUInt(node, "transaction-id");

        var users = new List<WaitingRoomUser>();
        foreach (Node child in node.GetChildren("user"))
        {
            if (users.Count >= MaxWaitingRoomUsers)
            {
                throw new InvalidDataException("Waiting-room snapshot exceeds user limit.");
            }
            Jid uJid = CallStanzas.TryParseAttrJid(child, "jid")
                ?? throw new InvalidDataException("<waiting_room>/<user> missing 'jid'.");
            Jid? pn = CallStanzas.TryParseAttrJid(child, "user_pn");
            string state = child.GetAttr("state") ?? throw new InvalidDataException("<waiting_room>/<user> missing 'state'.");
            users.Add(new WaitingRoomUser(uJid, pn, state));
        }

        return new WaitingRoom(callId, creator, token, media, enabled, isAdmin, txId, users);
    }

    private static Node BuildWaitingRoomUserAction(string tag, string callId, Jid creator, Jid user, string requestId)
    {
        if (string.IsNullOrEmpty(user.User))
        {
            throw new ArgumentException("Waiting room user is required.", nameof(user));
        }
        return BuildWaitingRoomRequest(
            tag,
            callId,
            creator,
            requestId,
            new Dictionary<string, NodeValue>(StringComparer.Ordinal),
            [new Node("user", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = user })]);
    }

    private static Node BuildWaitingRoomRequest(
        string tag,
        string callId,
        Jid creator,
        string requestId,
        Dictionary<string, NodeValue> extraAttrs,
        IReadOnlyList<Node>? children = null)
    {
        extraAttrs["call-id"] = callId;
        extraAttrs["call-creator"] = creator;
        Node action = children is { Count: > 0 }
            ? new Node(tag, extraAttrs, new NodeContent.NodesContent(children))
            : new Node(tag, extraAttrs);
        return CallStanzas.CallWrap(new Jid(callId, Server.Call), requestId, action);
    }

    private static Node[] BuildGroupUsers(IReadOnlyList<GroupCallParticipant> participants, Jid? videoCreator, bool video)
    {
        var users = new Node[participants.Count];
        for (int i = 0; i < participants.Count; i++)
        {
            GroupCallParticipant p = participants[i];
            if (string.IsNullOrEmpty(p.Jid.User) || p.Devices.Count == 0)
            {
                throw new ArgumentException($"Participant {i} requires a JID and at least one device.", nameof(participants));
            }
            var devs = new Node[p.Devices.Count];
            for (int d = 0; d < p.Devices.Count; d++)
            {
                GroupCallDevice dev = p.Devices[d];
                var devAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = dev.Jid };
                if (dev.Capability is { Length: > 0 } rawCap)
                {
                    byte[] cap = rawCap;
                    if (video && videoCreator is { } vc && vc.ToNonAd() == dev.Jid.ToNonAd())
                    {
                        if (rawCap.AsSpan().SequenceEqual(CallStanzas.CapabilityOffer))
                        {
                            cap = CallStanzas.CapabilityVideoOffer;
                        }
                        else if (rawCap.AsSpan().SequenceEqual(CallStanzas.CapabilityStandardOpusOffer))
                        {
                            cap = CallStanzas.CapabilityStandardOpusVideoOffer;
                        }
                    }
                    devs[d] = new Node("device", devAttrs, new[] { CallStanzas.CapabilityNode(cap, dev.CapabilityVersion ?? 1) });
                }
                else
                {
                    devs[d] = new Node("device", devAttrs);
                }
            }

            var userAttrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = p.Jid };
            if (!string.IsNullOrEmpty(p.State))
            {
                userAttrs["state"] = p.State;
            }
            users[i] = new Node("user", userAttrs, devs);
        }
        return users;
    }

    private static Node VideoOfferNode(byte orientation) =>
        new("video", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["enc"] = CallStanzas.VideoEncH264,
            ["dec"] = CallStanzas.VideoDecH264,
            ["screen_width"] = "0",
            ["screen_height"] = "0",
            ["device_orientation"] = orientation.ToString(CultureInfo.InvariantCulture),
        });

    private static Node ValidatedCallAckChild(Node ack, string expectedType)
    {
        ValidateCallAck(ack, expectedType);
        return ack.GetChild(expectedType)
            ?? throw new InvalidDataException($"<ack type={expectedType}> missing <{expectedType}> child.");
    }

    private static void ValidateCallAck(Node ack, string expectedType)
    {
        ArgumentNullException.ThrowIfNull(ack);
        if (!string.Equals(ack.Tag, "ack", StringComparison.Ordinal)
            || !string.Equals(ack.GetAttr("class"), "call", StringComparison.Ordinal)
            || !string.Equals(ack.GetAttr("type"), expectedType, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unexpected ack envelope for '{expectedType}'.");
        }
        string? err = ack.GetAttr("error");
        if (!string.IsNullOrEmpty(err))
        {
            throw new InvalidDataException($"Call request '{expectedType}' rejected with error {err}.");
        }
    }

    private static void ValidateCallIdentity(string callId, string requestId, Jid creator)
    {
        if (string.IsNullOrWhiteSpace(callId))
        {
            throw new ArgumentException("callId is required.", nameof(callId));
        }
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId is required.", nameof(requestId));
        }
        if (string.IsNullOrEmpty(creator.User))
        {
            throw new ArgumentException("callCreator is required.", nameof(creator));
        }
    }

    private static uint ParseRequiredUInt(Node node, string attr)
    {
        string raw = node.GetAttr(attr) ?? throw new InvalidDataException($"<{node.Tag}> missing '{attr}'.");
        if (!uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint val))
        {
            throw new InvalidDataException($"<{node.Tag}> invalid unsigned integer '{attr}={raw}'.");
        }
        return val;
    }

    private static uint? TryParseOptionalUInt(Node node, string attr) =>
        uint.TryParse(node.GetAttr(attr), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint val) ? val : null;
}
