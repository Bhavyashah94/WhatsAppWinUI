using System.Collections.Concurrent;

namespace WhatsApp.Protocol.HistorySync;

public interface IContactRegistry
{
    void Upsert(string jid, string? name = null, string? pushname = null, string? phoneNumber = null, bool isAddressBook = false);
    bool TryGet(string jid, out ContactRecord? contact);
    IReadOnlyCollection<ContactRecord> GetAll();
    int Count { get; }
    void Clear();
}

public sealed class ContactRegistry : IContactRegistry
{
    private readonly ConcurrentDictionary<string, ContactRecord> _contacts = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _contacts.Count;

    public void Upsert(string jid, string? name = null, string? pushname = null, string? phoneNumber = null, bool isAddressBook = false)
    {
        if (string.IsNullOrWhiteSpace(jid)) return;

        string normalizedJid = Normalize(jid);

        _contacts.AddOrUpdate(
            normalizedJid,
            _ => new ContactRecord(normalizedJid, name, pushname, phoneNumber, isAddressBook),
            (_, existing) => new ContactRecord(
                normalizedJid,
                Name: !string.IsNullOrWhiteSpace(name) ? name : existing.Name,
                Pushname: !string.IsNullOrWhiteSpace(pushname) ? pushname : existing.Pushname,
                PhoneNumber: !string.IsNullOrWhiteSpace(phoneNumber) ? phoneNumber : existing.PhoneNumber,
                IsAddressBookContact: isAddressBook || existing.IsAddressBookContact
            )
        );
    }

    public bool TryGet(string jid, out ContactRecord? contact)
    {
        string normalizedJid = Normalize(jid);
        return _contacts.TryGetValue(normalizedJid, out contact);
    }

    public IReadOnlyCollection<ContactRecord> GetAll() => _contacts.Values.ToList();

    public void Clear() => _contacts.Clear();

    private static string Normalize(string raw)
    {
        int atIndex = raw.IndexOf('@');
        if (atIndex == -1) return raw;

        string user = raw[..atIndex];
        string server = raw[(atIndex + 1)..];

        if (server.Equals("c.us", StringComparison.OrdinalIgnoreCase))
        {
            server = "s.whatsapp.net";
        }

        return $"{user}@{server.ToLowerInvariant()}";
    }
}
