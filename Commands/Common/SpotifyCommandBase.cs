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

    /// <summary>
    /// Whether <see cref="Run"/> reads <see cref="PlayerStateCache.State"/>
    /// (track, playback, volume). The cache is then refreshed first unless it
    /// is fresh. Commands that only act on the active device return false and
    /// cost one request.
    /// </summary>
    protected virtual bool NeedsPlayerState => true;

    /// <summary>
    /// Whether <see cref="Run"/> moves playback to another track, so the cache
    /// watches for the new one (<see cref="PlayerStateCache.ExpectTrackChange"/>).
    /// </summary>
    protected virtual bool ChangesTrack => false;

    public virtual async Task Execute(CommandContext ctx)
    {
        try
        {
            if (Client.RateLimitedUntilUtc is { } until)
            {
                Logger.Warn($"{Descriptor.CommandName}: skipped, Spotify rate limit lasts until {until.ToLocalTime():HH:mm:ss}.");
                return;
            }

            var spotify = await Client.GetClientAsync();
            if (spotify == null)
            {
                Logger.Warn($"{Descriptor.CommandName}: not connected — login required.");
                return;
            }

            if (NeedsPlayerState)
                await Player.EnsureFreshAsync();

            await Run(spotify, ctx);
            if (ChangesTrack)
                Player.ExpectTrackChange();
            // Refresh cached state so buttons on screen reflect the change; with
            // none shown the next press fetches it anyway.
            if (Player.HasDemand)
                await Player.RefreshNowAsync();
        }
        catch (APITooManyRequestsException ex)
        {
            Client.NoteRateLimit(ex);
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
    /// The player state for drawing a button or value. Tells the cache the
    /// display is on screen, so it polls every <paramref name="pollInterval"/>
    /// while the host keeps drawing it.
    /// </summary>
    protected PlayerSnapshot ShownState(TimeSpan pollInterval)
    {
        Player.MarkDisplayed(pollInterval);
        return Player.State;
    }

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

    /// <summary>
    /// The device to target, or null for Spotify's active device. A device
    /// from stale state may no longer be the one playing, so it is only used
    /// while the state is fresh.
    /// </summary>
    protected string? DeviceId(CommandContext ctx, PlayerSnapshot state)
    {
        return Player.IsFresh && !string.IsNullOrEmpty(state.DeviceId) ? state.DeviceId : null;
    }
}
