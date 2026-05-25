# LoupixDeck.Plugin.SpotifyPremium

Spotify Premium integration plugin for [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck),
built against [LoupixDeck.PluginSdk](https://github.com/RadiatorTwo/LoupixDeck.PluginSdk).

Requires a Spotify Premium account.

## Commands

Playback: `SpotifyPremium.TogglePlayback`, `SpotifyPremium.NextTrack`,
`SpotifyPremium.PreviousTrack`, `SpotifyPremium.ShufflePlay`,
`SpotifyPremium.ChangeRepeatState`, `SpotifyPremium.PlayNavigateLeft`,
`SpotifyPremium.PlayNavigateRight`, `SpotifyPremium.PlayAndNavigate`.

Volume: `SpotifyPremium.Mute`, `SpotifyPremium.Unmute`,
`SpotifyPremium.ToggleMute`, `SpotifyPremium.DirectVolume`,
`SpotifyPremium.VolumeUp`, `SpotifyPremium.VolumeDown`,
`SpotifyPremium.VolumeAdjustment`.

Library / playlists: `SpotifyPremium.ToggleLike`,
`SpotifyPremium.SaveToPlaylist`, `SpotifyPremium.StartPlaylist` (one menu
entry per playlist of the logged-in user).

Devices: `SpotifyPremium.OpenDeviceSelector` — touch-screen folder listing
the available Spotify Connect devices for transfer.

Auth: `SpotifyPremium.Login` — triggers the OAuth flow.

## Settings

Configured in LoupixDeck's plugin settings: Spotify **Client ID**,
**Client Secret** and **OAuth Callback Port** (default `5543`). Create an
app at [developer.spotify.com](https://developer.spotify.com) and set the
Redirect URI to exactly `http://127.0.0.1:<port>/callback`. After saving,
use **Connect to Spotify** to run the OAuth flow; the refresh token is
persisted to `plugins/spotifypremium/settings.json`.

## Build & deploy

```bash
dotnet build LoupixDeck.Plugin.SpotifyPremium.csproj -c Release
```

Copy the build output together with `plugin.json` into
`LoupixDeck/plugins/spotifypremium/`.
