using LoupixDeck.Plugin.SpotifyPremium.Commands.Common;
using LoupixDeck.Plugin.SpotifyPremium.Spotify;
using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Volume;

internal sealed class MuteCommand : SpotifyCommandBase
{
    public MuteCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.Mute",
        DisplayName = "Mute",
        Group = "Spotify Premium · Volume"
    };
    protected override Task Run(SpotifyAPI.Web.SpotifyClient s, CommandContext ctx)
    {
        // Remember the previous level so Unmute can restore it.
        ctx.Host.Settings.Set("last_volume", (long)Math.Max(Player.State.VolumePercent, 1));
        ctx.Host.Settings.Save();
        return s.Player.SetVolume(new PlayerVolumeRequest(0) { DeviceId = DeviceId(ctx, Player.State) });
    }
}

internal sealed class UnmuteCommand : SpotifyCommandBase
{
    public UnmuteCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.Unmute",
        DisplayName = "Unmute",
        Group = "Spotify Premium · Volume"
    };
    protected override Task Run(SpotifyAPI.Web.SpotifyClient s, CommandContext ctx)
    {
        var target = (int)Math.Clamp(ctx.Host.Settings.Get<long>("last_volume", 50), 1, 100);
        return s.Player.SetVolume(new PlayerVolumeRequest(target) { DeviceId = DeviceId(ctx, Player.State) });
    }
}

internal sealed class ToggleMuteCommand : SpotifyCommandBase, IDisplayCommand
{
    public ToggleMuteCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.ToggleMute",
        DisplayName = "Toggle Mute",
        Group = "Spotify Premium · Volume"
    };
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(3);
    public string GetText(CommandContext ctx) => Player.State.VolumePercent == 0 ? "🔇" : $"🔊 {Player.State.VolumePercent}%";

    protected override Task Run(SpotifyAPI.Web.SpotifyClient s, CommandContext ctx)
    {
        if (Player.State.VolumePercent > 0)
        {
            ctx.Host.Settings.Set("last_volume", (long)Player.State.VolumePercent);
            ctx.Host.Settings.Save();
            return s.Player.SetVolume(new PlayerVolumeRequest(0) { DeviceId = DeviceId(ctx, Player.State) });
        }

        var restore = (int)Math.Clamp(ctx.Host.Settings.Get<long>("last_volume", 50), 1, 100);
        return s.Player.SetVolume(new PlayerVolumeRequest(restore) { DeviceId = DeviceId(ctx, Player.State) });
    }
}

internal sealed class DirectVolumeCommand : SpotifyCommandBase
{
    public DirectVolumeCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.DirectVolume",
        DisplayName = "Set Volume",
        Group = "Spotify Premium · Volume",
        ParameterTemplate = "({Volume})",
        Parameters = [new CommandParameter("Volume", typeof(int))]
    };

    protected override Task Run(SpotifyAPI.Web.SpotifyClient s, CommandContext ctx)
    {
        if (ctx.Parameters.Length == 0 || !int.TryParse(ctx.Parameters[0], out var raw))
            return Task.CompletedTask;
        var clamped = Math.Clamp(raw, 0, 100);
        return s.Player.SetVolume(new PlayerVolumeRequest(clamped) { DeviceId = DeviceId(ctx, Player.State) });
    }
}

internal abstract class VolumeStepBase : SpotifyCommandBase
{
    // The volume rotary uses a producer/consumer model:
    //   - Each tick (producer) updates _localVolume in-memory, repaints the
    //     overlay, and queues the latest target. Returns immediately.
    //   - A single worker (consumer) drains the queue, sending one SetVolume
    //     call at a time. Ticks that arrive during a send simply overwrite
    //     the queued target so only the latest value is sent next.
    // Result: UI feedback is instant (no waiting on HTTP), the rotary feels
    // continuous, and we send at most ~5 API calls/second.
    private static readonly object Gate = new();
    private static int _localVolume = -1;
    private static int _queuedTarget = -1;
    private static int _workerRunning; // 0 = idle, 1 = a worker is draining
    private static DateTime _lastTickUtc = DateTime.MinValue;
    private static readonly TimeSpan ResyncAfter = TimeSpan.FromSeconds(5);

    protected VolumeStepBase(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    protected abstract int Step { get; }

    public override ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton;

    public override Task Execute(CommandContext ctx)
    {
        int target;
        lock (Gate)
        {
            // Re-sync from Spotify only after a long idle window — during
            // active rotation _localVolume is authoritative so the user
            // never sees a snap-back to a stale background-polled value.
            if (_localVolume < 0 || DateTime.UtcNow - _lastTickUtc > ResyncAfter)
                _localVolume = Player.State.VolumePercent;

            _localVolume = Math.Clamp(_localVolume + Step, 0, 100);
            _lastTickUtc = DateTime.UtcNow;
            _queuedTarget = _localVolume;
            target = _localVolume;
        }

        // Instant local feedback regardless of HTTP latency. Player.State
        // is updated so the rest of the plugin (display commands) sees the
        // new value immediately, and the overlay shows it on the touch
        // slot adjacent to the rotary that fired.
        Player.ApplyLocalVolume(target);
        if (ctx.SourceIndex is int rotaryIdx)
        {
            var slot = ctx.Host.GetTouchSlotForRotary(rotaryIdx);
            if (slot >= 0)
                ctx.Host.OverlayTouchText(slot, $"{target}%", SpotifyPremiumPlugin.VolumeOverlayDuration);
        }

        // Ensure there's exactly one worker draining the queue. Subsequent
        // ticks while the worker runs just update _queuedTarget; the
        // running worker picks it up on its next loop iteration.
        if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
            _ = Task.Run(() => DrainAsync(ctx));

        return Task.CompletedTask;
    }

    private async Task DrainAsync(CommandContext ctx)
    {
        try
        {
            while (true)
            {
                int next;
                lock (Gate)
                {
                    if (_queuedTarget < 0)
                    {
                        // Atomically clear the running flag while holding
                        // Gate. A tick that arrives concurrently either:
                        //   (a) already enqueued before this lock — we'd
                        //       see its value above instead of -1, OR
                        //   (b) blocks on Gate; once we exit, it enqueues
                        //       and finds _workerRunning=0 (we set it
                        //       below), so it starts a new worker. No
                        //       value is ever lost.
                        Interlocked.Exchange(ref _workerRunning, 0);
                        return;
                    }
                    next = _queuedTarget;
                    _queuedTarget = -1;
                }

                try
                {
                    var spotify = await Client.GetClientAsync();
                    if (spotify == null) return;

                    await spotify.Player.SetVolume(new PlayerVolumeRequest(next)
                    {
                        DeviceId = DeviceId(ctx, Player.State)
                    });
                }
                catch (APIException ex)
                {
                    Logger.Warn($"{Descriptor.CommandName}: SetVolume failed: {ex.Response?.StatusCode} {ex.Message}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"{Descriptor.CommandName}: SetVolume failed", ex);
                }
            }
        }
        finally
        {
            // Safety net for unexpected exits — normal exits already
            // cleared this inside the lock.
            Interlocked.Exchange(ref _workerRunning, 0);
        }
    }

    protected override Task Run(SpotifyAPI.Web.SpotifyClient s, CommandContext ctx) => Task.CompletedTask;
}

internal sealed class VolumeUpCommand : VolumeStepBase
{
    public VolumeUpCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    protected override int Step => 2;
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.VolumeUp",
        DisplayName = "Volume +2%",
        Group = "Spotify Premium · Volume"
    };
}

internal sealed class VolumeDownCommand : VolumeStepBase
{
    public VolumeDownCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    protected override int Step => -2;
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.VolumeDown",
        DisplayName = "Volume -2%",
        Group = "Spotify Premium · Volume"
    };
}

internal sealed class SpotifyVolumeAdjustment : SpotifyCommandBase, IAdjustmentCommand
{
    public SpotifyVolumeAdjustment(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.VolumeAdjustment",
        DisplayName = "Volume (Adjustment)",
        Group = "Spotify Premium · Volume",
        HiddenFromMenu = true
    };
    public override ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder;
    protected override Task Run(SpotifyAPI.Web.SpotifyClient s, CommandContext ctx) => Task.CompletedTask;

    public async Task ApplyAdjustment(CommandContext ctx, int ticks)
    {
        var s = await Client.GetClientAsync(); if (s == null) return;
        var target = Math.Clamp(Player.State.VolumePercent + ticks * 2, 0, 100);
        await s.Player.SetVolume(new PlayerVolumeRequest(target) { DeviceId = Player.State.DeviceId });
    }

    public async Task ApplyReset(CommandContext ctx)
    {
        // Press = play/pause toggle, matching the Loupedeck original.
        var s = await Client.GetClientAsync(); if (s == null) return;
        if (Player.State.IsPlaying)
            await s.Player.PausePlayback(new PlayerPausePlaybackRequest { DeviceId = Player.State.DeviceId });
        else
            await s.Player.ResumePlayback(new PlayerResumePlaybackRequest { DeviceId = Player.State.DeviceId });
    }

    public string? GetValueText(CommandContext ctx) => $"{Player.State.VolumePercent}%";
}
