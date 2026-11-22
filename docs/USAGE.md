# Usage Guide

## Running the Application

Start **TTSMixmaster** from the Start menu. With the portable zip, run `TTSMixmaster.exe` from
the extracted folder. See the [Installation Guide](INSTALLATION.md) to install it.

## Configuration

### Initial Setup
1. Open the "Configuration" tab
2. **Multi-Service Setup** (all credentials are optional):
   - **Last.fm**: Enter API key, secret, and username for collections access
   - **YouTube**: Enter Data API v3 key and optional Channel ID for personal playlists
   - **Spotify**: Enter Client ID, Client Secret, and optional User ID for personal playlists
   - **Azure Storage**: Enter connection string and container name (optional for cloud uploads)
3. Set your preferred download and output paths
4. Configure audio quality settings (128, 192, 256, 320 kbps)
5. Click "Save Configuration"

### Test Connections
Use the individual test buttons to verify each service:
- "Test Last.fm Connection" - Validates API credentials and username
- "Test YouTube Connection" - Verifies API key and access
- "Test Spotify Connection" - Confirms Client ID and Secret
- "Test Azure Connection" - Tests storage connection and container access

## Working with Collections & Playlists

### Getting Started
1. Go to the "Collections & Playlists" tab
2. **Select Music Service**: Choose from Last.fm, YouTube, or Spotify using the service dropdown

### Service-Specific Usage

#### Last.fm Collections
- View available collections (top tracks, loved tracks, recent tracks)
- Select time period for top tracks (overall, 7 days, 1 month, etc.)
- Configure track limits (default: 50, maximum: 1000)

#### YouTube Playlists
- Browse user playlists, liked videos, or search public playlists
- Access personal playlists with Channel ID
- Search public playlists by keyword

#### Spotify Playlists
- Access user playlists or search public playlists
- Browse personal playlists with User ID
- Search and discover public playlists

### Loading Tracks
1. Select a playlist/collection from the dropdown
2. Click "Load Tracks" to fetch track information
3. View track listings with artist, title, and additional metadata
4. **Multi-Service Workflow**: Switch between services and combine collections/playlists as needed

## Downloading Audio

### Basic Download Process
1. Switch to the "Download" tab
2. Select your preferred search engine (YouTube, SoundCloud, or both)
3. Configure audio quality if desired (128, 192, 256, 320 kbps)
4. Click "Start Download" to begin downloading all tracks from loaded collections/playlists

### Monitoring Progress
- Monitor real-time progress in the progress bar and results panel
- View detailed results showing successful downloads and any failures with error messages
- Download results are saved for use in upload and TTS generation steps

## Uploading to Cloud Storage

### Azure Blob Storage (Recommended)
1. Go to the "Upload" tab
2. Ensure Azure Storage is configured in the Configuration tab
3. Click "Upload to Azure" to upload downloaded files to cloud storage
4. Generated URLs will be automatically configured for TTS compatibility

### Optional Settings
- Set a title prefix for your upload items
- Click "Prepare Upload Folders" to organize files before upload
- Monitor upload progress and view detailed results
- Click "Show Instructions" for detailed Azure setup guidance

## Generating TTS Code

### Basic TTS Generation
1. Navigate to the "TTS Format" tab
2. Click "Generate TTS Code"
3. Preview the generated code in the preview panel
4. Files are automatically saved to your TTS output directory
5. Click "Open Output Folder" to access generated files

### Customization Options

#### TTS Object Customization
- **Nickname**: Enter custom nickname for your TTS object (uses playlist name if empty)
- **Description**: Add custom description (auto-generated if empty)
- **Primary Image URL**: Set custom image for the front/top of your TTS object
- **Secondary Image URL**: Set custom image for the back/bottom of your TTS object
- **Note**: Leave image fields empty to use default Steam Workshop images

#### Format Options
- **Output Format**: Choose from Lua script, JSON data, save file, or all formats
- **Simple vs. Advanced Format**:
  - Simple format: Basic track listing (like Woody's Progressive Metal example)
  - Full format: Complete music player with controls and advanced features

### Using Custom Images
When using custom image URLs for TTS objects:
- Use direct links to image files (JPG, PNG, GIF)
- Ensure URLs are publicly accessible
- Recommended image size: 512x512 or higher
- Test URLs in a browser before using them

## Working with Generated Files

### File Types
The application generates several file types for TTS use:

1. **Lua Scripts** (`.lua` files):
   - Complete music player objects with controls
   - Simple playlist format for basic usage
   - Copy-paste ready code for TTS scripting

2. **JSON Data** (`.json` files):
   - Structured data for external integrations
   - Track metadata and URL information
   - Configuration data for advanced usage

3. **TTS Save Files** (`.json` save format):
   - Complete TTS save file with embedded music player
   - Custom object images and properties
   - Ready-to-load save files

### File Organization
- **Downloads**: `./downloads/` - Downloaded audio files
- **Uploads**: `./uploads/` - Prepared upload folders
- **TTS Output**: `./tts_formatted/` - Generated TTS files

## Multi-Service Workflows

### Combining Services
1. Load collections/playlists from multiple services
2. Download audio from all loaded sources
3. Upload all files to cloud storage
4. Generate unified TTS code for all tracks

### Service-Specific Tips
- **Last.fm**: Best for personal music history and discovery
- **YouTube**: Great for specific playlists and public content
- **Spotify**: Excellent for curated playlists and music discovery
- **Azure**: Essential for reliable cloud hosting of audio files

## Best Practices

### Performance Tips
- Start with smaller playlists to test the workflow
- Use appropriate audio quality settings for your needs
- Monitor disk space during downloads
- Use Azure upload for reliable TTS integration

### Organization Tips
- Use descriptive names for playlists and collections
- Set custom nicknames and descriptions for TTS objects
- Organize output files by project or theme
- Keep track of API usage limits

## Troubleshooting
For detailed troubleshooting information, see the [Troubleshooting Guide](TROUBLESHOOTING.md).
