using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Spotify;

/// <summary>
/// Holds the latest snapshot of Spotify's <c>/me/player</c>. Commands consult
/// <see cref="State"/> directly when rendering text or making toggle decisions,
/// instead of each issuing their own API call.
///
/// Polls in the background only while a button that shows player state is on
/// screen (see <see cref="MarkDisplayed"/>). Every Spotify request counts
/// against the developer account's daily Development Mode quota, so with only
/// plain buttons nothing is polled and a command fetches the state it needs
/// when pressed (<see cref="EnsureFreshAsync"/>).
/// Fires <see cref="Changed"/> only when a relevant field actually differs.
/// </summary>
public sealed class PlayerStateCache : IDisposable
{
    private readonly SpotifyClientProvider _clientProvider;
    private readonly IPluginHost _host;
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
    private static readonly TimeSpan LikedRecheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>Poll interval while a play/pause, mute, shuffle, repeat or volume display is on screen.</summary>
    public static readonly TimeSpan LivePollInterval = TimeSpan.FromSeconds(10);

    /// <summary>Poll interval while only track-level state (like heart, track name) is on screen.</summary>
    public static readonly TimeSpan TrackPollInterval = TimeSpan.FromSeconds(30);

    // With no active device nothing changes until the user starts Spotify.
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(30);
    // The host redraws a display button every few seconds while it is on
    // screen; demand older than this means none is shown any more.
    private static readonly TimeSpan DemandTimeout = TimeSpan.FromSeconds(15);
    // State this recent is used as is when a command is pressed.
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LoopTick = TimeSpan.FromSeconds(1);
    // When each requested poll interval was last asked for by a display.
    private readonly Dictionary<TimeSpan, DateTime> _demand = new();
    private DateTime _lastPollUtc = DateTime.MinValue;
    private static readonly TimeSpan LocalTrustWindow = TimeSpan.FromSeconds(5);
    // Track position lives outside the snapshot: it changes on every poll and
    // would otherwise fire Changed (and redraw every button) each time.
    private readonly object _progressGate = new();
    private int _progressMs;
    private int _durationMs;
    private DateTime _progressAtUtc = DateTime.MinValue;

    public PlayerStateCache(SpotifyClientProvider clientProvider, IPluginHost host)
    {
        _clientProvider = clientProvider;
        _host = host;
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
    /// Called by a display command each time the host draws it, so the cache
    /// polls while that button is on screen. <paramref name="pollInterval"/> is
    /// how often its state needs refreshing; the shortest one shown wins.
    /// </summary>
    public void MarkDisplayed(TimeSpan pollInterval)
    {
        lock (_demand)
            _demand[pollInterval] = DateTime.UtcNow;
    }

    /// <summary>Whether a button showing player state is on screen.</summary>
    public bool HasDemand => DemandedInterval() != null;

    /// <summary>The shortest poll interval a display on screen asks for, or null when none is shown.</summary>
    private TimeSpan? DemandedInterval()
    {
        var cutoff = DateTime.UtcNow - DemandTimeout;
        lock (_demand)
        {
            TimeSpan? shortest = null;
            foreach (var (interval, markedUtc) in _demand)
            {
                if (markedUtc >= cutoff && (shortest == null || interval < shortest))
                    shortest = interval;
            }
            return shortest;
        }
    }

    /// <summary>Whether <see cref="State"/> was polled within the last few seconds.</summary>
    public bool IsFresh => DateTime.UtcNow - _lastPollUtc < FreshFor;

    /// <summary>Polls now unless <see cref="State"/> is fresh. Call before acting on the state.</summary>
    public async Task EnsureFreshAsync()
    {
        if (!IsFresh) await PollOnceAsync(_cts.Token);
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

        // The tick only checks whether a poll is due; it sends nothing itself.
        while (!ct.IsCancellationRequested)
        {
            if (PollDue()) await PollOnceAsync(ct);
            try { await Task.Delay(LoopTick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private bool PollDue()
    {
        if (DemandedInterval() is not { } interval) return false;

        if (string.IsNullOrEmpty(State.DeviceId) && interval < IdlePollInterval)
            interval = IdlePollInterval;

        return DateTime.UtcNow - _lastPollUtc >= interval;
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        // Keep the last known state while Spotify blocks requests; an empty
        // snapshot would claim nothing is playing.
        if (_clientProvider.RateLimitedUntilUtc != null) return;

        // Counted from the attempt, so a failing poll is not retried at once.
        _lastPollUtc = DateTime.UtcNow;

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
        catch (APITooManyRequestsException ex)
        {
            _clientProvider.NoteRateLimit(ex);
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
        catch (APITooManyRequestsException ex)
        {
            _clientProvider.NoteRateLimit(ex);
            if (!sameTrack) _liked = false;
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
