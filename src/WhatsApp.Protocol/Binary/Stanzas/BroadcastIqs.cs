using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Crypto;

namespace WhatsApp.Protocol.Binary.Stanzas;

public sealed record BroadcastListInfo(
    Jid ListJid,
    string? Name,
    IReadOnlyList<Jid> RecipientJids);

/// <summary>
/// IQ &amp; stanza builders for numeric user Broadcast Lists (<c>{id}@broadcast</c>,
/// <c>xmlns="w:b"</c>) and encrypted CAG comments (<c>EncCommentMessage</c>).
/// </summary>
public static class BroadcastIqs
{
    public const string Xmlns = "w:b";

    public static bool IsUserBroadcastJid(Jid jid) =>
        jid.Server == Server.Broadcast && !string.Equals(jid.User, "status", StringComparison.OrdinalIgnoreCase);

    public static Node BuildGetBroadcastLists(string iqId) =>
        StanzaBuilder.Iq(iqId, "get", Xmlns, StanzaBuilder.ServerPn, new Node("lists"));

    public static IReadOnlyList<BroadcastListInfo> ParseGetBroadcastListsResponse(Node response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Node? listsNode = response.GetChild("lists");
        if (listsNode is null)
        {
            return Array.Empty<BroadcastListInfo>();
        }

        var result = new List<BroadcastListInfo>();
        foreach (Node listNode in listsNode.GetChildren("list"))
        {
            string? rawId = listNode.GetAttr("id");
            if (string.IsNullOrWhiteSpace(rawId))
            {
                continue;
            }

            Jid listJid = Jid.TryParse(rawId, out Jid parsed) && parsed.Server == Server.Broadcast
                ? parsed
                : new Jid(rawId, Server.Broadcast);

            string? name = listNode.GetAttr("name");
            var recipients = new List<Jid>();
            foreach (Node rec in listNode.GetChildren("recipient"))
            {
                if (rec.GetAttr("jid") is { Length: > 0 } rStr && Jid.TryParse(rStr, out Jid rJid))
                {
                    recipients.Add(rJid.ToNonAd());
                }
            }
            result.Add(new BroadcastListInfo(listJid, name, recipients));
        }

        return result;
    }

    public static Node BuildBroadcastMessageStanza(
        string messageId,
        Jid broadcastListJid,
        IReadOnlyList<Jid> recipients,
        Message message,
        byte[] skmsgCiphertext,
        IReadOnlyList<(Jid DeviceJid, string EncType, byte[] SkdmCiphertext)>? skdmTargets = null,
        string? addressingMode = "lid",
        byte[]? deviceIdentityBytes = null,
        ReportingTokenResult? reportingToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(skmsgCiphertext);
        if (!IsUserBroadcastJid(broadcastListJid))
        {
            throw new ArgumentException($"Expected numeric @broadcast list JID, got '{broadcastListJid}'.", nameof(broadcastListJid));
        }

        var participantNodes = new List<Node>(recipients.Count + (skdmTargets?.Count ?? 0));
        var skdmUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (skdmTargets is { Count: > 0 })
        {
            foreach (var (devJid, encType, skdmCt) in skdmTargets)
            {
                skdmUsers.Add(devJid.ToNonAdString());
                participantNodes.Add(new Node(
                    "to",
                    new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = devJid },
                    new[] { MessageStanzas.BuildEncryptedEncNode(encType, skdmCt, mediaType: null, hideDecryptFail: false) }));
            }
        }

        foreach (Jid user in recipients)
        {
            Jid bare = user.ToNonAd();
            if (!skdmUsers.Contains(bare.ToString()))
            {
                participantNodes.Add(new Node("to", new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["jid"] = bare }));
            }
        }

        string stanzaType = MessageStanzas.ClassifyStanzaType(message);
        string? mediaType = MessageStanzas.ClassifyMediaType(message);
        bool hideDecryptFail = MessageStanzas.ShouldHideDecryptFail(message);

        var children = new List<Node>
        {
            new("participants", participantNodes.ToArray()),
            MessageStanzas.BuildEncryptedEncNode("skmsg", skmsgCiphertext, mediaType, hideDecryptFail)
        };
        if (deviceIdentityBytes is { Length: > 0 })
        {
            children.Add(new Node("device-identity", deviceIdentityBytes));
        }
        if (reportingToken is not null)
        {
            children.Add(MessageStanzas.BuildReportingNode(reportingToken));
        }

        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = messageId,
            ["to"] = broadcastListJid.ToNonAd(),
            ["type"] = stanzaType
        };
        if (!string.IsNullOrEmpty(addressingMode))
        {
            attrs["addressing_mode"] = addressingMode;
        }

        return new Node("message", attrs, new NodeContent.NodesContent(children));
    }

    public static Message BuildEncCommentMessage(
        MessageKey targetMessageKey,
        byte[] encPayloadWithTag,
        byte[] iv12,
        byte[]? commentMessageSecret32 = null)
    {
        ArgumentNullException.ThrowIfNull(targetMessageKey);
        ArgumentNullException.ThrowIfNull(encPayloadWithTag);
        ArgumentNullException.ThrowIfNull(iv12);
        var msg = new Message
        {
            EncCommentMessage = new Message.Types.EncCommentMessage
            {
                TargetMessageKey = targetMessageKey.Clone(),
                EncPayload = ByteString.CopyFrom(encPayloadWithTag),
                EncIv = ByteString.CopyFrom(iv12)
            }
        };
        if (commentMessageSecret32 is { Length: 32 })
        {
            msg.MessageContextInfo = new MessageContextInfo
            {
                MessageSecret = ByteString.CopyFrom(commentMessageSecret32)
            };
        }
        return msg;
    }
}
