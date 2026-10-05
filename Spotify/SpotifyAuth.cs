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
    /// Checks that <paramref name="value"/> is a redirect URI this plugin can
    /// receive: plain http on a loopback address (Spotify only allows http for
    /// loopback IPs), so a local listener can catch the redirect. Returns null
    /// when valid, else the English reason (translate before showing it).
    /// </summary>
    public static string? ValidateRedirectUri(string? value, out Uri? redirectUri)
    {
        redirectUri = null;
        if (string.IsNullOrWhiteSpace(value)) return "Redirect URI missing.";

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri))
            return "Redirect URI is not a valid address.";
        if (uri.Scheme != Uri.UriSchemeHttp)
            return "Redirect URI must start with http:// - the plugin receives the answer itself.";
        if (!uri.IsLoopback)
            return "Redirect URI must point to this PC, e.g. http://127.0.0.1:5543/callback.";
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return "Redirect URI must not contain ? or #.";

        redirectUri = uri;
        return null;
    }

    /// <summary>
    /// Returns a short status string suitable for the settings action button.
    /// On success the token is already persisted and the caller can rebuild
    /// the SpotifyClient. On failure no token is stored.
    /// </summary>
    public async Task<string> AuthorizeAsync(string clientId, string clientSecret, Uri redirectUri, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return Tr("Client ID missing.");
        if (string.IsNullOrWhiteSpace(clientSecret)) return Tr("Client Secret missing.");

        // The listener takes every path on the port; requests for other paths
        // (a favicon, a stale tab) are answered and ignored below.
        string prefix = $"{redirectUri.Scheme}://{redirectUri.Authority}/";

        HttpListener listener = new();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"Cannot listen on {prefix}: {ex.Message}");
            return PluginText.Format(_host, "Port {0} cannot be opened: {1}", redirectUri.Port, ex.Message);
        }

        try
        {
            var loginRequest = new LoginRequest(redirectUri, clientId, LoginRequest.ResponseType.Code)
            {
                Scope = RequiredScopes.ToList()
            };

            if (!_host.OpenBrowser(loginRequest.ToUri().ToString()))
                return Tr("Could not open browser.");

            // 2-minute hard timeout — the user might abandon the consent screen.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            HttpListenerContext? context = await WaitForCallbackAsync(listener, redirectUri, timeoutCts.Token);
            if (context == null)
                return Tr("Timed out waiting for Spotify response.");

            var query = context.Request.QueryString;
            var code = query.Get("code");
            var error = query.Get("error");

            await WriteResponse(context,
                error != null
                    ? PluginText.Format(_host, "Spotify returned an error: {0}. You can close this window.", error)
                    : Tr("Spotify connection successful. You can close this window."));

            if (error != null) return PluginText.Format(_host, "Spotify error: {0}", error);
            if (string.IsNullOrEmpty(code)) return Tr("No authorization code received.");

            var tokenResponse = await new OAuthClient().RequestToken(
                new AuthorizationCodeTokenRequest(clientId, clientSecret, code, redirectUri));

            _tokenStore.Save(new TokenData
            {
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn),
                TokenType = tokenResponse.TokenType,
                Scope = tokenResponse.Scope ?? string.Empty
            });

            return Tr("Connected to Spotify.");
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Spotify authorization failed", ex);
            return PluginText.Format(_host, "Error: {0}", ex.Message);
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

    private string Tr(string english) => PluginText.Tr(_host, english);

    /// <summary>
    /// Waits for the request on the redirect path. Anything else that reaches
    /// the port gets a 404 and is skipped. Null on timeout.
    /// </summary>
    private static async Task<HttpListenerContext?> WaitForCallbackAsync(HttpListener listener, Uri redirectUri, CancellationToken ct)
    {
        string expectedPath = redirectUri.AbsolutePath.TrimEnd('/');

        while (true)
        {
            Task<HttpListenerContext> contextTask = listener.GetContextAsync();
            Task completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, ct));
            if (completed != contextTask) return null;

            HttpListenerContext context = await contextTask;
            string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            if (string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase))
                return context;

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
    }

    private static async Task WriteResponse(HttpListenerContext context, string message)
    {
        var html = $"<html><body style='font-family:sans-serif;text-align:center;padding:3em'><h2>{WebUtility.HtmlEncode(message)}</h2></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.OutputStream.Close();
    }
}
