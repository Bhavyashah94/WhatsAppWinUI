using System.Buffers.Binary;
using WhatsApp.Api;
using WhatsApp.Calling;
using WhatsApp.Protocol.Binary;
using WhatsApp.Protocol.Binary.Stanzas;

namespace WhatsApp.Cli;

internal static class CallHandlers
{
    public static async Task<CommandResult> HandleCallAsync(
        CliCommandContext ctx,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        if (args.Count < 2)
        {
            await ctx.Err.WriteLineAsync("Usage: call <dial <jid> [--video]|reject <callId> <peerJid> <creatorJid> [reason]|end <callId> [reason]|mute <callId> <true|false>|loopback-tone <callId> [--frames N]|group-start <peerJid...> [--group <gJid>] [--video]|link-create [audio|video]|link-query <token> [audio|video]|link-join <token> [audio|video]|waiting-room <callId> <creatorJid> <on|off|admit|deny> [userJid]|raise-hand <callId> <creatorJid> <true|false>|screen-share <callId> <creatorJid> <start|stop>>").ConfigureAwait(false);
            return new CommandResult(1);
        }

        ConnectionOrchestrator orch = await CliCommandHelpers.EnsureOrchestratorAsync(ctx, ct).ConfigureAwait(false);
        CallClient calls = orch.ActiveCalls ?? throw new InvalidOperationException("CallClient is not active.");
        CallSignalingClient signaling = orch.ActiveCallSignaling ?? throw new InvalidOperationException("CallSignalingClient is not active.");
        string sub = args[1].ToLowerInvariant();

        switch (sub)
        {
            case "dial" when args.Count >= 3:
            {
                var (pos, _, flags) = CliCommandHelpers.ParseOptions(args, 2, new HashSet<string>(), new HashSet<string> { "--video" });
                Jid peer = CliCommandHelpers.ParseTargetJid(pos[0]);
                bool video = flags.Contains("--video");
                string callId = await calls.PlaceCallAsync(peer, video: video, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Placed {(video ? "video" : "audio")} call to {peer}: callId={callId}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "reject" when args.Count >= 5:
            {
                string reason = args.Count > 5 ? args[5] : "rejected";
                await signaling.SendRejectAsync(args[2], CliCommandHelpers.ParseTargetJid(args[3]), CliCommandHelpers.ParseTargetJid(args[4]), reason, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Rejected call {args[2]} (reason={reason}).").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "end" when args.Count >= 3:
            {
                string? reason = args.Count > 3 ? args[3] : null;
                await calls.EndCallAsync(args[2], reason, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Ended call {args[2]}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "mute" when args.Count >= 4:
            {
                bool muted = args[3].Equals("true", StringComparison.OrdinalIgnoreCase);
                await calls.SetMutedAsync(args[2], muted, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Set call {args[2]} muted={muted}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "loopback-tone" when args.Count >= 3:
            {
                var (_, opts, _) = CliCommandHelpers.ParseOptions(args, 3, new HashSet<string> { "--frames" }, new HashSet<string>());
                int frames = opts.TryGetValue("--frames", out var fv) && int.TryParse(fv[^1], out int f) ? Math.Clamp(f, 1, 50) : 5;
                var codec = new PassThroughAudioCodec();
                short[] pcm = GenerateSineWaveFrame(sampleRate: 16_000, samples: 320, hz: 440.0);
                byte[] encoded = codec.EncodePcm16(pcm);
                for (int i = 0; i < frames; i++)
                {
                    await calls.SendAudioFrameAsync(args[2], new AudioFrame(encoded, 16_000, 20), ct).ConfigureAwait(false);
                }
                await ctx.Out.WriteLineAsync($"Sent {frames} PCM/Opus-passthrough loopback frames on call {args[2]}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "group-start" when args.Count >= 3:
            {
                var (pos, opts, flags) = CliCommandHelpers.ParseOptions(args, 2, new HashSet<string> { "--group" }, new HashSet<string> { "--video" });
                Jid? grpJid = opts.TryGetValue("--group", out var gv) ? CliCommandHelpers.ParseTargetJid(gv[^1]) : null;
                Jid[] peers = pos.Select(CliCommandHelpers.ParseTargetJid).ToArray();
                ActiveGroupCall grp = await calls.StartGroupCallAsync(peers, grpJid, flags.Contains("--video"), ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Started group call {grp.CallId} with {peers.Length} peer(s).").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "link-create":
            {
                CallLinkMedia media = args.Count > 2 && args[2].Equals("video", StringComparison.OrdinalIgnoreCase)
                    ? CallLinkMedia.Video : CallLinkMedia.Audio;
                CallLink link = await calls.CreateCallLinkAsync(media, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Created {media} call link: token={link.Token}, url={link.Url}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "link-query" when args.Count >= 3:
            {
                CallLinkMedia media = args.Count > 3 && args[3].Equals("video", StringComparison.OrdinalIgnoreCase)
                    ? CallLinkMedia.Video : CallLinkMedia.Audio;
                CallLinkPreview prev = await calls.QueryCallLinkAsync(args[2], media, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Call link {args[2]}: waitingRoom={prev.WaitingRoomEnabled}, media={prev.Media}, admin={prev.IsAdmin}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "link-join" when args.Count >= 3:
            {
                CallLinkMedia media = args.Count > 3 && args[3].Equals("video", StringComparison.OrdinalIgnoreCase)
                    ? CallLinkMedia.Video : CallLinkMedia.Audio;
                CallLinkJoin join = await calls.JoinCallLinkAsync(args[2], media, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Joined call link {args[2]}: callId={join.CallId}").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "waiting-room" when args.Count >= 5:
            {
                string callId = args[2];
                Jid creator = CliCommandHelpers.ParseTargetJid(args[3]);
                string act = args[4].ToLowerInvariant();
                if (act is "on" or "off")
                {
                    await calls.ToggleWaitingRoomAsync(callId, creator, act == "on", ct).ConfigureAwait(false);
                }
                else if (act == "admit" && args.Count >= 6)
                {
                    await calls.AdmitWaitingRoomUserAsync(callId, creator, CliCommandHelpers.ParseTargetJid(args[5]), ct).ConfigureAwait(false);
                }
                else if (act == "deny" && args.Count >= 6)
                {
                    await calls.DenyWaitingRoomUserAsync(callId, creator, CliCommandHelpers.ParseTargetJid(args[5]), ct).ConfigureAwait(false);
                }
                else
                {
                    await ctx.Err.WriteLineAsync("Usage: call waiting-room <callId> <creatorJid> <on|off|admit|deny> [userJid]").ConfigureAwait(false);
                    return new CommandResult(1);
                }
                await ctx.Out.WriteLineAsync($"Updated waiting room ({act}) on call {callId}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "raise-hand" when args.Count >= 5:
            {
                bool raised = args[4].Equals("true", StringComparison.OrdinalIgnoreCase);
                await calls.SetRaiseHandAsync(args[2], CliCommandHelpers.ParseTargetJid(args[3]), raised, ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Set raiseHand={raised} on call {args[2]}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            case "screen-share" when args.Count >= 5:
            {
                ScreenShareState state = args[4].Equals("start", StringComparison.OrdinalIgnoreCase)
                    ? ScreenShareState.Started : ScreenShareState.Stopped;
                await calls.SetScreenShareAsync(args[2], CliCommandHelpers.ParseTargetJid(args[3]), state, ct: ct).ConfigureAwait(false);
                await ctx.Out.WriteLineAsync($"Set screenShare={state} on call {args[2]}.").ConfigureAwait(false);
                return new CommandResult(0);
            }
            default:
                await ctx.Err.WriteLineAsync("Unknown call subcommand.").ConfigureAwait(false);
                return new CommandResult(1);
        }
    }

    private static short[] GenerateSineWaveFrame(int sampleRate, int samples, double hz)
    {
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            pcm[i] = (short)(Math.Sin(2.0 * Math.PI * hz * i / sampleRate) * 8000.0);
        }
        return pcm;
    }
}
