using Microsoft.Data.Sqlite;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Noise;

namespace WhatsApp.Storage.Sqlite;

internal sealed class SqliteDeviceStore : IDeviceStore
{
    private readonly SqliteRunner _runner;
    private readonly int _deviceId;

    public SqliteDeviceStore(SqliteRunner runner, int deviceId)
    {
        _runner = runner;
        _deviceId = deviceId;
    }

    public ValueTask<DeviceCredentials?> LoadAsync(CancellationToken ct = default) =>
        new(_runner.RunReadAsync(ReadDeviceAsync, ct));

    public ValueTask<DeviceCredentials> LoadOrCreateAsync(CancellationToken ct = default) =>
        new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            var existing = await ReadDeviceAsync(conn, token).ConfigureAwait(false);
            if (existing != null)
            {
                return existing;
            }

            var created = DeviceCredentials.CreateNew();
            await UpsertDeviceAsync(conn, created, token).ConfigureAwait(false);
            return created;
        }, ct));

    public ValueTask SaveAsync(DeviceCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return new(_runner.RunWriteTransactionAsync(
            (conn, token) => UpsertDeviceAsync(conn, credentials, token),
            ct));
    }

    public ValueTask ClearPairingAsync(CancellationToken ct = default) =>
        new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using (var updateDevice = conn.CreateCommand())
            {
                updateDevice.CommandText = """
                    UPDATE device
                    SET lid = NULL,
                        pn = NULL,
                        lid_migrated = 0,
                        server_static_public = NULL,
                        edge_routing_info = NULL,
                        next_pre_key_id = 1,
                        updated_at = $updatedAt
                    WHERE id = $id;
                    """;
                updateDevice.Parameters.AddWithValue("$id", _deviceId);
                updateDevice.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await updateDevice.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            foreach (string table in new[]
            {
                "signal_identities",
                "signal_sessions",
                "prekeys",
                "signed_prekeys",
                "sender_keys",
                "app_state_keys",
                "app_state_versions",
                "app_state_mutation_macs",
                "lid_pn_mapping",
                "device_list_cache",
                "tc_tokens",
                "msg_secrets"
            })
            {
                using var clearCmd = conn.CreateCommand();
                clearCmd.CommandText = $"DELETE FROM {table} WHERE device_id = $deviceId;";
                clearCmd.Parameters.AddWithValue("$deviceId", _deviceId);
                await clearCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, ct));

    private async Task<DeviceCredentials?> ReadDeviceAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT lid,
                   pn,
                   lid_migrated,
                   registration_id,
                   noise_key,
                   identity_key,
                   signed_pre_key,
                   signed_pre_key_id,
                   signed_pre_key_signature,
                   adv_secret_key,
                   push_name,
                   server_static_public,
                   edge_routing_info,
                   next_pre_key_id
            FROM device
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", _deviceId);

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        string? lidStr = reader.IsDBNull(0) ? null : reader.GetString(0);
        string? pnStr = reader.IsDBNull(1) ? null : reader.GetString(1);

        return new DeviceCredentials
        {
            Lid = string.IsNullOrEmpty(lidStr) ? null : Jid.Parse(lidStr),
            Pn = string.IsNullOrEmpty(pnStr) ? null : Jid.Parse(pnStr),
            LidMigrated = reader.GetInt64(2) != 0,
            RegistrationId = unchecked((uint)reader.GetInt64(3)),
            NoiseKey = DeserializeKeyPair((byte[])reader.GetValue(4)),
            IdentityKey = DeserializeKeyPair((byte[])reader.GetValue(5)),
            SignedPreKey = DeserializeKeyPair((byte[])reader.GetValue(6)),
            SignedPreKeyId = unchecked((uint)reader.GetInt64(7)),
            SignedPreKeySignature = (byte[])reader.GetValue(8),
            AdvSecretKey = (byte[])reader.GetValue(9),
            PushName = reader.GetString(10),
            ServerStaticPublic = reader.IsDBNull(11) ? null : (byte[])reader.GetValue(11),
            EdgeRoutingInfo = reader.IsDBNull(12) ? null : (byte[])reader.GetValue(12),
            NextPreKeyId = unchecked((uint)Math.Max(1, reader.GetInt64(13)))
        };
    }

    private async Task UpsertDeviceAsync(SqliteConnection conn, DeviceCredentials credentials, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO device (
                id,
                lid,
                pn,
                lid_migrated,
                registration_id,
                noise_key,
                identity_key,
                signed_pre_key,
                signed_pre_key_id,
                signed_pre_key_signature,
                adv_secret_key,
                push_name,
                server_static_public,
                edge_routing_info,
                next_pre_key_id,
                updated_at
            )
            VALUES (
                $id,
                $lid,
                $pn,
                $lidMigrated,
                $registrationId,
                $noiseKey,
                $identityKey,
                $signedPreKey,
                $signedPreKeyId,
                $signedPreKeySignature,
                $advSecretKey,
                $pushName,
                $serverStaticPublic,
                $edgeRoutingInfo,
                $nextPreKeyId,
                $updatedAt
            )
            ON CONFLICT(id) DO UPDATE SET
                lid = excluded.lid,
                pn = excluded.pn,
                lid_migrated = excluded.lid_migrated,
                registration_id = excluded.registration_id,
                noise_key = excluded.noise_key,
                identity_key = excluded.identity_key,
                signed_pre_key = excluded.signed_pre_key,
                signed_pre_key_id = excluded.signed_pre_key_id,
                signed_pre_key_signature = excluded.signed_pre_key_signature,
                adv_secret_key = excluded.adv_secret_key,
                push_name = excluded.push_name,
                server_static_public = excluded.server_static_public,
                edge_routing_info = excluded.edge_routing_info,
                next_pre_key_id = excluded.next_pre_key_id,
                updated_at = excluded.updated_at;
            """;

        cmd.Parameters.AddWithValue("$id", _deviceId);
        cmd.Parameters.AddWithValue("$lid", (object?)credentials.Lid?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pn", (object?)credentials.Pn?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lidMigrated", credentials.LidMigrated ? 1 : 0);
        cmd.Parameters.AddWithValue("$registrationId", unchecked((long)credentials.RegistrationId));
        cmd.Parameters.AddWithValue("$noiseKey", SerializeKeyPair(credentials.NoiseKey));
        cmd.Parameters.AddWithValue("$identityKey", SerializeKeyPair(credentials.IdentityKey));
        cmd.Parameters.AddWithValue("$signedPreKey", SerializeKeyPair(credentials.SignedPreKey));
        cmd.Parameters.AddWithValue("$signedPreKeyId", unchecked((long)credentials.SignedPreKeyId));
        cmd.Parameters.AddWithValue("$signedPreKeySignature", credentials.SignedPreKeySignature);
        cmd.Parameters.AddWithValue("$advSecretKey", credentials.AdvSecretKey);
        cmd.Parameters.AddWithValue("$pushName", credentials.PushName ?? string.Empty);
        cmd.Parameters.AddWithValue("$serverStaticPublic", (object?)credentials.ServerStaticPublic ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$edgeRoutingInfo", (object?)credentials.EdgeRoutingInfo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nextPreKeyId", unchecked((long)Math.Max(1U, credentials.NextPreKeyId)));
        cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static byte[] SerializeKeyPair(KeyPair keyPair)
    {
        if (keyPair.PrivateKey is not { Length: 32 } || keyPair.PublicKey is not { Length: 32 })
        {
            throw new ArgumentException("Curve25519 KeyPair requires 32-byte PrivateKey and 32-byte PublicKey.", nameof(keyPair));
        }

        byte[] packed = new byte[64];
        Buffer.BlockCopy(keyPair.PrivateKey, 0, packed, 0, 32);
        Buffer.BlockCopy(keyPair.PublicKey, 0, packed, 32, 32);
        return packed;
    }

    internal static KeyPair DeserializeKeyPair(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 64)
        {
            throw new InvalidDataException($"Invalid serialized KeyPair length: {bytes.Length} (expected 64).");
        }

        return new KeyPair(bytes[..32].ToArray(), bytes[32..64].ToArray());
    }
}
