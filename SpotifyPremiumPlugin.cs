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
        Version = new Version(1, 0, 0),
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

    public async Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        if (!_clientProvider.IsAuthorized)
            return Array.Empty<MenuNode>();

        try
        {
            var spotify = await _clientProvider.GetClientAsync();
            if (spotify == null) return Array.Empty<MenuNode>();

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
            return Array.Empty<MenuNode>();
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
