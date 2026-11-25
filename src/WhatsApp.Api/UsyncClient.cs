using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;

namespace WhatsApp.Api;

public sealed record ResolvedDeviceList(
    Jid UserJid,
    IReadOnlyList<uint> DeviceIds,
    bool AdvVerified,
    string? DeviceListHash = null);

/// <summary>
/// Runtime USync client handling device list sync (with ADV key-index verification and
/// <see cref="IProtocolStore"/> caching), <c>@username</c> -&gt; LID resolution, contact lookup,
/// and full user info queries, automatically updating <see cref="ILidPnCache"/> and persisting mappings.
/// Mirrors <c>whatsapp-rust/src/usync.rs</c> and <c>WAWebQueryExistsJob</c>.
/// </summary>
public sealed class UsyncClient
{
    private readonly IqClient _iqClient;
    private readonly IWhatsAppBackend _backend;
    private readonly ILidPnCache _lidPnCache;
    private readonly TimeProvider _timeProvider;
    private long _sidCounter;

    public UsyncClient(
        IqClient iqClient,
        IWhatsAppBackend backend,
        ILidPnCache lidPnCache,
        TimeProvider? timeProvider = null)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private string NextSid()
    {
        long n = Interlocked.Increment(ref _sidCounter);
        return $"{_timeProvider.GetUtcNow().ToUnixTimeSeconds()}.{n}";
    }

    /// <summary>
    /// Resolves a <c>@username</c> (with or without leading <c>@</c>) to a LID and optional PN/verified-name
    /// via <c>usync</c> (<c>contact addressing_mode="lid"</c> + <c>business</c>), persisting any learned
    /// LID-PN mapping into <see cref="ILidPnCache"/> and <see cref="IProtocolStore"/>.
    /// </summary>
    public async Task<UsernameLookupResult> LookupUsernameAsync(
        string username,
        string? usernameKeyPin = null,
        CancellationToken ct = default)
    {
        string iqId = _iqClient.NextRequestId();
        string sid = NextSid();
        Node iq = UsyncProtocol.BuildUsernameLookupQuery(iqId, sid, username, usernameKeyPin);
        Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        UsernameLookupResult result = UsyncProtocol.ParseUsernameLookupResponse(response);

        if (result.Status == UsernameLookupStatus.Found
            && result.LidJid is Jid lid
            && result.PnJid is Jid pn)
        {
            await LearnMappingAsync(pn.ToNonAdString(), lid.ToNonAdString(), LidPnLearningSource.UsernameLookup, ct)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Synchronizes device lists for <paramref name="userJids"/>, authenticating companion device lists
    /// via <see cref="PairingCrypto.VerifyKeyIndexList"/> against stored primary identities (or falling back
    /// to primary device <c>0</c> when unvalidated companions appear, matching WA Web <c>handleOmittedResult</c>).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ResolvedDeviceList>> GetDeviceListsAsync(
        IReadOnlyList<Jid> userJids,
        UsyncContext context = UsyncContext.Message,
        bool allowCachedOnly = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userJids);
        if (userJids.Count == 0)
        {
            return new Dictionary<string, ResolvedDeviceList>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, ResolvedDeviceList>(StringComparer.OrdinalIgnoreCase);
        var toFetch = new List<UsyncUserSpec>();

        foreach (Jid raw in userJids)
        {
            Jid bare = raw.ToNonAd();
            string bareStr = bare.ToString();
            IReadOnlyList<uint> cached = await _backend.Protocol.GetDeviceListAsync(bareStr, ct).ConfigureAwait(false);
            if (allowCachedOnly && cached.Count > 0)
            {
                result[bareStr] = new ResolvedDeviceList(bare, cached, AdvVerified: true);
                continue;
            }

            Jid? knownLid = bare.IsPn() && _lidPnCache.TryGetLid(bareStr, out string? lidStr) && !string.IsNullOrEmpty(lidStr) && Jid.TryParse(lidStr, out Jid parsedLid)
                ? parsedLid
                : null;
            toFetch.Add(new UsyncUserSpec { Jid = bare, KnownLid = knownLid });
        }

        if (toFetch.Count == 0)
        {
            return result;
        }

        string iqId = _iqClient.NextRequestId();
        Node iq = UsyncProtocol.BuildQuery(
            iqId,
            NextSid(),
            UsyncMode.Query,
            context,
            UsyncProtocolSet.Devices | UsyncProtocolSet.Lid,
            toFetch);

        Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        UsyncResponse parsed = UsyncProtocol.ParseResponse(response);

        foreach (UsyncUserResult user in parsed.Users)
        {
            if (user.Jid is not Jid uJid)
            {
                continue;
            }

            Jid bare = uJid.ToNonAd();
            string bareStr = bare.ToString();

            if (user.Lid is Jid lidVal && bare.IsPn())
            {
                await LearnMappingAsync(bareStr, lidVal.ToNonAdString(), LidPnLearningSource.Usync, ct)
                    .ConfigureAwait(false);
            }
            else if (user.PnJid is Jid pnVal && bare.IsLid())
            {
                await LearnMappingAsync(pnVal.ToNonAdString(), bareStr, LidPnLearningSource.Usync, ct)
                    .ConfigureAwait(false);
            }

            (IReadOnlyList<uint> filteredIds, bool verified) = await ValidateAndFilterDevicesAsync(bare, user, ct)
                .ConfigureAwait(false);

            await _backend.Protocol.SaveDeviceListAsync(bareStr, filteredIds, ct).ConfigureAwait(false);
            result[bareStr] = new ResolvedDeviceList(bare, filteredIds, verified, user.DeviceListHash);
        }

        return result;
    }

    public async Task<IReadOnlyList<UsyncUserResult>> LookupContactsAsync(
        IReadOnlyList<string> phoneNumbers,
        UsyncContext context = UsyncContext.Interactive,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(phoneNumbers);
        if (phoneNumbers.Count == 0)
        {
            return Array.Empty<UsyncUserResult>();
        }

        var users = phoneNumbers.Select(p => new UsyncUserSpec { Phone = p }).ToArray();
        string iqId = _iqClient.NextRequestId();
        Node iq = UsyncProtocol.BuildQuery(
            iqId,
            NextSid(),
            UsyncMode.Query,
            context,
            UsyncProtocolSet.Contact | UsyncProtocolSet.Lid | UsyncProtocolSet.Business,
            users);

        Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        UsyncResponse parsed = UsyncProtocol.ParseResponse(response);

        foreach (UsyncUserResult user in parsed.Users)
        {
            if (user.Jid is Jid pn && user.Lid is Jid lid)
            {
                await LearnMappingAsync(pn.ToNonAdString(), lid.ToNonAdString(), LidPnLearningSource.Usync, ct)
                    .ConfigureAwait(false);
            }
        }

        return parsed.Users;
    }

    public async Task<IReadOnlyDictionary<string, UsyncUserResult>> GetUserInfoAsync(
        IReadOnlyList<Jid> jids,
        UsyncProtocolSet protocols = UsyncProtocolSet.Business | UsyncProtocolSet.Status | UsyncProtocolSet.Picture | UsyncProtocolSet.Devices | UsyncProtocolSet.Lid | UsyncProtocolSet.Username,
        UsyncContext context = UsyncContext.Background,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jids);
        var users = new List<UsyncUserSpec>(jids.Count);
        foreach (Jid raw in jids)
        {
            Jid bare = raw.ToNonAd();
            byte[]? tcToken = await _backend.Protocol.GetTcTokenAsync(bare.ToString(), ct).ConfigureAwait(false);
            users.Add(new UsyncUserSpec { Jid = bare, TcToken = tcToken });
        }

        string iqId = _iqClient.NextRequestId();
        Node iq = UsyncProtocol.BuildQuery(
            iqId,
            NextSid(),
            UsyncMode.Full,
            context,
            protocols,
            users);

        Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);
        UsyncResponse parsed = UsyncProtocol.ParseResponse(response);

        var dict = new Dictionary<string, UsyncUserResult>(StringComparer.OrdinalIgnoreCase);
        foreach (UsyncUserResult user in parsed.Users)
        {
            if (user.Jid is not Jid uJid)
            {
                continue;
            }

            string bareStr = uJid.ToNonAdString();
            dict[bareStr] = user;

            if (uJid.IsPn() && user.Lid is Jid lid)
            {
                await LearnMappingAsync(bareStr, lid.ToNonAdString(), LidPnLearningSource.Usync, ct).ConfigureAwait(false);
            }
            else if (uJid.IsLid() && user.PnJid is Jid pn)
            {
                await LearnMappingAsync(pn.ToNonAdString(), bareStr, LidPnLearningSource.Usync, ct).ConfigureAwait(false);
            }
        }

        return dict;
    }

    private async Task<(IReadOnlyList<uint> DeviceIds, bool Verified)> ValidateAndFilterDevicesAsync(
        Jid bareUser,
        UsyncUserResult user,
        CancellationToken ct)
    {
        if (user.Devices.Count == 0)
        {
            return ([0u], true);
        }

        bool hasCompanions = user.Devices.Any(d => d.Id != 0);
        if (!hasCompanions)
        {
            return (user.Devices.Select(d => d.Id).Distinct().ToArray(), true);
        }

        if (user.SignedKeyIndexBytes is not { Length: > 0 } signedBytes)
        {
            // Companions returned without ADV key-index signature -> fall back to primary device 0 only.
            return ([0u], false);
        }

        bool hasHosted = user.Devices.Any(d => d.IsHosted);
        PairingCrypto.DecodedKeyIndexList? decoded = null;

        if (hasHosted)
        {
            decoded = PairingCrypto.VerifyHostedKeyIndexList(signedBytes, out byte[]? hostedAccountPub);
            if (decoded is not null && hostedAccountPub is { Length: 32 })
            {
                await _backend.Signal.SaveIdentityAsync(bareUser.ToProtocolAddress(), hostedAccountPub, ct).ConfigureAwait(false);
            }
        }
        else
        {
            byte[]? trustedPub = await LoadPrimaryIdentityAsync(bareUser, ct).ConfigureAwait(false);
            if (trustedPub is { Length: >= 32 })
            {
                byte[] normalized32 = trustedPub.Length == 33 && trustedPub[0] == 0x05 ? trustedPub[1..] : trustedPub[..32];
                decoded = PairingCrypto.VerifyKeyIndexList(signedBytes, normalized32);
                if (decoded is null)
                {
                    return ([0u], false);
                }
            }
            else
            {
                decoded = PairingCrypto.DecodeKeyIndexList(signedBytes);
            }
        }

        if (decoded is null)
        {
            return ([0u], false);
        }

        var filtered = new List<uint> { 0u };
        foreach (UsyncDeviceInfo dev in user.Devices)
        {
            if (dev.Id == 0)
            {
                continue;
            }

            if (dev.KeyIndex is uint ki && (decoded.ValidIndexes.Contains(ki) || ki > decoded.CurrentIndex))
            {
                filtered.Add(dev.Id);
            }
        }

        return (filtered.Distinct().ToArray(), true);
    }

    private async Task<byte[]?> LoadPrimaryIdentityAsync(Jid bareUser, CancellationToken ct)
    {
        byte[]? id = await _backend.Signal.GetIdentityAsync(bareUser.ToProtocolAddress(), ct).ConfigureAwait(false);
        if (id is not null)
        {
            return id;
        }

        string bareStr = bareUser.ToString();
        if (bareUser.IsPn() && _lidPnCache.TryGetLid(bareStr, out string? lidStr) && !string.IsNullOrEmpty(lidStr) && Jid.TryParse(lidStr, out Jid lidJid))
        {
            return await _backend.Signal.GetIdentityAsync(lidJid.ToProtocolAddress(), ct).ConfigureAwait(false);
        }
        if (bareUser.IsLid() && _lidPnCache.TryGetPn(bareStr, out string? pnStr) && !string.IsNullOrEmpty(pnStr) && Jid.TryParse(pnStr, out Jid pnJid))
        {
            return await _backend.Signal.GetIdentityAsync(pnJid.ToProtocolAddress(), ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task LearnMappingAsync(string pnJid, string lidJid, LidPnLearningSource source, CancellationToken ct)
    {
        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        _lidPnCache.Learn(pnJid, lidJid, source, now);
        await _backend.Protocol.UpsertLidPnMappingsAsync([new LidPnMappingRecord(pnJid, lidJid)], ct).ConfigureAwait(false);
    }
}
