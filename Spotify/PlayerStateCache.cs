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
    // Same for play/pause: right after Pause/Resume the poll still reports
    // the previous playback state.
    private DateTime _lastLocalPlayingUtc = DateTime.MinValue;
    private DateTime _lastLocalShuffleUtc = DateTime.MinValue;
    private DateTime _lastLocalRepeatUtc = DateTime.MinValue;
    // The like state is not part of /me/player: it is checked once per track
    // and re-checked now and then, so a like made in the Spotify app shows up.
    private string _likedTrackUri = string.Empty;
    private bool _liked;
    private DateTime _likedCheckedUtc = DateTime.MinValue;
    private DateTime _lastLocalLikedUtc = DateTime.MinValue;
    private static readonly TimeSpan LikedRecheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LocalTrustWindow = TimeSpan.FromSeconds(5);
    // Track position lives outside the snapshot: it changes on every poll and
    // would otherwise fire Changed (and redraw every button) each time.
    private readonly object _progressGate = new();
    private int _progressMs;
    private int _durationMs;
    private DateTime _progressAtUtc = DateTime.MinValue;

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

    /// <summary>Pushes a locally-known shuffle state, see <see cref="ApplyLocalPlaying"/>.</summary>
    public void ApplyLocalShuffle(bool enabled)
    {
        _lastLocalShuffleUtc = DateTime.UtcNow;
        Update(State with { ShuffleEnabled = enabled });
    }

    /// <summary>Pushes a locally-known repeat state ("off", "context" or "track"), see <see cref="ApplyLocalPlaying"/>.</summary>
    public void ApplyLocalRepeat(string repeatState)
    {
        _lastLocalRepeatUtc = DateTime.UtcNow;
        Update(State with { RepeatState = repeatState });
    }

    /// <summary>
    /// Pushes a locally-known like state for the current track into the
    /// snapshot. Call it after a successful like/unlike call.
    /// </summary>
    public void ApplyLocalLiked(bool isLiked)
    {
        _likedTrackUri = State.TrackUri;
        _liked = isLiked;
        _likedCheckedUtc = DateTime.UtcNow;
        _lastLocalLikedUtc = DateTime.UtcNow;
        Update(State with { IsLiked = isLiked });
    }

    /// <summary>
    /// Pushes a locally-known playback state into the snapshot, the play/pause
    /// counterpart of <see cref="ApplyLocalVolume"/>. Call it after a successful
    /// Pause/Resume call.
    /// </summary>
    public void ApplyLocalPlaying(bool isPlaying)
    {
        _lastLocalPlayingUtc = DateTime.UtcNow;
        if (isPlaying != State.IsPlaying)
        {
            // Keep the position estimate continuous across the switch.
            lock (_progressGate)
            {
                if (State.IsPlaying)
                    _progressMs += (int)(DateTime.UtcNow - _progressAtUtc).TotalMilliseconds;
                _progressAtUtc = DateTime.UtcNow;
            }
        }

        Update(State with { IsPlaying = isPlaying });
    }

    /// <summary>
    /// Estimated playback position in milliseconds: the last polled progress
    /// plus the time elapsed since, while playing.
    /// </summary>
    public int EstimatePositionMs()
    {
        lock (_progressGate)
        {
            var position = (double)_progressMs;
            if (State.IsPlaying)
                position += (DateTime.UtcNow - _progressAtUtc).TotalMilliseconds;
            return (int)Math.Clamp(position, 0, Math.Max(_durationMs, 0));
        }
    }

    /// <summary>Length of the current track in milliseconds, 0 if unknown.</summary>
    public int DurationMs
    {
        get { lock (_progressGate) return _durationMs; }
    }

    /// <summary>
    /// Records a locally requested seek so the next estimate starts from it
    /// instead of the stale polled position.
    /// </summary>
    public void ApplyLocalPosition(int positionMs)
    {
        lock (_progressGate)
        {
            _progressMs = positionMs;
            _progressAtUtc = DateTime.UtcNow;
        }
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
            lock (_progressGate)
            {
                _progressMs = playback.ProgressMs;
                _durationMs = track?.DurationMs ?? 0;
                _progressAtUtc = DateTime.UtcNow;
            }
            var polledVolume = playback.Device?.VolumePercent ?? 0;
            // Within the trust window the local copy stays authoritative —
            // prevents the polled value from snapping the rotary backwards
            // when Spotify hasn't reflected our recent SetVolume yet.
            var volume = DateTime.UtcNow - _lastLocalVolumeUtc < LocalTrustWindow
                ? State.VolumePercent
                : polledVolume;
            var isPlaying = DateTime.UtcNow - _lastLocalPlayingUtc < LocalTrustWindow
                ? State.IsPlaying
                : playback.IsPlaying;
            var shuffle = DateTime.UtcNow - _lastLocalShuffleUtc < LocalTrustWindow
                ? State.ShuffleEnabled
                : playback.ShuffleState;
            var repeat = DateTime.UtcNow - _lastLocalRepeatUtc < LocalTrustWindow
                ? State.RepeatState
                : playback.RepeatState ?? "off";

            var snap = new PlayerSnapshot
            {
                IsPlaying = isPlaying,
                ShuffleEnabled = shuffle,
                RepeatState = repeat,
                TrackId = track?.Id ?? string.Empty,
                TrackUri = track?.Uri ?? string.Empty,
                TrackName = track?.Name ?? string.Empty,
                ArtistName = track?.Artists?.FirstOrDefault()?.Name ?? string.Empty,
                DeviceId = playback.Device?.Id ?? string.Empty,
                DeviceName = playback.Device?.Name ?? string.Empty,
                VolumePercent = volume,
                IsLiked = await ResolveLikedAsync(client, track?.Uri ?? string.Empty)
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

    /// <summary>
    /// Whether <paramref name="trackUri"/> is in the user's Liked Songs. Asks
    /// Spotify only on a track change or after <see cref="LikedRecheckInterval"/>;
    /// a local toggle stays authoritative for the trust window.
    /// </summary>
    private async Task<bool> ResolveLikedAsync(SpotifyAPI.Web.SpotifyClient client, string trackUri)
    {
        if (string.IsNullOrEmpty(trackUri)) return false;

        bool sameTrack = trackUri == _likedTrackUri;
        if (sameTrack && (DateTime.UtcNow - _lastLocalLikedUtc < LocalTrustWindow
                          || DateTime.UtcNow - _likedCheckedUtc < LikedRecheckInterval))
            return _liked;

        try
        {
            var saved = await client.Library.CheckItems(new LibraryCheckItemsRequest(new[] { trackUri }));
            _liked = saved is { Count: > 0 } && saved[0];
        }
        catch (APIException ex)
        {
            _host.Logger.Warn($"Like state check failed: {ex.Message}");
            // Keep the last known value for this track, assume "not liked" for a new one.
            if (!sameTrack) _liked = false;
        }

        _likedTrackUri = trackUri;
        _likedCheckedUtc = DateTime.UtcNow;
        return _liked;
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
    public bool IsLiked { get; init; }

    public static readonly PlayerSnapshot Empty = new();
}
