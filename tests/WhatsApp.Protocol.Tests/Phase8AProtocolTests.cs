using Whatsapp;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class Phase8AProtocolTests
{
    [TestMethod]
    public void GroupManagementIq_CreateQueryAddAndSettings_RoundTripCleanly()
    {
        Jid adminLid = Jid.Parse("100000000000001@lid");
        Jid memberLid = Jid.Parse("100000000000002@lid");
        Jid memberPn = Jid.Parse("15550002222@s.whatsapp.net");
        Jid groupJid = Jid.Parse("120363000000000123@g.us");

        var createOpts = new GroupCreateOptions(
            Subject: "Engineering Sync",
            Participants:
            [
                new GroupParticipantOptions(memberLid, memberPn, PrivacyToken: [0xDE, 0xAD, 0xBE, 0xEF])
            ],
            LinkMode: MemberLinkMode.AllMemberLink,
            AddMode: MemberAddMode.AdminAdd,
            ApprovalMode: MembershipApprovalMode.On,
            EphemeralExpirationSeconds: 86400,
            Description: "Daily coordination");

        Node createIq = GroupManagementIq.BuildCreateGroup("iq-create-1", createOpts);
        Assert.AreEqual("w:g2", createIq.GetAttr("xmlns"));
        Assert.AreEqual("g.us", createIq.GetAttr("to"));
        Node createChild = createIq.GetChild("create")!;
        Assert.AreEqual("Engineering Sync", createChild.GetAttr("subject"));
        Node partNode = createChild.GetChildren("participant").Single();
        Assert.AreEqual("100000000000002@lid", partNode.GetAttr("jid"));
        Assert.AreEqual("15550002222@s.whatsapp.net", partNode.GetAttr("phone_number"));
        Assert.AreEqual("deadbeef", ((NodeContent.StringContent)partNode.GetChild("privacy")!.Content!).Text);
        Assert.AreEqual("Daily coordination", ((NodeContent.StringContent)createChild.GetChild("description")!.GetChild("body")!.Content!).Text);

        var serverGroupNode = new Node("group", new Dictionary<string, NodeValue>
        {
            ["id"] = groupJid.User,
            ["subject"] = "Engineering Sync",
            ["addressing_mode"] = "lid",
            ["creator"] = adminLid,
            ["creation"] = "1700000000"
        }, new Node[]
        {
            new("participant", new Dictionary<string, NodeValue>
            {
                ["jid"] = adminLid,
                ["type"] = "superadmin",
                ["participant_label"] = "Lead",
                ["participant_label_mtime"] = "1700000500"
            }),
            new("participant", new Dictionary<string, NodeValue>
            {
                ["jid"] = memberLid,
                ["phone_number"] = memberPn,
                ["group_history_sent"] = "false"
            }),
            new("locked"),
            new("announcement"),
            new("ephemeral", new Dictionary<string, NodeValue> { ["expiration"] = "86400", ["trigger"] = "3" }),
            new("membership_approval_mode", null, new[] { new Node("group_join", new Dictionary<string, NodeValue> { ["state"] = "on" }) }),
            new("member_add_mode", "admin_add"),
            new("member_link_mode", "all_member_link"),
            new("member_share_group_history_mode", "all_member_share"),
            new("group_history"),
            new("allow_admin_reports"),
            new("no_frequently_forwarded"),
            new("description", new Dictionary<string, NodeValue> { ["id"] = "DESC0001" }, new[] { new Node("body", "Daily coordination") })
        });

        GroupMetadata parsed = GroupManagementIq.ParseCreateGroupResponse(new Node("iq", null, new[] { serverGroupNode }), createOpts);
        Assert.AreEqual(groupJid, parsed.Id);
        Assert.AreEqual("lid", parsed.AddressingMode);
        Assert.IsTrue(parsed.IsLocked);
        Assert.IsTrue(parsed.IsAnnouncement);
        Assert.IsTrue(parsed.MembershipApproval);
        Assert.IsTrue(parsed.HasGroupHistory);
        Assert.IsTrue(parsed.AllowAdminReports);
        Assert.IsTrue(parsed.NoFrequentlyForwarded);
        Assert.AreEqual(86400u, parsed.Ephemeral?.ExpirationSeconds);
        Assert.AreEqual(MemberAddMode.AdminAdd, parsed.MemberAddMode);
        Assert.AreEqual(MemberLinkMode.AllMemberLink, parsed.MemberLinkMode);
        Assert.AreEqual(MemberShareHistoryMode.AllMemberShare, parsed.ShareHistoryMode);
        Assert.AreEqual("DESC0001", parsed.DescriptionId);
        Assert.AreEqual(ParticipantType.SuperAdmin, parsed.Participants[0].Role);
        Assert.AreEqual("Lead", parsed.Participants[0].Details?.ParticipantLabel);
        Assert.AreEqual(false, parsed.Participants[1].Details?.GroupHistorySent);

        // phash NotModified
        Node qIq = GroupManagementIq.BuildQueryGroup("iq-q-1", groupJid, "2:Jxw0XT8D");
        Assert.AreEqual("2:Jxw0XT8D", qIq.GetChild("query")!.GetAttr("phash"));
        GroupQueryResult notMod = GroupManagementIq.ParseQueryGroupResponse(new Node("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }));
        Assert.IsFalse(notMod.IsModified);
        Assert.IsNull(notMod.Metadata);

        // V4 add_request parse on participant add 403
        Node addResp = new("iq", null, new[]
        {
            new Node("add", null, new[]
            {
                new Node("participant", new Dictionary<string, NodeValue> { ["jid"] = memberLid, ["error"] = "403" }, new[]
                {
                    new Node("add_request", new Dictionary<string, NodeValue> { ["code"] = "V4CODE99", ["expiration"] = "1700086400" })
                })
            })
        });
        IReadOnlyList<ParticipantChangeResult> addRes = GroupManagementIq.ParseParticipantActionResponse(addResp, "add");
        Assert.AreEqual(1, addRes.Count);
        Assert.IsFalse(addRes[0].IsOk);
        Assert.AreEqual("V4CODE99", addRes[0].AddRequest?.Code);
        Assert.AreEqual(1700086400UL, addRes[0].AddRequest?.Expiration);
    }

    [TestMethod]
    public void MexStanzas_UpdateGroupProperty_And_GroupProps_ParseCleanly()
    {
        Jid groupJid = Jid.Parse("120363000000000123@g.us");
        Node mexReq = MexStanzas.BuildQuery(
            "mex-1",
            MexStanzas.UpdateGroupProperty,
            MexStanzas.BuildUpdateMemberShareHistoryModeVariables(groupJid, MemberShareHistoryMode.AllMemberShare));

        Assert.AreEqual("w:mex", mexReq.GetAttr("xmlns"));
        Node q = mexReq.GetChild("query")!;
        Assert.AreEqual("9418211574894172", q.GetAttr("query_id"));
        string jsonText = ((NodeContent.StringContent)q.Content!).Text;
        StringAssert.Contains(jsonText, "\"member_share_group_history_mode\":\"ALL_MEMBER_SHARE\"");

        MexExecutionResponse okResp = MexStanzas.ParseResponse(new Node("iq", null, new[]
        {
            new Node("result", "{\"data\":{\"xwa2_group_update_property\":{\"id\":\"120363000000000123@g.us\",\"state\":\"ACTIVE\"}}}")
        }));
        Assert.IsFalse(okResp.HasFatalErrors);

        MexExecutionResponse errResp = MexStanzas.ParseResponse(new Node("iq", null, new[]
        {
            new Node("result", "{\"errors\":[{\"message\":\"Forbidden\",\"extensions\":{\"error_code\":403,\"is_retryable\":false}}]}")
        }));
        Assert.IsTrue(errResp.HasFatalErrors);
        Assert.AreEqual(403, errResp.Errors[0].Extensions?.ErrorCode);

        Node gpReq = AbPropsIq.BuildGetGroupProps("gp-1", groupJid);
        Assert.AreEqual("abt", gpReq.GetAttr("xmlns"));
        Assert.AreEqual(groupJid.ToString(), gpReq.GetChild("props")!.GetAttr("group"));

        GroupPropsResponse gpResp = AbPropsIq.ParseGroupPropsResponse(new Node("iq", null, new[]
        {
            new Node("props", new Dictionary<string, NodeValue> { ["hash"] = "h-grp-1" }, new[]
            {
                new Node("prop", new Dictionary<string, NodeValue> { ["config_code"] = "23245", ["config_value"] = "1" })
            })
        }));
        Assert.AreEqual("h-grp-1", gpResp.Hash);
        Assert.AreEqual("1", gpResp.ExperimentProps[AbPropsIq.PropGroupHistorySendGroupLevel]);
    }

    [TestMethod]
    public void GroupHistoryCodec_PolicySelectionCompression_And_MessageClassification_Work()
    {
        Jid groupJid = Jid.Parse("120363000000000123@g.us");
        var acctProps = new Dictionary<uint, string>
        {
            [AbPropsIq.PropGroupHistorySend] = "false",
            [AbPropsIq.PropGroupHistoryCountLimit] = "2",
            [AbPropsIq.PropGroupHistoryTimeLimitSecs] = "3600"
        };
        var grpProps = new GroupPropsResponse("gh-hash", new Dictionary<uint, string>
        {
            [AbPropsIq.PropGroupHistorySendGroupLevel] = "1"
        });

        GroupHistoryLimits limits = GroupHistoryCodec.ResolveLimits(acctProps, grpProps);
        Assert.AreEqual(2, limits.MaxMessages);
        Assert.AreEqual(3600L, limits.TimeWindowSeconds);

        long now = 1_700_005_000L;
        var msgs = new[]
        {
            // Too old (outside 3600s window)
            new WebMessageInfo
            {
                Key = new MessageKey { RemoteJid = groupJid.ToString(), Id = "M0" },
                Message = new Message { Conversation = "too old" },
                MessageTimestamp = (ulong)(now - 5000),
                Status = WebMessageInfo.Types.Status.ServerAck
            },
            // Ephemeral excluded
            new WebMessageInfo
            {
                Key = new MessageKey { RemoteJid = groupJid.ToString(), Id = "M1" },
                Message = new Message { Conversation = "ephemeral secret" },
                MessageTimestamp = (ulong)(now - 100),
                Status = WebMessageInfo.Types.Status.ServerAck,
                EphemeralDuration = 86400
            },
            // Eligible #1
            new WebMessageInfo
            {
                Key = new MessageKey { RemoteJid = groupJid.ToString(), Id = "M2" },
                Message = new Message
                {
                    Conversation = "hello 1",
                    MessageContextInfo = new MessageContextInfo
                    {
                        MessageSecret = Google.Protobuf.ByteString.CopyFrom(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
                        ReportingTokenVersion = 2
                    }
                },
                MessageTimestamp = (ulong)(now - 300),
                Status = WebMessageInfo.Types.Status.DeliveryAck
            },
            // Eligible #2 (newest)
            new WebMessageInfo
            {
                Key = new MessageKey { RemoteJid = groupJid.ToString(), Id = "M3" },
                Message = new Message { Conversation = "hello 2" },
                MessageTimestamp = (ulong)(now - 60),
                Status = WebMessageInfo.Types.Status.Read
            },
            // Older than M2 -> capped out by MaxMessages=2
            new WebMessageInfo
            {
                Key = new MessageKey { RemoteJid = groupJid.ToString(), Id = "M4" },
                Message = new Message { Conversation = "older eligible" },
                MessageTimestamp = (ulong)(now - 600),
                Status = WebMessageInfo.Types.Status.ServerAck
            }
        };

        var (selected, oldestTs) = GroupHistoryCodec.SelectEligibleMessages(groupJid, msgs, now, limits);
        Assert.AreEqual(2, selected.Count);
        Assert.AreEqual("M3", selected[0].Key.Id);
        Assert.AreEqual("M2", selected[1].Key.Id);
        Assert.AreEqual(now - 300, oldestTs);
        Assert.IsTrue(selected[1].Message.MessageContextInfo.HasMessageSecret);
        Assert.IsFalse(selected[1].Message.MessageContextInfo.HasReportingTokenVersion);

        byte[] compressed = GroupHistoryCodec.CompressGroupHistory(selected);
        GroupHistory decompressed = GroupHistoryCodec.DecompressGroupHistory(compressed);
        Assert.AreEqual(2, decompressed.Messages.Count);
        Assert.AreEqual("hello 2", decompressed.Messages[0].Message.Conversation);

        var bundleMsg = GroupHistoryCodec.BuildBundleMessage(
            "/v/hist.enc",
            new byte[32],
            new byte[32],
            new byte[32],
            now,
            new Message.Types.MessageHistoryMetadata { MessageCount = 2, OldestMessageTimestampInBundle = oldestTs });
        Assert.AreEqual("media", MessageStanzas.ClassifyStanzaType(bundleMsg));
        Assert.AreEqual("group_history", MessageStanzas.ClassifyMediaType(bundleMsg));

        var noticeMsg = GroupHistoryCodec.BuildNoticeMessage(bundleMsg.MessageHistoryBundle.MessageHistoryMetadata);
        Assert.AreEqual("text", MessageStanzas.ClassifyStanzaType(noticeMsg));
        Assert.IsTrue(MessageStanzas.ShouldHideDecryptFail(noticeMsg));

        Message labelSet = RichMessageBuilders.BuildMemberLabelMessage("VIP", now);
        Node? labelMeta = MessageStanzas.InferMetaNode(labelSet);
        Assert.AreEqual("member_tag", labelMeta?.GetAttr("appdata"));
        Assert.AreEqual("user_update", labelMeta?.GetAttr("tag_reason"));
        Assert.IsTrue(MessageStanzas.ShouldHideDecryptFail(labelSet));

        Node? clearMeta = MessageStanzas.InferMetaNode(RichMessageBuilders.BuildMemberLabelMessage("", now));
        Assert.AreEqual("user_delete", clearMeta?.GetAttr("tag_reason"));
    }

    [TestMethod]
    public void NotificationStanzas_ParseGroupNotification_ExtractsMultiActionFields()
    {
        Jid groupJid = Jid.Parse("120363000000000123@g.us");
        Jid adminLid = Jid.Parse("100000000000001@lid");
        Jid addedLid = Jid.Parse("100000000000002@lid");

        var notif = new Node("notification", new Dictionary<string, NodeValue>
        {
            ["from"] = groupJid,
            ["id"] = "notif-1",
            ["type"] = "w:gp2",
            ["participant"] = adminLid,
            ["addressing_mode"] = "lid",
            ["t"] = "1700000000"
        }, new Node[]
        {
            new("subject", new Dictionary<string, NodeValue> { ["subject"] = "New Topic" }),
            new("locked"),
            new("ephemeral", new Dictionary<string, NodeValue> { ["expiration"] = "604800" }),
            new("member_share_group_history_mode", "all_member_share"),
            new("group_history"),
            new("add", null, new[]
            {
                new Node("participant", new Dictionary<string, NodeValue> { ["jid"] = addedLid })
            })
        });

        ParsedGroupNotification? parsed = NotificationStanzas.ParseGroupNotification(notif);
        Assert.IsNotNull(parsed);
        Assert.AreEqual("lid", parsed.AddressingMode);
        Assert.AreEqual(6, parsed.Actions?.Count);
        Assert.AreEqual("New Topic", parsed.Actions![0].Subject);
        Assert.AreEqual(true, parsed.Actions[1].ToggleValue);
        Assert.AreEqual(604800u, parsed.Actions[2].EphemeralExpiration);
        Assert.AreEqual("all_member_share", parsed.Actions[3].ModeValue);
        Assert.AreEqual(true, parsed.Actions[4].ToggleValue);
        Assert.AreEqual(addedLid, parsed.Actions[5].ParticipantJids[0]);
    }
}
