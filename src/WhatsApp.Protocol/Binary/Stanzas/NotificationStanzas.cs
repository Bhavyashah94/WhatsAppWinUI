using System.Globalization;
using System.Text;

namespace WhatsApp.Protocol.Binary.Stanzas;

public enum DeviceNotificationOp
{
    Add = 0,
    Remove = 1,
    Update = 2,
}

public sealed record DeviceNotificationElement(
    Jid DeviceJid,
    uint? KeyIndex,
    Jid? LidDeviceJid)
{
    public uint DeviceId => DeviceJid.Device;
}

public sealed record ParsedDevicesNotification(
    Jid From,
    string? StanzaId,
    long TimestampSeconds,
    DeviceNotificationOp Operation,
    IReadOnlyList<DeviceNotificationElement> Devices,
    long? KeyIndexTimestampSeconds,
    byte[]? SignedKeyIndexBytes,
    string? ContactHash);

public enum EncryptNotificationKind
{
    Unknown = 0,
    IdentityChange = 1,
    PreKeyLow = 2,
    Digest = 3,
}

public sealed record ParsedEncryptNotification(
    EncryptNotificationKind Kind,
    Jid From,
    string? StanzaId,
    long TimestampSeconds,
    Jid? LidJid,
    string? DisplayName,
    bool IsOffline,
    bool HasCount,
    int? RemainingPreKeyCount,
    bool HasPqCount);

public sealed record ParsedDisappearingModeNotification(
    Jid From,
    uint DurationSeconds,
    long SettingTimestampSeconds,
    bool EphemeralityDisabled,
    bool IsActionOnly,
    string? Action);

public sealed record ParsedPictureNotification(
    Jid From,
    Jid TargetJid,
    Jid? AuthorJid,
    bool Removed,
    string? PictureId,
    string? ContactHash,
    long TimestampSeconds);

public sealed record ParsedBusinessNotification(
    Jid From,
    string? StanzaId,
    string UpdateType,
    Jid? TargetJid,
    string? ContactHash,
    string? VerifiedName,
    long TimestampSeconds);

public sealed record ParsedContactsNotification(
    Jid From,
    string Action,
    Jid? OldPnJid,
    Jid? NewPnJid,
    Jid? OldLidJid,
    Jid? NewLidJid,
    string? ContactHash,
    long TimestampSeconds);

public sealed record ParsedAccountSyncNotification(
    Jid From,
    string? PushName,
    ParsedDevicesNotification? Devices,
    ParsedDisappearingModeNotification? DisappearingMode,
    string? UsernameAction,
    long TimestampSeconds);

public sealed record ParsedGroupAction(
    string Action,
    IReadOnlyList<Jid> ParticipantJids,
    IReadOnlyList<Jid> ParticipantPnJids,
    string? Subject = null,
    string? Description = null,
    string? DescriptionId = null,
    uint? EphemeralExpiration = null,
    bool? ToggleValue = null,
    string? ModeValue = null,
    string? PrevAddressingMode = null,
    IReadOnlyList<Jid>? LinkedGroupJids = null,
    string? LinkKind = null,
    string? UnlinkReason = null);

public sealed record ParsedGroupNotification(
    Jid From,
    string? StanzaId,
    long TimestampSeconds,
    bool IsDirtyNotification,
    IReadOnlyList<Jid> DirtyGroupJids,
    string Action,
    Jid? ActorJid,
    IReadOnlyList<Jid> ParticipantJids,
    IReadOnlyList<Jid> ParticipantPnJids,
    string? Subject,
    bool? RotateSenderKeyRequired,
    IReadOnlyList<ParsedGroupAction>? Actions = null,
    string? AddressingMode = null);

public sealed record ParsedMexNotification(
    Jid From,
    string? StanzaId,
    string Operation,
    string RawPayloadJson,
    long TimestampSeconds);

/// <summary>
/// Typed parsers for incoming <c>&lt;notification&gt;</c> stanzas matching
/// live <c>WAWebHandle*Notification</c> and <c>wacore/src/stanza/*</c>.
/// </summary>
public static class NotificationStanzas
{
    public static long ParseTimestampSeconds(Node notification, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (long.TryParse(notification.GetAttr("t"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ts) && ts > 0)
        {
            return ts;
        }
        return (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
    }

    public static ParsedDevicesNotification? ParseDevicesNotification(Node notification, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!TryGetFromJid(notification, out Jid from))
        {
            return null;
        }

        // Priority matching WAWebHandleDeviceNotification: remove > add > update
        Node? opNode = notification.GetChild("remove")
            ?? notification.GetChild("add")
            ?? notification.GetChild("update");
        if (opNode is null)
        {
            return null;
        }

        long notifTs = ParseTimestampSeconds(notification, time);
        string? stanzaId = notification.GetAttr("id");

        if (opNode.Tag == "update")
        {
            string? hash = opNode.GetAttr("hash");
            if (string.IsNullOrEmpty(hash))
            {
                return null;
            }
            return new ParsedDevicesNotification(
                from,
                stanzaId,
                notifTs,
                DeviceNotificationOp.Update,
                Array.Empty<DeviceNotificationElement>(),
                null,
                null,
                hash);
        }

        DeviceNotificationOp op = opNode.Tag == "remove"
            ? DeviceNotificationOp.Remove
            : DeviceNotificationOp.Add;

        Node? keyIdxNode = opNode.GetChild("key-index-list");
        if (keyIdxNode is null
            || !long.TryParse(keyIdxNode.GetAttr("ts"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long keyTs)
            || (op == DeviceNotificationOp.Remove && keyTs == 0))
        {
            return null;
        }

        byte[]? signedBytes = keyIdxNode.Content is NodeContent.BytesContent bc && bc.Bytes.Length > 0
            ? bc.Bytes
            : null;

        var devices = new List<DeviceNotificationElement>();
        foreach (Node devNode in opNode.GetChildren("device"))
        {
            if (devNode.GetAttr("jid") is not { Length: > 0 } devJidStr
                || !Jid.TryParse(devJidStr, out Jid devJid))
            {
                continue;
            }

            Jid? lidDevJid = null;
            if (devNode.GetAttr("lid") is { Length: > 0 } lidStr && Jid.TryParse(lidStr, out Jid parsedLid))
            {
                // WA Web rejects device elements where jid and lid disagree on device ID.
                if (parsedLid.Device != devJid.Device)
                {
                    return null;
                }
                lidDevJid = parsedLid;
            }

            uint? keyIdx = uint.TryParse(devNode.GetAttr("key-index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ki)
                ? ki
                : null;

            devices.Add(new DeviceNotificationElement(devJid, keyIdx, lidDevJid));
        }

        if (devices.Count == 0)
        {
            return null;
        }

        return new ParsedDevicesNotification(
            from,
            stanzaId,
            notifTs,
            op,
            devices,
            keyTs,
            signedBytes,
            ContactHash: null);
    }

    public static ParsedEncryptNotification? ParseEncryptNotification(Node notification, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!TryGetFromJid(notification, out Jid from))
        {
            return null;
        }

        long ts = ParseTimestampSeconds(notification, time);
        string? stanzaId = notification.GetAttr("id");
        bool isOffline = notification.GetAttr("offline") is not null;

        if (notification.GetChild("identity") is not null)
        {
            Jid? lidJid = notification.GetAttr("lid") is { Length: > 0 } lStr && Jid.TryParse(lStr, out Jid lj) ? lj : null;
            return new ParsedEncryptNotification(
                EncryptNotificationKind.IdentityChange,
                from,
                stanzaId,
                ts,
                lidJid,
                notification.GetAttr("display_name") ?? notification.GetAttr("notify"),
                isOffline,
                HasCount: false,
                RemainingPreKeyCount: null,
                HasPqCount: false);
        }

        Node? countNode = notification.GetChild("count");
        Node? pqCountNode = notification.GetChild("pq_count");
        if (countNode is not null || pqCountNode is not null)
        {
            int? countVal = countNode is not null
                && int.TryParse(countNode.GetAttr("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c)
                ? c
                : null;

            return new ParsedEncryptNotification(
                EncryptNotificationKind.PreKeyLow,
                from,
                stanzaId,
                ts,
                LidJid: null,
                DisplayName: null,
                isOffline,
                HasCount: countNode is not null,
                RemainingPreKeyCount: countVal,
                HasPqCount: pqCountNode is not null);
        }

        if (notification.GetChild("digest") is not null)
        {
            return new ParsedEncryptNotification(
                EncryptNotificationKind.Digest,
                from,
                stanzaId,
                ts,
                LidJid: null,
                DisplayName: null,
                isOffline,
                HasCount: false,
                RemainingPreKeyCount: null,
                HasPqCount: false);
        }

        return new ParsedEncryptNotification(
            EncryptNotificationKind.Unknown,
            from,
            stanzaId,
            ts,
            null,
            null,
            isOffline,
            false,
            null,
            false);
    }

    public static ParsedDisappearingModeNotification? ParseDisappearingModeNotification(Node notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!TryGetFromJid(notification, out Jid from))
        {
            return null;
        }

        Node? dm = notification.GetChild("disappearing_mode");
        if (dm is null)
        {
            return null;
        }

        if (dm.GetAttr("action") is { Length: > 0 } action)
        {
            return new ParsedDisappearingModeNotification(from, 0, 0, false, IsActionOnly: true, Action: action);
        }

        if (!long.TryParse(dm.GetAttr("t"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long settingTs))
        {
            return null;
        }

        uint duration = uint.TryParse(dm.GetAttr("duration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint d)
            ? d
            : 0u;
        bool disabled = string.Equals(dm.GetAttr("ephemerality_disabled"), "true", StringComparison.OrdinalIgnoreCase);
        return new ParsedDisappearingModeNotification(from, duration, settingTs, disabled, IsActionOnly: false, Action: null);
    }

    public static ParsedPictureNotification? ParsePictureNotification(Node notification, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!TryGetFromJid(notification, out Jid from))
        {
            return null;
        }

        long ts = ParseTimestampSeconds(notification, time);
        if (notification.GetChild("set") is { } setNode)
        {
            Jid target = setNode.GetAttr("jid") is { Length: > 0 } jStr && Jid.TryParse(jStr, out Jid j) ? j : from;
            Jid? author = setNode.GetAttr("author") is { Length: > 0 } aStr && Jid.TryParse(aStr, out Jid a) ? a : null;
            return new ParsedPictureNotification(
                from,
                target,
                author,
                Removed: false,
                PictureId: setNode.GetAttr("id"),
                ContactHash: setNode.GetAttr("hash"),
                ts);
        }

        if (notification.GetChild("delete") is { } delNode)
        {
            Jid target = delNode.GetAttr("jid") is { Length: > 0 } jStr && Jid.TryParse(jStr, out Jid j) ? j : from;
            Jid? author = delNode.GetAttr("author") is { Length: > 0 } aStr && Jid.TryParse(aStr, out Jid a) ? a : null;
            return new ParsedPictureNotification(from, target, author, Removed: true, PictureId: null, ContactHash: delNode.GetAttr("hash"), ts);
        }

        if (notification.Content is not NodeContent.NodesContent nc || nc.Nodes.Count == 0)
        {
            Jid target = notification.GetAttr("jid") is { Length: > 0 } jStr && Jid.TryParse(jStr, out Jid j) ? j : from;
            Jid? author = notification.GetAttr("author") is { Length: > 0 } aStr && Jid.TryParse(aStr, out Jid a) ? a : null;
            return new ParsedPictureNotification(from, target, author, Removed: true, PictureId: null, ContactHash: null, ts);
        }

        return null;
    }

    public static ParsedContactsNotification? ParseContactsNotification(Node notification, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!TryGetFromJid(notification, out Jid from))
        {
            return null;
        }

        long ts = ParseTimestampSeconds(notification, time);

        if (notification.GetChild("modify") is { } modNode)
        {
            Jid? oldPn = TryParseAttrJid(modNode, "old");
            Jid? newPn = TryParseAttrJid(modNode, "new");
            Jid? oldLid = TryParseAttrJid(modNode, "old_lid");
            Jid? newLid = TryParseAttrJid(modNode, "new_lid");
            return new ParsedContactsNotification(from, "modify", oldPn, newPn, oldLid, newLid, null, ts);
        }

        if (notification.GetChild("update") is { } updNode)
        {
            return new ParsedContactsNotification(
                from,
                "update",
                TryParseAttrJid(updNode, "jid"),
                null,
                null,
                null,
                updNode.GetAttr("hash"),
                ts);
        }

        if (notification.GetChild("remove") is { } remNode)
        {
            return new ParsedContactsNotification(from, "remove", TryParseAttrJid(remNode, "jid") ?? from, null, null, null, null, ts);
        }

        return null;
    }

    public static ParsedBusinessNotification? ParseBusinessNotification(Node notification, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!TryGetFromJid(notification, out Jid from))
        {
            return null;
        }

        long ts = ParseTimestampSeconds(notification, time);
        Node? firstChild = (notification.Content as NodeContent.NodesContent)?.Nodes.FirstOrDefault();
        if (firstChild is null)
        {
            return null;
        }

        Jid? targetJid = TryParseAttrJid(firstChild, "jid");
        string? hash = firstChild.GetAttr("hash");
        string updateType = firstChild.Tag switch
        {
            "remove" => targetJid is not null ? "remove_jid" : "remove_hash",
            "verified_name" => targetJid is not null ? "verified_name_jid" : "verified_name_hash",
            "profile" => hash is not null ? "profile_hash" : "profile",
            _ => firstChild.Tag
        };

        string? verifiedName = firstChild.Tag == "verified_name"
            ? UsyncProtocol.DecodeVerifiedNameFromNode(firstChild)?.Name ?? firstChild.GetAttr("name")
            : null;

        return new ParsedBusinessNotification(
            from,
            notification.GetAttr("id"),
            updateType,
            targetJid,
            hash,
            verifiedName,
            ts);
    }

    public static ParsedAccountSyncNotification? ParseAccountSyncNotification(Node notification, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!TryGetFromJid(notification, out Jid from))
        {
            return null;
        }

        long ts = ParseTimestampSeconds(notification, time);
        string? pushName = notification.GetAttr("pushname");
        ParsedDevicesNotification? devices = notification.GetChild("devices") is not null
            ? ParseAccountSyncDevices(notification, from, ts)
            : null;
        ParsedDisappearingModeNotification? dm = ParseDisappearingModeNotification(notification);
        string? usernameAction = notification.GetChild("username")?.GetAttr("action");

        return new ParsedAccountSyncNotification(from, pushName, devices, dm, usernameAction, ts);
    }

    private static ParsedDevicesNotification? ParseAccountSyncDevices(Node notification, Jid from, long notifTs)
    {
        Node? devWrapper = notification.GetChild("devices");
        if (devWrapper is null)
        {
            return null;
        }

        long keyTs = long.TryParse(devWrapper.GetChild("key-index-list")?.GetAttr("ts"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long t)
            ? t
            : notifTs;
        byte[]? signedBytes = devWrapper.GetChild("key-index-list")?.Content is NodeContent.BytesContent bc
            ? bc.Bytes
            : null;

        var devices = new List<DeviceNotificationElement>();
        foreach (Node d in devWrapper.GetChildren("device"))
        {
            if (TryParseAttrJid(d, "jid") is Jid jid)
            {
                uint? ki = uint.TryParse(d.GetAttr("key-index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint k) ? k : null;
                devices.Add(new DeviceNotificationElement(jid, ki, TryParseAttrJid(d, "lid")));
            }
        }

        return new ParsedDevicesNotification(
            from,
            notification.GetAttr("id"),
            notifTs,
            DeviceNotificationOp.Update,
            devices,
            keyTs,
            signedBytes,
            ContactHash: null);
    }

    public static ParsedGroupNotification? ParseGroupNotification(Node notification, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!TryGetFromJid(notification, out Jid from))
        {
            return null;
        }

        long ts = ParseTimestampSeconds(notification, time);
        string? stanzaId = notification.GetAttr("id");

        if (notification.GetChild("groups_dirty") is { } dirtyNode)
        {
            var dirtyGroups = new List<Jid>();
            foreach (Node g in dirtyNode.GetChildren("group"))
            {
                if (TryParseAttrJid(g, "jid") is Jid gJid)
                {
                    dirtyGroups.Add(gJid);
                }
            }
            return new ParsedGroupNotification(
                from,
                stanzaId,
                ts,
                IsDirtyNotification: true,
                dirtyGroups,
                "groups_dirty",
                ActorJid: null,
                Array.Empty<Jid>(),
                Array.Empty<Jid>(),
                Subject: null,
                RotateSenderKeyRequired: false);
        }

        Node? actionNode = (notification.Content as NodeContent.NodesContent)?.Nodes.FirstOrDefault();
        string action = actionNode?.Tag ?? "unknown";
        Jid? actor = TryParseAttrJid(notification, "participant") ?? TryParseAttrJid(notification, "author");
        string? addressingMode = notification.GetAttr("addressing_mode");

        var actions = new List<ParsedGroupAction>();
        var allParticipantJids = new List<Jid>();
        var allParticipantPns = new List<Jid>();
        bool rotateSenderKey = false;

        if (notification.Content is NodeContent.NodesContent nc)
        {
            foreach (Node act in nc.Nodes)
            {
                var pJids = new List<Jid>();
                var pPns = new List<Jid>();
                foreach (Node p in act.GetChildren("participant"))
                {
                    if (TryParseAttrJid(p, "jid") is Jid pj)
                    {
                        pJids.Add(pj);
                        allParticipantJids.Add(pj);
                    }
                    if (TryParseAttrJid(p, "phone_number") is Jid pn)
                    {
                        pPns.Add(pn);
                        allParticipantPns.Add(pn);
                    }
                }

                string? actSubject = act.GetAttr("subject");
                string? actDesc = act.GetChild("body")?.Content switch
                {
                    NodeContent.StringContent sc => sc.Text,
                    NodeContent.BytesContent bc => Encoding.UTF8.GetString(bc.Bytes),
                    _ => null
                };
                string? actDescId = act.GetAttr("id");
                uint? actEph = act.Tag == "ephemeral" && uint.TryParse(act.GetAttr("expiration"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ev)
                    ? ev
                    : (act.Tag == "not_ephemeral" ? 0u : null);
                bool? toggle = act.Tag switch
                {
                    "locked" or "announcement" or "no_frequently_forwarded" or "allow_admin_reports" or "group_history" => true,
                    "unlocked" or "not_announcement" or "frequently_forwarded_ok" or "not_allow_admin_reports" or "no_group_history" => false,
                    "membership_approval_mode" => string.Equals(act.GetChild("group_join")?.GetAttr("state"), "on", StringComparison.OrdinalIgnoreCase),
                    _ => null
                };
                string? modeVal = act.Tag is "member_add_mode" or "member_link_mode" or "member_share_group_history_mode"
                    ? (act.Content is NodeContent.StringContent msc ? msc.Text : null)
                    : null;

                var linkedGroups = new List<Jid>();
                if (act.Tag is "link" or "unlink" or "created_sub_group_suggestion" or "revoked_sub_group_suggestions")
                {
                    foreach (Node lg in act.GetChildren("group").Concat(act.GetChildren("sub_group_suggestion")))
                    {
                        if (TryParseAttrJid(lg, "jid") is Jid gj)
                        {
                            linkedGroups.Add(gj);
                        }
                    }
                }
                string? linkKind = act.GetAttr("link_type") ?? act.GetAttr("unlink_type");
                string? unlinkReason = act.GetAttr("unlink_reason");

                if (act.Tag is "remove" or "modify")
                {
                    rotateSenderKey = true;
                }

                actions.Add(new ParsedGroupAction(
                    act.Tag,
                    pJids,
                    pPns,
                    actSubject,
                    actDesc,
                    actDescId,
                    actEph,
                    toggle,
                    modeVal,
                    act.GetAttr("prev_addressing_mode"),
                    linkedGroups.Count > 0 ? linkedGroups : null,
                    linkKind,
                    unlinkReason));
            }
        }

        string? subject = actionNode?.GetAttr("subject");

        return new ParsedGroupNotification(
            from,
            stanzaId,
            ts,
            IsDirtyNotification: false,
            Array.Empty<Jid>(),
            action,
            actor,
            allParticipantJids,
            allParticipantPns,
            subject,
            rotateSenderKey,
            actions,
            addressingMode);
    }

    public static ParsedMexNotification? ParseMexNotification(Node notification, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!TryGetFromJid(notification, out Jid from))
        {
            return null;
        }

        long ts = ParseTimestampSeconds(notification, time);
        Node? update = notification.GetChild("update");
        if (update is null)
        {
            return null;
        }

        string op = update.GetAttr("op_name") ?? "unknown";
        string payload = update.Content switch
        {
            NodeContent.StringContent sc => sc.Text,
            NodeContent.BytesContent bc => Encoding.UTF8.GetString(bc.Bytes),
            _ => "{}"
        };
        return new ParsedMexNotification(from, notification.GetAttr("id"), op, payload, ts);
    }

    public static IReadOnlyList<PrivacySettingEntry> ParsePrivacyNotification(Node notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Node? privacyNode = notification.GetChild("privacy");
        if (privacyNode is null)
        {
            return Array.Empty<PrivacySettingEntry>();
        }

        var entries = new List<PrivacySettingEntry>();
        foreach (Node cat in privacyNode.GetChildren("category"))
        {
            string? name = cat.GetAttr("name");
            string? val = cat.GetAttr("value");
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(val))
            {
                entries.Add(new PrivacySettingEntry(
                    PrivacyProtocol.ParseCategory(name),
                    name,
                    PrivacyProtocol.ParseValue(val),
                    val,
                    cat.GetAttr("dhash")));
            }
        }
        return entries;
    }

    public static string? ParseStatusAboutText(Node notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Node? setNode = notification.GetChild("set");
        return setNode?.Content switch
        {
            NodeContent.StringContent sc => sc.Text,
            NodeContent.BytesContent bc => Encoding.UTF8.GetString(bc.Bytes),
            _ => null
        };
    }

    private static bool TryGetFromJid(Node node, out Jid from)
    {
        if (node.GetAttr("from") is { Length: > 0 } fromStr && Jid.TryParse(fromStr, out from))
        {
            return true;
        }

        from = default;
        return false;
    }

    private static Jid? TryParseAttrJid(Node node, string attrName) =>
        node.GetAttr(attrName) is { Length: > 0 } s && Jid.TryParse(s, out Jid jid) ? jid : null;
}
