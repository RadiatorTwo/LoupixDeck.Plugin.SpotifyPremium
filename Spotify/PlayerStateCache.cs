using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Spotify;

/// <summary>
/// Polls Spotify's <c>/me/player</c> in the background and exposes the latest
/// snapshot. Commands consult <see cref="State"/> directly when rendering text
/// or making toggle decisions, instead of each issuing their own API call —
/// avoids hammering Spotify when multiple buttons reflect the same state.
/// Fires <see cref="Changed"/> only when a relevant field actually differs.
/// </summary>
public sealed class PlayerStateCache : IDisposable
{
    private readonly SpotifyClientProvider _clientProvider;
    private readonly IPluginHost _host;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    // Local volume writes win over polled volume for a short window. After
    // SetVolume, Spotify's /me/player can still report the old value for a
    // second or two — using the polled value would snap the UI backwards.
    private DateTime _lastLocalVolumeUtc = DateTime.MinValue;
    private static readonly TimeSpan LocalVolumeTrustWindow = TimeSpan.FromSeconds(5);

    public PlayerStateCache(SpotifyClientProvider clientProvider, IPluginHost host, TimeSpan? pollInterval = null)
    {
        _clientProvider = clientProvider;
        _host = host;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
    }

    public PlayerSnapshot State { get; private set; } = PlayerSnapshot.Empty;

    public event Action<PlayerSnapshot>? Changed;

    public void Start()
    {
        if (_loopTask != null) return;
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task RefreshNowAsync()
    {
        await PollOnceAsync(_cts.Token);
    }

    /// <summary>
    /// Pushes a locally-known volume into the snapshot so display commands
    /// reflect the change immediately, without waiting for the next background
    /// poll. Used by the volume adjustment after a successful SetVolume call.
    /// </summary>
    public void ApplyLocalVolume(int percent)
    {
        _lastLocalVolumeUtc = DateTime.UtcNow;
        Update(State with { VolumePercent = Math.Clamp(percent, 0, 100) });
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // Small grace period before first poll so Initialize() can finish.
        try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct);
            try { await Task.Delay(_pollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var client = await _clientProvider.GetClientAsync(ct);
            if (client == null)
            {
                Update(PlayerSnapshot.Empty);
                return;
            }

            var playback = await client.Player.GetCurrentPlayback();
            if (playback == null)
            {
                Update(PlayerSnapshot.Empty);
                return;
            }

            var track = playback.Item as FullTrack;
            var polledVolume = playback.Device?.VolumePercent ?? 0;
            // Within the trust window the local copy stays authoritative —
            // prevents the polled value from snapping the rotary backwards
            // when Spotify hasn't reflected our recent SetVolume yet.
            var volume = DateTime.UtcNow - _lastLocalVolumeUtc < LocalVolumeTrustWindow
                ? State.VolumePercent
                : polledVolume;

            var snap = new PlayerSnapshot
            {
                IsPlaying = playback.IsPlaying,
                ShuffleEnabled = playback.ShuffleState,
                RepeatState = playback.RepeatState ?? "off",
                TrackId = track?.Id ?? string.Empty,
                TrackUri = track?.Uri ?? string.Empty,
                TrackName = track?.Name ?? string.Empty,
                ArtistName = track?.Artists?.FirstOrDefault()?.Name ?? string.Empty,
                DeviceId = playback.Device?.Id ?? string.Empty,
                DeviceName = playback.Device?.Name ?? string.Empty,
                VolumePercent = volume
            };

            Update(snap);
        }
        catch (APIException ex)
        {
            _host.Logger.Warn($"PlayerState poll failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _host.Logger.Error("PlayerState poll error", ex);
        }
    }

    private void Update(PlayerSnapshot snap)
    {
        if (snap.Equals(State)) return;
        State = snap;
        try { Changed?.Invoke(snap); }
        catch (Exception ex) { _host.Logger.Error("PlayerStateCache.Changed handler error", ex); }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}

public sealed record PlayerSnapshot
{
    public bool IsPlaying { get; init; }
    public bool ShuffleEnabled { get; init; }
    public string RepeatState { get; init; } = "off";
    public string TrackId { get; init; } = string.Empty;
    public string TrackUri { get; init; } = string.Empty;
    public string TrackName { get; init; } = string.Empty;
    public string ArtistName { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public int VolumePercent { get; init; }

    public static readonly PlayerSnapshot Empty = new();
}
