# Configuration Reference

## Environment Variables

All API credentials are optional - configure only the services you want to use:

### Last.fm Configuration
- `LASTFM_API_KEY` - Your Last.fm API key (for collections access)
- `LASTFM_API_SECRET` - Your Last.fm API secret (for collections access)
- `LASTFM_USERNAME` - Your Last.fm username (for personal collections)

### YouTube Configuration
- `YOUTUBE_API_KEY` - Your YouTube Data API v3 key (for playlist access)
- `YOUTUBE_CHANNEL_ID` - Your YouTube Channel ID (optional, for personal playlists)

### Spotify Configuration
- `SPOTIFY_CLIENT_ID` - Your Spotify Client ID (for playlist access)
- `SPOTIFY_CLIENT_SECRET` - Your Spotify Client Secret (for playlist access)
- `SPOTIFY_USER_ID` - Your Spotify User ID (optional, for personal playlists)
- `SPOTIFY_REDIRECT_URI` - OAuth redirect URI (default: http://127.0.0.1:8888/callback)
- `SPOTIFY_USE_OAUTH` - Use OAuth for user data access (default: true, set to false for public data only)

### Azure Storage Configuration
- `AZURE_STORAGE_CONNECTION_STRING` - Azure Storage connection string (optional, for cloud uploads)
- `AZURE_CONTAINER_NAME` - Azure Storage container name (optional, defaults to "tts-audio")

### Application Settings
- `DOWNLOAD_PATH` - Path for downloaded files (default: ./downloads)
- `UPLOAD_PATH` - Path for upload preparation (default: ./uploads)
- `TTS_OUTPUT_PATH` - Path for TTS formatted files (default: ./tts_formatted)
- `AUDIO_QUALITY` - Audio quality in kbps: 128, 192, 256, 320 (default: 192)
- `THEME` - UI theme: dark, light (default: dark)
- `WINDOW_SIZE` - Application window size (default: 1200x800)

## GUI Configuration

The application provides a comprehensive configuration interface:

### API Settings
Multi-service credentials and connection testing:
- Last.fm API key, secret, and username
- YouTube Data API v3 key and channel ID
- Spotify Client ID, secret, and user ID

### Azure Settings
Cloud storage configuration for uploads:
- Connection string
- Container name
- Upload options

### Path Settings
Customizable paths for:
- Downloads directory
- Uploads directory
- TTS output directory

### Audio Settings
- Quality selection (128, 192, 256, 320 kbps)
- Search engine preferences
- Download options

### Theme Settings
- Dark/light mode selection
- Window size configuration
- UI preferences

## Configuration Files

### .env File
Contains sensitive API credentials and configuration:
```env
# Last.fm Configuration
LASTFM_API_KEY=your_api_key_here
LASTFM_API_SECRET=your_api_secret_here
LASTFM_USERNAME=your_username

# YouTube Configuration
YOUTUBE_API_KEY=your_youtube_api_key_here
YOUTUBE_CHANNEL_ID=your_channel_id_optional

# Spotify Configuration
SPOTIFY_CLIENT_ID=your_spotify_client_id_here
SPOTIFY_CLIENT_SECRET=your_spotify_client_secret_here
SPOTIFY_USER_ID=your_spotify_user_id_optional
SPOTIFY_REDIRECT_URI=http://127.0.0.1:8888/callback
SPOTIFY_USE_OAUTH=true

# Azure Configuration
AZURE_STORAGE_CONNECTION_STRING=your_azure_connection_string
AZURE_CONTAINER_NAME=tts-audio
```

### config.json File
Contains application settings and preferences:
```json
{
  "download_path": "./downloads",
  "upload_path": "./uploads",
  "tts_output_path": "./tts_formatted",
  "audio_quality": "192",
  "theme": "dark",
  "window_size": "1200x800"
}
```

## Service-Specific Configuration

### Last.fm Setup
1. Visit https://www.last.fm/api/account/create
2. Create an API account
3. Get your API key and secret
4. Add your Last.fm username

### YouTube Setup
1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Create or select a project
3. Enable YouTube Data API v3
4. Create an API key
5. (Optional) Add your Channel ID

### Spotify Setup
1. Visit [Spotify Developer Dashboard](https://developer.spotify.com/dashboard/)
2. Create a new app (or use an existing one)
3. In the app settings, add `http://127.0.0.1:8888/callback` as a Redirect URI
4. Get Client ID and Client Secret from the app dashboard
5. **For Development Mode Apps**: Add your Spotify account email to the "User Management" section
6. (Optional) Add your Spotify User ID for additional features

**Important**: The application uses OAuth Authorization Code flow to access user playlists and private data. When you first connect to Spotify, your browser will open automatically to authenticate. After logging in and granting permissions, the application will save your credentials for future use.

**Redirect URI**: The default redirect URI is `http://127.0.0.1:8888/callback`. Make sure this exactly matches the URI in your Spotify app settings. If you need to change it, update both the Spotify Dashboard and the `spotify_redirect_uri` setting in your config.

### Azure Setup
1. Create an Azure Storage account
2. Get the connection string
3. Choose or create a container name

## Default Values

### Paths
- Download Path: `./downloads`
- Upload Path: `./uploads`
- TTS Output Path: `./tts_formatted`

### Audio Settings
- Quality: 192 kbps
- Search Engine: YouTube
- Format: MP3

### UI Settings
- Theme: Dark
- Window Size: 1200x800
- Tab: Configuration (on startup)

### Azure Settings
- Container Name: "tts-audio"
- Public Access: Blob level
- Upload Timeout: 300 seconds

## Configuration Validation

### Required vs Optional
- **Required for Basic Usage**: None (application works without any API credentials)
- **Required for Last.fm**: API key, secret, username
- **Required for YouTube**: API key
- **Required for Spotify**: Client ID, Client Secret
- **Required for Azure**: Connection string

### Testing Configuration
Use the built-in test buttons to validate:
- Last.fm: Tests API credentials and user access
- YouTube: Tests API key and quota
- Spotify: Tests Client ID and Secret
- Azure: Tests connection and container access

### Common Configuration Issues
- Invalid API credentials
- Expired API keys
- Incorrect usernames or IDs
- Network connectivity issues
- Service quotas exceeded

## Advanced Configuration

### Custom Paths
You can set custom paths for all directories:
```env
DOWNLOAD_PATH=/custom/downloads
UPLOAD_PATH=/custom/uploads
TTS_OUTPUT_PATH=/custom/tts_output
```

### Audio Quality Settings
Supported quality levels:
- 128 kbps: Basic quality, smaller files
- 192 kbps: Good quality, balanced size
- 256 kbps: High quality, larger files
- 320 kbps: Maximum quality, largest files

### Theme Customization
- Dark: Default dark theme
- Light: Light theme for bright environments
- System: Follows system theme (if supported)

### Window Configuration
- Standard: 1200x800 (default)
- Compact: 1000x600
- Large: 1400x900
- Custom: Set any valid dimensions

## Backup and Restore

### Backing Up Configuration
Important files to backup:
- `.env` - API credentials
- `config.json` - Application settings
- Custom path directories

### Restoring Configuration
1. Copy backed up files to project directory
2. Verify file permissions
3. Test configuration using built-in test buttons
4. Update any expired credentials

### Migration Between Systems
1. Export `.env` and `config.json`
2. Set up new environment
3. Import configuration files
4. Update paths if necessary
5. Test all services
