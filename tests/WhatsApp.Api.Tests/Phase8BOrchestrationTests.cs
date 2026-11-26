using System.Threading.Channels;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Storage.Sqlite;
using LocalWirePair = WhatsApp.Api.Tests.Phase6COrchestrationTests.LocalWirePair;

namespace WhatsApp.Api.Tests;

[TestClass]
public sealed class Phase8BOrchestrationTests
{
    [TestMethod]
    public async Task CommunityClient_CreateRecoverDescription_SubgroupsCagMex_JoinApproval_And_Notifications_Work()
    {
        await using var backend = new SqliteBackend(":memory:");
        await backend.InitializeAsync();
        DeviceCredentials creds = await backend.Device.LoadOrCreateAsync();
        creds.Pn = Jid.PnDevice("15550000001", 1);
        creds.Lid = Jid.LidDevice("100000000000001", 1);
        await backend.Device.SaveAsync(creds);

        var events = Channel.CreateUnbounded<WhatsAppEvent>();
        var lidPn = new LidPnCache();
        await using var wire = new LocalWirePair();
        await using var router = new StanzaRouter(wire.Socket, wire.Iq, new StanzaHandlers());

        var usync = new UsyncClient(wire.Iq, backend, lidPn);
        var preKeyFetcher = new PreKeyFetchClient(wire.Iq, backend, lidPn, creds);
        var retryCoord = new RetryCoordinator(wire.Socket, backend, preKeyFetcher, lidPn, creds, e => events.Writer.WriteAsync(e));
        var sender = new MessageSender(wire.Socket, usync, preKeyFetcher, backend, lidPn, creds, retryCoord);
        var mex = new MexClient(wire.Iq);
        var groups = new GroupClient(wire.Iq, mex, sender, backend, lidPn, emitEvent: e => events.Writer.WriteAsync(e));
        var communities = new CommunityClient(wire.Iq, mex, groups, backend, lidPn);
        var notifications = new NotificationRouter(backend, lidPn, creds, wire.Socket, wire.Iq, syncd: null, e => events.Writer.WriteAsync(e), groupClient: groups);

        Jid communityJid = Jid.Parse("120363000000002000@g.us");
        Jid cagJid = Jid.Parse("120363000000002001@g.us");
        Jid sgJid = Jid.Parse("120363000000002002@g.us");

        // 1) Create community where SetDescription fails -> CommunityConfigurationException preserves created JID
        Task<GroupMetadata> createTask = communities.CreateCommunityAsync(new CreateCommunityOptions("Alpha Community", "Community rules"));
        Node createReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("w:g2", createReq.GetAttr("xmlns"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = createReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("group", new Dictionary<string, NodeValue> { ["id"] = communityJid.User, ["subject"] = "Alpha Community" })
        }));

        Node descReq = await wire.ReadClientNodeAsync();
        Assert.IsNotNull(descReq.GetChild("description"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = descReq.GetAttr("id")!, ["type"] = "error" }, new[]
        {
            new Node("error", new Dictionary<string, NodeValue> { ["code"] = "409", ["text"] = "conflict" })
        }));

        CommunityConfigurationException cfgEx = await Assert.ThrowsExceptionAsync<CommunityConfigurationException>(() => createTask);
        Assert.AreEqual(communityJid, cfgEx.CreatedCommunityJid);
        Assert.AreEqual("SetDescription", cfgEx.Step);

        // 2) MEX FindAnnouncementGroupAsync (WAWebMexFetchAllSubgroupsJobQuery = 9935467776504344)
        Task<CommunitySubgroupInfo?> cagTask = communities.FindAnnouncementGroupAsync(communityJid);
        Node mexReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual("w:mex", mexReq.GetAttr("xmlns"));
        Assert.AreEqual("9935467776504344", mexReq.GetChild("query")!.GetAttr("query_id"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = mexReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("result", $"{{\"data\":{{\"xwa2_group_query_by_id\":{{\"default_sub_group\":{{\"id\":\"{cagJid}\",\"subject\":{{\"value\":\"Announcements\"}}}},\"sub_groups\":{{\"edges\":[{{\"node\":{{\"id\":\"{sgJid}\",\"subject\":{{\"value\":\"Dev Chat\"}},\"properties\":{{\"general_chat\":\"true\",\"hidden_group\":\"false\"}}}}}}]}}}}}}}}")
        }));
        CommunitySubgroupInfo? cag = await cagTask;
        Assert.IsNotNull(cag);
        Assert.AreEqual(cagJid, cag.Id);
        Assert.IsTrue(cag.IsDefaultSubGroup);

        // 3) JoinSubgroupAsync pending approval -> CommunityMembershipApprovalRequiredException
        Task<GroupMetadata> joinTask = communities.JoinSubgroupAsync(communityJid, sgJid);
        Node joinReq = await wire.ReadClientNodeAsync();
        Assert.AreEqual(sgJid.ToString(), joinReq.GetChild("join_linked_group")!.GetAttr("jid"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = joinReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("membership_approval_request", new Dictionary<string, NodeValue> { ["jid"] = sgJid })
        }));
        var approvalEx = await Assert.ThrowsExceptionAsync<CommunityMembershipApprovalRequiredException>(() => joinTask);
        Assert.AreEqual(sgJid, approvalEx.SubgroupJid);

        // 4) GetLinkedGroupsParticipantsAsync learns LID<->PN mappings cleanly
        Jid memberLid = Jid.Parse("100000000000077@lid");
        Jid memberPn = Jid.Parse("15557770077@s.whatsapp.net");
        Task<IReadOnlyList<GroupParticipantInfo>> linkedPartsTask = communities.GetLinkedGroupsParticipantsAsync(communityJid);
        Node lpReq = await wire.ReadClientNodeAsync();
        Assert.IsNotNull(lpReq.GetChild("linked_groups_participants"));
        wire.SendServerNode(new Node("iq", new Dictionary<string, NodeValue> { ["id"] = lpReq.GetAttr("id")!, ["type"] = "result" }, new[]
        {
            new Node("linked_groups_participants", new[]
            {
                new Node("participant", new Dictionary<string, NodeValue> { ["jid"] = memberLid, ["phone_number"] = memberPn, ["type"] = "member" })
            })
        }));
        IReadOnlyList<GroupParticipantInfo> linkedParts = await linkedPartsTask;
        Assert.AreEqual(1, linkedParts.Count);
        Assert.AreEqual(memberPn.ToNonAdString(), lidPn.TryGetPn(memberLid.ToNonAdString(), out string? resolved) ? resolved : null);

        // 5) NotificationRouter emits CommunityLinkChangedEvent on w:gp2 <unlink>
        await notifications.HandleAsync(new Node("notification", new Dictionary<string, NodeValue>
        {
            ["from"] = communityJid,
            ["id"] = "comm-unlink-1",
            ["type"] = "w:gp2",
            ["t"] = "1700002000"
        }, new[]
        {
            new Node("unlink", new Dictionary<string, NodeValue> { ["unlink_type"] = "sub_group_unlink", ["unlink_reason"] = "parent" }, new[]
            {
                new Node("group", new Dictionary<string, NodeValue> { ["jid"] = sgJid, ["subject"] = "Dev Chat", ["s_t"] = "1700002000" })
            })
        }), sendAck: false);

        var emitted = new List<WhatsAppEvent>();
        while (events.Reader.TryRead(out WhatsAppEvent? evt)) emitted.Add(evt);
        var linkEvt = emitted.OfType<CommunityLinkChangedEvent>().Single();
        Assert.AreEqual("unlink", linkEvt.Action);
        Assert.AreEqual("sub_group_unlink", linkEvt.LinkKind);
        Assert.AreEqual("parent", linkEvt.UnlinkReason);
        Assert.AreEqual(sgJid.ToString(), linkEvt.SubgroupJids[0]);
    }
}
