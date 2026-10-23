using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Spotify;

/// <summary>
/// Holds the latest snapshot of Spotify's <c>/me/player</c>. Commands consult
/// <see cref="State"/> directly when rendering text or making toggle decisions,
/// instead of each issuing their own API call.
///
/// Polls in the background only while a button that shows player state is on
/// screen (see <see cref="MarkDisplayed"/>) or a stateful button is bound to
/// such a command, which covers the LED buttons the host never draws. A bound
/// button counts wherever it is: the host reports touch buttons on every page
/// of the active workspace and LED buttons of every profile, shown or not;
/// it has no API that tells them apart, so this is accepted. One poll
/// serves every button, at the shortest interval any of them needs. Every Spotify request counts
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
    // The like state is not part of /me/player: it is checked once per track.
    // A like made in the Spotify app during the track shows from the next one.
    private string _likedTrackUri = string.Empty;
    private bool _liked;
    // A track whose like check failed is asked again only after a pause, so a
    // check that keeps failing does not add a request to every poll.
    private static readonly TimeSpan LikedRetryInterval = TimeSpan.FromSeconds(60);
    private string _likedFailedUri = string.Empty;
    private DateTime _likedRetryUtc = DateTime.MinValue;

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
    // Commands whose bound stateful buttons need polling, with their interval;
    // the host's button list is read every BoundCheckInterval, which costs no
    // Spotify request.
    private static readonly TimeSpan BoundCheckInterval = TimeSpan.FromSeconds(30);
    private readonly IReadOnlyDictionary<string, TimeSpan> _stateCommands;
    private TimeSpan? _boundInterval;
    private DateTime _boundCheckedUtc = DateTime.MinValue;
    // Spaces polls out, failed ones included.
    private DateTime _lastPollAttemptUtc = DateTime.MinValue;
    // When State last matched Spotify; a failed poll leaves it unchanged.
    private DateTime _lastPollSuccessUtc = DateTime.MinValue;
    private static readonly TimeSpan LocalTrustWindow = TimeSpan.FromSeconds(5);
    // Track position lives outside the snapshot: it changes on every poll and
    // would otherwise fire Changed (and redraw every button) each time.
    private readonly object _progressGate = new();
    private int _progressMs;
    private int _durationMs;
    private DateTime _progressAtUtc = DateTime.MinValue;
    // A track change shows on screen as soon as Spotify reports it: once the
    // estimated position passes the end of the track, and for a few seconds
    // after a skip or a new playlist from the deck (Spotify reports the new
    // track only after a moment). Both only while something is on screen.
    private static readonly TimeSpan TrackEndRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TrackChangeWindow = TimeSpan.FromSeconds(5);
    private string _changingFromTrackId = string.Empty;
    private DateTime _trackChangeUntilUtc = DateTime.MinValue;

    /// <param name="stateCommands">Commands that show player state on a stateful
    /// button, with how often that state needs refreshing.</param>
    public PlayerStateCache(SpotifyClientProvider clientProvider, IPluginHost host,
        IReadOnlyDictionary<string, TimeSpan> stateCommands)
    {
        _clientProvider = clientProvider;
        _host = host;
        _stateCommands = stateCommands;
    }

    public PlayerSnapshot State { get; private set; } = PlayerSnapshot.Empty;

    public event Action<PlayerSnapshot>? Changed;

    /// <summary>
    /// Raised after every successful poll, changed or not. Dial values are
    /// redrawn on it: the track position moves without a change to the
    /// snapshot, and a redraw is what tells the cache a dial is still bound.
    /// </summary>
    public event Action? Polled;

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

    /// <summary>
    /// The shortest poll interval a display on screen or a bound stateful
    /// button asks for, or null when nothing needs polling.
    /// </summary>
    private TimeSpan? DemandedInterval()
    {
        var cutoff = DateTime.UtcNow - DemandTimeout;
        lock (_demand)
        {
            TimeSpan? shortest = _boundInterval;
            foreach (var (interval, markedUtc) in _demand)
            {
                if (markedUtc >= cutoff && (shortest == null || interval < shortest))
                    shortest = interval;
            }
            return shortest;
        }
    }

    /// <summary>Re-reads which state commands have a stateful button bound.</summary>
    private void CheckBoundButtons()
    {
        if (DateTime.UtcNow - _boundCheckedUtc < BoundCheckInterval) return;
        _boundCheckedUtc = DateTime.UtcNow;

        TimeSpan? shortest = null;
        var bound = new List<string>();
        foreach (var (commandName, interval) in _stateCommands)
        {
            try
            {
                if (_host.GetButtonStates(commandName).Count == 0) continue;
                bound.Add(commandName);
                if (shortest == null || interval < shortest)
                    shortest = interval;
            }
            catch (Exception ex)
            {
                _host.Logger.Warn($"Reading the buttons bound to {commandName} failed: {ex.Message}");
            }
        }

        TimeSpan? previous;
        lock (_demand)
        {
            previous = _boundInterval;
            _boundInterval = shortest;
        }

        if (shortest != previous)
        {
            _host.Logger.Info(shortest is { } every
                ? $"Polling Spotify every {every.TotalSeconds:0} s for the stateful buttons bound to {string.Join(", ", bound)}."
                : "No stateful Spotify button is bound; polling only while a Spotify display is on screen.");
        }
    }

    /// <summary>Whether <see cref="State"/> was polled within the last few seconds.</summary>
    public bool IsFresh => DateTime.UtcNow - _lastPollSuccessUtc < FreshFor;

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
            LastLocalSeekUtc = _progressAtUtc;
        }
    }

    /// <summary>
    /// Called after a command that moves to another track (skip, a new
    /// playlist, album or Liked Songs), so the next few polls come every
    /// second until Spotify reports a different track.
    /// </summary>
    public void ExpectTrackChange()
    {
        lock (_progressGate)
        {
            _changingFromTrackId = State.TrackId;
            _trackChangeUntilUtc = DateTime.UtcNow + TrackChangeWindow;
        }
    }

    private bool TrackChangePending()
    {
        lock (_progressGate)
            return DateTime.UtcNow < _trackChangeUntilUtc && State.TrackId == _changingFromTrackId;
    }

    /// <summary>Whether the track should have finished by now, going by the last polled position.</summary>
    private bool TrackEnded()
    {
        lock (_progressGate)
        {
            if (!State.IsPlaying || _durationMs <= 0) return false;
            var position = _progressMs + (DateTime.UtcNow - _progressAtUtc).TotalMilliseconds;
            return position >= _durationMs + 500;
        }
    }

    /// <summary>When a seek was last requested from the deck, so a display can tell scrubbing from playback.</summary>
    public DateTime LastLocalSeekUtc { get; private set; } = DateTime.MinValue;

    private async Task LoopAsync(CancellationToken ct)
    {
        // Small grace period before first poll so Initialize() can finish.
        try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); }
        catch (OperationCanceledException) { return; }

        // The tick only checks whether a poll is due; it sends nothing itself.
        while (!ct.IsCancellationRequested)
        {
            CheckBoundButtons();
            if (PollDue()) await PollOnceAsync(ct);
            try { await Task.Delay(LoopTick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private bool PollDue()
    {
        if (DemandedInterval() is not { } interval) return false;

        // Every loop tick until the new track shows, then the normal interval.
        if (TrackChangePending()) return true;
        if (TrackEnded() && DateTime.UtcNow - _lastPollAttemptUtc >= TrackEndRetry) return true;

        if (string.IsNullOrEmpty(State.DeviceId) && interval < IdlePollInterval)
            interval = IdlePollInterval;

        return DateTime.UtcNow - _lastPollAttemptUtc >= interval;
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        // Keep the last known state while Spotify blocks requests; an empty
        // snapshot would claim nothing is playing.
        if (_clientProvider.RateLimitedUntilUtc != null) return;

        // Counted from the attempt, so a failing poll is not retried at once.
        _lastPollAttemptUtc = DateTime.UtcNow;

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
                _lastPollSuccessUtc = DateTime.UtcNow;
                RaisePolled();
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
                AlbumImageUrl = CoverUrl(track),
                DeviceId = playback.Device?.Id ?? string.Empty,
                DeviceName = playback.Device?.Name ?? string.Empty,
                VolumePercent = volume,
                IsLiked = await ResolveLikedAsync(client, track?.Uri ?? string.Empty)
            };

            Update(snap);
            _lastPollSuccessUtc = DateTime.UtcNow;
            RaisePolled();
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
    /// The cover to show: the smallest one still 300 px wide, which is sharp on
    /// the 240 px CT wheel, else the largest there is.
    /// </summary>
    private static string CoverUrl(FullTrack? track)
    {
        var images = track?.Album?.Images;
        if (images is not { Count: > 0 }) return string.Empty;
        var image = images.Where(i => i.Width >= 300).MinBy(i => i.Width)
                    ?? images.MaxBy(i => i.Width);
        return image?.Url ?? string.Empty;
    }

    /// <summary>
    /// Whether <paramref name="trackUri"/> is in the user's Liked Songs. Asks
    /// Spotify once per track; a failed check is tried again after
    /// <see cref="LikedRetryInterval"/>. A 429 is not caught here: it ends the
    /// poll in <see cref="PollOnceAsync"/>, so the last known state stays.
    /// </summary>
    private async Task<bool> ResolveLikedAsync(SpotifyAPI.Web.SpotifyClient client, string trackUri)
    {
        if (string.IsNullOrEmpty(trackUri)) return false;

        if (trackUri == _likedTrackUri) return _liked;

        // Shown as "not liked" until a check succeeds.
        if (trackUri == _likedFailedUri && DateTime.UtcNow < _likedRetryUtc) return false;

        try
        {
            var saved = await client.Library.CheckItems(new LibraryCheckItemsRequest(new[] { trackUri }));
            _liked = saved is { Count: > 0 } && saved[0];
            _likedTrackUri = trackUri;
            _likedFailedUri = string.Empty;
            return _liked;
        }
        catch (APIException ex) when (ex is not APITooManyRequestsException)
        {
            // Logged once per track, not on every retry.
            if (trackUri != _likedFailedUri)
                _host.Logger.Warn($"Like state check failed: {ex.Message}");
            _likedFailedUri = trackUri;
            _likedRetryUtc = DateTime.UtcNow + LikedRetryInterval;
            return false;
        }
    }

    private void RaisePolled()
    {
        try { Polled?.Invoke(); }
        catch (Exception ex) { _host.Logger.Error("PlayerStateCache.Polled handler error", ex); }
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
    public string AlbumImageUrl { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public int VolumePercent { get; init; }
    public bool IsLiked { get; init; }

    public static readonly PlayerSnapshot Empty = new();
}
