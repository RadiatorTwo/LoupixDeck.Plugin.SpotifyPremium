using LoupixDeck.Plugin.SpotifyPremium.Commands.Common;
using LoupixDeck.Plugin.SpotifyPremium.Spotify;
using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Library;

internal sealed class ToggleLikeCommand : SpotifyCommandBase, IDisplayImageCommand
{
    public const string Name = "SpotifyPremium.ToggleLike";

    public ToggleLikeCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Toggle Like",
        Group = "Spotify Premium",
        Icon = SpotifyIcons.Like,
        ButtonLayout = SpotifyIcons.DrawnByCommand,
        Description = "Like or unlike the current track",
        States = SpotifyStates.Like,
        HiddenFromMenu = true
    };

    /// <summary>Changes arrive as pushes from the player cache; polling is only a safety net.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(5);

    public static string StateOf(PlayerSnapshot state) => state.IsLiked ? SpotifyStates.Liked : SpotifyStates.NotLiked;

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas) =>
        SpotifyStates.Resolve(ctx, Name, StateOf(Player.State)) == SpotifyStates.Liked
            ? SpotifyStates.Draw(canvas, SpotifyIcons.HeartSymbol, PluginText.Tr(ctx.Host, "Liked"), SpotifyStates.Green)
            : SpotifyStates.Draw(canvas, SpotifyIcons.HeartOutlineSymbol, PluginText.Tr(ctx.Host, "Like"), SpotifyStates.Normal);

    protected override async Task Run(SpotifyAPI.Web.SpotifyClient spotify, CommandContext ctx)
    {
        // The /me/library endpoints take Spotify URIs, not bare track IDs.
        var trackUri = Player.State.TrackUri;
        if (string.IsNullOrEmpty(trackUri))
        {
            Logger.Info("ToggleLike: no track playing.");
            return;
        }

        // Library.SaveItems/RemoveItems send "uris" in the request body, but
        // PUT/DELETE /me/library expect it as a query parameter — call it directly.
        var api = await Client.GetConnectorAsync();
        if (api == null) return;
        var query = new Dictionary<string, string> { ["uris"] = trackUri };

        // Ask Spotify rather than trusting the cache: the track may have been
        // liked elsewhere since the last check.
        var saved = await spotify.Library.CheckItems(new LibraryCheckItemsRequest(new[] { trackUri }));
        bool wasLiked = saved is { Count: > 0 } && saved[0];
        if (wasLiked)
            await api.Delete(SpotifyUrls.Library(), query, null, CancellationToken.None);
        else
            await api.Put(SpotifyUrls.Library(), query, null, CancellationToken.None);

        // Only if the track is still the one we toggled.
        if (Player.State.TrackUri == trackUri)
            Player.ApplyLocalLiked(!wasLiked);
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
        Icon = SpotifyIcons.AddToPlaylist,
        ButtonLayout = SpotifyIcons.IconWithCaption(SpotifyIcons.AddToPlaylist, "Add to Playlist"),
        Description = "Add the current track to a playlist",
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

        await spotify.Playlists.AddPlaylistItems(playlistId, new PlaylistAddItemsRequest(new[] { trackUri }));
    }
}
