using LoupixDeck.Plugin.SpotifyPremium.Commands.Common;
using LoupixDeck.Plugin.SpotifyPremium.Spotify;
using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Playback;

internal sealed class TogglePlaybackCommand : SpotifyCommandBase, IDisplayCommand
{
    public TogglePlaybackCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.TogglePlayback",
        DisplayName = "Toggle Play/Pause",
        Group = "Spotify Premium",
        HiddenFromMenu = true
    };

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(3);
    public string GetText(CommandContext ctx) => Player.State.IsPlaying ? "▶ ❚❚" : "▶";

    protected override async Task Run(SpotifyAPI.Web.SpotifyClient spotify, CommandContext ctx)
    {
        var device = DeviceId(ctx, Player.State);
        if (Player.State.IsPlaying)
            await spotify.Player.PausePlayback(new PlayerPausePlaybackRequest { DeviceId = device });
        else
            await spotify.Player.ResumePlayback(new PlayerResumePlaybackRequest { DeviceId = device });
    }
}

internal sealed class NextTrackCommand : SpotifyCommandBase
{
    public NextTrackCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.NextTrack",
        DisplayName = "Next Track",
        Group = "Spotify Premium",
        HiddenFromMenu = true
    };

    protected override Task Run(SpotifyAPI.Web.SpotifyClient spotify, CommandContext ctx)
        => spotify.Player.SkipNext(new PlayerSkipNextRequest { DeviceId = DeviceId(ctx, Player.State) });
}

internal sealed class PreviousTrackCommand : SpotifyCommandBase
{
    public PreviousTrackCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.PreviousTrack",
        DisplayName = "Previous Track",
        Group = "Spotify Premium",
        HiddenFromMenu = true
    };

    protected override Task Run(SpotifyAPI.Web.SpotifyClient spotify, CommandContext ctx)
        => spotify.Player.SkipPrevious(new PlayerSkipPreviousRequest { DeviceId = DeviceId(ctx, Player.State) });
}

internal sealed class ShufflePlayCommand : SpotifyCommandBase, IDisplayCommand
{
    public ShufflePlayCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.ShufflePlay",
        DisplayName = "Toggle Shuffle",
        Group = "Spotify Premium",
        HiddenFromMenu = true
    };

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(3);
    public string GetText(CommandContext ctx) => Player.State.ShuffleEnabled ? "Shuffle ✓" : "Shuffle";

    protected override Task Run(SpotifyAPI.Web.SpotifyClient spotify, CommandContext ctx)
        => spotify.Player.SetShuffle(new PlayerShuffleRequest(!Player.State.ShuffleEnabled)
        {
            DeviceId = DeviceId(ctx, Player.State)
        });
}

internal sealed class ChangeRepeatStateCommand : SpotifyCommandBase, IDisplayCommand
{
    public ChangeRepeatStateCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.ChangeRepeatState",
        DisplayName = "Cycle Repeat Mode",
        Group = "Spotify Premium",
        HiddenFromMenu = true
    };

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(3);
    public string GetText(CommandContext ctx) => Player.State.RepeatState switch
    {
        "track" => "Repeat 1",
        "context" => "Repeat ⟳",
        _ => "Repeat off"
    };

    protected override Task Run(SpotifyAPI.Web.SpotifyClient spotify, CommandContext ctx)
    {
        var next = Player.State.RepeatState switch
        {
            "off" => PlayerSetRepeatRequest.State.Context,
            "context" => PlayerSetRepeatRequest.State.Track,
            _ => PlayerSetRepeatRequest.State.Off
        };
        return spotify.Player.SetRepeat(new PlayerSetRepeatRequest(next)
        {
            DeviceId = DeviceId(ctx, Player.State)
        });
    }
}

/// <summary>
/// Left/right pair that lets the user bind navigation to a rotary's two
/// rotation directions, plus an adjustment-style command for forward-compat.
/// </summary>
internal sealed class PlayNavigateLeftCommand : SpotifyCommandBase
{
    public PlayNavigateLeftCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.PlayNavigate.Left",
        DisplayName = "Previous Track (Rotary Left)",
        Group = "Spotify Premium",
        HiddenFromMenu = true
    };
    public override ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton;
    protected override Task Run(SpotifyAPI.Web.SpotifyClient s, CommandContext ctx)
        => s.Player.SkipPrevious(new PlayerSkipPreviousRequest { DeviceId = DeviceId(ctx, Player.State) });
}

internal sealed class PlayNavigateRightCommand : SpotifyCommandBase
{
    public PlayNavigateRightCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.PlayNavigate.Right",
        DisplayName = "Next Track (Rotary Right)",
        Group = "Spotify Premium",
        HiddenFromMenu = true
    };
    public override ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder | ButtonTargets.SimpleButton;
    protected override Task Run(SpotifyAPI.Web.SpotifyClient s, CommandContext ctx)
        => s.Player.SkipNext(new PlayerSkipNextRequest { DeviceId = DeviceId(ctx, Player.State) });
}

internal sealed class PlayAndNavigateAdjustment : SpotifyCommandBase, IAdjustmentCommand
{
    public PlayAndNavigateAdjustment(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.PlayAndNavigate",
        DisplayName = "Track Navigation (Adjustment)",
        Group = "Spotify Premium",
        HiddenFromMenu = true
    };
    public override ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder;
    protected override Task Run(SpotifyAPI.Web.SpotifyClient s, CommandContext ctx) => Task.CompletedTask;

    public async Task ApplyAdjustment(CommandContext ctx, int ticks)
    {
        var s = await Client.GetClientAsync(); if (s == null) return;
        if (ticks > 0)
            await s.Player.SkipNext(new PlayerSkipNextRequest { DeviceId = Player.State.DeviceId });
        else if (ticks < 0)
            await s.Player.SkipPrevious(new PlayerSkipPreviousRequest { DeviceId = Player.State.DeviceId });
    }

    public async Task ApplyReset(CommandContext ctx)
    {
        var s = await Client.GetClientAsync(); if (s == null) return;
        if (Player.State.IsPlaying)
            await s.Player.PausePlayback(new PlayerPausePlaybackRequest { DeviceId = Player.State.DeviceId });
        else
            await s.Player.ResumePlayback(new PlayerResumePlaybackRequest { DeviceId = Player.State.DeviceId });
    }

    public string? GetValueText(CommandContext ctx) => Player.State.TrackName;
}
