namespace WhatsApp.Protocol.Binary;

public abstract record NodeValue
{
    public sealed record StringValue(string Text) : NodeValue
    {
        public override string ToString() => Text;
    }

    public sealed record JidValue(Jid Jid) : NodeValue
    {
        public override string ToString() => Jid.ToString();
    }

    public static implicit operator NodeValue(string text) => new StringValue(text);
    public static implicit operator NodeValue(Jid jid) => new JidValue(jid);
    public static implicit operator string(NodeValue value) => value.ToString();
}

public abstract record NodeContent
{
    public sealed record BytesContent(byte[] Bytes) : NodeContent;
    public sealed record StringContent(string Text) : NodeContent;
    public sealed record NodesContent(IReadOnlyList<Node> Nodes) : NodeContent;

    public static implicit operator NodeContent(byte[] bytes) => new BytesContent(bytes);
    public static implicit operator NodeContent(string text) => new StringContent(text);
    public static implicit operator NodeContent(List<Node> nodes) => new NodesContent(nodes);
    public static implicit operator NodeContent(Node[] nodes) => new NodesContent(nodes);
}

public class Node
{
    public string Tag { get; set; }
    public Dictionary<string, NodeValue> Attrs { get; set; }
    public NodeContent? Content { get; set; }

    public Node(string tag, Dictionary<string, NodeValue>? attrs = null, NodeContent? content = null)
    {
        Tag = tag;
        Attrs = attrs ?? new Dictionary<string, NodeValue>(StringComparer.Ordinal);
        Content = content;
    }

    public Node(string tag, NodeContent content) : this(tag, null, content)
    {
    }

    public string? GetAttr(string key) => Attrs.TryGetValue(key, out var val) ? val.ToString() : null;

    public Node SetAttr(string key, string value)
    {
        Attrs[key] = new NodeValue.StringValue(value);
        return this;
    }

    public Node SetAttr(string key, Jid jid)
    {
        Attrs[key] = new NodeValue.JidValue(jid);
        return this;
    }

    public Node? GetChild(string tag)
    {
        if (Content is NodeContent.NodesContent nodes)
        {
            return nodes.Nodes.FirstOrDefault(n => n.Tag == tag);
        }
        return null;
    }

    public IEnumerable<Node> GetChildren(string tag)
    {
        if (Content is NodeContent.NodesContent nodes)
        {
            return nodes.Nodes.Where(n => n.Tag == tag);
        }
        return Enumerable.Empty<Node>();
    }

    public override string ToString()
    {
        var attrStr = string.Join(" ", Attrs.Select(kv => $"{kv.Key}=\"{kv.Value}\""));
        var open = string.IsNullOrEmpty(attrStr) ? $"<{Tag}" : $"<{Tag} {attrStr}";

        return Content switch
        {
            NodeContent.StringContent s => $"{open}>{s.Text}</{Tag}>",
            NodeContent.BytesContent b => $"{open}>[{b.Bytes.Length} bytes]</{Tag}>",
            NodeContent.NodesContent n => $"{open}>{string.Join("", n.Nodes.Select(c => c.ToString()))}</{Tag}>",
            _ => $"{open}/>"
        };
    }
}
