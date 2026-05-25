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
        Group = "Spotify Premium · Playlists",
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
        Group = "Spotify Premium"
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public Task Execute(CommandContext ctx)
    {
        var folder = new DeviceSelectorFolderProvider(_client, _player, ctx.Host.Logger);
        ctx.Host.OpenFolder(folder);
        return Task.CompletedTask;
    }
}
