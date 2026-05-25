using LoupixDeck.Plugin.SpotifyPremium.Commands.Auth;
using LoupixDeck.Plugin.SpotifyPremium.Commands.Library;
using LoupixDeck.Plugin.SpotifyPremium.Commands.Playback;
using LoupixDeck.Plugin.SpotifyPremium.Commands.Playlists;
using LoupixDeck.Plugin.SpotifyPremium.Commands.Volume;
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
    private const string SettingCallbackPort = "callback_port";
    private const int DefaultCallbackPort = 5543;
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
        Version = new Version(1, 1, 0),
        SdkVersion = new Version(1, 6, 0),
        Author = "RadiatorTwo",
        Description = "Control Spotify Premium from LoupixDeck: playback, volume, devices, playlists and likes."
    };

    public override void Initialize(IPluginHost host)
    {
        _host = host;
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
            new ToggleLikeCommand(_clientProvider, _playerState, host.Logger),
            new SaveToPlaylistCommand(_clientProvider, _playerState, host.Logger),
            new StartPlaylistCommand(_clientProvider, host.Logger),
            new OpenDeviceSelectorCommand(_clientProvider, _playerState)
        ];
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    public override void Shutdown()
    {
        _playerState?.Dispose();
    }

    // ---- IPluginSettingsPage ----

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema =>
    [
        new PluginSettingDescriptor
        {
            Key = "__heading_spotify_app",
            Label = "Spotify App",
            Kind = PluginSettingKind.Heading,
            Description = "Create an app at developer.spotify.com and paste the values here. The Redirect URI must be exactly http://127.0.0.1:<port>/callback.",
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = SettingClientId,
            Label = "Client ID",
            Kind = PluginSettingKind.Text,
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = SettingClientSecret,
            Label = "Client Secret",
            Kind = PluginSettingKind.Password,
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = SettingCallbackPort,
            Label = "OAuth Callback Port",
            Kind = PluginSettingKind.Number,
            DefaultValue = (long)DefaultCallbackPort
        },
new PluginSettingDescriptor
        {
            Key = "__heading_connection",
            Label = "Connection",
            Kind = PluginSettingKind.Heading,
            Description = _tokenStore?.HasToken == true
                ? "Currently connected. Use 'Disconnect' to clear the token."
                : "Not connected yet. Enter Client ID/Secret, save, then click 'Connect to Spotify'.",
            DefaultValue = string.Empty
        }
    ];

    public IReadOnlyList<PluginSettingAction> SettingsActions =>
        _tokenStore?.HasToken == true
            ? [DisconnectAction()]
            : [ConnectAction()];

    public void OnSettingsSaved()
    {
        _clientProvider?.Invalidate();
    }

    internal Task<string> ConnectAsync()
    {
        var clientId = _host.Settings.Get<string>(SettingClientId);
        var clientSecret = _host.Settings.Get<string>(SettingClientSecret);
        var port = (int)_host.Settings.Get<long>(SettingCallbackPort, DefaultCallbackPort);
        if (port is <= 0 or >= 65536) port = DefaultCallbackPort;
        return _auth.AuthorizeAsync(clientId, clientSecret, port);
    }

    private PluginSettingAction ConnectAction() => new()
    {
        Label = "Connect to Spotify",
        Invoke = async () =>
        {
            var result = await ConnectAsync();
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
            return Task.FromResult("Token cleared.");
        }
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
        ["SpotifyPremium.Mute"]               = "Volume",
        ["SpotifyPremium.Unmute"]             = "Volume",
        ["SpotifyPremium.ToggleMute"]         = "Volume",
        ["SpotifyPremium.DirectVolume"]       = "Volume",
        ["SpotifyPremium.VolumeUp"]           = "Volume",
        ["SpotifyPremium.VolumeDown"]         = "Volume",
        ["SpotifyPremium.ToggleLike"]         = "Library",
        ["SpotifyPremium.OpenDeviceSelector"] = "Devices",
        ["SpotifyPremium.Login"]              = "Account"
    };

    private static readonly string[] CategoryOrder =
        ["Playback", "Volume", "Library", "Playlists", "Devices", "Account"];

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
                }
            ];
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"Failed to build playlist menu: {ex.Message}");
            return new List<MenuNode>();
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
