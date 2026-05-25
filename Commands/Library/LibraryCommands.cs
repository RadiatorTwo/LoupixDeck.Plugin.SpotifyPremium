using LoupixDeck.Plugin.SpotifyPremium.Commands.Common;
using LoupixDeck.Plugin.SpotifyPremium.Spotify;
using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Library;

internal sealed class ToggleLikeCommand : SpotifyCommandBase, IDisplayCommand
{
    public ToggleLikeCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.ToggleLike",
        DisplayName = "Toggle Like",
        Group = "Spotify Premium",
        HiddenFromMenu = true
    };

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(5);

    // The cached snapshot doesn't hold the like state — only render a stable
    // glyph that reflects "we'd toggle the current track". Refreshed when the
    // user actually presses (via PlayerStateCache.RefreshNowAsync).
    public string GetText(CommandContext ctx)
        => string.IsNullOrEmpty(Player.State.TrackId) ? "♥" : "♥";

    protected override async Task Run(SpotifyAPI.Web.SpotifyClient spotify, CommandContext ctx)
    {
        var trackId = Player.State.TrackId;
        if (string.IsNullOrEmpty(trackId))
        {
            Logger.Info("ToggleLike: no track playing.");
            return;
        }

        var saved = await spotify.Library.CheckTracks(new LibraryCheckTracksRequest(new[] { trackId }));
        if (saved is { Count: > 0 } && saved[0])
            await spotify.Library.RemoveTracks(new LibraryRemoveTracksRequest(new[] { trackId }));
        else
            await spotify.Library.SaveTracks(new LibrarySaveTracksRequest(new[] { trackId }));
    }
}

/// <summary>
/// Adds the currently playing track to the playlist whose ID is passed as a
/// parameter. The plugin's <see cref="IMenuContributor"/> implementation bakes
/// every user playlist into a submenu entry.
/// </summary>
internal sealed class SaveToPlaylistCommand : SpotifyCommandBase
{
    public SaveToPlaylistCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.SaveToPlaylist",
        DisplayName = "Add Track to Playlist",
        Group = "Spotify Premium",
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

        await spotify.Playlists.AddItems(playlistId, new PlaylistAddItemsRequest(new[] { trackUri }));
    }
}
