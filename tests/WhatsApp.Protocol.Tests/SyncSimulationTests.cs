using System.Text.Json;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Protocol.Tests;

[TestClass]
public sealed class SyncSimulationTests
{
    private static string BuildSyntheticGroupsJson(int count)
    {
        var groups = Enumerable.Range(1, count).Select(i => new
        {
            id = $"12036300000000{i:D4}@g.us",
            subject = $"Sample Group {i:D2}",
            owner = $"10000000000{i:D4}@lid",
            creation = 1700000000UL + (ulong)i,
            desc = i % 2 == 0 ? $"Synthetic group description {i}" : null
        });
        return JsonSerializer.Serialize(groups);
    }

    private static string BuildSyntheticContactsJson() => JsonSerializer.Serialize(new object[]
    {
        new { id = "100000000000101@lid", name = "Alice Example", pushname = "Alice", phoneNumber = "15550100101@s.whatsapp.net", isAddressBookContact = true },
        new { id = "100000000000102@lid", name = "Bob Sample", pushname = "Bobby", phoneNumber = "15550100102@s.whatsapp.net", isAddressBookContact = true },
        new { id = "100000000000103@lid", name = (string?)null, pushname = "Charlie Push", phoneNumber = "15550100103@s.whatsapp.net", isAddressBookContact = false },
        new { id = "100000000000104@lid", name = (string?)null, pushname = (string?)null, phoneNumber = "15550100104@s.whatsapp.net", isAddressBookContact = false }
    });

    [TestMethod]
    public void Simulation_Resolves_All_Synthetic_Groups()
    {
        using var doc = JsonDocument.Parse(BuildSyntheticGroupsJson(100));
        var groupRegistry = new GroupRegistry();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string id = el.GetProperty("id").GetString()!;
            string subject = el.GetProperty("subject").GetString()!;
            string? owner = el.TryGetProperty("owner", out var oProp) ? oProp.GetString() : null;
            ulong creation = el.TryGetProperty("creation", out var cProp) ? cProp.GetUInt64() : 0;
            string? desc = el.TryGetProperty("desc", out var dProp) && dProp.ValueKind == JsonValueKind.String ? dProp.GetString() : null;

            groupRegistry.Upsert(new GroupMetadataRecord(id, subject, owner, creation, desc));
        }

        Assert.AreEqual(100, groupRegistry.Count);

        foreach (var group in groupRegistry.GetAll())
        {
            string title = DisplayNameResolver.ResolveTitle(group.Id, isGroup: true, groupRegistry: groupRegistry);
            Assert.IsFalse(string.IsNullOrWhiteSpace(title));
            Assert.AreEqual(group.Subject, title);
        }
    }

    [TestMethod]
    public void Simulation_Resolves_Synthetic_Contacts_And_Lid_Mappings()
    {
        using var doc = JsonDocument.Parse(BuildSyntheticContactsJson());
        var lidPnCache = new LidPnCache();
        var contactRegistry = new ContactRegistry();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string id = el.GetProperty("id").GetString()!;
            string? name = el.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String ? nProp.GetString() : null;
            string? pushname = el.TryGetProperty("pushname", out var pProp) && pProp.ValueKind == JsonValueKind.String ? pProp.GetString() : null;
            string? phone = el.TryGetProperty("phoneNumber", out var phProp) && phProp.ValueKind == JsonValueKind.String ? phProp.GetString() : null;
            bool isAddressBook = el.TryGetProperty("isAddressBookContact", out var abProp) && abProp.GetBoolean();

            if (!string.IsNullOrEmpty(phone))
            {
                lidPnCache.Learn(phone, id);
            }

            contactRegistry.Upsert(id, name, pushname, phone, isAddressBook);
        }

        Assert.AreEqual(4, contactRegistry.Count);
        Assert.AreEqual(4, lidPnCache.Count);

        Assert.AreEqual("Alice Example", DisplayNameResolver.ResolveTitle("100000000000101@lid", false, contactRegistry: contactRegistry, lidPnCache: lidPnCache));
        Assert.AreEqual("Bob Sample", DisplayNameResolver.ResolveTitle("100000000000102@lid", false, contactRegistry: contactRegistry, lidPnCache: lidPnCache));
        Assert.AreEqual("Charlie Push", DisplayNameResolver.ResolveTitle("100000000000103@lid", false, contactRegistry: contactRegistry, lidPnCache: lidPnCache));
        Assert.AreEqual("+1 (555) 010-0104", DisplayNameResolver.ResolveTitle("100000000000104@lid", false, contactRegistry: contactRegistry, lidPnCache: lidPnCache));
    }

    [TestMethod]
    public void Simulation_Classifies_Synthetic_Messages_Across_Types()
    {
        string messagesJson = JsonSerializer.Serialize(new object[]
        {
            new { type = "chat", directPath = (string?)null, mediaKey = (string?)null },
            new { type = "sticker", directPath = "/v/t62.15575-24/synth_1.enc", mediaKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=" },
            new { type = "chat", directPath = (string?)null, mediaKey = (string?)null },
            new { type = "sticker", directPath = "/v/t62.15575-24/synth_2.enc", mediaKey = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=" }
        });

        using var doc = JsonDocument.Parse(messagesJson);
        int stickerCount = 0;
        int textCount = 0;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string typeStr = el.GetProperty("type").GetString()!;
            if (typeStr == "sticker")
            {
                stickerCount++;
                string? directPath = el.GetProperty("directPath").GetString();
                string? mediaKey = el.GetProperty("mediaKey").GetString();
                Assert.IsFalse(string.IsNullOrEmpty(directPath));
                Assert.IsFalse(string.IsNullOrEmpty(mediaKey));
                Assert.IsTrue(directPath.Contains("v/t62"));
            }
            else if (typeStr == "chat")
            {
                textCount++;
            }
        }

        Assert.AreEqual(2, stickerCount);
        Assert.AreEqual(2, textCount);
    }

    [TestMethod]
    public void Simulation_EndToEnd_Sync_State_Reconstruction()
    {
        var groupRegistry = new GroupRegistry();
        using (var gDoc = JsonDocument.Parse(BuildSyntheticGroupsJson(10)))
        {
            foreach (var el in gDoc.RootElement.EnumerateArray())
            {
                groupRegistry.Upsert(new GroupMetadataRecord(
                    el.GetProperty("id").GetString()!,
                    el.GetProperty("subject").GetString()!,
                    null,
                    0,
                    null));
            }
        }

        var lidPnCache = new LidPnCache();
        var contactRegistry = new ContactRegistry();
        using (var cDoc = JsonDocument.Parse(BuildSyntheticContactsJson()))
        {
            foreach (var el in cDoc.RootElement.EnumerateArray())
            {
                string id = el.GetProperty("id").GetString()!;
                string? name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                string? pushname = el.TryGetProperty("pushname", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                string? phone = el.TryGetProperty("phoneNumber", out var ph) && ph.ValueKind == JsonValueKind.String ? ph.GetString() : null;

                if (!string.IsNullOrEmpty(phone))
                {
                    lidPnCache.Learn(phone, id);
                }
                contactRegistry.Upsert(id, name, pushname, phone);
            }
        }

        string chatsJson = JsonSerializer.Serialize(new object[]
        {
            new { id = "120363000000000001@g.us", isGroup = true },
            new { id = "120363000000000002@g.us", isGroup = true },
            new { id = "100000000000101@lid", isGroup = false },
            new { id = "100000000000103@lid", isGroup = false },
            new { id = "100000000000104@lid", isGroup = false },
            new { id = "15550100199@s.whatsapp.net", isGroup = false }
        });

        using var chatDoc = JsonDocument.Parse(chatsJson);
        int totalChats = 0;
        int resolvedTitlesCount = 0;

        foreach (var el in chatDoc.RootElement.EnumerateArray())
        {
            totalChats++;
            string chatId = el.GetProperty("id").GetString()!;
            bool isGroup = el.GetProperty("isGroup").GetBoolean();

            string title = DisplayNameResolver.ResolveTitle(
                chatId,
                isGroup,
                groupRegistry,
                contactRegistry,
                lidPnCache);

            Assert.IsFalse(string.IsNullOrWhiteSpace(title));
            resolvedTitlesCount++;
        }

        Assert.AreEqual(6, totalChats);
        Assert.AreEqual(totalChats, resolvedTitlesCount);
    }
}
