using System.Globalization;
using LoupixDeck.Plugin.SpotifyPremium.Commands.Common;
using LoupixDeck.Plugin.SpotifyPremium.Spotify;
using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Playback;

/// <summary>
/// Shared seek logic: moves the playback position by a number of seconds,
/// starting from the cache's estimated position.
/// </summary>
internal abstract class SeekStepBase : SpotifyCommandBase
{
    public const int DefaultSeconds = 10;

    protected SeekStepBase(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    /// <summary>+1 seeks forward, -1 backward.</summary>
    protected abstract int Direction { get; }

    public override ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton | ButtonTargets.TouchButton;

    protected override Task Run(SpotifyAPI.Web.SpotifyClient s, CommandContext ctx)
    {
        var seconds = DefaultSeconds;
        if (ctx.Parameters is { Length: > 0 } &&
            int.TryParse(ctx.Parameters[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed != 0)
        {
            seconds = Math.Abs(parsed);
        }

        return SeekBy(s, Player, Direction * seconds * 1000);
    }

    /// <summary>Seeks relative to the estimated position, clamped to the track.</summary>
    internal static Task SeekBy(SpotifyAPI.Web.SpotifyClient s, PlayerStateCache player, int deltaMs)
    {
        if (string.IsNullOrEmpty(player.State.TrackUri)) return Task.CompletedTask;

        // Stop just short of the end so seeking forward doesn't skip the track.
        var max = Math.Max(player.DurationMs - 1000, 0);
        var target = Math.Clamp(player.EstimatePositionMs() + deltaMs, 0, max);
        player.ApplyLocalPosition(target);
        return s.Player.SeekTo(new PlayerSeekToRequest(target)
        {
            DeviceId = string.IsNullOrEmpty(player.State.DeviceId) ? null : player.State.DeviceId
        });
    }
}

internal sealed class SeekForwardCommand : SeekStepBase
{
    public SeekForwardCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    protected override int Direction => 1;
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.SeekForward",
        DisplayName = "Seek Forward",
        Group = "Spotify Premium",
        Icon = SpotifyIcons.SeekForward,
        Description = "Jump forward in the current track",
        ParameterTemplate = "({Seconds})",
        Parameters = [new CommandParameter("Seconds", typeof(int)) { DefaultValue = "10" }],
        HiddenFromMenu = true
    };
}

internal sealed class SeekBackwardCommand : SeekStepBase
{
    public SeekBackwardCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    protected override int Direction => -1;
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.SeekBackward",
        DisplayName = "Seek Backward",
        Group = "Spotify Premium",
        Icon = SpotifyIcons.SeekBackward,
        Description = "Jump back in the current track",
        ParameterTemplate = "({Seconds})",
        Parameters = [new CommandParameter("Seconds", typeof(int)) { DefaultValue = "10" }],
        HiddenFromMenu = true
    };
}

internal sealed class SeekAdjustment : SpotifyCommandBase, IAdjustmentCommand
{
    private const int SecondsPerTick = 5;

    public SeekAdjustment(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.SeekAdjustment",
        DisplayName = "Seek (Adjustment)",
        Group = "Spotify Premium",
        Icon = SpotifyIcons.Seek,
        Description = "Scrub through the current track with a rotary encoder",
        HiddenFromMenu = true
    };
    public override ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder;
    protected override Task Run(SpotifyAPI.Web.SpotifyClient s, CommandContext ctx) => Task.CompletedTask;

    public async Task ApplyAdjustment(CommandContext ctx, int ticks)
    {
        try
        {
            var s = await Client.GetClientAsync(); if (s == null) return;
            await Player.EnsureFreshAsync();
            await SeekStepBase.SeekBy(s, Player, ticks * SecondsPerTick * 1000);
        }
        catch (APIException ex)
        {
            Logger.Warn($"{Descriptor.CommandName}: Spotify API returned {ex.Response?.StatusCode} {ex.Message}");
        }
    }

    public async Task ApplyReset(CommandContext ctx)
    {
        // Press = play/pause toggle, matching the other adjustments.
        var s = await Client.GetClientAsync(); if (s == null) return;
        await Player.EnsureFreshAsync();
        await TogglePlaybackAsync(s, DeviceId(ctx, Player.State));
    }

    /// <summary>
    /// The position on the track's length, with the time as the text and the track name as the
    /// detail, so a wheel or key layout can show either in a text layer of its own.
    /// </summary>
    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        var state = ShownState(PlayerStateCache.TrackPollInterval);
        var positionMs = Player.EstimatePositionMs();
        var durationMs = Player.DurationMs;
        // Without a known length there is no scale, so the dial shows the text alone.
        var normalized = durationMs > 0 ? (double)positionMs / durationMs : double.NaN;
        return new AdjustmentValue(normalized, $"{FormatTime(positionMs)} / {FormatTime(durationMs)}")
        {
            Detail = string.IsNullOrEmpty(state.TrackName) ? null : state.TrackName
        };
    }

    /// <summary>m:ss, or h:mm:ss from an hour up (long podcast episodes and mixes).</summary>
    internal static string FormatTime(int ms)
    {
        var time = TimeSpan.FromMilliseconds(ms);
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
