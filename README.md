# LoupixDeck.Plugin.SpotifyPremium

Spotify Premium integration plugin for [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck),
built against [LoupixDeck.PluginSdk](https://github.com/RadiatorTwo/LoupixDeck.PluginSdk).

Requires a Spotify Premium account.

## Commands

Playback: `SpotifyPremium.TogglePlayback`, `SpotifyPremium.NextTrack`,
`SpotifyPremium.PreviousTrack`, `SpotifyPremium.ShufflePlay`,
`SpotifyPremium.ChangeRepeatState`, `SpotifyPremium.PlayNavigateLeft`,
`SpotifyPremium.PlayNavigateRight`, `SpotifyPremium.PlayAndNavigate`,
`SpotifyPremium.SeekForward`, `SpotifyPremium.SeekBackward`,
`SpotifyPremium.SeekAdjustment`, `SpotifyPremium.AlbumArt`.

`SpotifyPremium.AlbumArt(Opacity,Spin)` shows the current track's cover on a
touch button or a wheel layer; pressing it toggles play/pause. `Opacity`
(0–100, default 100) dims it, for example behind text. `Spin` is the number
of seconds per full turn (default 0, still): the cover turns while the track
plays and holds still while paused or while you scrub. On a square touch
button the corners show as it turns, so spinning suits the round CT wheel.
Covers are downloaded from Spotify's image servers once per track and do not
count against the Web API quota.

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

### CT wheel: seek with track name and album art

`SpotifyPremium.SeekAdjustment` reports the track position to the dial
indicator, the time (`1:23 / 4:05`) as the value text and the track name as
the value detail. Without a custom layout the wheel shows the wheel mode's
name, the time and the indicator. To show the track name and cover:

1. Bind Seek to a wheel mode and open its appearance editor.
2. Select the label text layer and set its **Text source** to **Value
   detail**. It then shows the track name. Left on **Wheel label**, it shows
   the wheel mode's name, which is blank unless you named the mode.
3. Restyle the indicator and the time text as you like.
4. Add a live image layer, pick Album Art as its command and move it to the
   bottom. To set the parameters, edit the command text under the picker:
   `SpotifyPremium.AlbumArt(60,30)` dims the cover to 60% and turns it once
   every 30 seconds.

## Setup

The plugin talks to the Spotify Web API through a Spotify app of your own. Creating one is free
and takes a few minutes.

### 1. Create a Spotify app

1. Open the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) (or press
   **Open Spotify Dashboard** in the plugin settings) and sign in with your Spotify account.
2. Click **Create app**. Name and description can be anything, e.g. `LoupixDeck`.
3. Under **Redirect URIs**, paste the redirect URI from the plugin settings and click **Add**.
   **Copy Redirect URI** in the plugin settings puts it on the clipboard. The default is
   `http://127.0.0.1:5543/callback`.
4. Under **Which API/SDKs are you planning to use?** tick **Web API**, accept the terms and save.

### 2. Enter the credentials

1. In the app, open **Settings → Basic Information**.
2. Copy the **Client ID**, then click **View client secret** and copy the **Client Secret**.
3. In LoupixDeck open **Plugins → Spotify Premium → Settings**, paste both values and press
   **Save**.

### 3. Connect

1. Press **Connect to Spotify**. Your browser opens Spotify's consent page.
2. Confirm it. The browser shows *Spotify connection successful* and the status in the plugin
   settings reads *Connected*.

The plugin keeps the refresh token in `plugins/spotifypremium/settings.json` and renews the access
on its own. **Disconnect** removes the stored login.

### Redirect URI

While you sign in, the plugin briefly listens on the address of the redirect URI to receive
Spotify's answer. So the redirect URI must:

- be identical, character for character, in the plugin settings and in the Spotify app,
- start with `http://` and point to this PC — `http://127.0.0.1:<port>/<path>` (Spotify does not
  accept `localhost`; `http://[::1]:<port>/<path>` also works),
- use a port that no other program is using.

To use a different port or path, change it in the plugin settings, save, press **Copy Redirect
URI** and add the new value to the Spotify app.

Settings from older plugin versions only stored a port (**OAuth Callback Port**). They are carried
over automatically as `http://127.0.0.1:<port>/callback`, so an existing setup keeps working.

### Troubleshooting

| Message | Cause |
|---|---|
| *INVALID_CLIENT: Invalid redirect URI* (in the browser) | The redirect URI in the plugin is not registered in the Spotify app. Copy it again and add it under **Redirect URIs**. |
| *Port … cannot be opened* | Another program uses the port. Pick a different port in the redirect URI and register the new value in the Spotify app. |
| *Redirect URI must point to this PC …* / *must start with http://* | The redirect URI is not a local `http://` address. |
| *Timed out waiting for Spotify response* | The consent page was not confirmed within two minutes. Press **Connect to Spotify** again. |
| *Error: invalid_client* | Client ID or Client Secret is wrong. Copy both again; a rotated secret invalidates the old one. |
| Commands do nothing | Spotify Premium is required, and a Spotify app must be open on some device to receive commands. |

## Settings

| Setting | Meaning |
|---|---|
| Client ID / Client Secret | From the Spotify app, see [Setup](#setup). |
| Redirect URI | Must match a redirect URI of the Spotify app, see [Redirect URI](#redirect-uri). Default `http://127.0.0.1:5543/callback`. |

## Build & deploy

```bash
dotnet build LoupixDeck.Plugin.SpotifyPremium.csproj -c Release
```

Copy the build output together with `plugin.json` into
`LoupixDeck/plugins/spotifypremium/`.
