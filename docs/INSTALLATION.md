# Installation Guide

## Prerequisites
- Python 3.8 or higher
- Git (for cloning the repository)

## Complete Setup Guide

### 1. Clone the Repository
```bash
git clone <repository-url>
cd TTSMixmaster
```

### 2. Create Virtual Environment (Recommended)
```bash
python -m venv venv
venv\Scripts\activate  # On Windows
# source venv/bin/activate  # On Linux/Mac
```

### 3. Install Dependencies
```bash
pip install .
```
Note: This automatically includes FFmpeg via the `imageio-ffmpeg` package.

### 4. Setup Environment Configuration
```bash
# Copy environment template
cp .env.template .env
```

Edit `.env` with your API keys (all are optional - use only the services you need):
- **Last.fm API**: Key and secret (get from https://www.last.fm/api/account/create)
- **YouTube API**: Data API v3 key (get from Google Cloud Console)
- **Spotify API**: Client ID and Client Secret (get from Spotify Developer Dashboard)
- **Azure Storage**: Connection string for cloud uploads (optional)

## API Setup Guides

### Last.fm API (for accessing collections)
1. Visit https://www.last.fm/api/account/create
2. Create an API account and get your API key and secret
3. Add your Last.fm username to the configuration
4. **Note**: Last.fm provides "collections" (top tracks, loved tracks, recent tracks) rather than traditional playlists

### YouTube Data API v3 (for accessing playlists)
1. Go to the [Google Cloud Console](https://console.cloud.google.com/)
2. Create a new project or select an existing one
3. Enable the YouTube Data API v3
4. Create credentials (API key)
5. **Optional**: Add your YouTube Channel ID for personal playlists access
6. **Note**: Public playlist search works without Channel ID

### Spotify Web API (for accessing playlists)
1. Go to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard/)
2. Create a new app
3. Get your Client ID and Client Secret
4. **Optional**: Add your Spotify User ID for personal playlists access
5. **Note**: Public playlist search works without User ID

### Azure Storage (Optional)
- Azure Storage account for cloud uploads
- Connection string for authentication
- Container for storing audio files

## Verification

### Run the Application
```bash
python main.py
```

### Test Basic Functionality
```bash
python tests/test_basic.py
```

### Test Multi-Service Integration
```bash
python tests/test_multi_service.py
```

## Common Setup Issues

### Python Virtual Environment Issues
```bash
# If virtual environment creation fails
python -m pip install --upgrade pip
python -m pip install virtualenv
python -m virtualenv venv
```

### Dependency Installation Problems
```bash
# If pip install fails, try upgrading pip first
python -m pip install --upgrade pip

# Install dependencies one by one if batch install fails
pip install customtkinter
pip install yt-dlp
pip install requests
pip install python-dotenv
```

### FFmpeg Error (`'FFmpegPostProcessorPP'`)
- The `imageio-ffmpeg` package should handle FFmpeg automatically
- If issues persist, install FFmpeg manually from https://ffmpeg.org/

### Import Errors
- Ensure you're using the correct virtual environment
- Verify all dependencies are installed: `pip list`
- Try running from the project root directory

## Dependencies Not Installing
If you encounter issues with dependencies:
```bash
# For audio processing
pip install pydub

# For YouTube downloading
pip install yt-dlp

# For GUI
pip install customtkinter

# For environment variables
pip install python-dotenv

# For Azure storage (optional)
pip install azure-storage-blob
```

## Next Steps
Once installation is complete, see the [Usage Guide](USAGE.md) for detailed instructions on using the application.
