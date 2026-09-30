using LoupixDeck.Plugin.SpotifyPremium.Commands.Common;
using LoupixDeck.Plugin.SpotifyPremium.Spotify;
using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Playback;

/// <summary>
/// Adds a track or episode to the playback queue. The parameter accepts an
/// ID, a Spotify URI or an <c>open.spotify.com</c> share link; a bare ID is
/// treated as a track.
/// </summary>
internal sealed class AddToQueueCommand : SpotifyCommandBase
{
    public AddToQueueCommand(SpotifyClientProvider c, PlayerStateCache p, IPluginLogger l) : base(c, p, l) { }

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SpotifyPremium.AddToQueue",
        DisplayName = "Add to Queue",
        Group = "Spotify Premium",
        Icon = "\U000F0412",
        Description = "Add a track or episode (ID, URI or share link) to the queue",
        ParameterTemplate = "({Track})",
        Parameters = [new CommandParameter("Track", typeof(string))],
        HiddenFromMenu = true
    };

    protected override async Task Run(SpotifyAPI.Web.SpotifyClient spotify, CommandContext ctx)
    {
        var input = ctx.Parameters.FirstOrDefault();
        var uri = SpotifyUri.Normalize(input, "track") ?? SpotifyUri.Normalize(input, "episode");
        if (uri == null)
        {
            Logger.Warn($"{Descriptor.CommandName}: '{input}' is not a track or episode ID, URI or link.");
            return;
        }

        await spotify.Player.AddToQueue(new PlayerAddToQueueRequest(uri)
        {
            DeviceId = DeviceId(ctx, Player.State)
        });
    }
}
