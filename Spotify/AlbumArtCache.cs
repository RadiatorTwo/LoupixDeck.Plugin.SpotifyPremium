using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SpotifyPremium.Spotify;

/// <summary>
/// The current cover image, downloaded once per image URL and kept in memory,
/// so the album art command draws on the host's polling timer without waiting
/// on the network. Covers come from Spotify's image CDN, not the Web API, so
/// they do not count against the developer account's request quota.
/// </summary>
public sealed class AlbumArtCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    // A failed download is retried after this long, not on every draw.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly IPluginHost _host;
    private readonly string _refreshCommand;
    private readonly object _gate = new();
    private string? _url;
    private byte[]? _bytes;
    private string? _loadingUrl;
    private string? _failedUrl;
    private DateTime _failedAtUtc;

    /// <param name="refreshCommand">Command redrawn once a new cover has arrived.</param>
    public AlbumArtCache(IPluginHost host, string refreshCommand)
    {
        _host = host;
        _refreshCommand = refreshCommand;
    }

    /// <summary>
    /// The image at <paramref name="url"/>, or null while it is still loading or
    /// failed. Starts the download the first time a URL is asked for.
    /// </summary>
    public byte[]? Get(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        lock (_gate)
        {
            if (url == _url) return _bytes;

            bool retryDue = url != _failedUrl || DateTime.UtcNow - _failedAtUtc > RetryAfter;
            if (_loadingUrl == null && retryDue)
            {
                _loadingUrl = url;
                _ = LoadAsync(url);
            }
            return null;
        }
    }

    private async Task LoadAsync(string url)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            lock (_gate)
            {
                _url = url;
                _bytes = bytes;
                _loadingUrl = null;
            }

            try { _host.RequestButtonRefresh(_refreshCommand); }
            catch { /* nothing bound — the next poll draws it */ }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _failedUrl = url;
                _failedAtUtc = DateTime.UtcNow;
                _loadingUrl = null;
            }
            _host.Logger.Warn($"Downloading the album art failed: {ex.Message}");
        }
    }
}
