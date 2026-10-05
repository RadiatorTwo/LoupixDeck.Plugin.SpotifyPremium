using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Common;

/// <summary>
/// Icons and the icon-with-caption layout the commands create on a touch button (SDK 1.27
/// <see cref="CommandDescriptor.ButtonLayout"/>). One constant per role, so a command and its
/// layout cannot drift apart.
/// </summary>
internal static class SpotifyIcons
{
    // Material Design Icons code points — checked against the host's MDI 7.4 catalog.
    // MDI dropped its brand icons, so there is no Spotify logo; a music note stands for the plugin.
    public const string Plugin = "\U000F075A";             // mdi-music
    public const string Connect = "\U000F0342";            // mdi-login
    public const string PlayPause = "\U000F040E";          // mdi-play-pause
    public const string Next = "\U000F04AD";               // mdi-skip-next
    public const string Previous = "\U000F04AE";           // mdi-skip-previous
    public const string TrackNavigation = "\U000F04E1";    // mdi-swap-horizontal
    public const string Shuffle = "\U000F049D";            // mdi-shuffle
    public const string Repeat = "\U000F0456";             // mdi-repeat
    public const string SeekForward = "\U000F0211";        // mdi-fast-forward
    public const string SeekBackward = "\U000F045F";       // mdi-rewind
    public const string Seek = "\U000F0996";               // mdi-progress-clock
    public const string AddToQueue = "\U000F0DDE";         // mdi-music-note-plus
    public const string Like = "\U000F02D1";               // mdi-heart
    public const string LikedSongs = "\U000F02D2";         // mdi-heart-box
    public const string AddToPlaylist = "\U000F0412";      // mdi-playlist-plus
    public const string RemoveFromPlaylist = "\U000F0413"; // mdi-playlist-remove
    public const string Playlist = "\U000F0411";           // mdi-playlist-play
    public const string Album = "\U000F0025";              // mdi-album
    public const string Devices = "\U000F071F";            // mdi-speaker-wireless
    public const string Mute = "\U000F0581";               // mdi-volume-off
    public const string Unmute = "\U000F057E";             // mdi-volume-high
    public const string ToggleMute = "\U000F075F";         // mdi-volume-mute
    public const string Volume = "\U000F0580";             // mdi-volume-medium
    public const string VolumeUp = "\U000F075D";           // mdi-volume-plus
    public const string VolumeDown = "\U000F075E";         // mdi-volume-minus
    public const string VolumeDial = "\U000F057E";         // mdi-volume-high

    // Symbol names for IRenderCanvas.DrawSymbol (the stateful commands draw their own icon).
    public const string PlaySymbol = "play";
    public const string PauseSymbol = "pause";
    public const string VolumeSymbol = "volume-high";
    public const string VolumeOffSymbol = "volume-off";
    public const string HeartSymbol = "heart";
    public const string HeartOutlineSymbol = "heart-outline";
    public const string ShuffleSymbol = "shuffle";
    public const string ShuffleOffSymbol = "shuffle-disabled";
    public const string RepeatSymbol = "repeat";
    public const string RepeatOffSymbol = "repeat-off";
    public const string RepeatOnceSymbol = "repeat-once";

    // Pixel values for a 90 px key; the host scales them onto the key actually being written.
    internal const double IconScale = 0.5;
    internal const int IconOffsetY = -9;
    internal const int CaptionSize = 11;
    internal const int CaptionOffsetY = 27;
    internal const int CaptionBoxWidth = 88;
    internal const int CaptionBoxHeight = 22;

    /// <summary>Set to the host's translator in Initialize, before the commands are created.</summary>
    internal static Func<string, string> Translate { get; set; } = static english => english;

    /// <summary>For commands that draw the whole key per state: no static layers underneath.</summary>
    public static ButtonLayoutDescriptor DrawnByCommand { get; } = new() { Mode = ButtonLayoutMode.None };

    /// <summary>
    /// The icon with a short caption below it. A display command's runtime text replaces the
    /// caption, so the caption is what the key shows before the first update.
    /// </summary>
    public static ButtonLayoutDescriptor IconWithCaption(string glyph, string caption) => new()
    {
        Mode = ButtonLayoutMode.Custom,
        Layers =
        [
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Symbol,
                Glyph = glyph,
                IconScale = IconScale,
                OffsetY = IconOffsetY
            },
            new ButtonLayerDescriptor
            {
                Kind = ButtonLayerKind.Text,
                Text = Translate(caption),
                TextSize = CaptionSize,
                OffsetY = CaptionOffsetY,
                BoxWidth = CaptionBoxWidth,
                BoxHeight = CaptionBoxHeight
            }
        ]
    };
}
