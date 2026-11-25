using Microsoft.Data.Sqlite;
using WhatsApp.Protocol.Abstractions;

namespace WhatsApp.Storage.Sqlite;

internal sealed class SqliteMsgSecretStore : IMsgSecretStore
{
    private readonly SqliteRunner _runner;
    private readonly int _deviceId;

    public SqliteMsgSecretStore(SqliteRunner runner, int deviceId)
    {
        _runner = runner;
        _deviceId = deviceId;
    }

    public ValueTask PutMsgSecretsAsync(
        IReadOnlyList<(string MessageId, byte[] Secret, long? Timestamp)> entries,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        return new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO msg_secrets (message_id, device_id, secret, msg_timestamp, expires_at)
                VALUES ($messageId, $deviceId, $secret, $msgTimestamp, $expiresAt)
                ON CONFLICT(message_id, device_id) DO UPDATE SET
                    secret = excluded.secret,
                    msg_timestamp = excluded.msg_timestamp,
                    expires_at = excluded.expires_at;
                """;
            var idParam = cmd.Parameters.Add("$messageId", SqliteType.Text);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            var secretParam = cmd.Parameters.Add("$secret", SqliteType.Blob);
            var tsParam = cmd.Parameters.Add("$msgTimestamp", SqliteType.Integer);
            var expParam = cmd.Parameters.Add("$expiresAt", SqliteType.Integer);

            foreach (var (messageId, secret, timestamp) in entries)
            {
                if (string.IsNullOrEmpty(messageId))
                {
                    throw new ArgumentException("MessageId cannot be null or empty.", nameof(entries));
                }

                idParam.Value = messageId;
                secretParam.Value = secret ?? throw new ArgumentException($"Secret cannot be null for {messageId}.", nameof(entries));
                object tsVal = timestamp.HasValue ? timestamp.Value : DBNull.Value;
                tsParam.Value = tsVal;
                expParam.Value = tsVal;
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, ct));
    }

    public ValueTask<(byte[] Secret, long? Timestamp)?> GetMsgSecretAsync(string messageId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        return new(_runner.RunReadAsync<(byte[] Secret, long? Timestamp)?>(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT secret, msg_timestamp
                FROM msg_secrets
                WHERE message_id = $messageId AND device_id = $deviceId;
                """;
            cmd.Parameters.AddWithValue("$messageId", messageId);
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);

            using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            byte[] secret = (byte[])reader.GetValue(0);
            long? ts = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            return (secret, ts);
        }, ct));
    }

    public ValueTask DeleteExpiredMsgSecretsAsync(long cutoffTimestamp, CancellationToken ct = default) =>
        new(_runner.RunWriteTransactionAsync(async (conn, token) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM msg_secrets
                WHERE device_id = $deviceId
                  AND expires_at IS NOT NULL
                  AND expires_at <= $cutoff;
                """;
            cmd.Parameters.AddWithValue("$deviceId", _deviceId);
            cmd.Parameters.AddWithValue("$cutoff", cutoffTimestamp);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct));
}
