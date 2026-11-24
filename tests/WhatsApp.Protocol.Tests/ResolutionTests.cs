using Microsoft.VisualStudio.TestTools.UnitTesting;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class ResolutionTests
{
    [TestMethod]
    public void LidPnCache_Bidirectional_Lookup_With_Normalization()
    {
        var cache = new LidPnCache();

        cache.Learn("15550100101@c.us", "100000000000101@lid");

        // LID -> PN lookup
        Assert.IsTrue(cache.TryGetPn("100000000000101@lid", out string? pn));
        Assert.AreEqual("15550100101@s.whatsapp.net", pn);

        // PN -> LID lookup (even if queried with @c.us or @s.whatsapp.net)
        Assert.IsTrue(cache.TryGetLid("15550100101@s.whatsapp.net", out string? lid1));
        Assert.AreEqual("100000000000101@lid", lid1);

        Assert.IsTrue(cache.TryGetLid("15550100101@c.us", out string? lid2));
        Assert.AreEqual("100000000000101@lid", lid2);
    }

    [TestMethod]
    public void GroupSyncIq_Builds_Expected_Participating_Iq()
    {
        var node = GroupSyncIq.BuildParticipatingIq("test_iq_123");

        Assert.AreEqual("iq", node.Tag);
        Assert.AreEqual("test_iq_123", node.GetAttr("id"));
        Assert.AreEqual("g.us", node.GetAttr("to"));
        Assert.AreEqual("get", node.GetAttr("type"));
        Assert.AreEqual("w:g2", node.GetAttr("xmlns"));

        var child = node.GetChild("participating");
        Assert.IsNotNull(child);
    }

    [TestMethod]
    public void GroupSyncIq_Parses_Participating_Response()
    {
        // Construct synthetic XML response: <iq type="result"><groups><group id="120363000000000101" subject="Sample Discussion Group" creation="1700000101" owner="100000000000102@lid"><description>Synthetic group</description></group></groups></iq>
        var groupNode = new Node("group")
            .SetAttr("id", "120363000000000101")
            .SetAttr("subject", "Sample Discussion Group")
            .SetAttr("creation", "1700000101")
            .SetAttr("owner", "100000000000102@lid");

        groupNode.Content = new Node[]
        {
            new Node("description", new NodeContent.StringContent("Synthetic group"))
        };

        var groupsContainer = new Node("groups", new Node[] { groupNode });
        var iqResponse = new Node("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new Node[] { groupsContainer });

        var parsed = GroupSyncIq.ParseParticipatingResponse(iqResponse);

        Assert.AreEqual(1, parsed.Count);
        var g = parsed[0];
        Assert.AreEqual("120363000000000101@g.us", g.Id);
        Assert.AreEqual("Sample Discussion Group", g.Subject);
        Assert.AreEqual("100000000000102@lid", g.Owner);
        Assert.AreEqual(1700000101ul, g.Creation);
        Assert.AreEqual("Synthetic group", g.Description);
    }

    [TestMethod]
    public void DisplayNameResolver_Resolves_Group_Subject_Over_Numeric_Id()
    {
        var groupRegistry = new GroupRegistry();
        groupRegistry.Upsert(new GroupMetadataRecord(
            "120363000000000101@g.us",
            "Sample Discussion Group",
            "owner@lid",
            1700000101,
            "Description"
        ));

        string title = DisplayNameResolver.ResolveTitle(
            "120363000000000101@g.us",
            isGroup: true,
            groupRegistry: groupRegistry
        );

        Assert.AreEqual("Sample Discussion Group", title, "Group subject must resolve instead of raw ID 120363000000000101");
    }

    [TestMethod]
    public void DisplayNameResolver_Resolves_AddressBook_Name_Over_Pushname()
    {
        var contactRegistry = new ContactRegistry();
        contactRegistry.Upsert(
            "100000000000103@lid",
            name: "Alice Example",
            pushname: "Alice",
            phoneNumber: "15550100103@s.whatsapp.net",
            isAddressBook: true
        );

        string title = DisplayNameResolver.ResolveTitle(
            "100000000000103@lid",
            isGroup: false,
            contactRegistry: contactRegistry
        );

        Assert.AreEqual("Alice Example", title, "Address book name must take priority over pushname");
    }

    [TestMethod]
    public void DisplayNameResolver_Resolves_Pushname_When_No_AddressBook_Name()
    {
        var contactRegistry = new ContactRegistry();
        contactRegistry.Upsert(
            "100000000000104@lid",
            name: null,
            pushname: "Charlie Test",
            phoneNumber: "15550100104@s.whatsapp.net"
        );

        string title = DisplayNameResolver.ResolveTitle(
            "100000000000104@lid",
            isGroup: false,
            contactRegistry: contactRegistry
        );

        Assert.AreEqual("Charlie Test", title, "Pushname must be used when address book name is absent");
    }

    [TestMethod]
    public void DisplayNameResolver_Resolves_Lid_To_Formatted_PhoneNumber_When_No_Name()
    {
        var lidPnCache = new LidPnCache();
        lidPnCache.Learn("919000000001@s.whatsapp.net", "100000000000105@lid");

        string title = DisplayNameResolver.ResolveTitle(
            "100000000000105@lid",
            isGroup: false,
            lidPnCache: lidPnCache
        );

        Assert.AreEqual("+91 90000 00001", title, "LID without name must resolve to formatted international phone number");
    }

    [TestMethod]
    public void DisplayNameResolver_Formats_E164_Numbers_Properly()
    {
        Assert.AreEqual("+91 98765 43210", DisplayNameResolver.FormatPhoneNumber("919876543210@s.whatsapp.net"));
        Assert.AreEqual("+1 (415) 555-2671", DisplayNameResolver.FormatPhoneNumber("14155552671@c.us"));
        Assert.AreEqual("120363000000000101", DisplayNameResolver.FormatPhoneNumber("120363000000000101@g.us"));
    }
}
