using System.Collections.Concurrent;
using WhatsApp.Protocol.Binary;

namespace WhatsApp.Protocol.HistorySync;

public sealed record GroupMetadataRecord(
    string Id,
    string Subject,
    string? Owner,
    ulong Creation,
    string? Description,
    bool Restrict = false,
    bool Announce = false,
    int ParticipantsCount = 0
);

public interface IGroupRegistry
{
    void Upsert(GroupMetadataRecord group);
    bool TryGet(string groupId, out GroupMetadataRecord? group);
    IReadOnlyCollection<GroupMetadataRecord> GetAll();
    int Count { get; }
    void Clear();
}

public sealed class GroupRegistry : IGroupRegistry
{
    private readonly ConcurrentDictionary<string, GroupMetadataRecord> _groups = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _groups.Count;

    public void Upsert(GroupMetadataRecord group)
    {
        if (string.IsNullOrWhiteSpace(group.Id)) return;
        string normalizedId = NormalizeGroupId(group.Id);
        _groups[normalizedId] = group with { Id = normalizedId };
    }

    public bool TryGet(string groupId, out GroupMetadataRecord? group)
    {
        string normalizedId = NormalizeGroupId(groupId);
        return _groups.TryGetValue(normalizedId, out group);
    }

    public IReadOnlyCollection<GroupMetadataRecord> GetAll() => _groups.Values.ToList();

    public void Clear() => _groups.Clear();

    public static string NormalizeGroupId(string raw)
    {
        return raw.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) ? raw.ToLowerInvariant() : $"{raw}@g.us";
    }
}

/// <summary>
/// Handles w:g2 group queries conforming to Rust reference wacore::iq::groups.
/// </summary>
public static class GroupSyncIq
{
    public const string GroupServer = "g.us";
    public const string GroupXmlns = "w:g2";

    /// <summary>
    /// Builds an IQ request to fetch all participating groups:
    /// &lt;iq id="{id}" to="g.us" type="get" xmlns="w:g2"&gt;&lt;participating/&gt;&lt;/iq&gt;
    /// </summary>
    public static Node BuildParticipatingIq(string iqId)
    {
        var attrs = new Dictionary<string, NodeValue>(StringComparer.Ordinal)
        {
            ["id"] = iqId,
            ["to"] = GroupServer,
            ["type"] = "get",
            ["xmlns"] = GroupXmlns
        };

        var child = new Node("participating");
        return new Node("iq", attrs, new Node[] { child });
    }

    /// <summary>
    /// Parses a &lt;groups&gt; response into strongly typed GroupMetadataRecord items.
    /// </summary>
    public static IReadOnlyList<GroupMetadataRecord> ParseParticipatingResponse(Node responseNode)
    {
        var results = new List<GroupMetadataRecord>();

        // Find <groups> or examine children directly
        var groupsContainer = responseNode.Tag == "groups"
            ? responseNode
            : responseNode.GetChild("groups") ?? responseNode;

        foreach (var groupNode in groupsContainer.GetChildren("group"))
        {
            string? id = groupNode.GetAttr("id");
            if (string.IsNullOrWhiteSpace(id)) continue;

            string normalizedId = GroupRegistry.NormalizeGroupId(id);
            string subject = groupNode.GetAttr("subject") ?? string.Empty;
            string? owner = groupNode.GetAttr("creator") ?? groupNode.GetAttr("owner");
            
            ulong creation = 0;
            if (ulong.TryParse(groupNode.GetAttr("creation"), out ulong cVal))
            {
                creation = cVal;
            }

            bool restrict = groupNode.GetAttr("restrict") == "true" || groupNode.GetAttr("restrict") == "1";
            bool announce = groupNode.GetAttr("announce") == "true" || groupNode.GetAttr("announce") == "1";

            string? description = null;
            var descNode = groupNode.GetChild("description");
            if (descNode?.Content is NodeContent.StringContent sc)
            {
                description = sc.Text;
            }
            else if (descNode?.Content is NodeContent.BytesContent bc)
            {
                description = System.Text.Encoding.UTF8.GetString(bc.Bytes);
            }

            int participantsCount = groupNode.GetChildren("participant").Count();

            results.Add(new GroupMetadataRecord(
                normalizedId,
                subject,
                owner,
                creation,
                description,
                restrict,
                announce,
                participantsCount
            ));
        }

        return results;
    }
}
