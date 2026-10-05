using LoupixDeck.Plugin.SpotifyPremium.Spotify;
using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Commands.Common;

/// <summary>
/// Shared plumbing for every command that hits the Spotify API: pulls a
/// refresh-aware <see cref="SpotifyAPI.Web.SpotifyClient"/> and wraps the call
/// with try/catch + logger plumbing, so individual commands stay one method
/// each.
/// </summary>
internal abstract class SpotifyCommandBase : IPluginCommand
{
    protected readonly SpotifyClientProvider Client;
    protected readonly PlayerStateCache Player;
    protected readonly IPluginLogger Logger;

    protected SpotifyCommandBase(SpotifyClientProvider client, PlayerStateCache player, IPluginLogger logger)
    {
        Client = client;
        Player = player;
        Logger = logger;
    }

    public abstract CommandDescriptor Descriptor { get; }
    public virtual ButtonTargets SupportedTargets => ButtonTargets.All;

    public virtual async Task Execute(CommandContext ctx)
    {
        try
        {
            var spotify = await Client.GetClientAsync();
            if (spotify == null)
            {
                Logger.Warn($"{Descriptor.CommandName}: not connected — login required.");
                return;
            }

            await Run(spotify, ctx);
            // Refresh cached state immediately so display commands reflect the change.
            await Player.RefreshNowAsync();
        }
        catch (APIException ex)
        {
            Logger.Warn($"{Descriptor.CommandName}: Spotify API returned {ex.Response?.StatusCode} {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error($"{Descriptor.CommandName}: unexpected error", ex);
        }
    }

    protected abstract Task Run(SpotifyAPI.Web.SpotifyClient spotify, CommandContext ctx);

    /// <summary>
    /// Pauses while playing, resumes otherwise, and takes the new state into
    /// the cache right away so play/pause buttons switch without waiting for
    /// Spotify to report it.
    /// </summary>
    protected async Task TogglePlaybackAsync(SpotifyAPI.Web.SpotifyClient spotify, string? deviceId)
    {
        if (Player.State.IsPlaying)
        {
            await spotify.Player.PausePlayback(new PlayerPausePlaybackRequest { DeviceId = deviceId });
            Player.ApplyLocalPlaying(false);
        }
        else
        {
            await spotify.Player.ResumePlayback(new PlayerResumePlaybackRequest { DeviceId = deviceId });
            Player.ApplyLocalPlaying(true);
        }
    }

    /// <summary>Sets the volume and takes it into the cache right away.</summary>
    protected async Task SetVolumeAsync(SpotifyAPI.Web.SpotifyClient spotify, int percent, string? deviceId)
    {
        await spotify.Player.SetVolume(new PlayerVolumeRequest(percent) { DeviceId = deviceId });
        Player.ApplyLocalVolume(percent);
    }

    protected static string? DeviceId(CommandContext ctx, PlayerSnapshot state)
    {
        return string.IsNullOrEmpty(state.DeviceId) ? null : state.DeviceId;
    }
}
