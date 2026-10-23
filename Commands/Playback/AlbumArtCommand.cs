using System.Globalization;
using LoupixDeck.Plugin.SpotifyPremium.Commands.Common;
using LoupixDeck.Plugin.SpotifyPremium.Spotify;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Playback;

/// <summary>
/// The current track's cover, for a touch button or a live image layer on the
/// CT wheel (behind the Seek adjustment's time and track name, for instance).
/// It can turn slowly while the track plays, like a record. Pressing it
/// toggles play/pause.
/// </summary>
internal sealed class AlbumArtCommand : SpotifyCommandBase, IDisplayImageCommand
{
    public const string Name = "SpotifyPremium.AlbumArt";
    public const int DefaultOpacity = 100;
    // How long the cover holds still after a seek; the Seek dial sends one per tick.
    private static readonly TimeSpan ScrubHold = TimeSpan.FromSeconds(1.5);

    private readonly AlbumArtCache _art;

    public AlbumArtCommand(SpotifyClientProvider c, PlayerStateCache p, AlbumArtCache art, IPluginLogger l) : base(c, p, l)
    {
        _art = art;
    }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Album Art",
        Group = "Spotify Premium",
        Icon = SpotifyIcons.Album,
        ButtonLayout = SpotifyIcons.DrawnByCommand,
        Description = "Show the current track's cover, optionally turning while it plays; press to play or pause",
        ParameterTemplate = "({Opacity},{Spin})",
        Parameters =
        [
            new CommandParameter("Opacity", typeof(int)) { DefaultValue = "100" },
            // Seconds per full turn; 0 keeps the cover still.
            new CommandParameter("Spin", typeof(int)) { DefaultValue = "0" }
        ],
        HiddenFromMenu = true
    };

    public override ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    /// <summary>Four frames a second, the fastest the host polls a wheel image layer, so a spinning cover moves smoothly enough.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(250);

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        var state = ShownState(PlayerStateCache.TrackPollInterval);
        // Nothing playing: an empty frame, so a stale cover does not stay up.
        if (string.IsNullOrEmpty(state.AlbumImageUrl)) return true;

        // A new cover still loading: keep the previous one until it arrives.
        if (_art.Get(state.AlbumImageUrl) is not { } bytes) return false;

        var opacity = Math.Clamp(IntParameter(ctx, 0, DefaultOpacity), 0, 100);
        var angle = Advance(IntParameter(ctx, 1, 0), state.IsPlaying);

        // Turned about the centre. On the round wheel the corners stay off the glass; on a
        // square key they show, which is why spinning is off unless asked for.
        canvas.PushTransform();
        canvas.Translate(canvas.Width / 2f, canvas.Height / 2f);
        canvas.Rotate(angle);
        canvas.Translate(-canvas.Width / 2f, -canvas.Height / 2f);
        canvas.DrawImage(bytes, 0, 0, canvas.Width, canvas.Height, (byte)Math.Round(opacity * 2.55), PluginColor.White);
        canvas.PopTransform();
        return true;
    }

    // Spin angle, advanced by the time that passed between frames while the cover may turn.
    private readonly object _spinGate = new();
    private double _angle;
    private DateTime _lastFrameUtc = DateTime.MinValue;

    /// <summary>
    /// The angle to draw at. With <paramref name="secondsPerTurn"/> above 0 it moves on while
    /// playing and holds while paused or for a moment after each seek, so scrubbing does not
    /// spin the cover.
    /// </summary>
    private float Advance(int secondsPerTurn, bool isPlaying)
    {
        lock (_spinGate)
        {
            var now = DateTime.UtcNow;
            var elapsed = _lastFrameUtc == DateTime.MinValue ? 0 : (now - _lastFrameUtc).TotalSeconds;
            _lastFrameUtc = now;

            bool scrubbing = now - Player.LastLocalSeekUtc < ScrubHold;
            if (secondsPerTurn > 0 && isPlaying && !scrubbing)
                _angle = (_angle + elapsed * 360.0 / secondsPerTurn) % 360.0;
            return (float)_angle;
        }
    }

    private static int IntParameter(CommandContext ctx, int index, int fallback) =>
        ctx.Parameters is { } p && p.Length > index &&
        int.TryParse(p[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    protected override Task Run(SpotifyAPI.Web.SpotifyClient s, CommandContext ctx)
        => TogglePlaybackAsync(s, DeviceId(ctx, Player.State));
}
