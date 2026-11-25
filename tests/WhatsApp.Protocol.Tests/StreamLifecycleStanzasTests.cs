using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class StreamLifecycleStanzasTests
{
    [TestMethod]
    public void PingIq_DetectsServerPingAndBuildsPongToOrigin()
    {
        var pingFromServer = new Node(
            "iq",
            new Dictionary<string, NodeValue>
            {
                ["from"] = Jid.Parse("s.whatsapp.net"),
                ["id"] = "server-ping-42",
                ["type"] = "get",
                ["xmlns"] = "w:p"
            });

        Assert.IsTrue(PingIq.IsPingRequest(pingFromServer));
        Node pong = PingIq.BuildPong(pingFromServer);
        Assert.AreEqual("iq", pong.Tag);
        Assert.AreEqual("s.whatsapp.net", pong.GetAttr("to"));
        Assert.AreEqual("server-ping-42", pong.GetAttr("id"));
        Assert.AreEqual("result", pong.GetAttr("type"));

        var wrongXmlns = new Node(
            "iq",
            new Dictionary<string, NodeValue>
            {
                ["id"] = "1",
                ["type"] = "get",
                ["xmlns"] = "encrypt"
            });
        Assert.IsFalse(PingIq.IsPingRequest(wrongXmlns));
    }

    [TestMethod]
    public void ConnectFailureStanza_ParsesLoggedOutTempBanOutdatedAndTransientCodes()
    {
        var loggedOut = new Node("failure", new Dictionary<string, NodeValue>
        {
            ["reason"] = "401",
            ["location"] = "rva",
            ["logout_message_header"] = "Logged out",
            ["logout_message_subtext"] = "Device was unlinked",
            ["logout_message_locale"] = "en_US"
        });
        var loInfo = ConnectFailureStanza.Parse(loggedOut);
        Assert.AreEqual(ConnectFailureReason.LoggedOut, loInfo.Reason);
        Assert.IsTrue(loInfo.IsLoggedOut);
        Assert.IsFalse(loInfo.ShouldReconnect);
        Assert.AreEqual("Logged out", loInfo.LogoutMessageHeader);

        var tempBan = new Node("failure", new Dictionary<string, NodeValue>
        {
            ["reason"] = "402",
            ["code"] = "10",
            ["expire"] = "3600",
            ["message"] = "spam",
            ["url"] = "https://faq.whatsapp.com"
        });
        var tbInfo = ConnectFailureStanza.Parse(tempBan);
        Assert.AreEqual(ConnectFailureReason.TempBanned, tbInfo.Reason);
        Assert.AreEqual(10, tbInfo.Code);
        Assert.AreEqual(3600UL, tbInfo.ExpireSeconds);
        Assert.IsFalse(tbInfo.IsLoggedOut);
        Assert.IsFalse(tbInfo.ShouldReconnect);

        var outdated = ConnectFailureStanza.Parse(new Node("failure", new Dictionary<string, NodeValue> { ["reason"] = "405" }));
        Assert.AreEqual(ConnectFailureReason.ClientOutdated, outdated.Reason);
        Assert.IsFalse(outdated.ShouldReconnect);

        var transient = ConnectFailureStanza.Parse(new Node("failure", new Dictionary<string, NodeValue> { ["reason"] = "503" }));
        Assert.AreEqual(ConnectFailureReason.ServiceUnavailable, transient.Reason);
        Assert.IsTrue(transient.ShouldReconnect);

        var unknown = ConnectFailureStanza.Parse(new Node("failure", new Dictionary<string, NodeValue> { ["reason"] = "999" }));
        Assert.AreEqual(ConnectFailureReason.Unknown, unknown.Reason);
        Assert.AreEqual(999, unknown.RawReasonCode);
        Assert.IsFalse(unknown.ShouldReconnect);
    }

    [TestMethod]
    public void StreamErrorStanza_ParsesConflictsCodesAckAndMalformed()
    {
        var replaced = StreamErrorStanza.Parse(new Node("stream:error", new Node[]
        {
            new("conflict", new Dictionary<string, NodeValue> { ["type"] = "replaced" })
        }));
        Assert.AreEqual(StreamErrorKind.ConflictReplaced, replaced.Kind);

        var removed = StreamErrorStanza.Parse(new Node("stream:error", new Node[]
        {
            new("conflict", new Dictionary<string, NodeValue> { ["type"] = "device_removed" })
        }));
        Assert.AreEqual(StreamErrorKind.ConflictDeviceRemoved, removed.Kind);

        var code515 = StreamErrorStanza.Parse(new Node("stream:error", new Dictionary<string, NodeValue> { ["code"] = "515" }));
        Assert.AreEqual(StreamErrorKind.Code, code515.Kind);
        Assert.AreEqual("515", code515.Code);

        var ack = StreamErrorStanza.Parse(new Node("stream:error", new Node[]
        {
            new("ack", new Dictionary<string, NodeValue> { ["id"] = "abc-123" })
        }));
        Assert.AreEqual(StreamErrorKind.Ack, ack.Kind);
        Assert.AreEqual("abc-123", ack.AckId);

        var malformed = StreamErrorStanza.Parse(new Node("stream:error", new Node[] { new("xml-not-well-formed") }));
        Assert.AreEqual(StreamErrorKind.XmlNotWellFormed, malformed.Kind);
    }

    [TestMethod]
    public void InfoBulletinStanza_ParsesDirtyEdgeRoutingOfflinePreviewAndOffline()
    {
        byte[] routing = [1, 2, 3, 4, 5];
        var ib = new Node("ib", new Node[]
        {
            new("dirty", new Dictionary<string, NodeValue> { ["type"] = "groups", ["timestamp"] = "1700000000" }),
            new("dirty", new Dictionary<string, NodeValue> { ["type"] = "account_sync" }),
            new("edge_routing", new Node[] { new("routing_info", routing) }),
            new("offline_preview", new Dictionary<string, NodeValue>
            {
                ["count"] = "42",
                ["message"] = "30",
                ["notification"] = "7",
                ["receipt"] = "5"
            }),
            new("offline", new Dictionary<string, NodeValue> { ["count"] = "42" })
        });

        var parsed = InfoBulletinStanza.Parse(ib);
        Assert.AreEqual(2, parsed.DirtyBits.Count);
        Assert.AreEqual("groups", parsed.DirtyBits[0].Type);
        Assert.AreEqual(1700000000UL, parsed.DirtyBits[0].Timestamp);
        Assert.AreEqual("account_sync", parsed.DirtyBits[1].Type);
        Assert.IsNull(parsed.DirtyBits[1].Timestamp);
        CollectionAssert.AreEqual(routing, parsed.EdgeRoutingInfo);
        Assert.IsNotNull(parsed.OfflinePreview);
        Assert.AreEqual(42, parsed.OfflinePreview.Total);
        Assert.AreEqual(30, parsed.OfflinePreview.Messages);
        Assert.AreEqual(7, parsed.OfflinePreview.Notifications);
        Assert.AreEqual(5, parsed.OfflinePreview.Receipts);
        Assert.AreEqual(42, parsed.OfflineCompletedCount);
    }
}
