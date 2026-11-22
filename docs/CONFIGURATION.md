# Configuration

TTSMixmaster is configured from its **Settings** page. You shouldn't need to edit files by hand.

## Where settings live

| What | Location |
|---|---|
| Settings | `%LOCALAPPDATA%\TTSMixmaster\config.json` |
| Spotify sign-in (refresh token, DPAPI-encrypted for your Windows account) | `%LOCALAPPDATA%\TTSMixmaster\spotify_token.json` |
| Azure sign-in (authentication record; tokens are in the encrypted MSAL cache) | `%LOCALAPPDATA%\TTSMixmaster\azure-authentication-record.json` |
| Logs | `%LOCALAPPDATA%\TTSMixmaster\logs\` |
| Updated yt-dlp | `%LOCALAPPDATA%\TTSMixmaster\tools\` (the bundled copy ships with the app) |
| Downloaded audio (default) | `Music\TTSMixmaster\<Collection> [<service>-<id>]\` |
| Exported objects | `Documents\My Games\Tabletop Simulator\Saves\Saved Objects\TTSMixmaster\` |

On first run the app imports a `config.json` left by the old Python version (next to the old app or in its working folder) once. Old sign-in tokens aren't carried over, so you sign in to Spotify again.

## config.json keys

The keys are snake_case, so old Python config files load unchanged. Unknown keys are ignored.

| Key | Purpose |
|---|---|
| `lastfm_api_key`, `lastfm_api_secret`, `lastfm_username` | Last.fm charts (top, loved, recent tracks) |
| `youtube_api_key`, `youtube_channel_id` | YouTube Data API (public playlists) |
| `spotify_client_id`, `spotify_client_secret`, `spotify_user_id`, `spotify_redirect_uri`, `spotify_use_oauth` | Spotify. With OAuth (the default) only the client ID is needed; sign-in uses PKCE in your browser. The redirect URI must match your Spotify app registration (default `http://127.0.0.1:8888/callback`). |
| `azure_tenant_id`, `azure_subscription_id`, `azure_storage_account`, `azure_container_name` | The storage target picked after **Sign in to Azure** |
| `azure_client_id` | Override for the built-in Entra app ID (normally empty) |
| `azure_storage_connection_string` | Advanced fallback instead of signing in |
| `download_path`, `tts_output_path` | Override the audio / generated-files folders (empty = defaults above) |
| `tts_saved_objects_path` | Tabletop Simulator's Saved Objects folder, for non-standard installs |
| `audio_quality` | MP3 bitrate: 128, 192, 256 or 320 |
| `theme` | `system`, `light` or `dark` |

`azure_blob_freshness_days` is still accepted for old files but no longer used: uploads are content-addressed, so identical audio is reused and different audio never overwrites anything.

## Azure storage

Sign in from **Settings → Azure**, then pick a subscription, storage account and container. Uploads use your own Microsoft Entra account, so you need the **Storage Blob Data Contributor** role on the account. Tabletop Simulator streams the files anonymously, so the container must allow anonymous blob read. The picker checks both and offers one-click fixes when your account is allowed to make them. See [AZURE_SETUP.md](AZURE_SETUP.md).

## Backing up and moving to another PC

Copy `%LOCALAPPDATA%\TTSMixmaster\config.json`. Sign-in data is encrypted for your Windows account and doesn't move between machines; sign in again on the new PC.
