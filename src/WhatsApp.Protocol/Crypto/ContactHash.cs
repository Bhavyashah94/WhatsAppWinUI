using System.Security.Cryptography;
using System.Text;

namespace WhatsApp.Protocol.Crypto;

/// <summary>
/// WhatsApp legacy non-cryptographic contact identifier hashes used by server
/// hash-based notifications (<c>&lt;notification type="devices"&gt;&lt;update hash="..."/&gt;</c>,
/// <c>&lt;notification type="picture"&gt;&lt;set hash="..."/&gt;</c>,
/// <c>&lt;notification type="contacts"&gt;&lt;update hash="..."/&gt;</c>).
/// Matches live <c>WAWebApiContact.getContactHash</c>: Base64 of first 3 bytes of
/// <c>MD5(user + "WA_ADD_NOTIF")</c>.
/// </summary>
public static class ContactHash
{
    private const string Suffix = "WA_ADD_NOTIF";

    /// <summary>
    /// Computes the 3-byte hash prefix from a bare user ID (without <c>@server</c> or device suffix).
    /// </summary>
    public static byte[] ComputeBytes(string userOrJid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userOrJid);
        string user = ExtractUser(userOrJid);
        byte[] input = Encoding.UTF8.GetBytes(string.Concat(user, Suffix));
        byte[] digest = MD5.HashData(input);
        return digest[..3];
    }

    /// <summary>
    /// Computes the 4-character Base64 hash string matching <c>WAWebApiContact.getContactHash</c>.
    /// </summary>
    public static string ComputeBase64(string userOrJid) =>
        Convert.ToBase64String(ComputeBytes(userOrJid));

    private static string ExtractUser(string userOrJid)
    {
        int atIdx = userOrJid.IndexOf('@');
        string bare = atIdx >= 0 ? userOrJid[..atIdx] : userOrJid;
        int colonIdx = bare.IndexOf(':');
        return colonIdx >= 0 ? bare[..colonIdx] : bare;
    }
}
