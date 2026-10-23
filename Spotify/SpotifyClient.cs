using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;

namespace LoupixDeck.Plugin.SpotifyPremium.Spotify;

/// <summary>
/// Thin facade around <see cref="SpotifyAPI.Web.SpotifyClient"/> that owns
/// token refresh. All Spotify API calls in this plugin should go through
/// <see cref="GetClientAsync"/> so a near-expiry access token is refreshed
/// transparently before the call.
/// </summary>
public sealed class SpotifyClientProvider : IDisposable
{
    private readonly IPluginHost _host;
    private readonly TokenStore _tokenStore;
    private readonly SpotifyAuth _auth;
    private readonly Func<string?> _clientIdAccessor;
    private readonly Func<string?> _clientSecretAccessor;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private SpotifyAPI.Web.SpotifyClient? _cached;
    private IAPIConnector? _cachedConnector;
    private DateTime _cachedExpiryUtc;

    // Set from Retry-After on a 429. Spotify can block an app for hours;
    // further requests during the block only keep it busy.
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);
    private readonly object _rateLimitGate = new();
    private DateTime _rateLimitedUntilUtc = DateTime.MinValue;
    // Cancels the timers that report the end of a block, so an unloaded
    // plugin does not call into the host hours later.
    private readonly CancellationTokenSource _cts = new();

    public SpotifyClientProvider(
        IPluginHost host,
        TokenStore tokenStore,
        SpotifyAuth auth,
        Func<string?> clientIdAccessor,
        Func<string?> clientSecretAccessor)
    {
        _host = host;
        _tokenStore = tokenStore;
        _auth = auth;
        _clientIdAccessor = clientIdAccessor;
        _clientSecretAccessor = clientSecretAccessor;
    }

    public bool IsAuthorized => _tokenStore.HasToken;

    /// <summary>When Spotify's rate limit block ends, or null when there is none.</summary>
    public DateTime? RateLimitedUntilUtc
    {
        get
        {
            lock (_rateLimitGate)
                return DateTime.UtcNow < _rateLimitedUntilUtc ? _rateLimitedUntilUtc : null;
        }
    }

    /// <summary>Raised when a rate limit block starts, gets longer or ends.</summary>
    public event Action? RateLimitChanged;

    /// <summary>
    /// Records a 429 so no further requests go out until its Retry-After has
    /// passed. Logs only when the block starts or gets longer.
    /// </summary>
    public void NoteRateLimit(APITooManyRequestsException ex)
    {
        var wait = ex.RetryAfter > TimeSpan.Zero ? ex.RetryAfter : DefaultRetryAfter;
        var until = DateTime.UtcNow + wait;

        lock (_rateLimitGate)
        {
            if (until <= _rateLimitedUntilUtc) return;
            _rateLimitedUntilUtc = until;
        }

        _host.Logger.Warn($"Spotify rate limit: no requests until {until.ToLocalTime():HH:mm:ss} (Retry-After {wait.TotalSeconds:0} s).");
        RaiseRateLimitChanged();

        // A longer block set in the meantime makes this check a no-op; its own
        // timer reports the end.
        _ = Task.Delay(wait + TimeSpan.FromSeconds(1), _cts.Token).ContinueWith(_ =>
        {
            if (RateLimitedUntilUtc == null) RaiseRateLimitChanged();
        }, _cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    // Cancel only: a request still in flight may call NoteRateLimit after
    // this, and a disposed source would throw on its token.
    public void Dispose() => _cts.Cancel();

    private void RaiseRateLimitChanged()
    {
        try { RateLimitChanged?.Invoke(); }
        catch (Exception ex) { _host.Logger.Error("RateLimitChanged handler error", ex); }
    }

    /// <summary>
    /// The client for the stored token, or null when not connected or while
    /// Spotify's rate limit block lasts (see <see cref="RateLimitedUntilUtc"/>).
    /// </summary>
    public async Task<SpotifyAPI.Web.SpotifyClient?> GetClientAsync(CancellationToken ct = default)
    {
        if (RateLimitedUntilUtc != null) return null;

        var token = _tokenStore.Load();
        if (token == null) return null;

        // Refresh proactively when there's less than a minute left, so a call
        // started just before expiry doesn't fail mid-flight.
        if (DateTime.UtcNow >= token.ExpiresAtUtc.AddSeconds(-60))
        {
            await _refreshGate.WaitAsync(ct);
            try
            {
                token = _tokenStore.Load();
                if (token != null && DateTime.UtcNow >= token.ExpiresAtUtc.AddSeconds(-60))
                {
                    var clientId = _clientIdAccessor();
                    var clientSecret = _clientSecretAccessor();
                    if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
                    {
                        _host.Logger.Warn("Token refresh skipped: Client ID/Secret not configured.");
                        return null;
                    }

                    var refreshed = await _auth.RefreshAsync(clientId, clientSecret, token.RefreshToken);
                    if (refreshed == null) return null;
                    token = refreshed;
                    _cached = null;
                }
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        if (token == null) return null;

        if (_cached != null && token.ExpiresAtUtc == _cachedExpiryUtc)
            return _cached;

        // Keep the connector so callers can reach endpoints the typed clients
        // get wrong (see GetConnectorAsync).
        var config = SpotifyClientConfig.CreateDefault(token.AccessToken);
        _cachedConnector = config.BuildAPIConnector();
        _cached = new SpotifyAPI.Web.SpotifyClient(config.WithAPIConnector(_cachedConnector));
        _cachedExpiryUtc = token.ExpiresAtUtc;
        return _cached;
    }

    /// <summary>
    /// Raw connector sharing the token of <see cref="GetClientAsync"/>, for
    /// requests the typed clients build incorrectly.
    /// </summary>
    public async Task<IAPIConnector?> GetConnectorAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        return client == null ? null : _cachedConnector;
    }

    public void Invalidate()
    {
        _cached = null;
        _cachedConnector = null;
        _cachedExpiryUtc = DateTime.MinValue;
    }
}
