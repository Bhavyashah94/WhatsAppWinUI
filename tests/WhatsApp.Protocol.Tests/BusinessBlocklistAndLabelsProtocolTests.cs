using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class BusinessBlocklistAndLabelsProtocolTests
{
    [TestMethod]
    public void BlocklistIq_ParseGetAndNotification_HandlesAllShapes()
    {
        Jid lid1 = Jid.Parse("100000000000011@lid");
        Jid lid2 = Jid.Parse("100000000000012@lid");
        Jid pn1 = Jid.Parse("15550001111@s.whatsapp.net");

        Node updateIq = BlocklistIq.BuildUpdateItem("iq-blk-1", lid1, block: true, pnJid: pn1);
        Assert.AreEqual("blocklist", updateIq.GetAttr("xmlns"));
        Node item = updateIq.GetChild("item")!;
        Assert.AreEqual("block", item.GetAttr("action"));
        Assert.AreEqual(lid1.ToString(), item.GetAttr("jid"));
        Assert.AreEqual(pn1.ToString(), item.GetAttr("pn_jid"));

        Node listResp = new("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new[]
        {
            new Node("list", new[]
            {
                new Node("item", new Dictionary<string, NodeValue> { ["jid"] = lid1, ["t"] = "1710001000" }),
                new Node("item", new Dictionary<string, NodeValue> { ["jid"] = lid2 })
            })
        });
        IReadOnlyList<BlocklistEntry> parsedList = BlocklistIq.ParseGetBlocklistResponse(listResp);
        Assert.AreEqual(2, parsedList.Count);
        Assert.AreEqual(1710001000UL, parsedList[0].TimestampSeconds);
        Assert.IsNull(parsedList[1].TimestampSeconds);

        Node directResp = new("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new[]
        {
            new Node("item", new Dictionary<string, NodeValue> { ["jid"] = lid2, ["t"] = "1710002000" })
        });
        IReadOnlyList<BlocklistEntry> parsedDirect = BlocklistIq.ParseGetBlocklistResponse(directResp);
        Assert.AreEqual(1, parsedDirect.Count);
        Assert.AreEqual(lid2, parsedDirect[0].Jid);

        Node notif = new("notification", new Dictionary<string, NodeValue> { ["type"] = "blocklist" }, new[]
        {
            new Node("list", new[]
            {
                new Node("item", new Dictionary<string, NodeValue> { ["jid"] = lid1, ["action"] = "block" }),
                new Node("item", new Dictionary<string, NodeValue> { ["jid"] = lid2, ["action"] = "unblock" })
            })
        });
        var notifItems = BlocklistIq.ParseBlocklistNotification(notif);
        Assert.AreEqual(2, notifItems.Count);
        Assert.IsTrue(notifItems[0].Blocked);
        Assert.IsFalse(notifItems[1].Blocked);
    }

    [TestMethod]
    public void AppStateSchemas_LabelsAndQuickReplies_MatchWireVersionsAndIndexes()
    {
        Assert.AreEqual("regular", AppStateSchemas.LabelEdit.Collection);
        Assert.AreEqual(3, AppStateSchemas.LabelEdit.Version);
        Assert.AreEqual("""["label_edit","lbl-1"]""", Encoding.UTF8.GetString(AppStateSchemas.LabelEdit.BuildIndex("lbl-1")));

        Assert.AreEqual(3, AppStateSchemas.LabelJid.Version);
        Assert.AreEqual("""["label_jid","lbl-1","1555@s.whatsapp.net"]""",
            Encoding.UTF8.GetString(AppStateSchemas.LabelJid.BuildIndex("lbl-1", "1555@s.whatsapp.net")));

        Assert.AreEqual(3, AppStateSchemas.LabelMessage.Version);
        Assert.AreEqual("""["label_message","lbl-1","1555@s.whatsapp.net","MSG-1","0","0"]""",
            Encoding.UTF8.GetString(AppStateSchemas.LabelMessage.BuildIndex("lbl-1", "1555@s.whatsapp.net", "MSG-1", "0", "0")));

        Assert.AreEqual(3, AppStateSchemas.LabelReordering.Version);
        Assert.AreEqual("""["label_reordering"]""", Encoding.UTF8.GetString(AppStateSchemas.LabelReordering.BuildIndex()));

        Assert.AreEqual(1, AppStateSchemas.LabelSublist.Version);
        Assert.AreEqual("""["label_sublist","11","1555@s.whatsapp.net"]""",
            Encoding.UTF8.GetString(AppStateSchemas.LabelSublist.BuildIndex("11", "1555@s.whatsapp.net")));

        Assert.AreEqual(2, AppStateSchemas.QuickReply.Version);
        Assert.AreEqual("""["quick_reply","qr-1"]""", Encoding.UTF8.GetString(AppStateSchemas.QuickReply.BuildIndex("qr-1")));
    }

    [TestMethod]
    public void BusinessStanzas_ProfileCatalogCollectionsOrderAndMessages_WorkEndToEnd()
    {
        Jid bizJid = Jid.Parse("15550003333@s.whatsapp.net");
        Node getIq = BusinessStanzas.BuildGetBusinessProfileIq("iq-biz-1", bizJid);
        Assert.AreEqual("w:biz", getIq.GetAttr("xmlns"));
        Assert.AreEqual("244", getIq.GetChild("business_profile")?.GetAttr("v"));

        Node readResp = new("iq", new Dictionary<string, NodeValue> { ["type"] = "result" }, new[]
        {
            new Node("business_profile", new[]
            {
                new Node("profile", new Dictionary<string, NodeValue> { ["jid"] = bizJid }, new Node[]
                {
                    new("description", "Coffee Roasters"),
                    new("address", "123 Market St"),
                    new("email", "hello@coffee.test"),
                    new("website", "https://coffee.test"),
                    new("categories", new[] { new Node("category", new Dictionary<string, NodeValue> { ["id"] = "c1" }, "Cafe") }),
                    new("business_hours", new Dictionary<string, NodeValue> { ["timezone"] = "America/Los_Angeles" }, new[]
                    {
                        new Node("business_hours_config", new Dictionary<string, NodeValue>
                        {
                            ["day_of_week"] = "mon",
                            ["mode"] = "specific_hours",
                            ["open_time"] = "480",
                            ["close_time"] = "1020"
                        })
                    })
                })
            })
        });
        BusinessProfile? profile = BusinessStanzas.ParseBusinessProfileResponse(readResp);
        Assert.IsNotNull(profile);
        Assert.AreEqual("Coffee Roasters", profile.Description);
        Assert.AreEqual(1, profile.Websites.Count);
        Assert.AreEqual("Cafe", profile.Categories[0].Name);
        Assert.AreEqual(480u, profile.BusinessHours.Config[0].OpenTimeMinutes);

        Node updateIq = BusinessStanzas.BuildUpdateBusinessProfileIq("iq-biz-2", new BusinessProfileUpdate(
            Description: "Updated Bio",
            Websites: Array.Empty<string>(),
            BusinessHours: new BusinessHoursUpdate("UTC", "Holidays vary", [new BusinessHoursConfig("sun", BusinessHourMode.Open24H)])));
        Node bpDelta = updateIq.GetChild("business_profile")!;
        Assert.AreEqual("3", bpDelta.GetAttr("v"));
        Assert.AreEqual("delta", bpDelta.GetAttr("mutation_type"));
        Assert.IsNull(bpDelta.GetChild("website")!.Content);

        Assert.ThrowsException<ArgumentException>(() =>
            BusinessStanzas.BuildUpdateBusinessProfileIq("bad", new BusinessProfileUpdate(Websites: ["a", "b", "c"])));
        Assert.ThrowsException<ArgumentException>(() =>
            BusinessStanzas.BuildUpdateBusinessProfileIq("bad2", new BusinessProfileUpdate(
                BusinessHours: new BusinessHoursUpdate(null, null, [new BusinessHoursConfig("mon", BusinessHourMode.SpecificHours)]))));

        Node coverSet = BusinessStanzas.BuildSetCoverPhotoIq("cov-1", new CoverPhotoUpload("fbid-9", "hmac-1", 1710000000));
        Assert.AreEqual("update", coverSet.GetChild("business_profile")?.GetChild("cover_photo")?.GetAttr("op"));

        // Catalog & Order JSON parsing
        using JsonDocument catalogDoc = JsonDocument.Parse("""
            {
              "xwa_product_catalog_get_product_catalog": {
                "product_catalog": {
                  "products": [
                    {
                      "id": "prod-1",
                      "name": "Espresso Blend",
                      "currency": "USD",
                      "price": "1990",
                      "sale_price": { "price": "1490", "start_date": "2026-01-01" },
                      "is_hidden": "ISHIDDEN_FALSE",
                      "max_available": 25,
                      "product_availability": "IN_STOCK",
                      "status_info": { "status": "APPROVED", "can_appeal": false },
                      "media": { "images": [{ "id": "img-1", "request_image_url": "https://cdn/1.jpg" }], "videos": [] },
                      "compliance_info": { "country_code_origin": "US" }
                    }
                  ],
                  "paging": { "after": "cur-next" }
                }
              }
            }
            """);
        BusinessCatalog catalog = BusinessStanzas.ParseCatalogResponse(catalogDoc.RootElement);
        Assert.AreEqual(1, catalog.Products.Count);
        Assert.AreEqual(1990L, catalog.Products[0].Price?.Amount1000);
        Assert.AreEqual("USD", catalog.Products[0].Price?.Currency);
        // Incomplete sale window (start_date only, no end_date) strips dates while preserving price
        Assert.AreEqual(1490L, catalog.Products[0].SalePrice?.Price.Amount1000);
        Assert.IsNull(catalog.Products[0].SalePrice?.StartDate);
        Assert.AreEqual(false, catalog.Products[0].IsHidden);
        Assert.AreEqual("cur-next", catalog.AfterCursor);

        // Product & Order proto messages
        Message prodMsg = RichMessageBuilders.BuildProductMessage(bizJid, "prod-1", "Espresso Blend", priceAmount1000: 1990, currencyCode: "USD");
        Assert.AreEqual("prod-1", Message.Parser.ParseFrom(prodMsg.ToByteArray()).ProductMessage.Product.ProductId);

        Message orderMsg = RichMessageBuilders.BuildOrderMessage("ord-99", "tok-99", bizJid, 2, totalAmount1000: 3980, totalCurrencyCode: "USD", orderTitle: "Order #99");
        Assert.AreEqual("ord-99", Message.Parser.ParseFrom(orderMsg.ToByteArray()).OrderMessage.OrderId);
    }
}
