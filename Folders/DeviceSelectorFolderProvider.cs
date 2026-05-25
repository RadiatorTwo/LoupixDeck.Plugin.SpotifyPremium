using LoupixDeck.Plugin.SpotifyPremium.Spotify;
using LoupixDeck.PluginSdk;
using SpotifyAPI.Web;

namespace LoupixDeck.Plugin.SpotifyPremium.Folders;

/// <summary>
/// Touch-screen folder listing every Spotify Connect device on the user's
/// account. Tapping a slot transfers playback to that device. The list is
/// fetched once on <see cref="OnEnter"/> and refreshed via the player-state
/// cache (which sees device changes within a poll cycle).
/// </summary>
public sealed class DeviceSelectorFolderProvider : FolderProviderBase
{
    private readonly SpotifyClientProvider _clientProvider;
    private readonly PlayerStateCache _playerState;
    private readonly IPluginLogger _logger;
    private List<DeviceEntry> _devices = new();
    private string _activeDeviceId = string.Empty;

    public DeviceSelectorFolderProvider(SpotifyClientProvider clientProvider, PlayerStateCache playerState, IPluginLogger logger)
    {
        _clientProvider = clientProvider;
        _playerState = playerState;
        _logger = logger;
    }

    public override string Title => "Spotify Devices";

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        var entries = new List<FolderEntry>();
        // Slot 0 is reserved for "Active Device" — passes through an empty
        // device id, which Spotify interprets as "use the current device".
        entries.Add(new FolderEntry
        {
            SlotIndex = 0,
            Text = "Active Device",
            Bold = true,
            OnPress = () => Task.CompletedTask
        });

        // Slot 10 is the back button (reserved by the host); skip it.
        var slot = 1;
        foreach (var dev in _devices)
        {
            if (slot == 10) slot++;
            if (slot >= 15) break;

            var isActive = string.Equals(dev.Id, _activeDeviceId, StringComparison.Ordinal);
            entries.Add(new FolderEntry
            {
                SlotIndex = slot++,
                Text = (isActive ? "● " : "") + dev.Name,
                OnPress = () => TransferAsync(dev.Id)
            });
        }

        return entries;
    }

    public override void OnEnter()
    {
        _playerState.Changed += OnPlayerStateChanged;
        _ = LoadDevicesAsync();
    }

    public override void OnExit()
    {
        _playerState.Changed -= OnPlayerStateChanged;
    }

    private void OnPlayerStateChanged(PlayerSnapshot snap)
    {
        if (!string.Equals(snap.DeviceId, _activeDeviceId, StringComparison.Ordinal))
        {
            _activeDeviceId = snap.DeviceId;
            RaiseEntriesChanged();
        }
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            var spotify = await _clientProvider.GetClientAsync();
            if (spotify == null) return;

            var devicesResp = await spotify.Player.GetAvailableDevices();
            _devices = devicesResp.Devices.Select(d => new DeviceEntry(d.Id, d.Name)).ToList();
            _activeDeviceId = devicesResp.Devices.FirstOrDefault(d => d.IsActive)?.Id ?? string.Empty;
            RaiseEntriesChanged();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to load device list: {ex.Message}");
        }
    }

    private async Task TransferAsync(string deviceId)
    {
        try
        {
            var spotify = await _clientProvider.GetClientAsync();
            if (spotify == null) return;
            await spotify.Player.TransferPlayback(new PlayerTransferPlaybackRequest(new[] { deviceId }));
            await _playerState.RefreshNowAsync();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Device transfer failed: {ex.Message}");
        }
    }

    private sealed record DeviceEntry(string Id, string Name);
}
