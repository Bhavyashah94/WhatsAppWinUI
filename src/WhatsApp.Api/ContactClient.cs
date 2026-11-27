using Whatsapp;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.AppState;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Api;

/// <summary>
/// Runtime client for AppState-synced address-book contacts (<c>contact</c> and
/// <c>lid_contact</c> in <c>critical_unblock_low</c>) and local LID/PN resolution helpers.
/// </summary>
public sealed class ContactClient
{
    private readonly SyncdCoordinator _syncd;
    private readonly ILidPnCache _lidPnCache;
    private readonly TimeProvider _timeProvider;

    public ContactClient(
        SyncdCoordinator syncd,
        ILidPnCache lidPnCache,
        TimeProvider? timeProvider = null)
    {
        _syncd = syncd ?? throw new ArgumentNullException(nameof(syncd));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task SaveContactAsync(
        Jid pnJid,
        string fullName,
        string? firstName = null,
        string? username = null,
        bool saveOnPrimaryAddressbook = true,
        CancellationToken ct = default)
    {
        if (!pnJid.IsPn() || pnJid.Device != 0)
        {
            throw new ArgumentException($"Contact mutation requires a bare @s.whatsapp.net PN JID, got '{pnJid}'.", nameof(pnJid));
        }

        var act = new SyncActionValue.Types.ContactAction
        {
            FullName = fullName ?? string.Empty,
            SaveOnPrimaryAddressbook = saveOnPrimaryAddressbook
        };
        if (!string.IsNullOrEmpty(firstName)) act.FirstName = firstName;
        if (!string.IsNullOrEmpty(username)) act.Username = username;

        if (_lidPnCache.TryGetLid(pnJid.ToString(), out string? lidStr) && !string.IsNullOrEmpty(lidStr))
        {
            act.LidJid = lidStr;
        }

        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            ContactAction = act
        };

        return _syncd.SendMutationAsync(
            AppStateSchemas.Contact.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.Contact.BuildIndex(pnJid.ToString()),
            value,
            actionVersion: AppStateSchemas.Contact.Version,
            ct: ct);
    }

    public Task SaveLidContactAsync(
        Jid lidJid,
        string fullName,
        string? firstName = null,
        string? username = null,
        CancellationToken ct = default)
    {
        if (!lidJid.IsLid() || lidJid.Device != 0)
        {
            throw new ArgumentException($"LID contact mutation requires a bare @lid JID, got '{lidJid}'.", nameof(lidJid));
        }

        var act = new SyncActionValue.Types.LidContactAction
        {
            FullName = fullName ?? string.Empty
        };
        if (!string.IsNullOrEmpty(firstName)) act.FirstName = firstName;
        if (!string.IsNullOrEmpty(username)) act.Username = username;

        var value = new SyncActionValue
        {
            Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            LidContactAction = act
        };

        return _syncd.SendMutationAsync(
            AppStateSchemas.LidContact.Collection,
            SyncdMutation.Types.SyncdOperation.Set,
            AppStateSchemas.LidContact.BuildIndex(lidJid.ToString()),
            value,
            actionVersion: AppStateSchemas.LidContact.Version,
            ct: ct);
    }

    public Task DeleteContactAsync(Jid pnJid, CancellationToken ct = default)
    {
        if (!pnJid.IsPn() || pnJid.Device != 0)
        {
            throw new ArgumentException($"Contact deletion requires a bare @s.whatsapp.net PN JID, got '{pnJid}'.", nameof(pnJid));
        }

        return _syncd.SendMutationAsync(
            AppStateSchemas.Contact.Collection,
            SyncdMutation.Types.SyncdOperation.Remove,
            AppStateSchemas.Contact.BuildIndex(pnJid.ToString()),
            new SyncActionValue { Timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() },
            actionVersion: AppStateSchemas.Contact.Version,
            ct: ct);
    }
}
