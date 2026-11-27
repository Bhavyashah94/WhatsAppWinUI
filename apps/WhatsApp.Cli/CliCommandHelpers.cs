using System.Globalization;
using System.Security.Cryptography;
using WhatsApp.Api;
using WhatsApp.Protocol;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;
using WhatsApp.Protocol.Crypto;
using WhatsApp.Protocol.HistorySync;
using Whatsapp;

namespace WhatsApp.Cli;

internal sealed record CliCommandContext(CliRuntime Runtime, TextWriter Out, TextWriter Err);

internal static class CliCommandHelpers
{
    public static Jid ParseTargetJid(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        string trimmed = raw.Trim();
        if (Jid.TryParse(trimmed, out Jid parsed))
        {
            return parsed.ToNonAd();
        }

        string digits = new(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length >= 5 && digits.Length <= 20)
        {
            return Jid.Pn(digits);
        }

        throw new ArgumentException($"Invalid JID or phone number '{raw}'.");
    }

    public static bool TryParseNonNegativeLong(string raw, out long value) =>
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static long ParseTimestampSeconds(string raw)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unix))
        {
            return unix;
        }
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
        {
            return dto.ToUnixTimeSeconds();
        }
        throw new ArgumentException($"Invalid timestamp '{raw}'. Expected Unix seconds or ISO-8601 date/time.");
    }

    public static async Task<ConnectionOrchestrator> EnsureOrchestratorAsync(
        CliCommandContext ctx,
        CancellationToken ct)
    {
        if (ctx.Runtime.Orchestrator is { } active && ctx.Runtime.IsConnected)
        {
            return active;
        }

        if (!string.IsNullOrWhiteSpace(ctx.Runtime.Options.SingleCommand))
        {
            DeviceCredentials? creds = await ctx.Runtime.Backend.Device.LoadAsync(ct).ConfigureAwait(false);
            if (creds?.IsRegistered == true)
            {
                Task<ConnectedEvent> connTask = ctx.Runtime.WaitForEventAsync<ConnectedEvent>(
                    timeout: TimeSpan.FromSeconds(30), ct: ct);
                await ctx.Runtime.EnsureStartedAsync(ct).ConfigureAwait(false);
                await connTask.ConfigureAwait(false);
                if (ctx.Runtime.Orchestrator is { } connected)
                {
                    return connected;
                }
            }
        }

        return ctx.Runtime.Orchestrator
            ?? throw new InvalidOperationException("Not connected. Run 'connect' first.");
    }

    public static async Task<IReadOnlyList<Jid>?> ResolveGroupParticipantsIfNeededAsync(
        CliCommandContext ctx,
        Jid targetJid,
        CancellationToken ct)
    {
        if (!targetJid.IsGroup())
        {
            return null;
        }

        ConnectionOrchestrator orch = await EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        GroupClient groups = orch.ActiveGroups
            ?? throw new InvalidOperationException("GroupClient is not active.");
        if (groups.TryGetCached(targetJid, out GroupMetadata? cached) && cached is { Participants.Count: > 0 })
        {
            return cached.Participants.Select(p => p.Jid).ToArray();
        }

        GroupMetadata? fetched = await groups.QueryMetadataAsync(targetJid, bypassCache: false, ct).ConfigureAwait(false);
        if (fetched is null || fetched.Participants.Count == 0)
        {
            throw new InvalidOperationException($"Unable to resolve participants for group '{targetJid}'.");
        }
        return fetched.Participants.Select(p => p.Jid).ToArray();
    }

    public static async Task<(MessageKey Key, MessageRecord? Record)> ResolveMessageKeyAsync(
        CliCommandContext ctx,
        Jid chatJid,
        string messageId,
        CancellationToken ct,
        bool? fromMeOverride = null)
    {
        string bareChat = chatJid.ToNonAdString();
        MessageRecord? stored = await ctx.Runtime.Backend.History
            .GetMessageByIdAsync(messageId, bareChat, ct)
            .ConfigureAwait(false);

        bool fromMe = fromMeOverride ?? stored?.FromMe ?? false;
        var key = new MessageKey
        {
            RemoteJid = stored?.RemoteJid ?? bareChat,
            Id = messageId,
            FromMe = fromMe
        };

        if (chatJid.IsGroup() && !fromMe && !string.IsNullOrEmpty(stored?.SenderJid))
        {
            key.Participant = stored.SenderJid;
        }

        return (key, stored);
    }

    public static string InferMimeType(string filePath, WaMediaType mediaType)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".ogg" or ".opus" => "audio/ogg; codecs=opus",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".json" => "application/json",
            _ => mediaType switch
            {
                WaMediaType.Image => "image/jpeg",
                WaMediaType.Sticker => "image/webp",
                WaMediaType.Video or WaMediaType.Gif => "video/mp4",
                WaMediaType.Audio or WaMediaType.Ptt => "audio/ogg; codecs=opus",
                _ => "application/octet-stream"
            }
        };
    }

    public static string FormatTimestamp(ulong unixSeconds)
    {
        if (unixSeconds == 0) return "-";
        DateTimeOffset dto = DateTimeOffset.FromUnixTimeSeconds(unchecked((long)unixSeconds)).ToLocalTime();
        return dto.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Truncate(string? value, int maxLen = 72)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maxLen ? singleLine : singleLine[..maxLen] + "...";
    }

    public static string Sha256HexPrefix(ReadOnlySpan<byte> data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    public static (List<string> Positional, Dictionary<string, List<string>> Options, HashSet<string> Flags) ParseOptions(
        IReadOnlyList<string> args,
        int startIndex,
        IReadOnlySet<string> valueOptions,
        IReadOnlySet<string> flagOptions)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = startIndex; i < args.Count; i++)
        {
            string token = args[i];
            if (flagOptions.Contains(token))
            {
                flags.Add(token);
            }
            else if (valueOptions.Contains(token))
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException($"Missing value after option '{token}'.");
                }
                if (!options.TryGetValue(token, out List<string>? list))
                {
                    list = [];
                    options[token] = list;
                }
                list.Add(args[++i]);
            }
            else
            {
                positional.Add(token);
            }
        }

        return (positional, options, flags);
    }
}
