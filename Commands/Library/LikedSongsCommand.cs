using LoupixDeck.Plugin.SpotifyPremium.Commands.Common;
using LoupixDeck.Plugin.SpotifyPremium.Spotify;
using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Library;

/// <summary>
/// Starts playback of the user's Liked Songs. Spotify exposes them as the
/// <c>spotify:user:{id}:collection</c> context, so the user ID is looked up
/// once via <c>/me</c> and then reused.
/// </summary>
internal sealed class PlayLikedSongsCommand : SpotifyCommandBase
{
    private string? _userId;

    public PlayLikedSongsCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.PlayLikedSongs",
        DisplayName = "Play Liked Songs",
        Group = "Spotify Premium",
        Icon = "\U000F02D1",
        Description = "Start playback of your Liked Songs",
        HiddenFromMenu = true
    };

    protected override async Task Run(SpotifyAPI.Web.SpotifyClient spotify, CommandContext ctx)
    {
        _userId ??= (await spotify.UserProfile.Current()).Id;

        await spotify.Player.ResumePlayback(new PlayerResumePlaybackRequest
        {
            ContextUri = $"spotify:user:{_userId}:collection",
            DeviceId = DeviceId(ctx, Player.State)
        });
    }
}
