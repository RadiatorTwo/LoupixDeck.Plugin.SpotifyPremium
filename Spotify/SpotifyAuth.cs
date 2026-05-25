using System.Net;
using System.Text;
using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Spotify;

/// <summary>
/// Drives the Spotify OAuth Authorization Code flow: opens the user's browser
/// at Spotify's consent page, listens on a local HTTP loopback for the
/// redirect, exchanges the auth code for tokens, and persists them via
/// <see cref="TokenStore"/>. One <see cref="AuthorizeAsync"/> call per
/// connection attempt; the listener is torn down before the method returns.
/// </summary>
public sealed class SpotifyAuth
{
    public static readonly IReadOnlyList<string> RequiredScopes = new[]
    {
        Scopes.UserReadPlaybackState,
        Scopes.UserModifyPlaybackState,
        Scopes.UserReadCurrentlyPlaying,
        Scopes.Streaming,
        Scopes.PlaylistReadPrivate,
        Scopes.PlaylistReadCollaborative,
        Scopes.PlaylistModifyPublic,
        Scopes.PlaylistModifyPrivate,
        Scopes.UserLibraryRead,
        Scopes.UserLibraryModify,
        Scopes.UserReadPrivate,
        Scopes.UserReadEmail,
        Scopes.UserReadRecentlyPlayed,
        Scopes.UserTopRead
    };

    private readonly IPluginHost _host;
    private readonly TokenStore _tokenStore;

    public SpotifyAuth(IPluginHost host, TokenStore tokenStore)
    {
        _host = host;
        _tokenStore = tokenStore;
    }

    /// <summary>
    /// Returns a short status string suitable for the settings action button.
    /// On success the token is already persisted and the caller can rebuild
    /// the SpotifyClient. On failure no token is stored.
    /// </summary>
    public async Task<string> AuthorizeAsync(string clientId, string clientSecret, int port, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return "Client ID missing.";
        if (string.IsNullOrWhiteSpace(clientSecret)) return "Client Secret missing.";

        var redirectUri = $"http://127.0.0.1:{port}/callback";

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            return $"Port {port} cannot be opened: {ex.Message}";
        }

        try
        {
            var loginRequest = new LoginRequest(new Uri(redirectUri), clientId, LoginRequest.ResponseType.Code)
            {
                Scope = RequiredScopes.ToList()
            };

            if (!_host.OpenBrowser(loginRequest.ToUri().ToString()))
                return "Could not open browser.";

            // 2-minute hard timeout — the user might abandon the consent screen.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));
            if (completed != contextTask)
                return "Timed out waiting for Spotify response.";

            var context = await contextTask;
            var query = context.Request.QueryString;
            var code = query.Get("code");
            var error = query.Get("error");

            await WriteResponse(context,
                error != null
                    ? $"Spotify returned an error: {error}. You can close this window."
                    : "Spotify connection successful. You can close this window.");

            if (error != null) return $"Spotify error: {error}";
            if (string.IsNullOrEmpty(code)) return "No authorization code received.";

            var tokenResponse = await new OAuthClient().RequestToken(
                new AuthorizationCodeTokenRequest(clientId, clientSecret, code, new Uri(redirectUri)));

            _tokenStore.Save(new TokenData
            {
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn),
                TokenType = tokenResponse.TokenType,
                Scope = tokenResponse.Scope ?? string.Empty
            });

            return "Connected to Spotify.";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
        finally
        {
            try { listener.Stop(); } catch { }
            try { ((IDisposable)listener).Dispose(); } catch { }
        }
    }

    public async Task<TokenData?> RefreshAsync(string clientId, string clientSecret, string refreshToken)
    {
        try
        {
            var resp = await new OAuthClient().RequestToken(
                new AuthorizationCodeRefreshRequest(clientId, clientSecret, refreshToken));

            var data = new TokenData
            {
                AccessToken = resp.AccessToken,
                // Spotify may or may not include a new refresh token; keep the old one if absent.
                RefreshToken = string.IsNullOrEmpty(resp.RefreshToken) ? refreshToken : resp.RefreshToken,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(resp.ExpiresIn),
                TokenType = resp.TokenType,
                Scope = resp.Scope ?? string.Empty
            };
            _tokenStore.Save(data);
            return data;
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Token refresh failed", ex);
            return null;
        }
    }

    private static async Task WriteResponse(HttpListenerContext context, string message)
    {
        var html = $"<html><body style='font-family:sans-serif;text-align:center;padding:3em'><h2>{message}</h2></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.OutputStream.Close();
    }
}
