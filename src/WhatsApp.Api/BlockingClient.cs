using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Api;

/// <summary>
/// Runtime contact blocking client (<c>xmlns="blocklist"</c>) enforcing modern WA's
/// paired <c>jid=LID</c> + <c>pn_jid=PN</c> wire contract on block and LID resolution
/// on unblock matching <c>src/features/blocking.rs</c>.
/// </summary>
public sealed class BlockingClient
{
    private readonly IqClient _iqClient;
    private readonly ILidPnCache _lidPnCache;
    private readonly Func<WhatsAppEvent, ValueTask>? _emitEvent;
    private readonly TimeProvider _timeProvider;

    public BlockingClient(
        IqClient iqClient,
        ILidPnCache lidPnCache,
        Func<WhatsAppEvent, ValueTask>? emitEvent = null,
        TimeProvider? timeProvider = null)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _emitEvent = emitEvent;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task BlockAsync(Jid userJid, CancellationToken ct = default)
    {
        (Jid lid, Jid pn) = ResolveLidAndPn(userJid.ToNonAd());
        string iqId = _iqClient.NextRequestId();
        Node iq = BlocklistIq.BuildUpdateItem(iqId, lid, block: true, pnJid: pn);
        await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        await EmitAsync(new BlocklistEntryUpdatedEvent(lid.ToString(), Blocked: true, NowUnixSeconds())).ConfigureAwait(false);
    }

    public async Task UnblockAsync(Jid userJid, CancellationToken ct = default)
    {
        Jid bare = userJid.ToNonAd();
        Jid lid = bare.IsLid()
            ? bare
            : ResolveLidAndPn(bare).Lid;

        string iqId = _iqClient.NextRequestId();
        Node iq = BlocklistIq.BuildUpdateItem(iqId, lid, block: false);
        await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        await EmitAsync(new BlocklistEntryUpdatedEvent(lid.ToString(), Blocked: false, NowUnixSeconds())).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BlocklistEntry>> GetBlocklistAsync(CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        Node iq = BlocklistIq.BuildGetBlocklist(iqId);
        Node resp = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        IReadOnlyList<BlocklistEntry> entries = BlocklistIq.ParseGetBlocklistResponse(resp);
        await EmitAsync(new BlocklistUpdatedEvent(entries, NowUnixSeconds())).ConfigureAwait(false);
        return entries;
    }

    public async Task<bool> IsBlockedAsync(Jid userJid, CancellationToken ct = default)
    {
        Jid bare = userJid.ToNonAd();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bare.ToString() };
        if (bare.IsLid() && _lidPnCache.TryGetPn(bare.ToString(), out string? pnStr) && !string.IsNullOrEmpty(pnStr))
        {
            candidates.Add(pnStr);
        }
        else if (bare.IsPn() && _lidPnCache.TryGetLid(bare.ToString(), out string? lidStr) && !string.IsNullOrEmpty(lidStr))
        {
            candidates.Add(lidStr);
        }

        IReadOnlyList<BlocklistEntry> entries = await GetBlocklistAsync(ct).ConfigureAwait(false);
        return entries.Any(e => candidates.Contains(e.Jid.ToNonAdString()));
    }

    private (Jid Lid, Jid Pn) ResolveLidAndPn(Jid bare)
    {
        if (bare.IsLid())
        {
            if (_lidPnCache.TryGetPn(bare.ToString(), out string? pnStr)
                && !string.IsNullOrEmpty(pnStr)
                && Jid.TryParse(pnStr, out Jid pn))
            {
                return (bare, pn.ToNonAd());
            }
            throw new InvalidOperationException($"No known LID<->PN mapping for '{bare}'.");
        }

        if (bare.IsPn())
        {
            if (_lidPnCache.TryGetLid(bare.ToString(), out string? lidStr)
                && !string.IsNullOrEmpty(lidStr)
                && Jid.TryParse(lidStr, out Jid lid))
            {
                return (lid.ToNonAd(), bare);
            }
            throw new InvalidOperationException($"No known LID<->PN mapping for '{bare}'.");
        }

        throw new ArgumentException($"JID '{bare}' is neither a PN nor a LID user.", nameof(bare));
    }

    private long NowUnixSeconds() => _timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private ValueTask EmitAsync(WhatsAppEvent evt) =>
        _emitEvent is not null ? _emitEvent(evt) : ValueTask.CompletedTask;
}
