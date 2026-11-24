namespace WhatsApp.Protocol.HistorySync;

/// <summary>
/// Implements WhatsApp Web's deterministic display title priority resolution:
/// Group Subject > Address Book Name > Pushname > Resolved Phone Number > Raw ID.
/// </summary>
public static class DisplayNameResolver
{
    public static string ResolveTitle(
        string jid,
        bool isGroup,
        IGroupRegistry? groupRegistry = null,
        IContactRegistry? contactRegistry = null,
        ILidPnCache? lidPnCache = null)
    {
        if (string.IsNullOrWhiteSpace(jid)) return string.Empty;

        // 1. Group Chat (@g.us): Group Subject takes highest priority
        if (isGroup || jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
        {
            if (groupRegistry != null && groupRegistry.TryGet(jid, out var group) && group != null && !string.IsNullOrWhiteSpace(group.Subject))
            {
                return group.Subject;
            }
            return ExtractUser(jid);
        }

        // 2. Direct Contact: Address Book Name takes priority over Pushname
        if (contactRegistry != null && contactRegistry.TryGet(jid, out var directContact) && directContact != null)
        {
            if (!string.IsNullOrWhiteSpace(directContact.Name))
            {
                return directContact.Name;
            }
            if (!string.IsNullOrWhiteSpace(directContact.Pushname))
            {
                return directContact.Pushname;
            }
        }

        // 3. LID Address (@lid): Resolve cryptographic mapping to Phone Number
        if (jid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase) && lidPnCache != null && lidPnCache.TryGetPn(jid, out var pn) && !string.IsNullOrWhiteSpace(pn))
        {
            // Check if address book has a name mapped to the resolved phone number
            if (contactRegistry != null && contactRegistry.TryGet(pn, out var pnContact) && pnContact != null)
            {
                if (!string.IsNullOrWhiteSpace(pnContact.Name))
                {
                    return pnContact.Name;
                }
                if (!string.IsNullOrWhiteSpace(pnContact.Pushname))
                {
                    return pnContact.Pushname;
                }
            }

            return FormatPhoneNumber(pn);
        }

        // 4. Standard Phone Number JID (@s.whatsapp.net or @c.us): format as international number
        if (jid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) || jid.EndsWith("@c.us", StringComparison.OrdinalIgnoreCase))
        {
            return FormatPhoneNumber(jid);
        }

        // 5. Last resort fallback: raw user ID string
        return ExtractUser(jid);
    }

    /// <summary>
    /// Formats E.164 phone numbers into readable international format (e.g. +91 98765 43210).
    /// </summary>
    public static string FormatPhoneNumber(string jidOrPhone)
    {
        if (jidOrPhone.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) ||
            jidOrPhone.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractUser(jidOrPhone);
        }

        string raw = ExtractUser(jidOrPhone);
        if (string.IsNullOrWhiteSpace(raw)) return jidOrPhone;

        string digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return raw;

        // India (+91)
        if (digits.Length == 12 && digits.StartsWith("91"))
        {
            return $"+91 {digits[2..7]} {digits[7..]}";
        }
        // US / Canada (+1)
        if (digits.Length == 11 && digits.StartsWith("1"))
        {
            return $"+1 ({digits[1..4]}) {digits[4..7]}-{digits[7..]}";
        }
        // General international
        if (digits.Length >= 8)
        {
            return $"+{digits}";
        }

        return raw;
    }

    private static string ExtractUser(string jid)
    {
        int atIndex = jid.IndexOf('@');
        return atIndex != -1 ? jid[..atIndex] : jid;
    }
}
