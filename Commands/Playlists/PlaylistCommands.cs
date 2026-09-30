using LoupixDeck.Plugin.SpotifyPremium.Commands.Common;
using LoupixDeck.Plugin.SpotifyPremium.Folders;
using LoupixDeck.Plugin.SpotifyPremium.Spotify;
using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Playlists;

/// <summary>
/// Starts playback of the playlist whose ID is passed as a parameter. The
/// plugin's <see cref="IMenuContributor"/> bakes every user playlist into a
/// submenu so the user doesn't have to type IDs.
/// </summary>
internal sealed class StartPlaylistCommand : IPluginCommand
{
    private readonly SpotifyClientProvider _client;
    private readonly IPluginLogger _logger;

    public StartPlaylistCommand(SpotifyClientProvider client, IPluginLogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.StartPlaylist",
        DisplayName = "Start Playlist",
        Group = "Spotify Premium",
        Icon = "\U000F040A",
        Description = "Start playback of a playlist",
        ParameterTemplate = "({PlaylistId})",
        Parameters = [new CommandParameter("PlaylistId", typeof(string))],
        HiddenFromMenu = true
    };

    public ButtonTargets SupportedTargets => ButtonTargets.All;

    public async Task Execute(CommandContext ctx)
    {
        if (ctx.Parameters.Length == 0) return;

        try
        {
            var spotify = await _client.GetClientAsync();
            if (spotify == null) return;

            await spotify.Player.ResumePlayback(new PlayerResumePlaybackRequest
            {
                ContextUri = $"spotify:playlist:{ctx.Parameters[0]}"
            });
        }
        catch (Exception ex)
        {
            _logger.Warn($"StartPlaylist failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Opens the touch-screen device-selector folder. Bindable to any button.
/// </summary>
internal sealed class OpenDeviceSelectorCommand : IPluginCommand
{
    private readonly SpotifyClientProvider _client;
    private readonly PlayerStateCache _player;

    public OpenDeviceSelectorCommand(SpotifyClientProvider client, PlayerStateCache player)
    {
        _client = client;
        _player = player;
    }

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.OpenDeviceSelector",
        DisplayName = "Open Device Selector",
        Group = "Spotify Premium",
        Icon = "\U000F075A",
        Description = "Pick a Spotify Connect device",
        HiddenFromMenu = true
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        var folder = new DeviceSelectorFolderProvider(_client, _player, ctx.Host.Logger);
        ctx.Host.OpenFolder(folder);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Removes every occurrence of the currently playing track from the playlist
/// whose ID is passed as a parameter. Counterpart of Add Track to Playlist;
/// the plugin's <see cref="IMenuContributor"/> bakes one entry per playlist.
/// </summary>
internal sealed class RemoveFromPlaylistCommand : SpotifyCommandBase
{
    public RemoveFromPlaylistCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.RemoveFromPlaylist",
        DisplayName = "Remove Track from Playlist",
        Group = "Spotify Premium",
        Icon = "\U000F0376",
        Description = "Remove the current track from a playlist",
        ParameterTemplate = "({PlaylistId})",
        Parameters = [new CommandParameter("PlaylistId", typeof(string))],
        HiddenFromMenu = true
    };

    protected override async Task Run(SpotifyAPI.Web.SpotifyClient spotify, CommandContext ctx)
    {
        if (ctx.Parameters.Length == 0) return;
        var playlistId = ctx.Parameters[0];
        var trackUri = Player.State.TrackUri;
        if (string.IsNullOrEmpty(playlistId) || string.IsNullOrEmpty(trackUri)) return;

        await spotify.Playlists.RemovePlaylistItems(playlistId, new PlaylistRemoveItemsRequestV2
        {
            Items = [new PlaylistRemoveItemsRequestV2.Item { Uri = trackUri }]
        });
    }
}

/// <summary>
/// Starts playback of an album. The parameter accepts an album ID, a
/// <c>spotify:album:</c> URI or an <c>open.spotify.com</c> share link; the
/// plugin's <see cref="IMenuContributor"/> bakes the user's saved albums.
/// </summary>
internal sealed class StartAlbumCommand : SpotifyCommandBase
{
    public StartAlbumCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.StartAlbum",
        DisplayName = "Start Album",
        Group = "Spotify Premium",
        Icon = "\U000F0025",
        Description = "Start playback of an album (ID, URI or share link)",
        ParameterTemplate = "({Album})",
        Parameters = [new CommandParameter("Album", typeof(string))],
        HiddenFromMenu = true
    };

    protected override async Task Run(SpotifyAPI.Web.SpotifyClient spotify, CommandContext ctx)
    {
        var albumUri = SpotifyUri.Normalize(ctx.Parameters.FirstOrDefault(), "album");
        if (albumUri == null)
        {
            Logger.Warn($"{Descriptor.CommandName}: '{ctx.Parameters.FirstOrDefault()}' is not an album ID, URI or link.");
            return;
        }

        await spotify.Player.ResumePlayback(new PlayerResumePlaybackRequest
        {
            ContextUri = albumUri,
            DeviceId = DeviceId(ctx, Player.State)
        });
    }
}
