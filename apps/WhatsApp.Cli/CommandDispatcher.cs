using WhatsApp.Api;
using WhatsApp.Protocol.Abstractions;
using WhatsApp.Protocol.Binary;

namespace WhatsApp.Cli;

public sealed record CommandResult(int ExitCode, bool ShouldExit = false);

/// <summary>
/// Dual-mode REPL and one-shot command dispatcher for <see cref="CliRuntime"/>.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly CliRuntime _runtime;
    private readonly TextWriter _out;
    private readonly TextWriter _err;

    public CommandDispatcher(CliRuntime runtime, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _out = stdout ?? Console.Out;
        _err = stderr ?? Console.Error;
    }

    public async Task<CommandResult> ExecuteAsync(string? commandLine, CancellationToken ct = default)
    {
        IReadOnlyList<string> args;
        try
        {
            string cleaned = (commandLine ?? string.Empty).Trim().TrimStart('\uFEFF').Trim();
            args = CommandTokenizer.Tokenize(cleaned);
        }
        catch (FormatException ex)
        {
            await _err.WriteLineAsync($"Syntax error: {ex.Message}").ConfigureAwait(false);
            return new CommandResult(1);
        }

        if (args.Count == 0)
        {
            return new CommandResult(0);
        }

        string cmd = args[0].ToLowerInvariant();
        var ctx = new CliCommandContext(_runtime, _out, _err);
        try
        {
            return cmd switch
            {
                "help" or "?" => await PrintHelpAsync().ConfigureAwait(false),
                "status" => await HandleStatusAsync(ct).ConfigureAwait(false),
                "pair" => await HandlePairAsync(args, ct).ConfigureAwait(false),
                "connect" => await HandleConnectAsync(args, ct).ConfigureAwait(false),
                "disconnect" => await HandleDisconnectAsync().ConfigureAwait(false),
                "reconnect" => await HandleReconnectAsync(args, ct).ConfigureAwait(false),
                "wait-sync" => await HandleWaitSyncAsync(args, ct).ConfigureAwait(false),
                "sleep" or "wait" => await HandleSleepAsync(args, ct).ConfigureAwait(false),
                "events" => await HandleEventsAsync(args).ConfigureAwait(false),
                "reset-pairing" => await HandleResetPairingAsync(args, ct).ConfigureAwait(false),
                "unlink" => await HandleUnlinkAsync(args, ct).ConfigureAwait(false),
                "chats" => await HistoryAndMediaHandlers.HandleChatsAsync(ctx, args, ct).ConfigureAwait(false),
                "history" => await HistoryAndMediaHandlers.HandleHistoryAsync(ctx, args, ct).ConfigureAwait(false),
                "search" => await HistoryAndMediaHandlers.HandleSearchAsync(ctx, args, ct).ConfigureAwait(false),
                "pdo" => await HistoryAndMediaHandlers.HandlePdoAsync(ctx, args, ct).ConfigureAwait(false),
                "media" => await HistoryAndMediaHandlers.HandleMediaAsync(ctx, args, ct).ConfigureAwait(false),
                "send" => await MessagingAndPresenceHandlers.HandleSendAsync(ctx, args, ct).ConfigureAwait(false),
                "reply" => await MessagingAndPresenceHandlers.HandleReplyAsync(ctx, args, ct).ConfigureAwait(false),
                "edit" => await MessagingAndPresenceHandlers.HandleEditAsync(ctx, args, ct).ConfigureAwait(false),
                "revoke" => await MessagingAndPresenceHandlers.HandleRevokeAsync(ctx, args, ct).ConfigureAwait(false),
                "react" => await MessagingAndPresenceHandlers.HandleReactAsync(ctx, args, ct).ConfigureAwait(false),
                "pin" => await MessagingAndPresenceHandlers.HandlePinOrKeepAsync(ctx, args, isPin: true, ct).ConfigureAwait(false),
                "keep" => await MessagingAndPresenceHandlers.HandlePinOrKeepAsync(ctx, args, isPin: false, ct).ConfigureAwait(false),
                "poll" => await MessagingAndPresenceHandlers.HandlePollAsync(ctx, args, ct).ConfigureAwait(false),
                "event" => await MessagingAndPresenceHandlers.HandleEventAsync(ctx, args, ct).ConfigureAwait(false),
                "comment" => await MessagingAndPresenceHandlers.HandleCommentAsync(ctx, args, ct).ConfigureAwait(false),
                "presence" => await MessagingAndPresenceHandlers.HandlePresenceAsync(ctx, args, ct).ConfigureAwait(false),
                "typing" => await MessagingAndPresenceHandlers.HandleTypingAsync(ctx, args, ct).ConfigureAwait(false),
                "read" => await MessagingAndPresenceHandlers.HandleReadAsync(ctx, args, ct).ConfigureAwait(false),
                "usync" => await GroupAndContactHandlers.HandleUsyncAsync(ctx, args, ct).ConfigureAwait(false),
                "contacts" => await GroupAndContactHandlers.HandleContactsAsync(ctx, args, ct).ConfigureAwait(false),
                "avatar" => await GroupAndContactHandlers.HandleAvatarAsync(ctx, args, ct).ConfigureAwait(false),
                "group" => await GroupAndContactHandlers.HandleGroupAsync(ctx, args, ct).ConfigureAwait(false),
                "chat" => await GroupAndContactHandlers.HandleChatActionAsync(ctx, args, ct).ConfigureAwait(false),
                "syncd" => await GroupAndContactHandlers.HandleSyncdAsync(ctx, args, ct).ConfigureAwait(false),
                "bot" => await MessagingAndPresenceHandlers.HandleBotAsync(ctx, args, ct).ConfigureAwait(false),
                "interact" => await MessagingAndPresenceHandlers.HandleInteractAsync(ctx, args, ct).ConfigureAwait(false),
                "privacy" => await AccountAndSyncdHandlers.HandlePrivacyAsync(ctx, args, ct).ConfigureAwait(false),
                "profile" => await AccountAndSyncdHandlers.HandleProfileAsync(ctx, args, ct).ConfigureAwait(false),
                "block" => await AccountAndSyncdHandlers.HandleBlockAsync(ctx, args, ct).ConfigureAwait(false),
                "label" => await AccountAndSyncdHandlers.HandleLabelAsync(ctx, args, ct).ConfigureAwait(false),
                "quickreply" => await AccountAndSyncdHandlers.HandleQuickReplyAsync(ctx, args, ct).ConfigureAwait(false),
                "safety" => await AccountAndSyncdHandlers.HandleSafetyAsync(ctx, args, ct).ConfigureAwait(false),
                "draft" => await AccountAndSyncdHandlers.HandleDraftAsync(ctx, args, ct).ConfigureAwait(false),
                "community" => await SocialAndBusinessHandlers.HandleCommunityAsync(ctx, args, ct).ConfigureAwait(false),
                "newsletter" => await SocialAndBusinessHandlers.HandleNewsletterAsync(ctx, args, ct).ConfigureAwait(false),
                "status-post" => await SocialAndBusinessHandlers.HandleStatusPostAsync(ctx, args, ct).ConfigureAwait(false),
                "broadcast" => await SocialAndBusinessHandlers.HandleBroadcastAsync(ctx, args, ct).ConfigureAwait(false),
                "business" => await SocialAndBusinessHandlers.HandleBusinessAsync(ctx, args, ct).ConfigureAwait(false),
                "call" => await CallHandlers.HandleCallAsync(ctx, args, ct).ConfigureAwait(false),
                "quit" or "exit" => new CommandResult(0, ShouldExit: true),
                _ => await UnknownCommandAsync(cmd).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException)
        {
            await _err.WriteLineAsync($"Command '{cmd}' canceled.").ConfigureAwait(false);
            return new CommandResult(1);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"Error in '{cmd}': {ex.Message}").ConfigureAwait(false);
            return new CommandResult(1);
        }
    }

    private async Task<CommandResult> PrintHelpAsync()
    {
        await _out.WriteLineAsync("""
            WhatsApp.Cli — Headless Verification Harness (Phase 11A + 11B)
            Session & Pairing:
              help                              Show this command list
              status                            Inspect device pairing, DB, generation, and Syncd/History counts
              pair qr [--timeout-sec 120] [--wait-sync-sec 0] [--qr-png <path>]
              pair phone <e164> [--timeout-sec 180] [--wait-sync-sec 0]
              connect [--wait-sync-sec 15]      Start session and wait for ConnectedEvent
              disconnect | reconnect [--wait-sync-sec 15] | wait-sync [--timeout-sec 20]
              sleep <seconds>                   Keep the session active for N seconds to receive live/retry stanzas
              events [count]                    Print recent buffered runtime events
              reset-pairing --confirm [--remote] Wipe persisted pairing credentials (and optionally unlink server-side)
              unlink --confirm                  Send server <remove-companion-device> for this device and wipe DB
              quit | exit                       Shut down cleanly

            History, Search, PDO & Media:
              chats list [limit] [--archived|--active] [--offset N]
              history <jid> [limit] [--before-ts <unixSec>]
              search <query...> [--chat <jid>] [--limit 25]
              pdo <chatJid> [count] | pdo placeholder <chatJid> <msgId>
              media download <msgId> <outFile> | media reupload <msgId>
              send media <image|video|audio|ptt|document|sticker> <jid> <file> [caption...] [--gif]
              send contact <jid> <phone> <displayName...>

            Messaging, Rich Features & Presence:
              send text <jid> <text...> [--mention ...] [--all] [--preview] [--spoiler] | reply <jid> <msgId> <text...> [--preview]
              send live-location <jid> <lat> <lng> [caption...] | send scheduled-call <jid> <unixMs> [title...]
              edit <jid> <msgId> <newText...> | revoke <jid> <msgId> [--admin] | draft <set|clear> <jid> [text...]
              react <jid> <msgId> <emoji|remove> [--encrypted] | pin <jid> <msgId> <true|false> | keep <jid> <msgId> <true|false>
              poll create <jid> <question> --opt <a> --opt <b> [--multi] | poll vote <jid> <msgId> --opt <a>
              interact <list|select|button|location|phone|address|callperm|flow> <jid> <msgId> [args...]
              event create <jid> <name> --start <unixSec|iso> [--end <ts>] [--desc <text>] [--loc <name>]
              event rsvp <jid> <msgId> <going|not_going|maybe> [--creator <jid>] | comment <groupJid> <parentMsgId> <text...>
              presence <available|unavailable> | presence sub <jid> | typing <jid> <composing|recording|paused> | read <jid> <msgId>

            Contacts, Groups, Account, Syncd, Social, Business & Calling:
              contacts <search [query]|save <phone> <name...>|delete <phone>> | usync <phoneOrJid>... | avatar <jid>
              privacy <get|set <cat> <val>|blacklist ...|default-disappearing <sec>> | profile <pushname|about|clear-about>
              block <list|add|remove|check> | label <create|delete|chat|msg|reorder> | quickreply <set|delete>
              safety <fingerprint|remove-device|rotate-spk|resync-appstate|report> | syncd [collection...]
              group <list|info|create|pic|settings|subject|desc|tag|invite|leave|add|remove|promote|demote> ...
              chat <delete|clear|pin|unpin|archive|unarchive|mute|unmute|read|unread|lock|unlock|star|unstar|delete-msg|disappearing|favorites> ...
              community <list|create|subgroups|link|unlink|join|deactivate> | broadcast <list|send>
              newsletter <list|info|directory|recommended|create|join|leave|messages|publish|react|edit|revoke|pin|unpin|mute|subscribe>
              status-post <text|revoke|react|group|mute> | business <profile|catalog|collections|order|send-order>
              call <dial|reject|end|mute|loopback-tone|group-start|link-create|link-query|link-join|waiting-room|raise-hand|screen-share>
            """).ConfigureAwait(false);
        return new CommandResult(0);
    }

    private async Task<CommandResult> HandleStatusAsync(CancellationToken ct)
    {
        CliStatusSnapshot st = await _runtime.GetStatusAsync(ct).ConfigureAwait(false);
        await _out.WriteLineAsync($"Profile DB         : {st.DatabasePath} (deviceId={st.DeviceId})").ConfigureAwait(false);
        await _out.WriteLineAsync($"Registered         : {st.IsRegistered} (PN={st.PnJid ?? "<unpaired>"}, LID={st.LidJid ?? "<none>"})").ConfigureAwait(false);
        await _out.WriteLineAsync($"Noise Static Key   : {(st.HasServerStaticPublic ? "present (IK ready)" : "none (XX)")}").ConfigureAwait(false);
        await _out.WriteLineAsync($"AppState Sync Key  : {(st.HasAppStateSyncKey ? "present" : "missing")}").ConfigureAwait(false);
        await _out.WriteLineAsync($"Session            : Running={st.IsRunning}, Connected={st.IsConnected}, Generation={st.CurrentGeneration}").ConfigureAwait(false);
        await _out.WriteLineAsync($"Local History      : Chats={st.ChatCount}, PersistedMsgs={st.PersistedMessageCount}, SessionBatches={st.HistoryBatchesReceived}, SessionMsgs={st.HistoryMessagesReceived}").ConfigureAwait(false);
        string vers = string.Join(", ", st.AppStateVersions.Select(kv => $"{kv.Key}=v{kv.Value}"));
        await _out.WriteLineAsync($"Syncd Collections  : {vers}").ConfigureAwait(false);
        return new CommandResult(0);
    }

    private async Task<CommandResult> HandlePairAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await _err.WriteLineAsync("Usage: pair qr [--timeout-sec 120] | pair phone <e164> [--timeout-sec 180]").ConfigureAwait(false);
            return new CommandResult(1);
        }

        string mode = args[1].ToLowerInvariant();
        if (mode == "qr")
        {
            int timeoutSec = ParseIntOption(args, "--timeout-sec", 120);
            int waitSyncSec = ParseIntOption(args, "--wait-sync-sec", 0);
            string? qrPngPath = ParseStringOption(args, "--qr-png");
            DeviceCredentials creds = await _runtime.Backend.Device.LoadOrCreateAsync(ct).ConfigureAwait(false);
            if (creds.IsRegistered)
            {
                await _out.WriteLineAsync($"Already paired as {creds.Pn}. Use 'connect' or 'reset-pairing --confirm' first.").ConfigureAwait(false);
                return new CommandResult(0);
            }

            Task<ConnectedEvent> connectedTask = _runtime.WaitForEventAsync<ConnectedEvent>(
                timeout: TimeSpan.FromSeconds(timeoutSec), ct: ct);
            Task<PairSuccessEvent> pairedTask = _runtime.WaitForEventAsync<PairSuccessEvent>(
                timeout: TimeSpan.FromSeconds(timeoutSec), ct: ct);

            using var pairLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await _runtime.EnsureStartedAsync(ct).ConfigureAwait(false);

            Task qrPrinter = Task.Run(async () =>
            {
                while (!pairLoopCts.IsCancellationRequested && !pairedTask.IsCompleted)
                {
                    try
                    {
                        QrCodeEvent qr = await _runtime.WaitForEventAsync<QrCodeEvent>(
                            timeout: TimeSpan.FromSeconds(Math.Min(timeoutSec, 65)),
                            ct: pairLoopCts.Token).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(qrPngPath))
                        {
                            string fullPng = Path.GetFullPath(qrPngPath);
                            string? dir = Path.GetDirectoryName(fullPng);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }
                            string tmpPng = $"{fullPng}.{Guid.NewGuid():N}.tmp";
                            await File.WriteAllBytesAsync(tmpPng, QrConsoleRenderer.RenderPngBytes(qr.QrData), pairLoopCts.Token).ConfigureAwait(false);
                            File.Move(tmpPng, fullPng, overwrite: true);
                        }
                        await _out.WriteLineAsync("\nScan this QR code in WhatsApp -> Linked Devices -> Link a Device:").ConfigureAwait(false);
                        await _out.WriteLineAsync(QrConsoleRenderer.RenderUnicodeHalfBlocks(qr.QrData)).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (TimeoutException) { break; }
                }
            }, pairLoopCts.Token);

            PairSuccessEvent ps;
            try
            {
                ps = await pairedTask.ConfigureAwait(false);
            }
            finally
            {
                await pairLoopCts.CancelAsync().ConfigureAwait(false);
                await qrPrinter.ConfigureAwait(false);
            }

            ConnectedEvent conn = await connectedTask.ConfigureAwait(false);
            await _out.WriteLineAsync($"Paired and connected! PN={conn.PnJid}, LID={conn.LidJid ?? ps.LidJid ?? "<pending>"}").ConfigureAwait(false);
            await WaitPostPairSyncWindowAsync(waitSyncSec, ct).ConfigureAwait(false);
            return new CommandResult(0);
        }
        else if (mode == "phone")
        {
            if (args.Count < 3)
            {
                await _err.WriteLineAsync("Usage: pair phone <e164> [--timeout-sec 180] [--wait-sync-sec 0]").ConfigureAwait(false);
                return new CommandResult(1);
            }
            string phone = args[2];
            int timeoutSec = ParseIntOption(args, "--timeout-sec", 180);
            int waitSyncSec = ParseIntOption(args, "--wait-sync-sec", 0);

            Task<ConnectedEvent> connectedTask = _runtime.WaitForEventAsync<ConnectedEvent>(
                timeout: TimeSpan.FromSeconds(timeoutSec), ct: ct);
            Task<PairSuccessEvent> pairedTask = _runtime.WaitForEventAsync<PairSuccessEvent>(
                timeout: TimeSpan.FromSeconds(timeoutSec), ct: ct);

            // Server sends initial pair-device ref once pre-pair Noise/config reaches pairing readiness.
            Task<QrCodeEvent> readyQrTask = _runtime.WaitForEventAsync<QrCodeEvent>(
                timeout: TimeSpan.FromSeconds(30), ct: ct);

            await _runtime.EnsureStartedAsync(ct).ConfigureAwait(false);
            _ = await readyQrTask.ConfigureAwait(false);

            PairingHandler pairing = _runtime.Orchestrator?.ActivePairing
                ?? throw new InvalidOperationException("PairingHandler is not active.");
            string code = await pairing.RequestPairCodeAsync(new PairCodeOptions { PhoneNumber = phone }, ct).ConfigureAwait(false);
            await _out.WriteLineAsync($"Enter this 8-character pairing code on your phone: {code}").ConfigureAwait(false);

            _ = await pairedTask.ConfigureAwait(false);
            ConnectedEvent conn = await connectedTask.ConfigureAwait(false);
            await _out.WriteLineAsync($"Phone-code linked and connected! PN={conn.PnJid}, LID={conn.LidJid}").ConfigureAwait(false);
            await WaitPostPairSyncWindowAsync(waitSyncSec, ct).ConfigureAwait(false);
            return new CommandResult(0);
        }

        await _err.WriteLineAsync($"Unknown pair mode '{mode}'.").ConfigureAwait(false);
        return new CommandResult(1);
    }

    private async Task<CommandResult> HandleConnectAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        int waitSyncSec = ParseIntOption(args, "--wait-sync-sec", 15);
        int connectTimeoutSec = ParseIntOption(args, "--timeout-sec", 30);

        if (_runtime.IsConnected)
        {
            await _out.WriteLineAsync("Already connected.").ConfigureAwait(false);
            return new CommandResult(0);
        }

        Task<ConnectedEvent> connTask = _runtime.WaitForEventAsync<ConnectedEvent>(
            timeout: TimeSpan.FromSeconds(connectTimeoutSec), ct: ct);
        Task<OfflineSyncCompletedEvent>? offlineTask = waitSyncSec > 0
            ? _runtime.WaitForEventAsync<OfflineSyncCompletedEvent>(timeout: TimeSpan.FromSeconds(waitSyncSec), ct: ct)
            : null;

        await _runtime.EnsureStartedAsync(ct).ConfigureAwait(false);
        ConnectedEvent conn = await connTask.ConfigureAwait(false);
        await _out.WriteLineAsync($"Connected (gen={_runtime.Orchestrator?.CurrentGeneration}): PN={conn.PnJid}, LID={conn.LidJid ?? "<none>"}").ConfigureAwait(false);

        if (offlineTask is not null)
        {
            try
            {
                OfflineSyncCompletedEvent off = await offlineTask.ConfigureAwait(false);
                await _out.WriteLineAsync($"Offline drain complete: processed={off.ProcessedCount}, timedOut={off.TimedOut}").ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await _out.WriteLineAsync("No offline batch completed within wait window (session active).").ConfigureAwait(false);
            }
        }
        return new CommandResult(0);
    }

    private async Task<CommandResult> HandleDisconnectAsync()
    {
        await _runtime.StopAsync().ConfigureAwait(false);
        await _out.WriteLineAsync("Disconnected cleanly.").ConfigureAwait(false);
        return new CommandResult(0);
    }

    private async Task<CommandResult> HandleReconnectAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        await _runtime.StopAsync().ConfigureAwait(false);
        return await HandleConnectAsync(args, ct).ConfigureAwait(false);
    }

    private async Task<CommandResult> HandleWaitSyncAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        int timeoutSec = ParseIntOption(args, "--timeout-sec", 20);
        TimeSpan window = TimeSpan.FromSeconds(timeoutSec);

        Task<HistorySyncReceivedEvent> histTask = _runtime.WaitForEventAsync<HistorySyncReceivedEvent>(timeout: window, ct: ct);
        Task<AppStateUpdatedEvent> syncdTask = _runtime.WaitForEventAsync<AppStateUpdatedEvent>(timeout: window, ct: ct);
        Task<OfflineSyncCompletedEvent> offlineTask = _runtime.WaitForEventAsync<OfflineSyncCompletedEvent>(timeout: window, ct: ct);

        Task first = await Task.WhenAny(histTask, syncdTask, offlineTask).ConfigureAwait(false);
        await first.ConfigureAwait(false);
        await _out.WriteLineAsync($"Sync event observed: {first.GetType().Name} completed (historyBatches={_runtime.HistoryBatchesReceived}, msgs={_runtime.HistoryMessagesReceived}).").ConfigureAwait(false);
        return new CommandResult(0);
    }

    private async Task<CommandResult> HandleSleepAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2 || !double.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds) || seconds < 0)
        {
            await _err.WriteLineAsync("Usage: sleep <seconds>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        return new CommandResult(0);
    }

    private async Task<CommandResult> HandleEventsAsync(IReadOnlyList<string> args)
    {
        int count = args.Count > 1 && int.TryParse(args[1], out int n) ? n : 20;
        var events = _runtime.GetRecentEvents(count);
        if (events.Count == 0)
        {
            await _out.WriteLineAsync("<no events buffered>").ConfigureAwait(false);
            return new CommandResult(0);
        }
        foreach (var (ts, evt) in events)
        {
            await _out.WriteLineAsync($"{ts:HH:mm:ss} {CliEventFormatter.FormatHuman(evt)}").ConfigureAwait(false);
        }
        return new CommandResult(0);
    }

    private async Task<CommandResult> HandleResetPairingAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!args.Contains("--confirm", StringComparer.OrdinalIgnoreCase))
        {
            await _err.WriteLineAsync("Refusing destructive reset without --confirm.").ConfigureAwait(false);
            return new CommandResult(1);
        }
        if (args.Contains("--remote", StringComparer.OrdinalIgnoreCase))
        {
            return await UnlinkCurrentDeviceAsync(ct).ConfigureAwait(false);
        }
        await _runtime.StopAsync().ConfigureAwait(false);
        await _runtime.Backend.Device.ClearPairingAsync(ct).ConfigureAwait(false);
        await _out.WriteLineAsync("Cleared pairing credentials from local SQLite store.").ConfigureAwait(false);
        return new CommandResult(0);
    }

    private async Task<CommandResult> HandleUnlinkAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!args.Contains("--confirm", StringComparer.OrdinalIgnoreCase))
        {
            await _err.WriteLineAsync("Refusing destructive server unlink without --confirm.").ConfigureAwait(false);
            return new CommandResult(1);
        }
        return await UnlinkCurrentDeviceAsync(ct).ConfigureAwait(false);
    }

    private async Task<CommandResult> UnlinkCurrentDeviceAsync(CancellationToken ct)
    {
        DeviceCredentials? creds = await _runtime.Backend.Device.LoadAsync(ct).ConfigureAwait(false);
        if (creds is null || !creds.IsRegistered)
        {
            await _runtime.StopAsync().ConfigureAwait(false);
            await _runtime.Backend.Device.ClearPairingAsync(ct).ConfigureAwait(false);
            await _out.WriteLineAsync("Device not registered; cleared local pairing store.").ConfigureAwait(false);
            return new CommandResult(0);
        }

        Jid deviceJid = creds.Lid ?? creds.Pn
            ?? throw new InvalidOperationException("Registered device credentials lack both LID and PN JIDs.");

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(new CliCommandContext(_runtime, _out, _err), ct).ConfigureAwait(false);
        AccountSafetyClient safety = orch.ActiveAccountSafety ?? throw new InvalidOperationException("AccountSafetyClient is not active.");
        await safety.RemoveCompanionDeviceAsync(deviceJid, reason: "user_initiated", ct).ConfigureAwait(false);
        await _runtime.StopAsync().ConfigureAwait(false);
        await _runtime.Backend.Device.ClearPairingAsync(ct).ConfigureAwait(false);
        await _out.WriteLineAsync($"Unlinked device {deviceJid} from WhatsApp server and cleared local SQLite pairing store.").ConfigureAwait(false);
        return new CommandResult(0);
    }

    private async Task<CommandResult> UnknownCommandAsync(string cmd)
    {
        await _err.WriteLineAsync($"Unknown command '{cmd}'. Type 'help' for usage.").ConfigureAwait(false);
        return new CommandResult(1);
    }

    private async Task WaitPostPairSyncWindowAsync(int waitSyncSec, CancellationToken ct)
    {
        if (waitSyncSec <= 0)
        {
            return;
        }
        await _out.WriteLineAsync($"Waiting {waitSyncSec}s for initial AppStateSyncKeyShare & HistorySync...").ConfigureAwait(false);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<DisconnectedEvent> terminalDisconnect = _runtime.WaitForEventAsync<DisconnectedEvent>(
            predicate: d => !d.WillReconnect,
            timeout: TimeSpan.FromSeconds(waitSyncSec + 1),
            ct: waitCts.Token);
        Task delayTask = Task.Delay(TimeSpan.FromSeconds(waitSyncSec), waitCts.Token);
        Task completed = await Task.WhenAny(delayTask, terminalDisconnect).ConfigureAwait(false);
        await waitCts.CancelAsync().ConfigureAwait(false);
        if (completed == terminalDisconnect && terminalDisconnect.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException($"Session disconnected terminally: {terminalDisconnect.Result.Reason}");
        }
        await delayTask.ConfigureAwait(false);
    }

    private static string? ParseStringOption(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static int ParseIntOption(IReadOnlyList<string> args, string flag, int defaultValue)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out int parsed) && parsed >= 0)
            {
                return parsed;
            }
        }
        return defaultValue;
    }
}
