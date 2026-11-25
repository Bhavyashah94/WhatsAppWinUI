using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.HistorySync;
using WhatsApp.Protocol.Signal;

namespace WhatsApp.Api;

public sealed record SessionEnsureResult(
    IReadOnlyDictionary<Jid, SignalSession> Sessions,
    IReadOnlyList<RejectedPreKeyDevice> RejectedDevices);

/// <summary>
/// Batch pre-key fetcher &amp; X3DH session initiator matching live WA Web
/// <c>WAWebEstablishE2EESessionsJob</c> and <c>whatsapp-rust/src/session.rs</c>.
/// </summary>
public sealed class PreKeyFetchClient
{
    public const int MaxDevicesPerBatch = 64;

    private readonly IqClient _iqClient;
    private readonly IWhatsAppBackend _backend;
    private readonly ILidPnCache _lidPnCache;
    private readonly DeviceCredentials _credentials;

    public PreKeyFetchClient(
        IqClient iqClient,
        IWhatsAppBackend backend,
        ILidPnCache lidPnCache,
        DeviceCredentials credentials)
    {
        _iqClient = iqClient ?? throw new ArgumentNullException(nameof(iqClient));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _lidPnCache = lidPnCache ?? throw new ArgumentNullException(nameof(lidPnCache));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    /// <summary>
    /// Ensures Signal sessions exist for every device in <paramref name="deviceJids"/>, fetching
    /// missing bundles in batched <c>encrypt</c> IQs, validating ADV companion identities,
    /// initiating X3DH sessions, and evicting cached device lists on 406 unregistered-device errors.
    /// </summary>
    public async Task<SessionEnsureResult> EnsureSessionsAsync(
        IReadOnlyList<Jid> deviceJids,
        string? reason = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceJids);

        var sessions = new Dictionary<Jid, SignalSession>();
        var missing = new List<Jid>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Jid raw in deviceJids)
        {
            string addr = raw.ToProtocolAddress();
            if (!seen.Add(addr))
            {
                continue;
            }

            SignalSession? existing = await _backend.Signal.GetSessionAsync(addr, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                sessions[raw] = existing;
            }
            else
            {
                missing.Add(raw);
            }
        }

        var rejectedAll = new List<RejectedPreKeyDevice>();
        if (missing.Count == 0)
        {
            return new SessionEnsureResult(sessions, rejectedAll);
        }

        for (int offset = 0; offset < missing.Count; offset += MaxDevicesPerBatch)
        {
            int count = Math.Min(MaxDevicesPerBatch, missing.Count - offset);
            Jid[] batch = missing.GetRange(offset, count).ToArray();

            var accountIdentities = await LoadExpectedAccountIdentitiesAsync(batch, ct).ConfigureAwait(false);
            string iqId = _iqClient.NextRequestId();
            Node iq = PreKeyIq.BuildFetchPreKeys(iqId, batch, reason);
            Node response = await _iqClient.SendIqAsync(iq, IqClient.DefaultTimeout, ct).ConfigureAwait(false);

            PreKeyFetchOutcome outcome = PreKeyIq.ParsePreKeysResponse(response, accountIdentities);

            foreach (var (devJid, bundle) in outcome.Bundles)
            {
                SignalSession established = await EstablishFromBundleAsync(devJid, bundle, ct).ConfigureAwait(false);
                sessions[devJid] = established;
            }

            if (outcome.RejectedDevices.Count > 0)
            {
                var invalidatedUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (RejectedPreKeyDevice rej in outcome.RejectedDevices)
                {
                    rejectedAll.Add(rej);
                    if (rej.Code == 406)
                    {
                        string userBare = rej.DeviceJid.ToNonAdString();
                        if (invalidatedUsers.Add(userBare))
                        {
                            await _backend.Protocol.DeleteDeviceListAsync(userBare, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        return new SessionEnsureResult(sessions, rejectedAll);
    }

    public async Task<SignalSession> EstablishFromBundleAsync(
        Jid deviceJid,
        PreKeyBundle bundle,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        SignalSession established = SignalCipher.InitiateSession(
            bundle,
            _credentials.IdentityKey,
            _credentials.RegistrationId);

        string addr = deviceJid.ToProtocolAddress();
        await _backend.Signal.SaveIdentityAsync(addr, bundle.IdentityKey, ct).ConfigureAwait(false);
        await _backend.Signal.SaveSessionAsync(addr, established, ct).ConfigureAwait(false);
        return established;
    }

    private async Task<Dictionary<string, byte[]>> LoadExpectedAccountIdentitiesAsync(
        IReadOnlyList<Jid> deviceJids,
        CancellationToken ct)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (Jid dev in deviceJids)
        {
            Jid bare = dev.ToNonAd();
            string bareStr = bare.ToString();
            if (map.ContainsKey(bareStr))
            {
                continue;
            }

            byte[]? id = await _backend.Signal.GetIdentityAsync(bare.ToProtocolAddress(), ct).ConfigureAwait(false);
            if (id is null && bare.IsPn() && _lidPnCache.TryGetLid(bareStr, out string? lidStr) && !string.IsNullOrEmpty(lidStr) && Jid.TryParse(lidStr, out Jid lidJid))
            {
                id = await _backend.Signal.GetIdentityAsync(lidJid.ToProtocolAddress(), ct).ConfigureAwait(false);
            }
            else if (id is null && bare.IsLid() && _lidPnCache.TryGetPn(bareStr, out string? pnStr) && !string.IsNullOrEmpty(pnStr) && Jid.TryParse(pnStr, out Jid pnJid))
            {
                id = await _backend.Signal.GetIdentityAsync(pnJid.ToProtocolAddress(), ct).ConfigureAwait(false);
            }

            if (id is { Length: >= 32 })
            {
                byte[] norm32 = id.Length == 33 && id[0] == 0x05 ? id[1..] : id[..32];
                map[bareStr] = norm32;
            }
        }

        return map;
    }
}
