using System.Text.Json;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class CommunityProtocolTests
{
    [TestMethod]
    public void CommunityIq_CreateLinkUnlinkQueryJoinAndParticipants_ParseAndBuildCleanly()
    {
        Jid communityJid = Jid.Parse("120363000000001000@g.us");
        Jid subgroup1 = Jid.Parse("120363000000001001@g.us");
        Jid subgroup2 = Jid.Parse("120363000000001002@g.us");

        // 1) CreateCommunity (only parent flags, no inline description)
        var createOpts = new CreateCommunityOptions(
            Name: "OSS Maintainers",
            Description: "Main hub",
            Closed: true,
            AllowNonAdminSubGroupCreation: true,
            CreateGeneralChat: true);
        Node createIq = CommunityIq.BuildCreateCommunity("c-1", createOpts);
        Assert.AreEqual("w:g2", createIq.GetAttr("xmlns"));
        Assert.AreEqual("g.us", createIq.GetAttr("to"));
        Node createNode = createIq.GetChild("create")!;
        Assert.AreEqual("OSS Maintainers", createNode.GetAttr("subject"));
        Assert.AreEqual("request_required", createNode.GetChild("parent")!.GetAttr("default_membership_approval_mode"));
        Assert.IsNotNull(createNode.GetChild("allow_non_admin_sub_group_creation"));
        Assert.IsNotNull(createNode.GetChild("create_general_chat"));

        // Validation guard
        Assert.ThrowsException<ArgumentException>(() =>
            CommunityIq.BuildCreateCommunity("bad", new CreateCommunityOptions(new string('a', 101))));

        // 2) Link subgroups (mixed visible & hidden) + response partitioning
        Node linkIq = CommunityIq.BuildLinkSubgroups("l-1", communityJid, [
            new LinkSubgroupOption(subgroup1, SubgroupVisibility.Visible),
            new LinkSubgroupOption(subgroup2, SubgroupVisibility.Hidden)
        ]);
        Node linkNode = linkIq.GetChild("links")!.GetChild("link")!;
        Assert.AreEqual("sub_group", linkNode.GetAttr("link_type"));
        Node[] linkGroups = linkNode.GetChildren("group").ToArray();
        Assert.IsNull(linkGroups[0].GetChild("hidden_group"));
        Assert.IsNotNull(linkGroups[1].GetChild("hidden_group"));

        var linkResp = CommunityIq.ParseLinkSubgroupsResponse(new Node("iq", null, new[]
        {
            new Node("links", null, new[]
            {
                new Node("link", new Dictionary<string, NodeValue> { ["link_type"] = "sub_group" }, new[]
                {
                    new Node("group", new Dictionary<string, NodeValue> { ["jid"] = subgroup1 }),
                    new Node("group", new Dictionary<string, NodeValue> { ["jid"] = subgroup2, ["error"] = "406" })
                })
            })
        }));
        Assert.IsTrue(linkResp[0].IsOk);
        Assert.AreEqual(406u, linkResp[1].ErrorCode);

        // 3) Unlink with orphan removal
        Node unlinkIq = CommunityIq.BuildUnlinkSubgroups("u-1", communityJid, [subgroup1], removeOrphanMembers: true);
        Node uGroup = unlinkIq.GetChild("unlink")!.GetChild("group")!;
        Assert.AreEqual("true", uGroup.GetAttr("remove_orphaned_members"));

        // 4) JoinLinkedGroup: bare reply, wrapper reply, and pending approval
        Assert.AreEqual(
            new JoinGroupOutcome(JoinGroupOutcomeKind.Joined, subgroup1),
            CommunityIq.ParseJoinLinkedGroupResponse(new Node("iq"), subgroup1));
        Assert.AreEqual(
            new JoinGroupOutcome(JoinGroupOutcomeKind.PendingApproval, subgroup1),
            CommunityIq.ParseJoinLinkedGroupResponse(
                new Node("iq", null, new[] { new Node("membership_approval_request", new Dictionary<string, NodeValue> { ["jid"] = subgroup1 }) }),
                subgroup1));

        // 5) LinkedGroupsParticipants (nested & direct shapes)
        var participants = CommunityIq.ParseLinkedGroupsParticipantsResponse(new Node("iq", null, new[]
        {
            new Node("linked_groups_participants", null, new[]
            {
                new Node("group", null, new[]
                {
                    new Node("participant", new Dictionary<string, NodeValue>
                    {
                        ["jid"] = "100000000000005@lid",
                        ["phone_number"] = "15551234567@s.whatsapp.net",
                        ["type"] = "admin"
                    })
                })
            })
        }));
        Assert.AreEqual(1, participants.Count);
        Assert.IsTrue(participants[0].IsAdmin);
    }

    [TestMethod]
    public void CommunityMexResponses_SubgroupsCountsSuggestions_And_LinkNotifications_Parse()
    {
        using JsonDocument sgDoc = JsonDocument.Parse("""
        {
          "xwa2_group_query_by_id": {
            "id": "120363000000001000@g.us",
            "default_sub_group": {
              "id": "120363000000001001@g.us",
              "subject": { "value": "Announcements", "creation_time": 1700000000 }
            },
            "sub_groups": {
              "edges": [
                {
                  "node": {
                    "id": "120363000000001002@g.us",
                    "subject": { "value": "General Chat" },
                    "properties": { "general_chat": "true", "hidden_group": "false" },
                    "total_participants_count": 42
                  }
                }
              ]
            }
          }
        }
        """);

        IReadOnlyList<CommunitySubgroupInfo> subgroups = CommunityIq.ParseSubgroupsMexResponse(sgDoc.RootElement);
        Assert.AreEqual(2, subgroups.Count);
        Assert.IsTrue(subgroups[0].IsDefaultSubGroup);
        Assert.AreEqual("Announcements", subgroups[0].Subject);
        Assert.IsTrue(subgroups[1].IsGeneralChat);
        Assert.AreEqual(42u, subgroups[1].ParticipantCount);

        IReadOnlyList<SubgroupParticipantCountInfo> counts = CommunityIq.ParseSubgroupParticipantCountsMexResponse(sgDoc.RootElement);
        Assert.AreEqual(1, counts.Count);
        Assert.AreEqual(42u, counts[0].TotalParticipantsCount);

        using JsonDocument sugDoc = JsonDocument.Parse("""
        {
          "xwa2_group_query_by_id": {
            "sub_group_suggestions": {
              "edges": [
                {
                  "node": {
                    "id": "120363000000001003@g.us",
                    "subject": { "value": "RustWG" },
                    "description": { "id": "D1", "value": "Working group" },
                    "is_existing_group": true,
                    "total_participants_count": 19
                  }
                }
              ]
            }
          }
        }
        """);
        IReadOnlyList<SubgroupSuggestionInfo> suggestions = CommunityIq.ParseSubgroupSuggestionsMexResponse(sugDoc.RootElement);
        Assert.AreEqual(1, suggestions.Count);
        Assert.AreEqual("RustWG", suggestions[0].Subject);
        Assert.IsTrue(suggestions[0].IsExistingGroup);
        Assert.AreEqual(19u, suggestions[0].TotalParticipantsCount);

        // Link notification w:gp2 parse
        Jid communityJid = Jid.Parse("120363000000001000@g.us");
        Jid sgJid = Jid.Parse("120363000000001002@g.us");
        ParsedGroupNotification? linkNotif = NotificationStanzas.ParseGroupNotification(new Node("notification", new Dictionary<string, NodeValue>
        {
            ["from"] = communityJid,
            ["id"] = "comm-n-1",
            ["type"] = "w:gp2",
            ["t"] = "1700000500"
        }, new[]
        {
            new Node("link", new Dictionary<string, NodeValue> { ["link_type"] = "sub_group_link" }, new[]
            {
                new Node("group", new Dictionary<string, NodeValue> { ["jid"] = sgJid, ["subject"] = "General Chat", ["s_t"] = "1700000500" })
            })
        }));

        Assert.IsNotNull(linkNotif);
        Assert.AreEqual("link", linkNotif.Actions![0].Action);
        Assert.AreEqual("sub_group_link", linkNotif.Actions[0].LinkKind);
        Assert.AreEqual(sgJid, linkNotif.Actions[0].LinkedGroupJids![0]);
    }
}
