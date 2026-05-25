using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Spotify;

/// <summary>
/// Thin facade around <see cref="SpotifyAPI.Web.SpotifyClient"/> that owns
/// token refresh. All Spotify API calls in this plugin should go through
/// <see cref="GetClientAsync"/> so a near-expiry access token is refreshed
/// transparently before the call.
/// </summary>
public sealed class SpotifyClientProvider
{
    private readonly IPluginHost _host;
    private readonly TokenStore _tokenStore;
    private readonly SpotifyAuth _auth;
    private readonly Func<string?> _clientIdAccessor;
    private readonly Func<string?> _clientSecretAccessor;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private SpotifyAPI.Web.SpotifyClient? _cached;
    private DateTime _cachedExpiryUtc;

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

    public async Task<SpotifyAPI.Web.SpotifyClient?> GetClientAsync(CancellationToken ct = default)
    {
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

        _cached = new SpotifyAPI.Web.SpotifyClient(token.AccessToken);
        _cachedExpiryUtc = token.ExpiresAtUtc;
        return _cached;
    }

    public void Invalidate()
    {
        _cached = null;
        _cachedExpiryUtc = DateTime.MinValue;
    }
}
