using LoupixDeck.Plugin.SpotifyPremium.Commands.Auth;
using LoupixDeck.Plugin.SpotifyPremium.Commands.Library;
using LoupixDeck.Plugin.SpotifyPremium.Commands.Playback;
using LoupixDeck.Plugin.SpotifyPremium.Commands.Playlists;
using LoupixDeck.Plugin.SpotifyPremium.Commands.Volume;
using LoupixDeck.Plugin.SpotifyPremium.Platform;
using LoupixDeck.Plugin.SpotifyPremium.Spotify;
using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium;

/// <summary>
/// Entry point of the Spotify Premium plugin. Port of the official Loupedeck
/// SpotifyPremiumPlugin. Owns the OAuth flow, a refresh-aware Spotify client,
/// and a background player-state cache that all commands consult.
/// </summary>
public sealed class SpotifyPremiumPlugin : LoupixPlugin, IPluginSettingsPage, IMenuContributor
{
    private const string SettingClientId = "client_id";
    private const string SettingClientSecret = "client_secret";
    private const string SettingRedirectUri = "redirect_uri";

    /// <summary>Only read to migrate settings saved before the redirect URI was configurable.</summary>
    private const string SettingCallbackPort = "callback_port";

    private const int DefaultCallbackPort = 5543;
    private const string DefaultRedirectUri = "http://127.0.0.1:5543/callback";
    private const string DeveloperDashboardUrl = "https://developer.spotify.com/dashboard";
    internal static readonly TimeSpan VolumeOverlayDuration = TimeSpan.FromMilliseconds(1500);

    private IPluginHost _host = null!;
    private TokenStore _tokenStore = null!;
    private SpotifyAuth _auth = null!;
    private SpotifyClientProvider _clientProvider = null!;
    private PlayerStateCache _playerState = null!;
    private List<IPluginCommand> _commands = new();

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "spotifypremium",
        Name = "Spotify Premium",
        Version = new Version(1, 2, 0),
        SdkVersion = new Version(1, 28, 0),
        Author = "RadiatorTwo",
        Description = "Control Spotify Premium from LoupixDeck: playback, volume, devices, playlists and likes.",
        Icon = LoadIcon()
    };

    /// <summary>The plugin icon (icon.png, embedded). Missing data only costs the icon.</summary>
    private static byte[]? LoadIcon()
    {
        using Stream? stream = typeof(SpotifyPremiumPlugin).Assembly.GetManifestResourceStream("LoupixDeck.Plugin.SpotifyPremium.icon.png");
        if (stream == null) return null;

        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        MigrateCallbackPort();
        _tokenStore = new TokenStore(host.Settings);
        _auth = new SpotifyAuth(host, _tokenStore);

        _clientProvider = new SpotifyClientProvider(
            host,
            _tokenStore,
            _auth,
            () => host.Settings.Get<string>(SettingClientId),
            () => host.Settings.Get<string>(SettingClientSecret));

        _playerState = new PlayerStateCache(_clientProvider, host);
        _playerState.Changed += OnPlayerStateChanged;
        _playerState.Start();

        _commands =
        [
            new LoginCommand(this),
            new TogglePlaybackCommand(_clientProvider, _playerState, host.Logger),
            new NextTrackCommand(_clientProvider, _playerState, host.Logger),
            new PreviousTrackCommand(_clientProvider, _playerState, host.Logger),
            new ShufflePlayCommand(_clientProvider, _playerState, host.Logger),
            new ChangeRepeatStateCommand(_clientProvider, _playerState, host.Logger),
            new MuteCommand(_clientProvider, _playerState, host.Logger),
            new UnmuteCommand(_clientProvider, _playerState, host.Logger),
            new ToggleMuteCommand(_clientProvider, _playerState, host.Logger),
            new DirectVolumeCommand(_clientProvider, _playerState, host.Logger),
            new VolumeUpCommand(_clientProvider, _playerState, host.Logger),
            new VolumeDownCommand(_clientProvider, _playerState, host.Logger),
            new SpotifyVolumeAdjustment(_clientProvider, _playerState, host.Logger),
            new PlayNavigateLeftCommand(_clientProvider, _playerState, host.Logger),
            new PlayNavigateRightCommand(_clientProvider, _playerState, host.Logger),
            new PlayAndNavigateAdjustment(_clientProvider, _playerState, host.Logger),
            new SeekForwardCommand(_clientProvider, _playerState, host.Logger),
            new SeekBackwardCommand(_clientProvider, _playerState, host.Logger),
            new SeekAdjustment(_clientProvider, _playerState, host.Logger),
            new AddToQueueCommand(_clientProvider, _playerState, host.Logger),
            new ToggleLikeCommand(_clientProvider, _playerState, host.Logger),
            new PlayLikedSongsCommand(_clientProvider, _playerState, host.Logger),
            new SaveToPlaylistCommand(_clientProvider, _playerState, host.Logger),
            new RemoveFromPlaylistCommand(_clientProvider, _playerState, host.Logger),
            new StartPlaylistCommand(_clientProvider, host.Logger),
            new StartAlbumCommand(_clientProvider, _playerState, host.Logger),
            new OpenDeviceSelectorCommand(_clientProvider, _playerState)
        ];
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        new CommandGroupDescriptor
        {
            Group = "Spotify Premium",
            Description = "Playback and library control",
            Icon = "\U000F075A",
            Section = CommandGroupSection.Plugins
        }
    ];

    public override void Shutdown()
    {
        _playerState?.Dispose();
    }

    // ---- IPluginSettingsPage ----

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema
    {
        get
        {
            bool connected = _tokenStore?.HasToken == true;

            return
            [
                new PluginSettingDescriptor
                {
                    Key = "__heading_status",
                    Label = PluginText.Format(_host, "Status: {0}", Tr(connected ? "Connected" : "Not connected")),
                    Kind = PluginSettingKind.Heading,
                    Description = connected
                        ? Tr("Spotify is connected. Use \"Disconnect\" to remove the stored login.")
                        : Tr("Follow the steps under \"Spotify app\", save, then press \"Connect to Spotify\"."),
                    DefaultValue = string.Empty
                },
                new PluginSettingDescriptor
                {
                    Key = "__heading_spotify_app",
                    Label = "Spotify app",
                    Kind = PluginSettingKind.Heading,
                    Description = SetupSteps,
                    DefaultValue = string.Empty
                },
                new PluginSettingDescriptor
                {
                    Key = SettingClientId,
                    Label = "Client ID",
                    Kind = PluginSettingKind.Text,
                    Description = "From the \"Basic Information\" page of your Spotify app",
                    DefaultValue = string.Empty
                },
                new PluginSettingDescriptor
                {
                    Key = SettingClientSecret,
                    Label = "Client Secret",
                    Kind = PluginSettingKind.Password,
                    Description = "On the same page, behind \"View client secret\"",
                    DefaultValue = string.Empty
                },
                new PluginSettingDescriptor
                {
                    Key = SettingRedirectUri,
                    Label = "Redirect URI",
                    Kind = PluginSettingKind.Text,
                    Description = "Must match a Redirect URI of your Spotify app character for character. Use http://127.0.0.1 with a free port - the plugin listens there while you sign in.",
                    DefaultValue = DefaultRedirectUri
                }
            ];
        }
    }

    // One string, so the host translates the whole instruction as one key.
    private const string SetupSteps =
        "1. Press \"Open Spotify Dashboard\", sign in and click \"Create app\". Name and description are up to you.\n"
        + "2. Press \"Copy Redirect URI\" and paste it under \"Redirect URIs\" in the app. Tick \"Web API\" and save.\n"
        + "3. Copy Client ID and Client Secret from the app's settings into the fields below and press Save.\n"
        + "4. Press \"Connect to Spotify\" and confirm in the browser. The README has a detailed guide.";

    public IReadOnlyList<PluginSettingAction> SettingsActions =>
    [
        _tokenStore?.HasToken == true ? DisconnectAction() : ConnectAction(),
        CopyRedirectUriAction(),
        OpenDashboardAction()
    ];

    public void OnSettingsSaved()
    {
        _clientProvider?.Invalidate();
    }

    private string Tr(string english) => PluginText.Tr(_host, english);

    /// <summary>
    /// The redirect URI from the settings. Settings saved before the URI was
    /// configurable only hold a port; <see cref="MigrateCallbackPort"/> turns
    /// that into a URI on load, so the port is only a last fallback here.
    /// </summary>
    private string ReadRedirectUri()
    {
        string? uri = _host.Settings.Get<string>(SettingRedirectUri)?.Trim();
        return string.IsNullOrEmpty(uri) ? RedirectUriForPort(ReadLegacyPort()) : uri;
    }

    private int ReadLegacyPort()
    {
        long port = _host.Settings.Get<long>(SettingCallbackPort, DefaultCallbackPort);
        return port is > 0 and < 65536 ? (int)port : DefaultCallbackPort;
    }

    private static string RedirectUriForPort(int port) => $"http://127.0.0.1:{port}/callback";

    /// <summary>
    /// Older versions stored only "callback_port" and built the URI from it.
    /// Keep such a setup working by writing the URI it used; the old key stays
    /// untouched so a downgrade still finds it.
    /// </summary>
    private void MigrateCallbackPort()
    {
        IPluginSettings settings = _host.Settings;
        if (settings.Contains(SettingRedirectUri) || !settings.Contains(SettingCallbackPort)) return;

        settings.Set(SettingRedirectUri, RedirectUriForPort(ReadLegacyPort()));
        settings.Save();
    }

    internal Task<string> ConnectAsync()
    {
        string clientId = _host.Settings.Get<string>(SettingClientId)?.Trim() ?? string.Empty;
        string clientSecret = _host.Settings.Get<string>(SettingClientSecret)?.Trim() ?? string.Empty;

        string? invalid = SpotifyAuth.ValidateRedirectUri(ReadRedirectUri(), out Uri? redirectUri);
        if (invalid != null || redirectUri == null)
            return Task.FromResult(Tr(invalid ?? "Redirect URI missing."));

        return _auth.AuthorizeAsync(clientId, clientSecret, redirectUri);
    }

    private PluginSettingAction ConnectAction() => new()
    {
        Label = "Connect to Spotify",
        Invoke = async () =>
        {
            string result = await ConnectAsync();
            _clientProvider.Invalidate();
            await _playerState.RefreshNowAsync();
            return result;
        }
    };

    private PluginSettingAction DisconnectAction() => new()
    {
        Label = "Disconnect",
        Invoke = () =>
        {
            _tokenStore.Clear();
            _clientProvider.Invalidate();
            return Task.FromResult(Tr("Disconnected - the stored login was removed."));
        }
    };

    private PluginSettingAction CopyRedirectUriAction() => new()
    {
        Label = "Copy Redirect URI",
        Invoke = async () =>
        {
            string uri = ReadRedirectUri();
            return await ClipboardWriter.TrySetTextAsync(uri)
                ? PluginText.Format(_host, "Copied to the clipboard: {0}", uri)
                : PluginText.Format(_host, "The clipboard is not available. Copy the Redirect URI from its field instead: {0}", uri);
        }
    };

    private PluginSettingAction OpenDashboardAction() => new()
    {
        Label = "Open Spotify Dashboard",
        Invoke = () => Task.FromResult(_host.OpenBrowser(DeveloperDashboardUrl)
            ? string.Empty
            : PluginText.Format(_host, "Could not open the browser. Open {0} yourself.", DeveloperDashboardUrl))
    };

    // ---- IMenuContributor ----

    // Maps each static command to the sub-folder it should appear under inside
    // the top-level "Spotify Premium" group. Commands not listed here are
    // excluded from the menu (e.g. adjustment commands surfaced via a different
    // selection UI).
    private static readonly Dictionary<string, string> StaticCategories = new(StringComparer.Ordinal)
    {
        ["SpotifyPremium.TogglePlayback"]     = "Playback",
        ["SpotifyPremium.NextTrack"]          = "Playback",
        ["SpotifyPremium.PreviousTrack"]      = "Playback",
        ["SpotifyPremium.ShufflePlay"]        = "Playback",
        ["SpotifyPremium.ChangeRepeatState"]  = "Playback",
        ["SpotifyPremium.PlayNavigate.Left"]  = "Playback",
        ["SpotifyPremium.PlayNavigate.Right"] = "Playback",
        ["SpotifyPremium.SeekForward"]        = "Playback",
        ["SpotifyPremium.SeekBackward"]       = "Playback",
        ["SpotifyPremium.SeekAdjustment"]     = "Playback",
        ["SpotifyPremium.AddToQueue"]         = "Playback",
        ["SpotifyPremium.Mute"]               = "Volume",
        ["SpotifyPremium.Unmute"]             = "Volume",
        ["SpotifyPremium.ToggleMute"]         = "Volume",
        ["SpotifyPremium.DirectVolume"]       = "Volume",
        ["SpotifyPremium.VolumeUp"]           = "Volume",
        ["SpotifyPremium.VolumeDown"]         = "Volume",
        ["SpotifyPremium.ToggleLike"]         = "Library",
        ["SpotifyPremium.PlayLikedSongs"]     = "Library",
        ["SpotifyPremium.OpenDeviceSelector"] = "Devices",
        ["SpotifyPremium.Login"]              = "Account"
    };

    private static readonly string[] CategoryOrder =
        ["Playback", "Volume", "Library", "Playlists", "Albums", "Devices", "Account"];

    public async Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        var categories = new Dictionary<string, List<MenuNode>>(StringComparer.Ordinal);

        foreach (var cmd in _commands)
        {
            var d = cmd.Descriptor;
            if (!StaticCategories.TryGetValue(d.CommandName, out var category))
                continue;
            if (!cmd.SupportedTargets.HasFlag(target))
                continue;

            if (!categories.TryGetValue(category, out var leafs))
            {
                leafs = new List<MenuNode>();
                categories[category] = leafs;
            }

            leafs.Add(new MenuNode { Name = d.DisplayName, CommandName = d.CommandName });
        }

        var playlistFolders = await BuildPlaylistFoldersAsync();
        if (playlistFolders.Count > 0)
            categories["Playlists"] = playlistFolders;

        var albumFolders = await BuildAlbumFoldersAsync();
        if (albumFolders.Count > 0)
            categories["Albums"] = albumFolders;

        var subFolders = CategoryOrder
            .Where(categories.ContainsKey)
            .Select(name => new MenuNode { Name = name, Children = categories[name] })
            .ToList();

        if (subFolders.Count == 0)
            return Array.Empty<MenuNode>();

        return [new MenuNode { Name = "Spotify Premium", Children = subFolders }];
    }

    private async Task<List<MenuNode>> BuildPlaylistFoldersAsync()
    {
        if (!_clientProvider.IsAuthorized)
            return new List<MenuNode>();

        try
        {
            var spotify = await _clientProvider.GetClientAsync();
            if (spotify == null) return new List<MenuNode>();

            var firstPage = await spotify.Playlists.CurrentUsers();
            var playlists = await spotify.PaginateAll(firstPage);

            List<MenuNode> ChildrenFor(string commandName) => playlists
                .Select(p => new MenuNode
                {
                    Name = p.Name ?? "(untitled)",
                    CommandName = commandName,
                    Parameters = new Dictionary<string, string> { ["PlaylistId"] = p.Id ?? string.Empty }
                })
                .ToList();

            return
            [
                new MenuNode
                {
                    Name = "Start Playlist",
                    Children = ChildrenFor("SpotifyPremium.StartPlaylist")
                },
                new MenuNode
                {
                    Name = "Add Track to Playlist",
                    Children = ChildrenFor("SpotifyPremium.SaveToPlaylist")
                },
                new MenuNode
                {
                    Name = "Remove Track from Playlist",
                    Children = ChildrenFor("SpotifyPremium.RemoveFromPlaylist")
                }
            ];
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"Failed to build playlist menu: {ex.Message}");
            return new List<MenuNode>();
        }
    }

    private async Task<List<MenuNode>> BuildAlbumFoldersAsync()
    {
        if (!_clientProvider.IsAuthorized)
            return new List<MenuNode>();

        // The by-link entry works even without saved albums: the user pastes
        // an album link into the command's parameter.
        var byLink = new MenuNode { Name = "Start Album by Link", CommandName = "SpotifyPremium.StartAlbum" };

        try
        {
            var spotify = await _clientProvider.GetClientAsync();
            if (spotify == null) return [byLink];

            var firstPage = await spotify.Library.GetAlbums(new LibraryAlbumsRequest { Limit = 50 });
            var albums = await spotify.PaginateAll(firstPage);

            var saved = albums
                .Where(a => a.Album != null)
                .Select(a => new MenuNode
                {
                    Name = a.Album.Artists is { Count: > 0 }
                        ? $"{a.Album.Name} – {a.Album.Artists[0].Name}"
                        : a.Album.Name ?? "(untitled)",
                    CommandName = "SpotifyPremium.StartAlbum",
                    Parameters = new Dictionary<string, string> { ["Album"] = a.Album.Id ?? string.Empty }
                })
                .ToList();

            return saved.Count == 0
                ? [byLink]
                : [new MenuNode { Name = "Start Saved Album", Children = saved }, byLink];
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"Failed to build album menu: {ex.Message}");
            return [byLink];
        }
    }

    private void OnPlayerStateChanged(PlayerSnapshot snap)
    {
        // Whenever Spotify state changes, ask the host to redraw any button
        // bound to a display command we own. The set is fixed and small.
        foreach (var name in new[]
        {
            "SpotifyPremium.TogglePlayback",
            "SpotifyPremium.ShufflePlay",
            "SpotifyPremium.ChangeRepeatState",
            "SpotifyPremium.ToggleMute",
            "SpotifyPremium.ToggleLike"
        })
        {
            try { _host.RequestButtonRefresh(name); }
            catch { /* host may not be ready or button not bound — ignore */ }
        }
    }
}
